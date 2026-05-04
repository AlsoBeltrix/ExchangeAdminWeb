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

public class ExchangeService : IExchangeService
{
    private readonly string _appId;
    private readonly string _organization;
    private readonly string _certSubject;
    private readonly ILogger<ExchangeService> _logger;
    private readonly string _hybridEndpoint;
    private readonly string _cloudTargetDomain;
    private readonly string _onPremTargetDomain;
    private readonly string _onPremTargetDatabases;
    private readonly long _cloudQuotaGB;
    private readonly string[] _excludedADGroups;
    private readonly string[] _adminNotificationEmails;
    private readonly string _onPremServerUri;
    private readonly DelineaService _delineaService;

    public ExchangeService(IConfiguration config, ILogger<ExchangeService> logger, DelineaService delineaService)
    {
        _appId = config["ExchangeOnline:AppId"]
            ?? throw new InvalidOperationException("ExchangeOnline:AppId is not configured.");
        _organization = config["ExchangeOnline:Organization"]
            ?? throw new InvalidOperationException("ExchangeOnline:Organization is not configured.");
        _certSubject = config["ExchangeOnline:CertificateSubject"] ?? "CN=EXO-Automation";
        _logger = logger;

        // Migration configuration
        _hybridEndpoint = config["Migration:HybridEndpoint"] ?? "hybrid1";
        _cloudTargetDomain = config["Migration:CloudTargetDeliveryDomain"] ?? "analog.mail.onmicrosoft.com";
        _onPremTargetDomain = config["Migration:OnPremTargetDeliveryDomain"] ?? "analog.com";
        _onPremTargetDatabases = config["Migration:OnPremTargetDatabases"] ?? "DAG2019";
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
                    var selfGrantError = validator.ValidateSelfGrant(currentUser, row.User);
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
                    var selfGrantError = validator.ValidateSelfGrant(currentUser, row.User);
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
        return await Task.Run(async () =>
        {
            var result = new MigrationEligibilityResult
            {
                EmailAddress = emailAddress,
                Status = MigrationStatus.Eligible
            };

            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                Connect(ps);

                // Check 1: Does user have an active migration?
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                var migUser = Invoke(ps);
                if (migUser.Any())
                {
                    result.Status = MigrationStatus.Ineligible;
                    result.IneligibilityReasons.Add("Migration already in progress");
                }

                // Check 2: Is user a cloud mailbox?
                ps.AddCommand("Get-Mailbox")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                var cloudMbx = Invoke(ps);
                var isCloudMailbox = false;

                if (cloudMbx.Any())
                {
                    var recipientType = cloudMbx.FirstOrDefault()?.Properties["RecipientTypeDetails"]?.Value?.ToString();
                    isCloudMailbox = recipientType?.Contains("UserMailbox") == true || recipientType?.Contains("SharedMailbox") == true;
                }

                // Apply direction-specific checks
                if (direction == MigrationDirection.ToCloud)
                {
                    // Migrating TO cloud - user should NOT already be in cloud
                    if (isCloudMailbox)
                    {
                        result.Status = MigrationStatus.Ineligible;
                        result.IneligibilityReasons.Add("Already a cloud mailbox");
                    }

                    // Check for excluded AD groups (ITAR, etc.) - ONLY for ToCloud migrations
                    // ITAR users cannot have data in the cloud but CAN migrate back to on-prem
                    if (_excludedADGroups.Length > 0)
                    {
                        try
                        {
                            // Get recipient info to find SamAccountName
                            ps.AddCommand("Get-Recipient")
                              .AddParameter("Identity", emailAddress)
                              .AddParameter("ErrorAction", "Stop");
                            var recipient = Invoke(ps);
                            var samAccountName = recipient.FirstOrDefault()?.Properties["SamAccountName"]?.Value?.ToString();

                            if (!string.IsNullOrEmpty(samAccountName))
                            {
                                // Import Active Directory module
                                ps.AddCommand("Import-Module")
                                  .AddParameter("Name", "ActiveDirectory")
                                  .AddParameter("ErrorAction", "Stop");
                                Invoke(ps);

                                // Get AD user with group memberships
                                ps.AddCommand("Get-ADUser")
                                  .AddParameter("Identity", samAccountName)
                                  .AddParameter("Properties", "memberOf")
                                  .AddParameter("ErrorAction", "Stop");
                                var adUser = Invoke(ps);
                                var memberOf = adUser.FirstOrDefault()?.Properties["memberOf"]?.Value;

                                if (memberOf != null)
                                {
                                    // Convert memberOf to string array
                                    var groups = new List<string>();
                                    if (memberOf is System.Collections.IEnumerable enumerable)
                                    {
                                        foreach (var item in enumerable)
                                        {
                                            if (item != null)
                                                groups.Add(item.ToString() ?? string.Empty);
                                        }
                                    }

                                    // Check each excluded group
                                    foreach (var excludedGroup in _excludedADGroups)
                                    {
                                        // Check if any group DN contains the excluded group name
                                        if (groups.Any(g => g.Contains(excludedGroup, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            result.Status = MigrationStatus.Ineligible;
                                            result.IneligibilityReasons.Add($"Member of excluded group: {excludedGroup}");
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
                            _logger.LogError(ex, "Error checking AD group membership for {Email} - check skipped. Ensure ActiveDirectory module is installed and app pool has AD access.", emailAddress);
                            // Don't fail eligibility check if AD is unavailable - log warning instead
                            result.IneligibilityReasons.Add($"Warning: Could not verify AD group membership ({ex.Message})");
                        }
                    }

                    // Check on-prem mailbox size against cloud quota
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
                else // ToOnPrem
                {
                    // Migrating TO on-prem - user MUST be in cloud
                    if (!isCloudMailbox)
                    {
                        result.Status = MigrationStatus.Ineligible;
                        result.IneligibilityReasons.Add("Not a cloud mailbox (must be in Exchange Online to migrate back to on-premises)");
                    }

                    // NOTE: AD group checks (SEC_ITAR_USERS, etc.) are NOT applicable for ToOnPrem migrations
                    // ITAR users are allowed to migrate back to on-premises
                }
            }
            catch (Exception ex)
            {
                result.Status = MigrationStatus.Ineligible;
                result.IneligibilityReasons.Add($"Error checking eligibility: {ex.Message}");
                _logger.LogError(ex, "Error checking migration eligibility for {Email}", emailAddress);
            }

            return result;
        });
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
        // For ToOnPrem, resolve the target database before entering the synchronous RunAsync lambda
        string? targetDatabase = null;
        if (direction == MigrationDirection.ToOnPrem)
        {
            try
            {
                var databases = await GetOnPremDatabasesAsync();
                if (databases.Count > 0)
                {
                    targetDatabase = databases.First();
                    _logger.LogInformation("Selected least-loaded on-prem database: {Database}", targetDatabase);
                }
            }
            catch (Exception dbEx)
            {
                _logger.LogWarning(dbEx, "Failed to query on-prem databases, falling back to configured value: {Database}", _onPremTargetDatabases);
            }
            targetDatabase ??= _onPremTargetDatabases;
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
                  .AddParameter("TargetDatabases", new[] { targetDatabase })
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

    public Task<List<MigrationBatchInfo>> GetMigrationBatchesAsync()
    {
        return Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            var batches = new List<MigrationBatchInfo>();

            try
            {
                Connect(ps);

                // Get all migration batches
                ps.AddCommand("Get-MigrationBatch")
                  .AddParameter("ErrorAction", "SilentlyContinue");

                var batchResults = Invoke(ps);

                foreach (var batchObj in batchResults)
                {
                    try
                    {
                        var batchName = batchObj.Properties["Identity"]?.Value?.ToString() ?? "Unknown";
                        var status = batchObj.Properties["Status"]?.Value?.ToString() ?? "Unknown";
                        var totalCount = Convert.ToInt32(batchObj.Properties["TotalCount"]?.Value ?? 0);
                        var syncedCount = Convert.ToInt32(batchObj.Properties["SyncedItemCount"]?.Value ?? 0);
                        var finalizedCount = Convert.ToInt32(batchObj.Properties["FinalizedItemCount"]?.Value ?? 0);
                        var failedCount = Convert.ToInt32(batchObj.Properties["FailedItemCount"]?.Value ?? 0);
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
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    ps.AddCommand("Disconnect-ExchangeOnline")
                      .AddParameter("Confirm", false);
                    ps.Invoke();
                }
                catch { /* best effort */ }
            }
        });
    }

    public Task<List<MigrationUserInfo>> GetMigrationBatchUsersAsync(string batchName)
    {
        return Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            var users = new List<MigrationUserInfo>();

            try
            {
                Connect(ps);

                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("BatchId", batchName)
                  .AddParameter("ErrorAction", "SilentlyContinue");

                var userResults = Invoke(ps);

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
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    ps.AddCommand("Disconnect-ExchangeOnline")
                      .AddParameter("Confirm", false);
                    ps.Invoke();
                }
                catch { /* best effort */ }
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
            ps.AddCommand("Complete-MigrationUser")
              .AddParameter("Identity", emailAddress)
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
              .AddParameter("ErrorAction", "SilentlyContinue");
            Invoke(ps);

            ps.AddCommand("Set-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("CompleteAfter", pastDate)
              .AddParameter("ErrorAction", "SilentlyContinue");
            Invoke(ps);

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
              .AddParameter("ErrorAction", "SilentlyContinue");
            Invoke(ps);
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
            ps.AddCommand("Resume-MoveRequest")
              .AddParameter("Identity", emailAddress)
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

    public Task<string?> GetMigrationUserReportAsync(string emailAddress)
    {
        return Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                Connect(ps);

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
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    ps.AddCommand("Disconnect-ExchangeOnline")
                      .AddParameter("Confirm", false);
                    ps.Invoke();
                }
                catch { }
            }
        });
    }

    public Task<string?> FindMigrationUserBatchAsync(string emailAddress)
    {
        return Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                Connect(ps);

                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "SilentlyContinue");

                var results = Invoke(ps);
                var user = results.FirstOrDefault();
                return user?.Properties["BatchId"]?.Value?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to find migration user {Email}", emailAddress);
                return (string?)null;
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    ps.AddCommand("Disconnect-ExchangeOnline")
                      .AddParameter("Confirm", false);
                    ps.Invoke();
                }
                catch { }
            }
        });
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

        return await Task.Run(() =>
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
        });
    }

    public async Task<List<string>> GetOnPremDatabasesAsync()
    {
        if (string.IsNullOrEmpty(_onPremServerUri))
        {
            _logger.LogError("OnPremExchange:ServerUri is not configured — cannot query databases");
            return new List<string>();
        }

        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
        {
            _logger.LogError("Cannot connect to on-prem Exchange: failed to retrieve credentials from Delinea");
            return new List<string>();
        }

        return await Task.Run(() =>
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

                var dbScript = ScriptBlock.Create("Get-MailboxDatabase -Status -ErrorAction Stop | Where-Object { $_.Mounted -eq $true } | Sort-Object @{Expression={($_ | Get-Mailbox -ResultSize Unlimited).Count}} | Select-Object Name");
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", ps.Runspace.SessionStateProxy.GetVariable("onpremSession"))
                  .AddParameter("ScriptBlock", dbScript);
                var dbResults = Invoke(ps);

                var databases = dbResults
                    .Select(r => r.Properties["Name"]?.Value?.ToString())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                _logger.LogInformation("Found {Count} on-prem databases: {Databases}", databases.Count, string.Join(", ", databases!));
                return databases!;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query on-prem databases");
                return new List<string>();
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
        });
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
        if (string.IsNullOrWhiteSpace(sizeString))
            return 0;

        // Exchange returns sizes like "1.234 GB (1,234,567,890 bytes)" or "500.1 MB (524,396,544 bytes)"
        // Try to extract the bytes value from parentheses first (most reliable)
        var bytesMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"\(([\d,]+)\s+bytes\)");
        if (bytesMatch.Success)
        {
            var bytesStr = bytesMatch.Groups[1].Value.Replace(",", "");
            if (long.TryParse(bytesStr, out var bytes))
                return Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 2);
        }

        // Fallback: parse the human-readable portion
        var gbMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"([\d.]+)\s*GB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (gbMatch.Success && double.TryParse(gbMatch.Groups[1].Value, out var gb))
            return Math.Round(gb, 2);

        var mbMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"([\d.]+)\s*MB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (mbMatch.Success && double.TryParse(mbMatch.Groups[1].Value, out var mb))
            return Math.Round(mb / 1024.0, 2);

        return 0;
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

    private Task<PermissionResult> RunAsync(Action<PowerShell> operation, Func<(string message, string? detail)>? successFormatter = null)
    {
        return Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                Connect(ps);
                operation(ps);

                if (successFormatter is not null)
                {
                    var (message, detail) = successFormatter();
                    return new PermissionResult { Success = true, Message = message, Detail = detail };
                }
                return PermissionResult.Ok();
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
                return PermissionResult.Fail(primary, detail);
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    ps.AddCommand("Disconnect-ExchangeOnline")
                      .AddParameter("Confirm", false);
                    ps.Invoke();
                }
                catch { /* best effort */ }
            }
        });
    }

    private void Connect(PowerShell ps)
    {
        var cert = FindCertificate();

        ps.AddCommand("Import-Module")
          .AddParameter("Name", "ExchangeOnlineManagement")
          .AddParameter("ErrorAction", "Stop");
        Invoke(ps);

        _logger.LogInformation("Connecting to EXO: AppId={AppId}, Thumbprint={Thumbprint}, Org={Org}",
            _appId, cert.Thumbprint, _organization);

        ps.AddCommand("Connect-ExchangeOnline")
          .AddParameter("AppId", _appId)
          .AddParameter("CertificateThumbprint", cert.Thumbprint)
          .AddParameter("Organization", _organization)
          .AddParameter("ShowBanner", false)
          .AddParameter("ErrorAction", "Stop");

        try
        {
            var result = ps.Invoke();
            foreach (var warn in ps.Streams.Warning)
                _logger.LogWarning("EXO Connect warning: {Msg}", warn.Message);
            foreach (var err in ps.Streams.Error)
                _logger.LogError("EXO Connect error: {Msg} | Exception: {Ex}", err.ToString(), err.Exception?.Message);
            foreach (var info in ps.Streams.Information)
                _logger.LogInformation("EXO Connect info: {Msg}", info.MessageData?.ToString());

            _logger.LogInformation("EXO Connect finished. HadErrors={HadErrors}, ErrorCount={ErrorCount}",
                ps.HadErrors, ps.Streams.Error.Count);

            if (ps.Streams.Error.Count > 0)
            {
                var msg = ps.Streams.Error.First().Exception?.Message
                       ?? ps.Streams.Error.First().ToString();
                ps.Commands.Clear();
                throw new InvalidOperationException(msg);
            }
            ps.Commands.Clear();
        }
        catch (RuntimeException ex)
        {
            ps.Commands.Clear();
            throw new InvalidOperationException($"EXO Connect failed: {ex.Message}", ex);
        }
    }

    private X509Certificate2 FindCertificate()
    {
        var locations = new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser };

        foreach (var location in locations)
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);

            var cert = store.Certificates
                .Find(X509FindType.FindBySubjectDistinguishedName, _certSubject, validOnly: false)
                .OfType<X509Certificate2>()
                .Where(c => c.HasPrivateKey)
                .OrderByDescending(c => c.NotBefore)
                .FirstOrDefault();

            if (cert is not null) return cert;
        }

        throw new InvalidOperationException(
            $"Certificate '{_certSubject}' with a private key was not found in LocalMachine\\My or CurrentUser\\My.");
    }

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
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (ps.HadErrors)
        {
            var msg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                   ?? ps.Streams.Error.FirstOrDefault()?.ToString()
                   ?? "An unknown error occurred.";
            throw new InvalidOperationException(msg);
        }

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
