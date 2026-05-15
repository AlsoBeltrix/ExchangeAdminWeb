using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class ExchangeService : IExchangeService, IIdentityResolver
{
    private readonly string _appId;
    private readonly string _organization;
    private readonly string _certSubject;
    private readonly ILogger<ExchangeService> _logger;
    private readonly string _hybridEndpoint;
    private readonly string _cloudTargetDomain;
    private readonly string _onPremTargetDomain;
    private readonly string _onPremTargetDAG;
    private readonly long _cloudQuotaGB;
    private readonly string[] _excludedADGroups;
    private readonly string[] _adminNotificationEmails;
    private readonly string _onPremServerUri;
    private readonly DelineaService _delineaService;
    private readonly ExoConnectionPool _exoPool;
    private static readonly SemaphoreSlim _onPremThrottle = new(2, 2);

    public ExchangeService(IConfiguration config, ILogger<ExchangeService> logger, DelineaService delineaService, ExoConnectionPool exoPool)
    {
        _appId = config["ExchangeOnline:AppId"]
            ?? throw new InvalidOperationException("ExchangeOnline:AppId is not configured.");
        _organization = config["ExchangeOnline:Organization"]
            ?? throw new InvalidOperationException("ExchangeOnline:Organization is not configured.");
        _certSubject = config["ExchangeOnline:CertificateSubject"] ?? "CN=EXO-Automation";
        _logger = logger;

        // Migration configuration
        _hybridEndpoint = config["Migration:HybridEndpoint"] ?? "hybrid1";
        _cloudTargetDomain = config["Migration:CloudTargetDeliveryDomain"] ?? "example.mail.onmicrosoft.com";
        _onPremTargetDomain = config["Migration:OnPremTargetDeliveryDomain"] ?? "example.com";
        _onPremTargetDAG = config["Migration:OnPremTargetDAG"] ?? "";
        _cloudQuotaGB = long.Parse(config["Migration:CloudQuotaGB"] ?? "100");
        _excludedADGroups = config.GetSection("Migration:ExcludedADGroups").Get<string[]>() ?? Array.Empty<string>();

        // Admin notification emails
        var adminEmail = config["Email:AdminNotificationEmail"] ?? "";
        _adminNotificationEmails = adminEmail.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToArray();

        _onPremServerUri = config["OnPremExchange:ServerUri"] ?? "";
        _delineaService = delineaService;
        _exoPool = exoPool;
    }

    public Task<PermissionResult> AddMailboxPermissionsAsync(string targetMailbox, string user, bool fullAccess, bool sendAs, bool autoMapping)
    {
        var permissions = new List<string>();
        if (fullAccess) permissions.Add("FullAccess");
        if (sendAs) permissions.Add("SendAs");

        return RunAsync(ps =>
        {
            ValidateMailbox(ps, targetMailbox);
            ValidateMailbox(ps, user);

            if (fullAccess)
            {
                ps.AddCommand("Add-MailboxPermission")
                  .AddParameter("Identity", targetMailbox)
                  .AddParameter("User", user)
                  .AddParameter("AccessRights", "FullAccess")
                  .AddParameter("AutoMapping", autoMapping)
                  .AddParameter("Confirm", false);
                Invoke(ps);
            }

            if (sendAs)
            {
                ps.AddCommand("Add-RecipientPermission")
                  .AddParameter("Identity", targetMailbox)
                  .AddParameter("Trustee", user)
                  .AddParameter("AccessRights", "SendAs")
                  .AddParameter("Confirm", false);
                Invoke(ps);
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

        return RunAsync(ps =>
        {
            ValidateMailbox(ps, targetMailbox);
            ValidateMailbox(ps, user);

            if (fullAccess)
            {
                ps.AddCommand("Remove-MailboxPermission")
                  .AddParameter("Identity", targetMailbox)
                  .AddParameter("User", user)
                  .AddParameter("AccessRights", "FullAccess")
                  .AddParameter("Confirm", false);
                Invoke(ps);
            }

            if (sendAs)
            {
                ps.AddCommand("Remove-RecipientPermission")
                  .AddParameter("Identity", targetMailbox)
                  .AddParameter("Trustee", user)
                  .AddParameter("AccessRights", "SendAs")
                  .AddParameter("Confirm", false);
                Invoke(ps);
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

        return RunAsync(ps =>
        {
            var resolvedMailbox = ValidateMailbox(ps, targetMailbox);
            ValidateMailbox(ps, user);

            calendarPath = GetCalendarFolderName(ps, resolvedMailbox);
            var level = accessRight.ToString();

            ps.AddCommand("Set-MailboxFolderPermission")
              .AddParameter("Identity", calendarPath)
              .AddParameter("User", user)
              .AddParameter("AccessRights", level)
              .AddParameter("ErrorAction", "Stop");
            try
            {
                Invoke(ps);
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
                Invoke(ps);
            }
        }, () => ($"{user} granted {accessRight} permission to {calendarPath}", null));
    }

    public Task<PermissionResult> RemoveCalendarPermissionAsync(string targetMailbox, string user)
    {
        string? calendarPath = null;

        return RunAsync(ps =>
        {
            var resolvedMailbox = ValidateMailbox(ps, targetMailbox);
            ValidateMailbox(ps, user);

            calendarPath = GetCalendarFolderName(ps, resolvedMailbox);
            ps.AddCommand("Remove-MailboxFolderPermission")
              .AddParameter("Identity", calendarPath)
              .AddParameter("User", user)
              .AddParameter("Confirm", false);
            Invoke(ps);
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

        var records = csv.GetRecords<MailboxPermissionCsvRow>().ToList();
        var errors = new List<string>();
        var entries = new List<BulkOperationEntry>();
        var successCount = 0;

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

                var result = isAdd
                    ? await AddMailboxPermissionsAsync(row.Target, row.User, fullAccess, sendAs, autoMap)
                    : await RemoveMailboxPermissionsAsync(row.Target, row.User, fullAccess, sendAs);

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

        var records = csv.GetRecords<CalendarPermissionCsvRow>().ToList();
        var errors = new List<string>();
        var entries = new List<BulkOperationEntry>();
        var successCount = 0;

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
                        // Log self-grant attempt to audit log
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

                var result = isSet
                    ? await SetCalendarPermissionAsync(row.Target, row.User, Enum.Parse<CalendarAccessRight>(row.AccessRight))
                    : await RemoveCalendarPermissionAsync(row.Target, row.User);

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
                        try
                        {
                            ps.AddCommand("Get-Recipient")
                              .AddParameter("Identity", emailAddress)
                              .AddParameter("ErrorAction", "Stop");
                            var recipient = Invoke(ps);
                            var samAccountName = recipient.FirstOrDefault()?.Properties["SamAccountName"]?.Value?.ToString();

                            if (!string.IsNullOrEmpty(samAccountName))
                            {
                                ps.AddCommand("Import-Module")
                                  .AddParameter("Name", "ActiveDirectory")
                                  .AddParameter("ErrorAction", "Stop");
                                Invoke(ps);

                                ps.AddCommand("Get-ADUser")
                                  .AddParameter("Identity", samAccountName)
                                  .AddParameter("Properties", "memberOf")
                                  .AddParameter("ErrorAction", "Stop");
                                var adUser = Invoke(ps);
                                var memberOf = adUser.FirstOrDefault()?.Properties["memberOf"]?.Value;

                                if (memberOf != null)
                                {
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
                                            r.Status = MigrationStatus.Ineligible;
                                            r.IneligibilityReasons.Add($"Member of excluded group: {excludedGroup}");
                                            _logger.LogWarning("User {Email} is ineligible for cloud migration - member of {Group}", emailAddress, excludedGroup);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Could not find SamAccountName for {Email} - AD group check skipped", emailAddress);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error checking AD group membership for {Email} - check skipped.", emailAddress);
                            r.IneligibilityReasons.Add($"Warning: Could not verify AD group membership ({ex.Message})");
                        }
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
            targetDatabases = await ResolveDagDatabasesAsync();
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

    // -------------------------------------------------------------------------
    // Migration Batch/User Actions
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // Lookup Operations
    // -------------------------------------------------------------------------

    public async Task<DelegationReportResult> GetMailboxDelegationAsync(string emailAddress)
    {
        return await RunPooledQueryAsync(ps =>
        {
            var result = new DelegationReportResult { EmailAddress = emailAddress };

            try
            {
                // Full Access
                ps.AddCommand("Get-MailboxPermission")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var perms = Invoke(ps);
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
                var recipPerms = Invoke(ps);
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
                    var calPerms = Invoke(ps);
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

    public async Task<MessageTraceResponse> GetMessageTraceAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string? subjectFilter)
    {
        return await RunPooledQueryAsync(ps =>
        {
            var response = new MessageTraceResponse();

            try
            {
                var allResults = new List<MessageTraceResult>();
                var page = 1;
                const int pageSize = 200;
                const int maxPages = 10;

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

                    var results = Invoke(ps);
                    if (!results.Any())
                        break;

                    foreach (var msg in results)
                    {
                        var subject = msg.Properties["Subject"]?.Value?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(subjectFilter) &&
                            !subject.Contains(subjectFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        allResults.Add(new MessageTraceResult
                        {
                            Received = msg.Properties["Received"]?.Value as DateTime? ?? DateTime.MinValue,
                            SenderAddress = msg.Properties["SenderAddress"]?.Value?.ToString() ?? "",
                            RecipientAddress = msg.Properties["RecipientAddress"]?.Value?.ToString() ?? "",
                            Subject = subject,
                            Status = msg.Properties["Status"]?.Value?.ToString() ?? "",
                            MessageId = msg.Properties["MessageId"]?.Value?.ToString() ?? "",
                            Size = msg.Properties["Size"]?.Value as long? ?? 0,
                            FromIP = msg.Properties["FromIP"]?.Value?.ToString() ?? "",
                            ToIP = msg.Properties["ToIP"]?.Value?.ToString() ?? "",
                            MessageTraceId = msg.Properties["MessageTraceId"]?.Value?.ToString()
                                ?? msg.Properties["MessageTraceID"]?.Value?.ToString() ?? ""
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
                response.Error = ex.Message;
                _logger.LogError(ex, "Error running message trace");
            }

            return response;
        });
    }

    public async Task<HistoricalSearchResponse> StartHistoricalSearchAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string notifyAddress, string reportTitle)
    {
        return await RunPooledQueryAsync(ps =>
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

                var results = Invoke(ps);
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
        var result = await RunPooledQueryAsync(ps =>
        {
            var r = new RecipientInfoResult { EmailAddress = emailAddress };

            try
            {
                ps.AddCommand("Get-Recipient")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var recipients = Invoke(ps);
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

                var typeDetails = r.RecipientType ?? "";
                if (typeDetails.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                    r.MailboxLocation = "On-Premises";
                else if (typeDetails.Contains("Mailbox", StringComparison.OrdinalIgnoreCase))
                    r.MailboxLocation = "Cloud";
                else
                    r.MailboxLocation = "Unknown";

                ps.AddCommand("Get-Mailbox")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Ignore");
                var mbxResults = InvokeOptional(ps);
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
                        var stats = Invoke(ps);
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
                        var archiveStats = Invoke(ps);
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

        if (result.Error == null && result.MailboxLocation == "On-Premises")
        {
            try
            {
                var onPremSize = await GetOnPremMailboxSizeAsync(emailAddress);
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
        return await RunPooledQueryAsync(ps =>
        {
            var result = new OutOfOfficeResult { EmailAddress = emailAddress, State = "Unknown" };

            try
            {
                ps.AddCommand("Get-MailboxAutoReplyConfiguration")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var results = Invoke(ps);
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
        return RunAsync(ps =>
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

            Invoke(ps);
        }, () => (state == "Disabled"
            ? $"Auto-reply disabled for {emailAddress}."
            : $"Auto-reply set to {state} for {emailAddress}.", (string?)null));
    }

    public static double? ParseSizeToGB(string? sizeString)
    {
        if (string.IsNullOrWhiteSpace(sizeString)) return null;

        // Exchange returns sizes like "1.234 GB (1,234,567,890 bytes)" or "500.1 MB (524,396,544 bytes)"
        // Try parenthesized bytes first (most reliable)
        var parenMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"\(([\d,]+)\s+bytes\)");
        if (parenMatch.Success && long.TryParse(parenMatch.Groups[1].Value.Replace(",", ""), out var parenBytes))
            return Math.Round(parenBytes / 1073741824.0, 2);

        // Bare bytes format: "1,234,567 bytes"
        var bareMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"^([\d,]+)\s*bytes$");
        if (bareMatch.Success && long.TryParse(bareMatch.Groups[1].Value.Replace(",", ""), out var bareBytes))
            return Math.Round(bareBytes / 1073741824.0, 2);

        // Human-readable GB: "1.234 GB"
        var gbMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"([\d.]+)\s*GB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (gbMatch.Success && double.TryParse(gbMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb))
            return Math.Round(gb, 2);

        // Human-readable MB: "500.1 MB"
        var mbMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"([\d.]+)\s*MB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (mbMatch.Success && double.TryParse(mbMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb))
            return Math.Round(mb / 1024.0, 2);

        // Human-readable KB: "512 KB"
        var kbMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"([\d.]+)\s*KB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (kbMatch.Success && double.TryParse(kbMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var kb))
            return Math.Round(kb / 1048576.0, 4);

        return null;
    }

    private static long? ParseLong(object? value)
    {
        if (value is long l) return l;
        if (value is int i) return i;
        if (value != null && long.TryParse(value.ToString(), out var parsed)) return parsed;
        return null;
    }

    // -------------------------------------------------------------------------
    // On-Prem Exchange Operations
    // -------------------------------------------------------------------------

    public async Task<(double mailboxSizeGB, double archiveSizeGB)?> GetOnPremMailboxSizeAsync(string emailAddress)
    {
        if (string.IsNullOrEmpty(_onPremServerUri))
        {
            _logger.LogError("OnPremExchange:ServerUri is not configured — cannot check mailbox size");
            return null;
        }

        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
        {
            _logger.LogError("Cannot connect to on-prem Exchange: failed to retrieve credentials from Delinea");
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

                var mbxScript = ScriptBlock.Create("param($Identity) Get-MailboxStatistics -Identity $Identity -ErrorAction Stop | Select-Object TotalItemSize");
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", ps.Runspace.SessionStateProxy.GetVariable("onpremSession"))
                  .AddParameter("ScriptBlock", mbxScript)
                  .AddParameter("ArgumentList", new object[] { emailAddress });
                var mbxStats = Invoke(ps);
                var totalItemSize = mbxStats.FirstOrDefault()?.Properties["TotalItemSize"]?.Value?.ToString();
                var mailboxGB = ParseExchangeSize(totalItemSize);

                _logger.LogInformation("On-prem mailbox size for {Email}: {Size} GB", emailAddress, mailboxGB);

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
                }
                catch
                {
                    _logger.LogInformation("No archive mailbox found for {Email}", emailAddress);
                }

                return ((double mailboxSizeGB, double archiveSizeGB)?)(mailboxGB, archiveGB);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get on-prem mailbox size for {Email}", emailAddress);
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

    private async Task<string[]?> ResolveDagDatabasesAsync()
    {
        if (string.IsNullOrWhiteSpace(_onPremTargetDAG))
        {
            _logger.LogError("Migration:OnPremTargetDAG is not configured — cannot resolve target databases");
            return null;
        }

        if (string.IsNullOrEmpty(_onPremServerUri))
        {
            _logger.LogError("OnPremExchange:ServerUri is not configured — cannot resolve DAG databases");
            return null;
        }

        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
        {
            _logger.LogError("Cannot connect to on-prem Exchange: failed to retrieve credentials from Delinea");
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

                var script = ScriptBlock.Create(
                    "param($DagName) Get-MailboxDatabase | Where-Object { $_.MasterServerOrAvailabilityGroup -eq $DagName -and $_.Recovery -eq $false } | Select-Object Name");
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", ps.Runspace.SessionStateProxy.GetVariable("onpremSession"))
                  .AddParameter("ScriptBlock", script)
                  .AddParameter("ArgumentList", new object[] { _onPremTargetDAG });
                var results = Invoke(ps);

                var databases = results
                    .Select(r => r.Properties["Name"]?.Value?.ToString())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Cast<string>()
                    .ToArray();

                if (databases.Length == 0)
                {
                    _logger.LogWarning("No databases found for DAG '{DagName}'", _onPremTargetDAG);
                    return (string[]?)null;
                }

                _logger.LogInformation("Resolved {Count} databases from DAG '{DagName}': {Databases}",
                    databases.Length, _onPremTargetDAG, string.Join(", ", databases));

                return databases;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve databases for DAG '{DagName}'", _onPremTargetDAG);
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

    private void ConnectOnPrem(PowerShell ps, string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username
            : $"{domain}\\{username}";

        var securePassword = new System.Security.SecureString();
        foreach (var c in password)
            securePassword.AppendChar(c);
        securePassword.MakeReadOnly();

        var credential = new PSCredential(fullUsername, securePassword);

        ps.AddCommand("New-PSSession")
          .AddParameter("ConfigurationName", "Microsoft.Exchange")
          .AddParameter("ConnectionUri", _onPremServerUri)
          .AddParameter("Authentication", "Kerberos")
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var sessions = Invoke(ps);
        var session = sessions.FirstOrDefault() ?? throw new InvalidOperationException("Failed to create on-prem Exchange session");

        ps.Runspace.SessionStateProxy.SetVariable("onpremSession", session.BaseObject);

        _logger.LogInformation("Connected to on-prem Exchange at {Uri}", _onPremServerUri);
    }

    private static double ParseExchangeSize(string? sizeString)
    {
        return ParseSizeToGB(sizeString) ?? 0;
    }

    // -------------------------------------------------------------------------

    private string GetCalendarFolderName(PowerShell ps, string mailbox)
    {
        ps.AddCommand("Get-MailboxFolderStatistics")
          .AddParameter("Identity", mailbox)
          .AddParameter("FolderScope", "Calendar")
          .AddParameter("ErrorAction", "Stop");

        var result = Invoke(ps);
        var folder = result.FirstOrDefault();

        if (folder is null)
            throw new InvalidOperationException($"No calendar folder found for {mailbox}");

        var rawFolderPath = folder.Properties["FolderPath"]?.Value?.ToString();
        _logger.LogInformation("Calendar folder lookup for {Mailbox}: raw FolderPath = '{RawPath}'", mailbox, rawFolderPath ?? "<null>");

        var folderPath = rawFolderPath ?? @"\Calendar";
        // Exchange Online may return forward slashes, but cmdlets require backslashes
        folderPath = folderPath.Replace("/", @"\");

        var fullPath = $"{mailbox}:{folderPath}";
        _logger.LogInformation("Constructed calendar identity: '{FullPath}'", fullPath);
        return fullPath;
    }

    private async Task<PermissionResult> RunAsync(Action<PowerShell> operation, Func<(string message, string? detail)>? successFormatter = null)
    {
        var pooled = await _exoPool.BorrowAsync();
        bool discard = false;
        try
        {
            var (result, hadConnectionError) = await Task.Run(() =>
            {
                ConnectionErrorFlag = false;
                var ps = pooled.PowerShell;
                try
                {
                    operation(ps);

                    if (successFormatter is not null)
                    {
                        var (message, detail) = successFormatter();
                        return (new PermissionResult { Success = true, Message = message, Detail = detail }, ConnectionErrorFlag);
                    }
                    return (PermissionResult.Ok(), ConnectionErrorFlag);
                }
                catch (Exception ex)
                {
                    var psErrors = ps.Streams.Error
                        .Select(e => e.Exception?.Message ?? e.ToString())
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .ToList();

                    var primary = psErrors.FirstOrDefault() ?? ex.Message;
                    var detail = psErrors.Count > 1 ? string.Join(" | ", psErrors.Skip(1)) : null;

                    _logger.LogError(ex, "Exchange operation failed: {Message}", primary);
                    return (PermissionResult.Fail(primary, detail), IsConnectionError(ex) || ConnectionErrorFlag);
                }
            });

            discard = hadConnectionError;
            return result;
        }
        finally
        {
            if (discard)
                _exoPool.Discard(pooled);
            else
                _exoPool.Return(pooled);
        }
    }


    public async Task<string?> ResolveToObjectIdAsync(string identity)
    {
        try
        {
            return await RunPooledQueryAsync(ps =>
            {
                ps.AddCommand("Get-Recipient")
                  .AddParameter("Identity", identity)
                  .AddParameter("ErrorAction", "Stop");

                var results = Invoke(ps);
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

    private async Task<T> ThrottledAsync<T>(Func<Task<T>> operation, SemaphoreSlim? throttle = null)
    {
        if (throttle != null)
        {
            if (!await throttle.WaitAsync(TimeSpan.FromMinutes(2)))
                throw new InvalidOperationException("Exchange service is busy. Please try again shortly.");
            try { return await operation(); }
            finally { throttle.Release(); }
        }
        return await operation();
    }

    private async Task<T> RunPooledQueryAsync<T>(Func<PowerShell, T> query)
    {
        var pooled = await _exoPool.BorrowAsync();
        bool discard = false;
        try
        {
            var (result, hadConnectionError) = await Task.Run(() =>
            {
                ConnectionErrorFlag = false;
                var r = query(pooled.PowerShell);
                return (r, ConnectionErrorFlag);
            });

            discard = hadConnectionError;
            return result;
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

    [ThreadStatic] private static bool ConnectionErrorFlag;

    private static bool IsConnectionError(Exception? ex) =>
        ex != null && (
            ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("runspace", StringComparison.OrdinalIgnoreCase));

    private static string ValidateMailbox(PowerShell ps, string mailbox)
    {
        ps.AddCommand("Get-Mailbox")
          .AddParameter("Identity", mailbox)
          .AddParameter("ErrorAction", "Stop");
        var result = Invoke(ps);
        var mbx = result.FirstOrDefault();

        // Return PrimarySmtpAddress for use in calendar paths
        return mbx?.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? mailbox;
    }

    private static Collection<PSObject> Invoke(PowerShell ps)
    {
        Collection<PSObject> result;
        try
        {
            result = ps.Invoke();
        }
        catch (RuntimeException ex)
        {
            if (IsConnectionError(ex))
                ConnectionErrorFlag = true;
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (ps.HadErrors)
        {
            var err = ps.Streams.Error.FirstOrDefault();
            var msg = err?.Exception?.Message ?? err?.ToString() ?? "An unknown error occurred.";
            if (IsConnectionError(err?.Exception))
                ConnectionErrorFlag = true;
            throw new InvalidOperationException(msg);
        }

        ps.Commands.Clear();
        return result;
    }

    private static Collection<PSObject> InvokeOptional(PowerShell ps)
    {
        var result = ps.Invoke();
        if (ps.Streams.Error.Any(e => IsConnectionError(e.Exception)))
            ConnectionErrorFlag = true;
        ps.Streams.Error.Clear();
        ps.Commands.Clear();
        return result;
    }

    private static bool ParseBool(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" or "x" => true,
            _ => false
        };
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
