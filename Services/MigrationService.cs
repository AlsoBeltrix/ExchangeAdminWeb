using System.Globalization;
using System.Management.Automation;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class MigrationService : ExchangeServiceBase
{
    private readonly IConfiguration _config;
    private readonly ModuleConfigService _moduleConfig;
    private readonly string[] _adminNotificationEmails;

    private string MigrationConfig(string key, string fallbackConfigKey, string defaultValue = "")
    {
        var val = _moduleConfig.GetValue("Migration", key);
        if (_moduleConfig.IsCorrupt) return "";
        if (val != null) return val;
        if (!_moduleConfig.HasConfigFile)
            return _config[fallbackConfigKey] ?? defaultValue;
        return defaultValue;
    }

    private string _hybridEndpoint => MigrationConfig("HybridEndpoint", "Migration:HybridEndpoint", "hybrid1");
    private string _cloudTargetDomain => MigrationConfig("CloudTargetDeliveryDomain", "Migration:CloudTargetDeliveryDomain");
    private string _onPremTargetDomain => MigrationConfig("OnPremTargetDeliveryDomain", "Migration:OnPremTargetDeliveryDomain");
    private string _onPremTargetDAG => MigrationConfig("OnPremTargetDAG", "Migration:OnPremTargetDAG");
    private long _cloudQuotaGB => long.TryParse(MigrationConfig("CloudQuotaGB", "Migration:CloudQuotaGB", "100"), out var v) ? v : 100;
    private string[] _excludedADGroups
    {
        get
        {
            var val = _moduleConfig.GetValue("Migration", "ExcludedADGroups");
            if (!string.IsNullOrEmpty(val))
                return val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!_moduleConfig.HasConfigFile)
                return _config.GetSection("Migration:ExcludedADGroups").Get<string[]>() ?? Array.Empty<string>();
            return Array.Empty<string>();
        }
    }

    public MigrationService(IConfiguration config, ExoConnectionPool exoPool, DelineaService delineaService, ILogger logger, string onPremServerUri, ModuleConfigService moduleConfig)
        : base(exoPool, delineaService, logger, onPremServerUri)
    {
        _config = config;
        _moduleConfig = moduleConfig;

        var adminEmail = config["Email:AdminNotificationEmail"] ?? "";
        _adminNotificationEmails = adminEmail.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToArray();
    }

    public async Task<MigrationEligibilityResult> CheckMigrationEligibilityAsync(string emailAddress, MigrationDirection direction)
    {
        var result = await RunPooledQueryAsync(ps =>
        {
            var r = new MigrationEligibilityResult
            {
                EmailAddress = emailAddress,
                Status = MigrationStatus.Eligible
            };

            try
            {
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Ignore");
                var migUser = InvokeOptional(ps);
                if (migUser.Any())
                {
                    r.Status = MigrationStatus.Ineligible;
                    r.IneligibilityReasons.Add("Migration already in progress");
                }

                ps.AddCommand("Get-Mailbox")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Ignore");
                var cloudMbx = InvokeOptional(ps);
                var isCloudMailbox = false;

                if (cloudMbx.Any())
                {
                    var recipientType = cloudMbx.FirstOrDefault()?.Properties["RecipientTypeDetails"]?.Value?.ToString();
                    isCloudMailbox = recipientType?.Contains("UserMailbox") == true || recipientType?.Contains("SharedMailbox") == true;
                }

                if (direction == MigrationDirection.ToCloud)
                {
                    if (isCloudMailbox)
                    {
                        r.Status = MigrationStatus.Ineligible;
                        r.IneligibilityReasons.Add("Already a cloud mailbox");
                    }

                    if (_excludedADGroups.Length > 0)
                    {
                        r.NeedsAdGroupCheck = true;
                    }
                }
                else
                {
                    if (!isCloudMailbox)
                    {
                        r.Status = MigrationStatus.Ineligible;
                        r.IneligibilityReasons.Add("Not a cloud mailbox (must be in Exchange Online to migrate back to on-premises)");
                    }
                }
            }
            catch (Exception ex)
            {
                r.Status = MigrationStatus.Ineligible;
                r.IneligibilityReasons.Add($"Error checking eligibility: {ex.Message}");
                _logger.LogError(ex, "Error checking migration eligibility for {Email}", emailAddress);
            }

            return r;
        });

        if (direction == MigrationDirection.ToCloud && result.NeedsAdGroupCheck)
        {
            try
            {
                CheckAdGroupMembership(result, _excludedADGroups);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking AD group membership for {Email}", emailAddress);
                result.IneligibilityReasons.Add($"Warning: Could not verify AD group membership ({ex.Message})");
            }
        }

        if (direction == MigrationDirection.ToCloud && result.Status == MigrationStatus.Eligible)
        {
            try
            {
                var sizeResult = await GetOnPremMailboxSizeAsync(emailAddress);

                if (sizeResult is not null)
                {
                    var (mailboxGB, archiveGB) = sizeResult.Value;
                    var totalGB = mailboxGB + archiveGB;
                    result.MailboxSizeGB = mailboxGB;
                    result.ArchiveSizeGB = archiveGB;
                    result.CloudQuotaGB = _cloudQuotaGB;

                    _logger.LogInformation("On-prem sizes for {Email}: Mailbox={MailboxGB:F2} GB, Archive={ArchiveGB:F2} GB, Total={TotalGB:F2} GB, Quota={QuotaGB} GB",
                        emailAddress, mailboxGB, archiveGB, totalGB, _cloudQuotaGB);

                    if (totalGB > _cloudQuotaGB)
                    {
                        result.Status = MigrationStatus.Ineligible;
                        result.IneligibilityReasons.Add($"Mailbox + archive size ({totalGB:F2} GB) exceeds cloud quota ({_cloudQuotaGB} GB)");
                    }
                }
                else
                {
                    result.Status = MigrationStatus.Ineligible;
                    _logger.LogWarning("Could not retrieve on-prem mailbox size for {Email} - marking ineligible", emailAddress);
                    result.IneligibilityReasons.Add("Could not verify on-prem mailbox size (on-prem connection unavailable)");
                }
            }
            catch (Exception sizeEx)
            {
                result.Status = MigrationStatus.Ineligible;
                _logger.LogError(sizeEx, "Error checking on-prem mailbox size for {Email}", emailAddress);
                result.IneligibilityReasons.Add($"Could not verify on-prem mailbox size ({sizeEx.Message})");
            }
        }

        return result;
    }

    public async Task<MigrationBatchResult> CheckBulkMigrationEligibilityAsync(Stream csvStream, MigrationDirection direction)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(csvStream, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        var records = csv.GetRecords<MigrationCsvRow>().ToList();
        var results = new List<MigrationEligibilityResult>();

        foreach (var row in records)
        {
            var eligibility = await CheckMigrationEligibilityAsync(row.EmailAddress, direction);
            results.Add(eligibility);
        }

        var eligible = results.Count(r => r.Status == MigrationStatus.Eligible);
        var ineligible = results.Count - eligible;

        return new MigrationBatchResult
        {
            BatchName = $"{DateTime.Now:yyyy-MM-dd}_{(direction == MigrationDirection.ToCloud ? "Move" : "MoveBack")}",
            Success = true,
            Direction = direction,
            EligibilityResults = results,
            TotalUsers = results.Count,
            EligibleUsers = eligible,
            IneligibleUsers = ineligible,
            AutoStart = false,
            AutoComplete = false
        };
    }

    public async Task<PermissionResult> CreateMigrationBatchAsync(MigrationDirection direction, List<string> eligibleEmails, string batchName, bool autoStart, bool autoComplete)
    {
        string[]? targetDatabases = null;
        if (direction == MigrationDirection.ToOnPrem)
        {
            targetDatabases = await ResolveDagDatabasesAsync(_onPremTargetDAG);
            if (targetDatabases == null || targetDatabases.Length == 0)
                return PermissionResult.Fail("Unable to resolve target databases from DAG. Check OnPremExchange and Migration:OnPremTargetDAG configuration.");
        }

        return await RunAsync(ps =>
        {
            // Create CSV content in memory
            var csvContent = "EmailAddress\r\n" + string.Join("\r\n", eligibleEmails);
            var csvBytes = Encoding.UTF8.GetBytes(csvContent);

            if (direction == MigrationDirection.ToCloud)
            {
                ps.AddCommand("New-MigrationBatch")
                  .AddParameter("Name", batchName)
                  .AddParameter("SourceEndpoint", _hybridEndpoint)
                  .AddParameter("TargetDeliveryDomain", _cloudTargetDomain)
                  .AddParameter("CSVData", csvBytes)
                  .AddParameter("NotificationEmails", _adminNotificationEmails)
                  .AddParameter("ErrorAction", "Stop");
            }
            else // ToOnPrem
            {
                ps.AddCommand("New-MigrationBatch")
                  .AddParameter("Name", batchName)
                  .AddParameter("TargetEndpoint", _hybridEndpoint)
                  .AddParameter("TargetDeliveryDomain", _onPremTargetDomain)
                  .AddParameter("TargetDatabases", targetDatabases)
                  .AddParameter("CSVData", csvBytes)
                  .AddParameter("NotificationEmails", _adminNotificationEmails)
                  .AddParameter("ErrorAction", "Stop");
            }

            Invoke(ps);

            // Auto-start if requested
            if (autoStart)
            {
                ps.AddCommand("Start-MigrationBatch")
                  .AddParameter("Identity", batchName)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps);
            }

            // Auto-complete if requested
            if (autoComplete)
            {
                ps.AddCommand("Set-MigrationBatch")
                  .AddParameter("Identity", batchName)
                  .AddParameter("CompleteAfter", DateTime.Now.AddHours(-1))
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps);
            }

        }, () =>
        {
            var userList = string.Join(", ", eligibleEmails);

            string message;
            if (autoStart && eligibleEmails.Count == 1)
            {
                message = $"Mailbox \"{eligibleEmails[0]}\" migration is in progress. Please monitor it in the portal.";
            }
            else if (autoStart)
            {
                message = $"Migration batch '{batchName}' with {eligibleEmails.Count} user(s) is in progress. Please monitor it in the portal.";
            }
            else if (eligibleEmails.Count == 1)
            {
                message = $"Mailbox \"{eligibleEmails[0]}\" migration batch created. Please monitor it in the portal.";
            }
            else
            {
                message = $"Migration batch '{batchName}' created successfully with {eligibleEmails.Count} user(s). Please monitor it in the portal.";
            }

            var details = $@"Batch Name: {batchName}
Direction: {direction}
Users: {userList}
Count: {eligibleEmails.Count}
Auto-Start: {autoStart}
Auto-Complete: {autoComplete}

Monitor progress in the Exchange Admin Center at:
https://admin.exchange.microsoft.com/#/migration";

            return (message, details);
        });
    }

    public async Task<List<MigrationBatchInfo>> GetMigrationBatchesAsync()
    {
        return await RunPooledQueryAsync(ps =>
        {
            var batches = new List<MigrationBatchInfo>();

            try
            {
                // Get all migration batches
                ps.AddCommand("Get-MigrationBatch")
                  .AddParameter("ErrorAction", "Ignore");

                var batchResults = InvokeOptional(ps);

                foreach (var batchObj in batchResults)
                {
                    try
                    {
                        var batchNameVal = batchObj.Properties["Identity"]?.Value?.ToString() ?? "Unknown";
                        var status = batchObj.Properties["Status"]?.Value?.ToString() ?? "Unknown";
                        var totalCount = Convert.ToInt32(batchObj.Properties["TotalCount"]?.Value ?? 0);
                        var syncedCount = Convert.ToInt32(batchObj.Properties["SyncedCount"]?.Value ?? batchObj.Properties["SyncedItemCount"]?.Value ?? 0);
                        var finalizedCount = Convert.ToInt32(batchObj.Properties["FinalizedCount"]?.Value ?? batchObj.Properties["FinalizedItemCount"]?.Value ?? 0);
                        var failedCount = Convert.ToInt32(batchObj.Properties["FailedCount"]?.Value ?? batchObj.Properties["FailedItemCount"]?.Value ?? 0);
                        var createdDateTime = batchObj.Properties["CreationDateTime"]?.Value as DateTime? ?? DateTime.MinValue;
                        var startDateTime = batchObj.Properties["StartDateTime"]?.Value as DateTime?;
                        var completedDateTime = batchObj.Properties["CompletionDateTime"]?.Value as DateTime?;
                        var targetEndpoint = batchObj.Properties["TargetEndpoint"]?.Value?.ToString();
                        var autoStartVal = Convert.ToBoolean(batchObj.Properties["AutoStart"]?.Value ?? false);
                        var autoCompleteVal = Convert.ToBoolean(batchObj.Properties["CompleteAfter"]?.Value != null);

                        // Determine direction based on endpoint (hybrid = ToOnPrem, null/cloud = ToCloud)
                        var direction = targetEndpoint?.Contains("hybrid", StringComparison.OrdinalIgnoreCase) == true
                            ? MigrationDirection.ToOnPrem
                            : MigrationDirection.ToCloud;

                        batches.Add(new MigrationBatchInfo
                        {
                            BatchName = batchNameVal,
                            Status = status,
                            CreatedDateTime = createdDateTime,
                            StartDateTime = startDateTime,
                            CompletedDateTime = completedDateTime,
                            TotalCount = totalCount,
                            SyncedCount = syncedCount,
                            FinalizedCount = finalizedCount,
                            FailedCount = failedCount,
                            TargetEndpoint = targetEndpoint,
                            AutoStart = autoStartVal,
                            AutoComplete = autoCompleteVal,
                            Direction = direction
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process migration batch");
                    }
                }

                return batches;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve migration batches");
                return new List<MigrationBatchInfo>();
            }
        });
    }

    public async Task<List<MigrationUserInfo>> GetMigrationBatchUsersAsync(string batchName)
    {
        return await RunPooledQueryAsync(ps =>
        {
            var users = new List<MigrationUserInfo>();

            try
            {
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("BatchId", batchName)
                  .AddParameter("ErrorAction", "Ignore");

                var userResults = InvokeOptional(ps);

                foreach (var userObj in userResults)
                {
                    var email = userObj.Properties["Identity"]?.Value?.ToString() ?? "Unknown";
                    var userStatus = userObj.Properties["Status"]?.Value?.ToString() ?? "Unknown";
                    var errorSummary = userObj.Properties["ErrorSummary"]?.Value?.ToString();
                    var lastSyncDateTime = userObj.Properties["LastSyncTime"]?.Value as DateTime?;
                    var itemsSynced = Convert.ToInt64(userObj.Properties["ItemsSynced"]?.Value ?? 0);
                    var itemsSkipped = Convert.ToInt64(userObj.Properties["ItemsSkipped"]?.Value ?? 0);

                    users.Add(new MigrationUserInfo
                    {
                        EmailAddress = email,
                        Status = userStatus,
                        ErrorSummary = errorSummary,
                        LastSyncDateTime = lastSyncDateTime,
                        ItemsSynced = itemsSynced,
                        ItemsSkipped = itemsSkipped
                    });
                }

                return users;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve migration users for batch {BatchName}", batchName);
                return new List<MigrationUserInfo>();
            }
        });
    }

    public Task<PermissionResult> CompleteMigrationBatchAsync(string batchName)
    {
        return RunAsync(ps =>
        {
            ps.AddCommand("Complete-MigrationBatch")
              .AddParameter("Identity", batchName)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        }, () => ($"Migration batch '{batchName}' completion initiated.", (string?)null));
    }

    public Task<PermissionResult> CompleteMigrationUserAsync(string emailAddress)
    {
        return RunAsync(ps =>
        {
            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("CompleteAfter", DateTime.UtcNow)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        }, () => ($"Migration completion initiated for {emailAddress}.", (string?)null));
    }

    public Task<PermissionResult> ApproveMigrationUserAsync(string emailAddress)
    {
        // Mirrors ApproveMigrationUser.ps1: approve skipped items, set complete, resume move request
        return RunAsync(ps =>
        {
            var pastDate = DateTime.Now.AddDays(-1);

            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ApproveSkippedItems", true)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);

            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("CompleteAfter", pastDate)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);

            ps.AddCommand("Set-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ApproveSkippedItems", true)
              .AddParameter("ErrorAction", "Ignore");
            InvokeOptional(ps);

            ps.AddCommand("Set-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("CompleteAfter", pastDate)
              .AddParameter("ErrorAction", "Ignore");
            InvokeOptional(ps);

            ps.AddCommand("Set-MoveRequest")
              .AddParameter("Identity", emailAddress)
              .AddParameter("SkippedItemApprovalTime", pastDate)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);

            ps.AddCommand("Resume-MoveRequest")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);

            ps.AddCommand("Complete-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Ignore");
            InvokeOptional(ps);
        }, () => ($"Approved skipped items and initiated completion for {emailAddress}.", (string?)null));
    }

    public Task<PermissionResult> StopMigrationUserAsync(string emailAddress)
    {
        return RunAsync(ps =>
        {
            ps.AddCommand("Stop-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        }, () => ($"Migration stopped for {emailAddress}.", (string?)null));
    }

    public Task<PermissionResult> ResumeMigrationUserAsync(string emailAddress)
    {
        return RunAsync(ps =>
        {
            ps.AddCommand("Start-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        }, () => ($"Migration resumed for {emailAddress}.", (string?)null));
    }

    public Task<PermissionResult> RemoveMigrationUserAsync(string emailAddress)
    {
        return RunAsync(ps =>
        {
            ps.AddCommand("Remove-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        }, () => ($"Migration user {emailAddress} removed.", (string?)null));
    }

    public Task<PermissionResult> RemoveMigrationBatchAsync(string batchName)
    {
        return RunAsync(ps =>
        {
            ps.AddCommand("Remove-MigrationBatch")
              .AddParameter("Identity", batchName)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        }, () => ($"Migration batch '{batchName}' removed.", (string?)null));
    }

    public async Task<string?> GetMigrationUserReportAsync(string emailAddress)
    {
        return await RunPooledQueryAsync(ps =>
        {
            try
            {
                ps.AddCommand("Get-MigrationUserStatistics")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("IncludeReport", true)
                  .AddParameter("ErrorAction", "Stop");

                var results = Invoke(ps);
                var stats = results.FirstOrDefault();
                if (stats is null) return (string?)null;

                var sb = new StringBuilder();

                var error = stats.Properties["Error"]?.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(error))
                    sb.AppendLine($"Error: {error}");

                var errorSummary = stats.Properties["ErrorSummary"]?.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(errorSummary))
                    sb.AppendLine($"Error Summary: {errorSummary}");

                var status = stats.Properties["Status"]?.Value?.ToString();
                sb.AppendLine($"Status: {status}");

                var skippedItemCount = stats.Properties["SkippedItemCount"]?.Value;
                sb.AppendLine($"Skipped Items: {skippedItemCount}");

                var syncedItemCount = stats.Properties["SyncedItemCount"]?.Value;
                sb.AppendLine($"Synced Items: {syncedItemCount}");

                var report = stats.Properties["Report"]?.Value;
                if (report is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine("--- Move Report ---");
                    sb.AppendLine(report.ToString());
                }

                return (string?)sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get migration report for {Email}", emailAddress);
                return (string?)$"Error retrieving report: {ex.Message}";
            }
        });
    }

    public async Task<MigrationUserSearchResult> FindMigrationUserBatchAsync(string searchTerm)
    {
        return await RunPooledQueryAsync(ps =>
        {
            try
            {
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("ResultSize", "Unlimited")
                  .AddParameter("ErrorAction", "Stop");

                var results = Invoke(ps);

                var users = new List<(string Email, string? BatchId)>();
                foreach (var user in results)
                {
                    var email = user.Properties["Identity"]?.Value?.ToString();
                    var batchId = user.Properties["BatchId"]?.Value?.ToString();
                    if (email != null)
                        users.Add((email, batchId));
                }

                return MatchMigrationUser(searchTerm, users);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to search migration users for {Term}", searchTerm);
                return MigrationUserSearchResult.Failed(ex.Message);
            }
        });
    }

    public static MigrationUserSearchResult MatchMigrationUser(
        string searchTerm, List<(string Email, string? BatchId)> users)
    {
        var term = searchTerm.Trim();

        var exact = users.FirstOrDefault(u =>
            u.Email.Equals(term, StringComparison.OrdinalIgnoreCase));

        if (exact.Email != null)
        {
            if (string.IsNullOrEmpty(exact.BatchId))
                return MigrationUserSearchResult.NotFound();
            return MigrationUserSearchResult.Found(exact.BatchId, exact.Email);
        }

        var partials = users
            .Where(u => u.Email.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (partials.Count == 0)
            return MigrationUserSearchResult.NotFound();

        if (partials.Count == 1)
        {
            var match = partials[0];
            if (string.IsNullOrEmpty(match.BatchId))
                return MigrationUserSearchResult.NotFound();
            return MigrationUserSearchResult.Found(match.BatchId, match.Email);
        }

        return MigrationUserSearchResult.Ambiguous(partials.Count);
    }
}
