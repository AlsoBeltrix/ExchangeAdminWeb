using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;
using Serilog.Events;

namespace ExchangeAdminWeb.Services;

public class ExchangeService : ExchangeServiceBase, IExchangeService, IIdentityResolver
{
    private readonly string _appId;
    private readonly string _organization;
    private readonly string _certSubject;
    private readonly string[] _adminNotificationEmails;
    private readonly ExtendedLogService _extLog;
    private readonly IConfiguration _config;

    private readonly ModuleConfigService _moduleConfig;

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

    public ExchangeService(IConfiguration config, ILogger<ExchangeService> logger, DelineaService delineaService, ModuleCredentialService moduleCredentials, ExoConnectionPool exoPool, ModuleConfigService moduleConfig, ExtendedLogService extLog)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "", moduleCredentials, "ExchangeService")
    {
        _appId = config["ExchangeOnline:AppId"]
            ?? throw new InvalidOperationException("ExchangeOnline:AppId is not configured.");
        _organization = config["ExchangeOnline:Organization"]
            ?? throw new InvalidOperationException("ExchangeOnline:Organization is not configured.");
        _certSubject = config["ExchangeOnline:CertificateSubject"] ?? "CN=EXO-Automation";
        _config = config;
        _moduleConfig = moduleConfig;

        // Admin notification emails
        var adminEmail = config["Email:AdminNotificationEmail"] ?? "";
        _adminNotificationEmails = adminEmail.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToArray();

        _extLog = extLog;
    }

    public Task<PermissionResult> AddMailboxPermissionsAsync(string targetMailbox, string user, bool fullAccess, bool sendAs, bool autoMapping)
    {
        var permissions = new List<string>();
        if (fullAccess) permissions.Add("FullAccess");
        if (sendAs) permissions.Add("SendAs");

        return RunAsync((ps, tracker) =>
        {
            ValidateMailbox(ps, targetMailbox);
            ValidateRecipient(ps, user);

            if (fullAccess)
            {
                ps.AddCommand("Add-MailboxPermission")
                  .AddParameter("Identity", targetMailbox)
                  .AddParameter("User", user)
                  .AddParameter("AccessRights", "FullAccess")
                  .AddParameter("AutoMapping", autoMapping)
                  .AddParameter("Confirm", false);
                Invoke(ps, tracker);
            }

            if (sendAs)
            {
                ps.AddCommand("Add-RecipientPermission")
                  .AddParameter("Identity", targetMailbox)
                  .AddParameter("Trustee", user)
                  .AddParameter("AccessRights", "SendAs")
                  .AddParameter("Confirm", false);
                Invoke(ps, tracker);
            }
        }, () =>
        {
            var rights = string.Join(" and ", permissions);
            var message = $"{user} has been granted {rights} rights to {targetMailbox}";
            var detail = $"Users can access this mailbox in Outlook or at the following link:\nhttps://outlook.office.com/mail/{targetMailbox}/";
            return (message, detail);
        });
    }

    public Task<PermissionResult> RemoveMailboxPermissionsAsync(string targetMailbox, string user, bool fullAccess, bool sendAs)
    {
        var permissions = new List<string>();
        if (fullAccess) permissions.Add("FullAccess");
        if (sendAs) permissions.Add("SendAs");

        return RunAsync((ps, tracker) =>
        {
            ValidateMailbox(ps, targetMailbox);
            ValidateRecipient(ps, user);

            if (fullAccess)
            {
                ps.AddCommand("Remove-MailboxPermission")
                  .AddParameter("Identity", targetMailbox)
                  .AddParameter("User", user)
                  .AddParameter("AccessRights", "FullAccess")
                  .AddParameter("Confirm", false);
                Invoke(ps, tracker);
            }

            if (sendAs)
            {
                ps.AddCommand("Remove-RecipientPermission")
                  .AddParameter("Identity", targetMailbox)
                  .AddParameter("Trustee", user)
                  .AddParameter("AccessRights", "SendAs")
                  .AddParameter("Confirm", false);
                Invoke(ps, tracker);
            }
        }, () =>
        {
            var rights = string.Join(" and ", permissions);
            return ($"{rights} rights removed for {user} on {targetMailbox}", null);
        });
    }

    public Task<PermissionResult> SetCalendarPermissionAsync(string targetMailbox, string user, CalendarAccessRight accessRight)
    {
        string? calendarPath = null;

        return RunAsync((ps, tracker) =>
        {
            var resolvedMailbox = ValidateMailbox(ps, targetMailbox);
            ValidateRecipient(ps, user);

            calendarPath = GetCalendarFolderName(ps, resolvedMailbox);
            var level = accessRight.ToString();

            ps.AddCommand("Set-MailboxFolderPermission")
              .AddParameter("Identity", calendarPath)
              .AddParameter("User", user)
              .AddParameter("AccessRights", level)
              .AddParameter("ErrorAction", "Stop");
            try
            {
                Invoke(ps, tracker);
            }
            catch
            {
                ps.Commands.Clear();
                ps.Streams.Error.Clear();
                ps.AddCommand("Add-MailboxFolderPermission")
                  .AddParameter("Identity", calendarPath)
                  .AddParameter("User", user)
                  .AddParameter("AccessRights", level)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
            }
        }, () => ($"{user} granted {accessRight} permission to {calendarPath}", null));
    }

    public Task<PermissionResult> RemoveCalendarPermissionAsync(string targetMailbox, string user)
    {
        string? calendarPath = null;

        return RunAsync((ps, tracker) =>
        {
            var resolvedMailbox = ValidateMailbox(ps, targetMailbox);
            ValidateRecipient(ps, user);

            calendarPath = GetCalendarFolderName(ps, resolvedMailbox);
            ps.AddCommand("Remove-MailboxFolderPermission")
              .AddParameter("Identity", calendarPath)
              .AddParameter("User", user)
              .AddParameter("Confirm", false);
            Invoke(ps, tracker);
        }, () => ($"Calendar permission removed for {user} on {calendarPath}", null));
    }

    public async Task<BulkOperationResult> ProcessMailboxPermissionsCsvAsync(Stream csvStream, bool isAdd, PermissionValidator validator, string currentUser, AuditService audit, string ipAddress, string ticketNumber)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(csvStream, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        var records = new List<MailboxPermissionCsvRow>();
        await foreach (var row in csv.GetRecordsAsync<MailboxPermissionCsvRow>())
        {
            records.Add(row);
            if (records.Count > 200)
                break;
        }
        if (records.Count > 200)
            return new BulkOperationResult { TotalRows = records.Count, FailedCount = records.Count, Errors = new() { "CSV exceeds 200 row limit. Please split into smaller files." } };
        var errors = new List<string>();
        var entries = new List<BulkOperationEntry>();
        var successCount = 0;
        var pendingRows = new List<(MailboxPermissionCsvRow row, bool fullAccess, bool sendAs, bool autoMap, string permType)>();

        foreach (var row in records)
        {
            try
            {
                var fullAccess = ParseBool(row.FullAccess);
                var sendAs = ParseBool(row.SendAs);
                var autoMap = ParseBool(row.AutoMapping ?? "true");

                if (!fullAccess && !sendAs)
                {
                    audit.LogMailboxPermission(
                        currentUser, ipAddress, $"Bulk{(isAdd ? "Add" : "Remove")}_NoPermissions",
                        row.Target, row.User, "None", false, ticketNumber,
                        errorDetail: "No permissions specified (FullAccess and SendAs are both false)");
                    errors.Add($"{row.Target}/{row.User}: No permissions specified (FullAccess and SendAs are both false)");
                    entries.Add(new BulkOperationEntry
                    {
                        Target = row.Target,
                        User = row.User,
                        Permission = "None",
                        Status = "FAILED",
                        Message = "No permissions specified"
                    });
                    continue;
                }

                var permissions = new List<string>();
                if (fullAccess) permissions.Add("FullAccess");
                if (sendAs) permissions.Add("SendAs");
                var permType = string.Join("+", permissions);

                // Validate target is not excluded
                var validationError = await validator.ValidateTargetMailboxAsync(row.Target);
                if (validationError is not null)
                {
                    audit.LogMailboxPermission(
                        currentUser, ipAddress, $"Bulk{(isAdd ? "Add" : "Remove")}_ExcludedTarget",
                        row.Target, row.User, permType, false, ticketNumber,
                        errorDetail: validationError);
                    errors.Add($"{row.Target}/{row.User}: {validationError}");
                    entries.Add(new BulkOperationEntry
                    {
                        Target = row.Target,
                        User = row.User,
                        Permission = "Mailbox",
                        Status = "FAILED",
                        Message = validationError
                    });
                    continue;
                }

                if (isAdd)
                {
                    var selfGrantError = await validator.ValidateSelfGrantAsync(currentUser, row.User);
                    if (selfGrantError is not null)
                    {
                        // Log self-grant attempt to audit log
                        audit.LogMailboxPermission(
                            currentUser, ipAddress, $"BulkAdd{permType}_SelfGrantAttempt",
                            row.Target, row.User, permType, false, ticketNumber, autoMap,
                            errorDetail: "Self-grant blocked: User attempted to grant permissions to themselves");

                        errors.Add($"{row.Target}/{row.User}: {selfGrantError}");
                        entries.Add(new BulkOperationEntry
                        {
                            Target = row.Target,
                            User = row.User,
                            Permission = "Mailbox",
                            Status = "FAILED",
                            Message = selfGrantError
                        });
                        continue;
                    }
                }

                pendingRows.Add((row, fullAccess, sendAs, autoMap, permType));
            }
            catch (Exception ex)
            {
                audit.LogMailboxPermission(
                    currentUser, ipAddress, $"Bulk{(isAdd ? "Add" : "Remove")}_Error",
                    row.Target, row.User, "Mailbox", false, ticketNumber,
                    errorDetail: ex.Message);
                errors.Add($"{row.Target}/{row.User}: {ex.Message}");
                entries.Add(new BulkOperationEntry
                {
                    Target = row.Target,
                    User = row.User,
                    Permission = "Mailbox",
                    Status = "ERROR",
                    Message = ex.Message
                });
            }
        }

        if (pendingRows.Count > 0)
        {
            await RunPooledBatchAsync((ps, tracker) => Task.Run(() =>
            {
                foreach (var (row, fullAccess, sendAs, autoMap, permType) in pendingRows)
                {
                    var result = ExecuteMailboxPermission(ps, tracker, row.Target, row.User, fullAccess, sendAs, autoMap, isAdd);

                    var action = $"Bulk{(isAdd ? "Add" : "Remove")}_{permType}";
                    audit.LogMailboxPermission(
                        currentUser, ipAddress, action, row.Target, row.User, permType,
                        result.Success, ticketNumber, isAdd && fullAccess ? autoMap : null,
                        errorDetail: result.Success ? null : result.Message);

                    if (result.Success)
                    {
                        successCount++;
                        entries.Add(new BulkOperationEntry
                        {
                            Target = row.Target,
                            User = row.User,
                            Permission = permType,
                            Status = "SUCCESS",
                            Message = result.Message
                        });
                    }
                    else
                    {
                        errors.Add($"{row.Target}/{row.User}: {result.Message}");
                        entries.Add(new BulkOperationEntry
                        {
                            Target = row.Target,
                            User = row.User,
                            Permission = permType,
                            Status = "FAILED",
                            Message = result.Message
                        });
                    }
                }
            }));
        }

        return new BulkOperationResult
        {
            TotalRows = records.Count,
            SuccessCount = successCount,
            FailedCount = errors.Count,
            Errors = errors,
            Entries = entries
        };
    }

    public async Task<BulkOperationResult> ProcessCalendarPermissionsCsvAsync(Stream csvStream, bool isSet, PermissionValidator validator, string currentUser, AuditService audit, string ipAddress, string ticketNumber)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(csvStream, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        var records = new List<CalendarPermissionCsvRow>();
        await foreach (var row in csv.GetRecordsAsync<CalendarPermissionCsvRow>())
        {
            records.Add(row);
            if (records.Count > 200)
                break;
        }
        if (records.Count > 200)
            return new BulkOperationResult { TotalRows = records.Count, FailedCount = records.Count, Errors = new() { "CSV exceeds 200 row limit. Please split into smaller files." } };
        var errors = new List<string>();
        var entries = new List<BulkOperationEntry>();
        var successCount = 0;
        var pendingRows = new List<(CalendarPermissionCsvRow row, string permType)>();

        foreach (var row in records)
        {
            try
            {
                var validationError = await validator.ValidateTargetMailboxAsync(row.Target);
                if (validationError is not null)
                {
                    audit.LogCalendarPermission(
                        currentUser, ipAddress, $"Bulk{(isSet ? "Set" : "Remove")}Calendar_ExcludedTarget",
                        row.Target, row.User, null, false, ticketNumber,
                        errorDetail: validationError);
                    errors.Add($"{row.Target}/{row.User}: {validationError}");
                    entries.Add(new BulkOperationEntry
                    {
                        Target = row.Target,
                        User = row.User,
                        Permission = "Calendar",
                        Status = "FAILED",
                        Message = validationError
                    });
                    continue;
                }

                if (isSet)
                {
                    var selfGrantError = await validator.ValidateSelfGrantAsync(currentUser, row.User);
                    if (selfGrantError is not null)
                    {
                        audit.LogCalendarPermission(
                            currentUser, ipAddress, "BulkSetCalendar_SelfGrantAttempt",
                            row.Target, row.User, row.AccessRight, false, ticketNumber,
                            errorDetail: "Self-grant blocked: User attempted to grant permissions to themselves");

                        errors.Add($"{row.Target}/{row.User}: {selfGrantError}");
                        entries.Add(new BulkOperationEntry
                        {
                            Target = row.Target,
                            User = row.User,
                            Permission = "Calendar",
                            Status = "FAILED",
                            Message = selfGrantError
                        });
                        continue;
                    }
                }

                var permType = isSet ? row.AccessRight : "Remove";
                pendingRows.Add((row, permType));
            }
            catch (Exception ex)
            {
                audit.LogCalendarPermission(
                    currentUser, ipAddress, $"Bulk{(isSet ? "Set" : "Remove")}Calendar_Error",
                    row.Target, row.User, null, false, ticketNumber,
                    errorDetail: ex.Message);
                errors.Add($"{row.Target}/{row.User}: {ex.Message}");
                entries.Add(new BulkOperationEntry
                {
                    Target = row.Target,
                    User = row.User,
                    Permission = "Calendar",
                    Status = "ERROR",
                    Message = ex.Message
                });
            }
        }

        if (pendingRows.Count > 0)
        {
            await RunPooledBatchAsync((ps, tracker) => Task.Run(() =>
            {
                foreach (var (row, permType) in pendingRows)
                {
                    var result = ExecuteCalendarPermission(ps, tracker, row.Target, row.User, isSet ? row.AccessRight : null, isSet);

                    var action = $"Bulk{(isSet ? "Set" : "Remove")}Calendar";
                    audit.LogCalendarPermission(
                        currentUser, ipAddress, action, row.Target, row.User,
                        isSet ? row.AccessRight : null, result.Success, ticketNumber,
                        errorDetail: result.Success ? null : result.Message);

                    if (result.Success)
                    {
                        successCount++;
                        entries.Add(new BulkOperationEntry
                        {
                            Target = row.Target,
                            User = row.User,
                            Permission = permType,
                            Status = "SUCCESS",
                            Message = result.Message
                        });
                    }
                    else
                    {
                        errors.Add($"{row.Target}/{row.User}: {result.Message}");
                        entries.Add(new BulkOperationEntry
                        {
                            Target = row.Target,
                            User = row.User,
                            Permission = permType,
                            Status = "FAILED",
                            Message = result.Message
                        });
                    }
                }
            }));
        }

        return new BulkOperationResult
        {
            TotalRows = records.Count,
            SuccessCount = successCount,
            FailedCount = errors.Count,
            Errors = errors,
            Entries = entries
        };
    }

    // -------------------------------------------------------------------------
    // Migration Operations
    // -------------------------------------------------------------------------

    public async Task<MigrationEligibilityResult> CheckMigrationEligibilityAsync(string emailAddress, MigrationDirection direction)
    {
        var result = await RunPooledQueryAsync((ps, tracker) =>
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
                var migUser = InvokeOptional(ps, tracker);
                if (migUser.Any())
                {
                    r.Status = MigrationStatus.Ineligible;
                    r.IneligibilityReasons.Add("Migration already in progress");
                }

                ps.AddCommand("Get-Mailbox")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Ignore");
                var cloudMbx = InvokeOptional(ps, tracker);
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
                CheckAdGroupMembership(result);
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
                var sizeResult = await GetOnPremMailboxSizeAsync(emailAddress, "Migration");

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

        var records = new List<MigrationCsvRow>();
        await foreach (var row in csv.GetRecordsAsync<MigrationCsvRow>())
            records.Add(row);
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
            targetDatabases = MigrationTargetDatabaseSelector.Resolve(_moduleConfig, _config);
            if (targetDatabases.Length == 0)
                return PermissionResult.Fail("No on-prem target databases are configured. Check Migration:OnPremTargetDatabases or the Migration module configuration.");
        }

        return await RunAsync((ps, tracker) =>
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
                  .AddParameter("TargetDatabases", targetDatabases!)
                  .AddParameter("CSVData", csvBytes)
                  .AddParameter("NotificationEmails", _adminNotificationEmails)
                  .AddParameter("ErrorAction", "Stop");
            }

            Invoke(ps, tracker);

            // Auto-start if requested
            if (autoStart)
            {
                ps.AddCommand("Start-MigrationBatch")
                  .AddParameter("Identity", batchName)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
            }

            // Auto-complete if requested
            if (autoComplete)
            {
                ps.AddCommand("Set-MigrationBatch")
                  .AddParameter("Identity", batchName)
                  .AddParameter("CompleteAfter", DateTime.Now.AddHours(-1))
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
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
On-Prem Target Databases: {(targetDatabases == null || targetDatabases.Length == 0 ? "N/A" : string.Join(", ", targetDatabases))}
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
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var batches = new List<MigrationBatchInfo>();

            try
            {
                // Get all migration batches
                ps.AddCommand("Get-MigrationBatch")
                  .AddParameter("ErrorAction", "Ignore");

                var batchResults = InvokeOptional(ps, tracker);

                foreach (var batchObj in batchResults)
                {
                    try
                    {
                        var batchName = batchObj.Properties["Identity"]?.Value?.ToString() ?? "Unknown";
                        var status = batchObj.Properties["Status"]?.Value?.ToString() ?? "Unknown";
                        var totalCount = Convert.ToInt32(batchObj.Properties["TotalCount"]?.Value ?? 0);
                        var syncedCount = Convert.ToInt32(batchObj.Properties["SyncedCount"]?.Value ?? batchObj.Properties["SyncedItemCount"]?.Value ?? 0);
                        var finalizedCount = Convert.ToInt32(batchObj.Properties["FinalizedCount"]?.Value ?? batchObj.Properties["FinalizedItemCount"]?.Value ?? 0);
                        var failedCount = Convert.ToInt32(batchObj.Properties["FailedCount"]?.Value ?? batchObj.Properties["FailedItemCount"]?.Value ?? 0);
                        var createdDateTime = batchObj.Properties["CreationDateTime"]?.Value as DateTime? ?? DateTime.MinValue;
                        var startDateTime = batchObj.Properties["StartDateTime"]?.Value as DateTime?;
                        var completedDateTime = batchObj.Properties["CompletionDateTime"]?.Value as DateTime?;
                        var targetEndpoint = batchObj.Properties["TargetEndpoint"]?.Value?.ToString();
                        var autoStart = Convert.ToBoolean(batchObj.Properties["AutoStart"]?.Value ?? false);
                        var autoComplete = Convert.ToBoolean(batchObj.Properties["CompleteAfter"]?.Value != null);

                        // Determine direction based on endpoint (hybrid = ToOnPrem, null/cloud = ToCloud)
                        var direction = targetEndpoint?.Contains("hybrid", StringComparison.OrdinalIgnoreCase) == true
                            ? MigrationDirection.ToOnPrem
                            : MigrationDirection.ToCloud;

                        batches.Add(new MigrationBatchInfo
                        {
                            BatchName = batchName,
                            Status = status,
                            CreatedDateTime = createdDateTime,
                            StartDateTime = startDateTime,
                            CompletedDateTime = completedDateTime,
                            TotalCount = totalCount,
                            SyncedCount = syncedCount,
                            FinalizedCount = finalizedCount,
                            FailedCount = failedCount,
                            TargetEndpoint = targetEndpoint,
                            AutoStart = autoStart,
                            AutoComplete = autoComplete,
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
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var users = new List<MigrationUserInfo>();

            try
            {
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("BatchId", batchName)
                  .AddParameter("ErrorAction", "Ignore");

                var userResults = InvokeOptional(ps, tracker);

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

    // -------------------------------------------------------------------------
    // Migration Batch/User Actions
    // -------------------------------------------------------------------------

    public Task<PermissionResult> CompleteMigrationBatchAsync(string batchName)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Complete-MigrationBatch")
              .AddParameter("Identity", batchName)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration batch '{batchName}' completion initiated.", (string?)null));
    }

    public Task<PermissionResult> CompleteMigrationUserAsync(string emailAddress)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("CompleteAfter", DateTime.UtcNow)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration completion initiated for {emailAddress}.", (string?)null));
    }

    public Task<PermissionResult> ApproveMigrationUserAsync(string emailAddress)
    {
        // Mirrors ApproveMigrationUser.ps1: approve skipped items, set complete, resume move request
        return RunAsync((ps, tracker) =>
        {
            var pastDate = DateTime.Now.AddDays(-1);

            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ApproveSkippedItems", true)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("CompleteAfter", pastDate)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            ps.AddCommand("Set-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ApproveSkippedItems", true)
              .AddParameter("ErrorAction", "Ignore");
            InvokeOptional(ps, tracker);

            ps.AddCommand("Set-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("CompleteAfter", pastDate)
              .AddParameter("ErrorAction", "Ignore");
            InvokeOptional(ps, tracker);

            ps.AddCommand("Set-MoveRequest")
              .AddParameter("Identity", emailAddress)
              .AddParameter("SkippedItemApprovalTime", pastDate)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            ps.AddCommand("Resume-MoveRequest")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            ps.AddCommand("Complete-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Ignore");
            InvokeOptional(ps, tracker);
        }, () => ($"Approved skipped items and initiated completion for {emailAddress}.", (string?)null));
    }

    public Task<PermissionResult> StopMigrationUserAsync(string emailAddress)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Stop-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration stopped for {emailAddress}.", (string?)null));
    }

    public Task<PermissionResult> ResumeMigrationUserAsync(string emailAddress)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Start-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration resumed for {emailAddress}.", (string?)null));
    }

    public Task<PermissionResult> RemoveMigrationUserAsync(string emailAddress)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Remove-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration user {emailAddress} removed.", (string?)null));
    }

    public Task<PermissionResult> RemoveMigrationBatchAsync(string batchName)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Remove-MigrationBatch")
              .AddParameter("Identity", batchName)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration batch '{batchName}' removed.", (string?)null));
    }

    public async Task<string?> GetMigrationUserReportAsync(string emailAddress)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            try
            {
                ps.AddCommand("Get-MigrationUserStatistics")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("IncludeReport", true)
                  .AddParameter("ErrorAction", "Stop");

                var results = Invoke(ps, tracker);
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
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            try
            {
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("ResultSize", "Unlimited")
                  .AddParameter("ErrorAction", "Stop");

                var results = Invoke(ps, tracker);

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

    // -------------------------------------------------------------------------
    // Lookup Operations
    // -------------------------------------------------------------------------

    public async Task<DelegationReportResult> GetMailboxDelegationAsync(string emailAddress)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var result = new DelegationReportResult { EmailAddress = emailAddress };

            try
            {
                // Full Access
                ps.AddCommand("Get-MailboxPermission")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var perms = Invoke(ps, tracker);
                foreach (var perm in perms)
                {
                    var user = perm.Properties["User"]?.Value?.ToString();
                    var accessRights = perm.Properties["AccessRights"]?.Value?.ToString();
                    var isInherited = perm.Properties["IsInherited"]?.Value as bool? ?? true;
                    if (user != null && !isInherited && accessRights?.Contains("FullAccess") == true
                        && !user.Equals("NT AUTHORITY\\SELF", StringComparison.OrdinalIgnoreCase))
                    {
                        result.FullAccess.Add(new DelegationEntry { User = user });
                    }
                }

                // Send As
                ps.AddCommand("Get-RecipientPermission")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var recipPerms = Invoke(ps, tracker);
                foreach (var perm in recipPerms)
                {
                    var trustee = perm.Properties["Trustee"]?.Value?.ToString();
                    if (trustee != null && !trustee.Equals("NT AUTHORITY\\SELF", StringComparison.OrdinalIgnoreCase))
                    {
                        result.SendAs.Add(new DelegationEntry { User = trustee });
                    }
                }

                // Calendar
                try
                {
                    var calendarPath = GetCalendarFolderName(ps, emailAddress);
                    ps.AddCommand("Get-MailboxFolderPermission")
                      .AddParameter("Identity", calendarPath)
                      .AddParameter("ErrorAction", "Stop");
                    var calPerms = Invoke(ps, tracker);
                    foreach (var perm in calPerms)
                    {
                        var user = perm.Properties["User"]?.Value?.ToString();
                        var rights = perm.Properties["AccessRights"]?.Value?.ToString();
                        if (user != null && rights != null
                            && !user.Equals("Default", StringComparison.OrdinalIgnoreCase)
                            && !user.Equals("Anonymous", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Calendar.Add(new CalendarDelegationEntry { User = user, AccessRights = rights });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not retrieve calendar permissions for {Email}", emailAddress);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _logger.LogError(ex, "Error getting delegation report for {Email}", emailAddress);
            }

            return result;
        });
    }

    public async Task<MessageTraceResponse> GetMessageTraceAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string? subjectFilter, string? messageId = null)
    {
        var responses = await Task.WhenAll(
            RunMessageTraceBackendAsync(() => GetCloudMessageTraceAsync(sender, recipient, startDate, endDate, subjectFilter, messageId), "Exchange Online"),
            RunMessageTraceBackendAsync(() => GetOnPremMessageTraceAsync(sender, recipient, startDate, endDate, subjectFilter, messageId), "On-prem"));

        var merged = new MessageTraceResponse();
        foreach (var partial in responses)
        {
            merged.Results.AddRange(partial.Results);
            if (partial.Truncated)
                merged.Truncated = true;
            if (!string.IsNullOrWhiteSpace(partial.Error))
                merged.Warnings.Add(partial.Error);
            merged.Warnings.AddRange(partial.Warnings);
        }

        merged.Results = merged.Results
            .OrderByDescending(r => r.Received)
            .Take(MessageTraceResponse.MaxResults)
            .ToList();
        merged.TotalAvailable = merged.Results.Count;
        if (merged.Results.Count >= MessageTraceResponse.MaxResults)
            merged.Truncated = true;

        if (merged.Results.Count == 0 && merged.Warnings.Count > 0)
            merged.Error = string.Join(" | ", merged.Warnings.Distinct(StringComparer.OrdinalIgnoreCase));

        return merged;
    }

    private async Task<MessageTraceResponse> RunMessageTraceBackendAsync(Func<Task<MessageTraceResponse>> query, string backend)
    {
        try
        {
            return await query();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Backend} message trace backend failed before returning a response", backend);
            return new MessageTraceResponse { Error = $"{backend} trace failed: {ex.Message}" };
        }
    }

    private async Task<MessageTraceResponse> GetCloudMessageTraceAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string? subjectFilter, string? messageId)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var response = new MessageTraceResponse();

            try
            {
                var allResults = new List<MessageTraceResult>();
                var page = 1;
                const int pageSize = 200;
                const int maxPages = 10;
                var normalizedMessageId = NormalizeMessageId(messageId);

                while (allResults.Count < MessageTraceResponse.MaxResults && page <= maxPages)
                {
                    ps.AddCommand("Get-MessageTrace")
                      .AddParameter("StartDate", startDate)
                      .AddParameter("EndDate", endDate)
                      .AddParameter("PageSize", pageSize)
                      .AddParameter("Page", page)
                      .AddParameter("ErrorAction", "Stop");

                    if (!string.IsNullOrWhiteSpace(sender))
                        ps.AddParameter("SenderAddress", sender);
                    if (!string.IsNullOrWhiteSpace(recipient))
                        ps.AddParameter("RecipientAddress", recipient);
                    if (!string.IsNullOrWhiteSpace(messageId))
                        ps.AddParameter("MessageId", messageId.Trim());

                    var results = Invoke(ps, tracker);
                    if (!results.Any())
                        break;

                    foreach (var msg in results)
                    {
                        var subject = msg.Properties["Subject"]?.Value?.ToString() ?? "";
                        var resultMessageId = msg.Properties["MessageId"]?.Value?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(subjectFilter) && !subject.Contains(subjectFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!MessageIdMatches(resultMessageId, normalizedMessageId))
                            continue;

                        allResults.Add(new MessageTraceResult
                        {
                            Received = GetPropertyDate(msg, "Received"),
                            SenderAddress = GetPropertyString(msg, "SenderAddress"),
                            RecipientAddress = GetPropertyString(msg, "RecipientAddress"),
                            Subject = subject,
                            Status = GetPropertyString(msg, "Status"),
                            MessageId = resultMessageId,
                            Size = GetPropertyLong(msg, "Size"),
                            FromIP = GetPropertyString(msg, "FromIP"),
                            ToIP = GetPropertyString(msg, "ToIP"),
                            MessageTraceId = GetPropertyString(msg, "MessageTraceId", "MessageTraceID"),
                            Backend = "ExchangeOnline"
                        });

                        if (allResults.Count >= MessageTraceResponse.MaxResults)
                        {
                            response.Truncated = true;
                            break;
                        }
                    }

                    if (results.Count < pageSize)
                        break;

                    page++;
                }

                if (page > maxPages && allResults.Count < MessageTraceResponse.MaxResults)
                    response.Truncated = true;

                response.Results = allResults;
                response.TotalAvailable = allResults.Count;
            }
            catch (Exception ex)
            {
                response.Error = $"Exchange Online trace failed: {ex.Message}";
                _logger.LogError(ex, "Error running Exchange Online message trace");
            }

            return response;
        });
    }

    private async Task<MessageTraceResponse> GetOnPremMessageTraceAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string? subjectFilter, string? messageId)
    {
        var response = new MessageTraceResponse();
        if (string.IsNullOrWhiteSpace(_onPremServerUri))
        {
            response.Warnings.Add("On-prem message tracking skipped: OnPremExchange:ServerUri is not configured.");
            return response;
        }

        var creds = await _moduleCredentials.GetCredentialsAsync("MessageTrace", "on-prem message tracking");
        if (creds is null)
        {
            response.Error = "On-prem message tracking failed: Message Analysis DelineaSecretId is not configured or credentials are unavailable.";
            return response;
        }

        return await ThrottledAsync(() => Task.Run(() =>
        {
            var result = new MessageTraceResponse();
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                var normalizedMessageId = NormalizeMessageId(messageId);
                var messageIdValues = MessageIdFilterValues(messageId);
                if (messageIdValues.Length == 0)
                    messageIdValues = [string.Empty];

                var servers = GetOnPremTransportServers(ps, session);
                if (servers.Length == 0)
                    servers = [string.Empty];

                var tracking = new List<PSObject>();
                var queryFailures = new List<string>();

                foreach (var server in servers)
                {
                    foreach (var messageIdValue in messageIdValues)
                    {
                        try
                        {
                            tracking.AddRange(InvokeOnPremMessageTrackingQuery(
                                ps,
                                session,
                                string.IsNullOrWhiteSpace(server) ? null : server,
                                startDate,
                                endDate,
                                sender,
                                recipient,
                                subjectFilter,
                                string.IsNullOrWhiteSpace(messageIdValue) ? null : messageIdValue));
                        }
                        catch (Exception ex)
                        {
                            ps.Commands.Clear();
                            ps.Streams.Error.Clear();
                            var targetServer = string.IsNullOrWhiteSpace(server) ? "default server" : server;
                            queryFailures.Add($"{targetServer}: {ex.Message}");
                        }
                    }
                }

                var mapped = new List<MessageTraceResult>();
                foreach (var item in tracking)
                {
                    var itemMessageId = GetPropertyString(item, "MessageId");
                    if (!MessageIdMatches(itemMessageId, normalizedMessageId))
                        continue;

                    var recipients = GetRecipients(item.Properties["Recipients"]?.Value).DefaultIfEmpty(string.Empty);
                    foreach (var itemRecipient in recipients)
                    {
                        mapped.Add(new MessageTraceResult
                        {
                            Received = GetPropertyDate(item, "Timestamp"),
                            SenderAddress = GetPropertyString(item, "Sender"),
                            RecipientAddress = itemRecipient,
                            Subject = GetPropertyString(item, "MessageSubject"),
                            Status = GetPropertyString(item, "EventId"),
                            MessageId = itemMessageId,
                            Size = GetPropertyLong(item, "TotalBytes"),
                            FromIP = GetPropertyString(item, "ClientIp"),
                            ToIP = GetPropertyString(item, "ServerIp"),
                            MessageTraceId = GetPropertyString(item, "InternalMessageId"),
                            Backend = "OnPrem",
                            EventId = GetPropertyString(item, "EventId"),
                            Server = GetPropertyString(item, "ServerHostname")
                        });
                    }
                }

                result.Results = mapped
                    .GroupBy(r => $"{r.Server}|{r.MessageTraceId}|{r.MessageId}|{r.RecipientAddress}|{r.Received:O}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderByDescending(r => r.Received)
                    .Take(MessageTraceResponse.MaxResults)
                    .ToList();
                result.TotalAvailable = result.Results.Count;
                if (tracking.Count >= MessageTraceResponse.MaxResults || result.Results.Count >= MessageTraceResponse.MaxResults)
                    result.Truncated = true;

                if (queryFailures.Count > 0)
                {
                    var sample = string.Join("; ", queryFailures.Take(3));
                    result.Warnings.Add($"Some on-prem message tracking queries failed ({queryFailures.Count}): {sample}");
                }
            }
            catch (Exception ex)
            {
                result.Error = $"On-prem message tracking failed: {ex.Message}";
                _logger.LogError(ex, "Error running on-prem message tracking log search");
            }
            finally
            {
                RemoveOnPremSession(ps);
            }

            return result;
        }), _onPremThrottle);
    }

    private static string[] GetOnPremTransportServers(PowerShell ps, object? session)
    {
        if (session is null)
            return Array.Empty<string>();

        try
        {
            var script = ScriptBlock.Create("Get-TransportService -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name");
            ps.AddCommand("Invoke-Command")
              .AddParameter("Session", session)
              .AddParameter("ScriptBlock", script);

            return InvokeOptional(ps)
                .Select(r => r.BaseObject?.ToString() ?? r.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
            return Array.Empty<string>();
        }
    }

    private static Collection<PSObject> InvokeOnPremMessageTrackingQuery(
        PowerShell ps,
        object? session,
        string? server,
        DateTime startDate,
        DateTime endDate,
        string? sender,
        string? recipient,
        string? subject,
        string? messageId)
    {
        if (session is null)
            return new Collection<PSObject>();

        var command = new StringBuilder();
        command.Append("Get-MessageTrackingLog");
        command.Append(" -Start ").Append(PowerShellLiteral(startDate));
        command.Append(" -End ").Append(PowerShellLiteral(endDate));
        command.Append(" -ResultSize ").Append(MessageTraceResponse.MaxResults.ToString(CultureInfo.InvariantCulture));
        command.Append(" -ErrorAction SilentlyContinue");
        AddMessageTrackingParameter(command, "Server", server);
        AddMessageTrackingParameter(command, "Sender", sender);
        AddMessageTrackingParameter(command, "Recipients", recipient);
        AddMessageTrackingParameter(command, "MessageSubject", subject);
        AddMessageTrackingParameter(command, "MessageId", messageId);
        command.Append(" | Select-Object Timestamp,Sender,Recipients,MessageSubject,EventId,MessageId,TotalBytes,ClientIp,ServerIp,ServerHostname,InternalMessageId");

        var script = ScriptBlock.Create(command.ToString());
        ps.AddCommand("Invoke-Command")
          .AddParameter("Session", session)
          .AddParameter("ScriptBlock", script);

        return InvokeOptional(ps);
    }

    private static void AddMessageTrackingParameter(StringBuilder command, string parameterName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        command.Append(" -").Append(parameterName).Append(' ').Append(PowerShellLiteral(value.Trim()));
    }

    private static string PowerShellLiteral(DateTime value) =>
        PowerShellLiteral(value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string GetPropertyString(PSObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = obj.Properties[name]?.Value;
            if (value != null)
                return value.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static DateTime GetPropertyDate(PSObject obj, string name) =>
        obj.Properties[name]?.Value is DateTime dt ? dt : DateTime.MinValue;

    private static long GetPropertyLong(PSObject obj, string name)
    {
        var value = obj.Properties[name]?.Value;
        if (value is long l) return l;
        if (value is int i) return i;
        return long.TryParse(value?.ToString(), out var parsed) ? parsed : 0;
    }

    private static IEnumerable<string> GetRecipients(object? value)
    {
        if (value == null)
            yield break;
        if (value is string s)
        {
            if (!string.IsNullOrWhiteSpace(s))
                yield return s;
            yield break;
        }
        if (value is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
            {
                var text = item?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
            }
        }
    }

    private static bool MessageIdMatches(string value, string? normalizedFilter)
    {
        if (string.IsNullOrWhiteSpace(normalizedFilter))
            return true;
        return string.Equals(NormalizeMessageId(value), normalizedFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] MessageIdFilterValues(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return Array.Empty<string>();

        var trimmed = messageId.Trim();
        var normalized = NormalizeMessageId(trimmed);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return new[] { trimmed, normalized, $"<{normalized}>" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return null;
        return messageId.Trim().Trim('<', '>');
    }
    public async Task<HistoricalSearchResponse> StartHistoricalSearchAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string notifyAddress, string reportTitle)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var response = new HistoricalSearchResponse();

            try
            {
                ps.AddCommand("Start-HistoricalSearch")
                  .AddParameter("StartDate", startDate)
                  .AddParameter("EndDate", endDate)
                  .AddParameter("ReportTitle", reportTitle)
                  .AddParameter("ReportType", "MessageTrace")
                  .AddParameter("NotifyAddress", new[] { notifyAddress })
                  .AddParameter("ErrorAction", "Stop");

                if (!string.IsNullOrWhiteSpace(sender))
                    ps.AddParameter("SenderAddress", sender);
                if (!string.IsNullOrWhiteSpace(recipient))
                    ps.AddParameter("RecipientAddress", recipient);

                var results = Invoke(ps, tracker);
                var result = results.FirstOrDefault();
                response.JobId = result?.Properties["JobId"]?.Value?.ToString();
                response.Success = true;
            }
            catch (Exception ex)
            {
                response.Error = ex.Message;
                _logger.LogError(ex, "Error starting historical search");
            }

            return response;
        });
    }

    public async Task<RecipientInfoResult> GetRecipientInfoAsync(string emailAddress)
    {
        var result = await RunPooledQueryAsync((ps, tracker) =>
        {
            var r = new RecipientInfoResult { EmailAddress = emailAddress };

            try
            {
                ps.AddCommand("Get-Recipient")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var recipients = Invoke(ps, tracker);
                var recip = recipients.FirstOrDefault();

                if (recip == null)
                {
                    r.Error = $"Recipient '{emailAddress}' not found.";
                    return r;
                }

                r.DisplayName = recip.Properties["DisplayName"]?.Value?.ToString();
                r.RecipientType = recip.Properties["RecipientTypeDetails"]?.Value?.ToString();
                r.WhenCreated = recip.Properties["WhenCreated"]?.Value as DateTime?;

                var emailAddresses = recip.Properties["EmailAddresses"]?.Value;
                if (emailAddresses is System.Collections.IEnumerable addrs)
                {
                    foreach (var addr in addrs)
                    {
                        var s = addr?.ToString();
                        if (s != null) r.EmailAddresses.Add(s);
                    }
                }

                ps.AddCommand("Get-Mailbox")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Ignore");
                var mbxResults = InvokeOptional(ps, tracker);
                var mbx = mbxResults.FirstOrDefault();

                if (mbx != null)
                {
                    r.ForwardingAddress = mbx.Properties["ForwardingSmtpAddress"]?.Value?.ToString()
                        ?? mbx.Properties["ForwardingAddress"]?.Value?.ToString();

                    try
                    {
                        ps.AddCommand("Get-MailboxStatistics")
                          .AddParameter("Identity", emailAddress)
                          .AddParameter("ErrorAction", "Stop");
                        var stats = Invoke(ps, tracker);
                        var stat = stats.FirstOrDefault();
                        if (stat != null)
                        {
                            r.ItemCount = ParseLong(stat.Properties["ItemCount"]?.Value);
                            r.DeletedItemCount = ParseLong(stat.Properties["DeletedItemCount"]?.Value);
                            r.LastLogonTime = stat.Properties["LastLogonTime"]?.Value as DateTime?;
                            r.MailboxSizeGB = ParseSizeToGB(stat.Properties["TotalItemSize"]?.Value?.ToString());
                            r.DeletedItemSizeGB = ParseSizeToGB(stat.Properties["TotalDeletedItemSize"]?.Value?.ToString());
                        }
                    }
                    catch (Exception ex)
                    {
                        r.Warnings.Add($"Could not retrieve mailbox statistics: {ex.Message}");
                    }

                    try
                    {
                        ps.AddCommand("Get-MailboxStatistics")
                          .AddParameter("Identity", emailAddress)
                          .AddParameter("Archive", true)
                          .AddParameter("ErrorAction", "Stop");
                        var archiveStats = Invoke(ps, tracker);
                        var archiveStat = archiveStats.FirstOrDefault();
                        if (archiveStat != null)
                        {
                            r.ArchiveEnabled = true;
                            r.ArchiveItemCount = ParseLong(archiveStat.Properties["ItemCount"]?.Value);
                            r.ArchiveDeletedItemCount = ParseLong(archiveStat.Properties["DeletedItemCount"]?.Value);
                            r.ArchiveSizeGB = ParseSizeToGB(archiveStat.Properties["TotalItemSize"]?.Value?.ToString());
                            r.ArchiveDeletedItemSizeGB = ParseSizeToGB(archiveStat.Properties["TotalDeletedItemSize"]?.Value?.ToString());
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                _logger.LogError(ex, "Error getting recipient info for {Email}", emailAddress);
            }

            return r;
        });

        if (result.Error == null)
        {
            try
            {
                result.MailboxLocation = await GetMailboxLocationAsync(emailAddress, "RecipientLookup") switch
                {
                    "OnPrem" => "On-Premises",
                    "Cloud" => "Cloud",
                    _ => "Unknown"
                };
            }
            catch (Exception ex)
            {
                result.MailboxLocation = "Unknown";
                result.Warnings.Add($"Could not determine mailbox location: {ex.Message}");
            }
        }

        if (result.Error == null && result.MailboxLocation == "On-Premises")
        {
            try
            {
                var onPremSize = await GetOnPremMailboxSizeAsync(emailAddress, "RecipientLookup");
                if (onPremSize != null)
                {
                    result.MailboxSizeGB = onPremSize.Value.mailboxSizeGB;
                    result.ArchiveSizeGB = onPremSize.Value.archiveSizeGB;
                }
                else
                {
                    result.Warnings.Add("On-premises mailbox size unavailable (connection not configured).");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not retrieve on-premises mailbox size: {ex.Message}");
            }
        }

        return result;
    }

    public async Task<OutOfOfficeResult> GetOutOfOfficeAsync(string emailAddress)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var result = new OutOfOfficeResult { EmailAddress = emailAddress, State = "Unknown" };

            try
            {
                ps.AddCommand("Get-MailboxAutoReplyConfiguration")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var results = Invoke(ps, tracker);
                var config = results.FirstOrDefault();

                if (config == null)
                {
                    result.Error = $"Could not retrieve auto-reply configuration for '{emailAddress}'.";
                    return result;
                }

                result.State = config.Properties["AutoReplyState"]?.Value?.ToString() ?? "Disabled";
                result.InternalMessage = config.Properties["InternalMessage"]?.Value?.ToString();
                result.ExternalMessage = config.Properties["ExternalMessage"]?.Value?.ToString();
                result.StartTime = config.Properties["StartTime"]?.Value as DateTime?;
                result.EndTime = config.Properties["EndTime"]?.Value as DateTime?;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _logger.LogError(ex, "Error getting OOF status for {Email}", emailAddress);
            }

            return result;
        });
    }

    public Task<PermissionResult> SetOutOfOfficeAsync(string emailAddress, string state, string? internalMessage, string? externalMessage, DateTime? startTime, DateTime? endTime)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Set-MailboxAutoReplyConfiguration")
              .AddParameter("Identity", emailAddress)
              .AddParameter("AutoReplyState", state)
              .AddParameter("ErrorAction", "Stop");

            if (state != "Disabled")
            {
                if (!string.IsNullOrWhiteSpace(internalMessage))
                    ps.AddParameter("InternalMessage", internalMessage);
                if (!string.IsNullOrWhiteSpace(externalMessage))
                    ps.AddParameter("ExternalMessage", externalMessage);
            }

            if (state == "Scheduled")
            {
                if (startTime.HasValue)
                    ps.AddParameter("StartTime", startTime.Value);
                if (endTime.HasValue)
                    ps.AddParameter("EndTime", endTime.Value);
            }

            Invoke(ps, tracker);
        }, () => (state == "Disabled"
            ? $"Auto-reply disabled for {emailAddress}."
            : $"Auto-reply set to {state} for {emailAddress}.", (string?)null));
    }

    // -------------------------------------------------------------------------
    // On-Prem Exchange Operations
    // -------------------------------------------------------------------------

    public async Task<(double mailboxSizeGB, double archiveSizeGB)?> GetOnPremMailboxSizeAsync(string emailAddress, string moduleId)
    {
        if (string.IsNullOrEmpty(_onPremServerUri))
            throw new InvalidOperationException("OnPremExchange:ServerUri is not configured");

        _extLog.Write(LogEventLevel.Debug, "Retrieving on-prem Exchange credentials for mailbox size check", "OnPremExchange", () => $"Module={moduleId}; Target={emailAddress}");
        var creds = await _moduleCredentials.GetCredentialsAsync(moduleId, "on-prem mailbox size check");
        if (creds is null)
        {
            _extLog.Write(LogEventLevel.Error, "On-prem Exchange credentials unavailable for mailbox size check", "OnPremExchange", () => $"Module={moduleId}; Target={emailAddress}");
            throw new InvalidOperationException($"Failed to retrieve on-prem credentials from Delinea — configure DelineaSecretId for the {moduleId} module");
        }

        return await ThrottledAsync(() => Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);

                var mbxScript = ScriptBlock.Create("param($Identity) Get-MailboxStatistics -Identity $Identity -ErrorAction Stop | Select-Object TotalItemSize");
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", ps.Runspace.SessionStateProxy.GetVariable("onpremSession"))
                  .AddParameter("ScriptBlock", mbxScript)
                  .AddParameter("ArgumentList", new object[] { emailAddress });
                var mbxStats = Invoke(ps);
                var totalItemSize = mbxStats.FirstOrDefault()?.Properties["TotalItemSize"]?.Value?.ToString();
                var mailboxGB = ParseExchangeSize(totalItemSize);

                _logger.LogInformation("On-prem mailbox size for {Email}: {Size} GB", emailAddress, mailboxGB);
                _extLog.Write(LogEventLevel.Information, "Retrieved on-prem mailbox size", "OnPremExchange", () => $"Target={emailAddress}; MailboxGB={mailboxGB:F2}");

                double archiveGB = 0;
                try
                {
                    var archiveScript = ScriptBlock.Create("param($Identity) Get-MailboxStatistics -Identity $Identity -Archive -ErrorAction Stop | Select-Object TotalItemSize");
                    ps.AddCommand("Invoke-Command")
                      .AddParameter("Session", ps.Runspace.SessionStateProxy.GetVariable("onpremSession"))
                      .AddParameter("ScriptBlock", archiveScript)
                      .AddParameter("ArgumentList", new object[] { emailAddress });
                    var archiveStats = Invoke(ps);
                    var archiveSize = archiveStats.FirstOrDefault()?.Properties["TotalItemSize"]?.Value?.ToString();
                    archiveGB = ParseExchangeSize(archiveSize);
                    _logger.LogInformation("On-prem archive size for {Email}: {Size} GB", emailAddress, archiveGB);
                    _extLog.Write(LogEventLevel.Information, "Retrieved on-prem archive size", "OnPremExchange", () => $"Target={emailAddress}; ArchiveGB={archiveGB:F2}");
                }
                catch
                {
                    _logger.LogInformation("No archive mailbox found for {Email}", emailAddress);
                    _extLog.Write(LogEventLevel.Debug, "No on-prem archive mailbox found", "OnPremExchange", () => $"Target={emailAddress}");
                }

                return ((double mailboxSizeGB, double archiveSizeGB)?)(mailboxGB, archiveGB);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get on-prem mailbox size for {Email}", emailAddress);
                _extLog.Write(LogEventLevel.Error, "Failed to get on-prem mailbox size", "OnPremExchange", () => $"Target={emailAddress}; ErrorType={ex.GetType().Name}");
                return null;
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                    if (session != null)
                    {
                        ps.AddCommand("Remove-PSSession").AddParameter("Session", session);
                        ps.Invoke();
                    }
                }
                catch { }
            }
        }), _onPremThrottle);
    }

    // -------------------------------------------------------------------------
    // On-Prem Permission Operations
    // -------------------------------------------------------------------------

    public async Task<string> GetMailboxLocationAsync(string identity, string moduleId)
    {
        if (await HasCloudMailboxAsync(identity))
            return "Cloud";

        return await GetOnPremMailboxLocationAsync(identity, moduleId) ?? "Unknown";
    }

    private async Task<string?> GetOnPremMailboxLocationAsync(string identity, string moduleId)
    {
        if (string.IsNullOrWhiteSpace(_onPremServerUri))
        {
            _logger.LogWarning("OnPremExchange:ServerUri is not configured; cannot determine on-prem mailbox location for {Identity}", identity);
            return null;
        }

        var creds = await _moduleCredentials.GetCredentialsAsync(moduleId, "on-prem mailbox location check");
        if (creds is null)
        {
            _logger.LogError("Cannot determine on-prem mailbox location for {Identity}: failed to retrieve {Module} credentials from Delinea", identity, moduleId);
            return null;
        }

        return await ThrottledAsync(() => Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");

                if (OnPremRecipientExists(ps, session, "Get-Mailbox", identity))
                    return "OnPrem";

                if (OnPremRecipientExists(ps, session, "Get-RemoteMailbox", identity))
                    return "Cloud";

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to determine on-prem mailbox location for {Identity}", identity);
                return null;
            }
            finally
            {
                RemoveOnPremSession(ps);
            }
        }), _onPremThrottle);
    }

    public async Task<PermissionResult> AddMailboxPermissionsOnPremAsync(string targetMailbox, string user, bool fullAccess, bool sendAs)
    {
        var creds = await _moduleCredentials.GetCredentialsAsync("MailboxPermissions", "on-prem mailbox permission add");
        if (creds is null)
            return PermissionResult.Fail("Cannot connect to on-prem Exchange: credentials unavailable.");

        return await ThrottledAsync(async () => await Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");

                var successes = new List<string>();
                var failures = new List<string>();

                if (fullAccess)
                {
                    try
                    {
                        var script = ScriptBlock.Create("param($Identity, $User) Add-MailboxPermission -Identity $Identity -User $User -AccessRights FullAccess -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { targetMailbox, user });
                        Invoke(ps);
                        successes.Add("FullAccess");
                    }
                    catch (Exception ex)
                    {
                        ps.Commands.Clear();
                        ps.Streams.Error.Clear();
                        failures.Add($"FullAccess: {ex.Message}");
                    }
                }

                if (sendAs)
                {
                    try
                    {
                        var script = ScriptBlock.Create("param($Identity, $Trustee) Add-ADPermission -Identity $Identity -User $Trustee -ExtendedRights 'Send As' -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { targetMailbox, user });
                        Invoke(ps);
                        successes.Add("SendAs");
                    }
                    catch (Exception ex)
                    {
                        ps.Commands.Clear();
                        ps.Streams.Error.Clear();
                        failures.Add($"SendAs: {ex.Message}");
                    }
                }

                if (failures.Count > 0 && successes.Count > 0)
                    return new PermissionResult { Success = false, Message = $"Partial: granted {string.Join(", ", successes)} on {targetMailbox} (on-premises). Failed: {string.Join("; ", failures)}", Detail = string.Join(", ", successes) };
                if (failures.Count > 0)
                    return PermissionResult.Fail($"Failed on {targetMailbox} (on-premises): {string.Join("; ", failures)}");
                return new PermissionResult { Success = true, Message = $"{user} has been granted {string.Join(" and ", successes)} on {targetMailbox} (on-premises)." };
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    var s = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                    if (s != null) { ps.AddCommand("Remove-PSSession").AddParameter("Session", s); ps.Invoke(); }
                }
                catch { }
            }
        }), _onPremThrottle);
    }

    public async Task<PermissionResult> RemoveMailboxPermissionsOnPremAsync(string targetMailbox, string user, bool fullAccess, bool sendAs)
    {
        var creds = await _moduleCredentials.GetCredentialsAsync("MailboxPermissions", "on-prem mailbox permission remove");
        if (creds is null)
            return PermissionResult.Fail("Cannot connect to on-prem Exchange: credentials unavailable.");

        return await ThrottledAsync(async () => await Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");

                var successes = new List<string>();
                var failures = new List<string>();

                if (fullAccess)
                {
                    try
                    {
                        var script = ScriptBlock.Create("param($Identity, $User) Remove-MailboxPermission -Identity $Identity -User $User -AccessRights FullAccess -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { targetMailbox, user });
                        Invoke(ps);
                        successes.Add("FullAccess");
                    }
                    catch (Exception ex)
                    {
                        ps.Commands.Clear();
                        ps.Streams.Error.Clear();
                        failures.Add($"FullAccess: {ex.Message}");
                    }
                }

                if (sendAs)
                {
                    try
                    {
                        var script = ScriptBlock.Create("param($Identity, $Trustee) Remove-ADPermission -Identity $Identity -User $Trustee -ExtendedRights 'Send As' -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { targetMailbox, user });
                        Invoke(ps);
                        successes.Add("SendAs");
                    }
                    catch (Exception ex)
                    {
                        ps.Commands.Clear();
                        ps.Streams.Error.Clear();
                        failures.Add($"SendAs: {ex.Message}");
                    }
                }

                if (failures.Count > 0 && successes.Count > 0)
                    return new PermissionResult { Success = false, Message = $"Partial: removed {string.Join(", ", successes)} on {targetMailbox} (on-premises). Failed: {string.Join("; ", failures)}", Detail = string.Join(", ", successes) };
                if (failures.Count > 0)
                    return PermissionResult.Fail($"Failed on {targetMailbox} (on-premises): {string.Join("; ", failures)}");
                return new PermissionResult { Success = true, Message = $"{string.Join(" and ", successes)} removed for {user} on {targetMailbox} (on-premises)." };
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    var s = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                    if (s != null) { ps.AddCommand("Remove-PSSession").AddParameter("Session", s); ps.Invoke(); }
                }
                catch { }
            }
        }), _onPremThrottle);
    }

    public async Task<PermissionResult> SetCalendarPermissionOnPremAsync(string targetMailbox, string user, string accessRight)
    {
        var creds = await _moduleCredentials.GetCredentialsAsync("CalendarPermissions", "on-prem calendar permission set");
        if (creds is null)
            return PermissionResult.Fail("Cannot connect to on-prem Exchange: credentials unavailable.");

        return await ThrottledAsync(async () => await Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");

                var script = ScriptBlock.Create(@"
                    param($Identity, $User, $AccessRights)
                    try {
                        Set-MailboxFolderPermission -Identity ""$Identity`:\Calendar"" -User $User -AccessRights $AccessRights -ErrorAction Stop
                    } catch {
                        Add-MailboxFolderPermission -Identity ""$Identity`:\Calendar"" -User $User -AccessRights $AccessRights -ErrorAction Stop
                    }");
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", session)
                  .AddParameter("ScriptBlock", script)
                  .AddParameter("ArgumentList", new object[] { targetMailbox, user, accessRight });
                Invoke(ps);

                return new PermissionResult { Success = true, Message = $"{user} granted {accessRight} on {targetMailbox} calendar (on-premises)." };
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    var s = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                    if (s != null) { ps.AddCommand("Remove-PSSession").AddParameter("Session", s); ps.Invoke(); }
                }
                catch { }
            }
        }), _onPremThrottle);
    }

    public async Task<PermissionResult> RemoveCalendarPermissionOnPremAsync(string targetMailbox, string user)
    {
        var creds = await _moduleCredentials.GetCredentialsAsync("CalendarPermissions", "on-prem calendar permission remove");
        if (creds is null)
            return PermissionResult.Fail("Cannot connect to on-prem Exchange: credentials unavailable.");

        return await ThrottledAsync(async () => await Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");

                var script = ScriptBlock.Create(@"
                    param($Identity, $User)
                    Remove-MailboxFolderPermission -Identity ""$Identity`:\Calendar"" -User $User -Confirm:$false -ErrorAction Stop");
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", session)
                  .AddParameter("ScriptBlock", script)
                  .AddParameter("ArgumentList", new object[] { targetMailbox, user });
                Invoke(ps);

                return new PermissionResult { Success = true, Message = $"Calendar permissions removed for {user} on {targetMailbox} (on-premises)." };
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    var s = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                    if (s != null) { ps.AddCommand("Remove-PSSession").AddParameter("Session", s); ps.Invoke(); }
                }
                catch { }
            }
        }), _onPremThrottle);
    }


    // -------------------------------------------------------------------------

    public async Task<string?> ResolveToObjectIdAsync(string identity)
    {
        try
        {
            return await RunPooledQueryAsync((ps, tracker) =>
            {
                ps.AddCommand("Get-Recipient")
                  .AddParameter("Identity", identity)
                  .AddParameter("ErrorAction", "Stop");

                var results = Invoke(ps, tracker);
                var recipient = results.FirstOrDefault();
                return recipient?.Properties["ExternalDirectoryObjectId"]?.Value?.ToString();
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve identity for {Identity}", identity);
            return null;
        }
    }

    private async Task RunPooledBatchAsync(Func<PowerShell, ConnectionErrorTracker, Task> batchOperation)
    {
        var pooled = await _exoPool.BorrowAsync();
        bool discard = false;
        try
        {
            var tracker = new ConnectionErrorTracker();
            await Task.Run(async () =>
            {
                await batchOperation(pooled.PowerShell, tracker);
            });

            discard = tracker.HasConnectionError;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            discard = true;
            throw;
        }
        finally
        {
            if (discard)
                _exoPool.Discard(pooled);
            else
                _exoPool.Return(pooled);
        }
    }

    private PermissionResult ExecuteMailboxPermission(PowerShell ps, ConnectionErrorTracker tracker, string targetMailbox, string user, bool fullAccess, bool sendAs, bool autoMapping, bool isAdd)
    {
        try
        {
            ValidateMailbox(ps, targetMailbox);
            ValidateRecipient(ps, user);

            if (isAdd)
            {
                if (fullAccess)
                {
                    ps.AddCommand("Add-MailboxPermission")
                      .AddParameter("Identity", targetMailbox)
                      .AddParameter("User", user)
                      .AddParameter("AccessRights", "FullAccess")
                      .AddParameter("AutoMapping", autoMapping)
                      .AddParameter("Confirm", false);
                    Invoke(ps, tracker);
                }
                if (sendAs)
                {
                    ps.AddCommand("Add-RecipientPermission")
                      .AddParameter("Identity", targetMailbox)
                      .AddParameter("Trustee", user)
                      .AddParameter("AccessRights", "SendAs")
                      .AddParameter("Confirm", false);
                    Invoke(ps, tracker);
                }
            }
            else
            {
                if (fullAccess)
                {
                    ps.AddCommand("Remove-MailboxPermission")
                      .AddParameter("Identity", targetMailbox)
                      .AddParameter("User", user)
                      .AddParameter("AccessRights", "FullAccess")
                      .AddParameter("Confirm", false);
                    Invoke(ps, tracker);
                }
                if (sendAs)
                {
                    ps.AddCommand("Remove-RecipientPermission")
                      .AddParameter("Identity", targetMailbox)
                      .AddParameter("Trustee", user)
                      .AddParameter("AccessRights", "SendAs")
                      .AddParameter("Confirm", false);
                    Invoke(ps, tracker);
                }
            }

            return PermissionResult.Ok();
        }
        catch (Exception ex)
        {
            var psErrors = ps.Streams.Error
                .Select(e => e.Exception?.Message ?? e.ToString())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();
            ps.Streams.Error.Clear();
            var primary = psErrors.FirstOrDefault() ?? ex.Message;
            if (IsConnectionError(ex)) tracker.HasConnectionError = true;
            return PermissionResult.Fail(primary);
        }
    }

    private PermissionResult ExecuteCalendarPermission(PowerShell ps, ConnectionErrorTracker tracker, string targetMailbox, string user, string? accessRight, bool isSet)
    {
        try
        {
            var resolvedMailbox = ValidateMailbox(ps, targetMailbox);
            ValidateRecipient(ps, user);
            var calendarPath = GetCalendarFolderName(ps, resolvedMailbox);

            if (isSet && accessRight != null)
            {
                ps.AddCommand("Set-MailboxFolderPermission")
                  .AddParameter("Identity", calendarPath)
                  .AddParameter("User", user)
                  .AddParameter("AccessRights", accessRight)
                  .AddParameter("ErrorAction", "Stop");
                try
                {
                    Invoke(ps, tracker);
                }
                catch
                {
                    ps.Commands.Clear();
                    ps.Streams.Error.Clear();
                    ps.AddCommand("Add-MailboxFolderPermission")
                      .AddParameter("Identity", calendarPath)
                      .AddParameter("User", user)
                      .AddParameter("AccessRights", accessRight)
                      .AddParameter("ErrorAction", "Stop");
                    Invoke(ps, tracker);
                }
            }
            else
            {
                ps.AddCommand("Remove-MailboxFolderPermission")
                  .AddParameter("Identity", calendarPath)
                  .AddParameter("User", user)
                  .AddParameter("Confirm", false);
                Invoke(ps, tracker);
            }

            return PermissionResult.Ok();
        }
        catch (Exception ex)
        {
            var psErrors = ps.Streams.Error
                .Select(e => e.Exception?.Message ?? e.ToString())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();
            ps.Streams.Error.Clear();
            var primary = psErrors.FirstOrDefault() ?? ex.Message;
            if (IsConnectionError(ex)) tracker.HasConnectionError = true;
            return PermissionResult.Fail(primary);
        }
    }

    private void CheckAdGroupMembership(MigrationEligibilityResult result)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module")
          .AddParameter("Name", "ActiveDirectory")
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Get-ADUser")
          .AddParameter("Filter", $"UserPrincipalName -eq '{result.EmailAddress.Replace("'", "''")}'")

          .AddParameter("Properties", "memberOf")
          .AddParameter("ErrorAction", "Stop");
        var adUser = ps.Invoke();
        ps.Commands.Clear();

        var memberOf = adUser.FirstOrDefault()?.Properties["memberOf"]?.Value;
        if (memberOf == null) return;

        var groups = new List<string>();
        if (memberOf is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item != null)
                    groups.Add(item.ToString() ?? string.Empty);
            }
        }

        foreach (var excludedGroup in _excludedADGroups)
        {
            if (groups.Any(g => g.Contains(excludedGroup, StringComparison.OrdinalIgnoreCase)))
            {
                result.Status = MigrationStatus.Ineligible;
                result.IneligibilityReasons.Add($"Member of excluded group: {excludedGroup}");
                _logger.LogWarning("User {Email} is ineligible for cloud migration - member of {Group}", result.EmailAddress, excludedGroup);
            }
        }
    }













}

public class MailboxPermissionCsvRow
{
    public string Target { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string? FullAccess { get; set; }
    public string? SendAs { get; set; }
    public string? AutoMapping { get; set; }
}

public class CalendarPermissionCsvRow
{
    public string Target { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string AccessRight { get; set; } = string.Empty;
}
