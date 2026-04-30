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

    public ExchangeService(IConfiguration config, ILogger<ExchangeService> logger)
    {
        _appId = config["ExchangeOnline:AppId"]
            ?? throw new InvalidOperationException("ExchangeOnline:AppId is not configured.");
        _organization = config["ExchangeOnline:Organization"]
            ?? throw new InvalidOperationException("ExchangeOnline:Organization is not configured.");
        _certSubject = config["ExchangeOnline:CertificateSubject"] ?? "CN=EXO-Automation";
        _logger = logger;
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
            ValidateMailbox(ps, targetMailbox);
            ValidateMailbox(ps, user);

            calendarPath = GetCalendarFolderName(ps, targetMailbox);
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
            ValidateMailbox(ps, targetMailbox);
            ValidateMailbox(ps, user);

            calendarPath = GetCalendarFolderName(ps, targetMailbox);
            ps.AddCommand("Remove-MailboxFolderPermission")
              .AddParameter("Identity", calendarPath)
              .AddParameter("User", user)
              .AddParameter("Confirm", false);
            Invoke(ps);
        }, () => ($"Calendar permission removed for {user} on {calendarPath}", null));
    }

    public async Task<BulkOperationResult> ProcessMailboxPermissionsCsvAsync(Stream csvStream, bool isAdd, PermissionValidator validator, string currentUser)
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
                // Validate target is not excluded
                var validationError = await validator.ValidateTargetMailboxAsync(row.Target);
                if (validationError is not null)
                {
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

                // Validate self-grant (only for Add operations)
                if (isAdd)
                {
                    var selfGrantError = validator.ValidateSelfGrant(currentUser, row.User);
                    if (selfGrantError is not null)
                    {
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

                var fullAccess = ParseBool(row.FullAccess);
                var sendAs = ParseBool(row.SendAs);
                var autoMap = ParseBool(row.AutoMapping ?? "true");

                var permissions = new List<string>();
                if (fullAccess) permissions.Add("FullAccess");
                if (sendAs) permissions.Add("SendAs");
                var permType = string.Join("+", permissions);

                var result = isAdd
                    ? await AddMailboxPermissionsAsync(row.Target, row.User, fullAccess, sendAs, autoMap)
                    : await RemoveMailboxPermissionsAsync(row.Target, row.User, fullAccess, sendAs);

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

    public async Task<BulkOperationResult> ProcessCalendarPermissionsCsvAsync(Stream csvStream, bool isSet, PermissionValidator validator, string currentUser)
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
                // Validate target is not excluded
                var validationError = await validator.ValidateTargetMailboxAsync(row.Target);
                if (validationError is not null)
                {
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

                // Validate self-grant (only for Set operations)
                if (isSet)
                {
                    var selfGrantError = validator.ValidateSelfGrant(currentUser, row.User);
                    if (selfGrantError is not null)
                    {
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

        var folderPath = folder.Properties["FolderPath"]?.Value?.ToString() ?? "/Calendar";
        return $"{mailbox}:{folderPath}";
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

        ps.AddCommand("Connect-ExchangeOnline")
          .AddParameter("AppId", _appId)
          .AddParameter("CertificateThumbprint", cert.Thumbprint)
          .AddParameter("Organization", _organization)
          .AddParameter("ShowBanner", false)
          .AddParameter("ErrorAction", "Stop");
        Invoke(ps);
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

    private static void ValidateMailbox(PowerShell ps, string mailbox)
    {
        ps.AddCommand("Get-Mailbox")
          .AddParameter("Identity", mailbox)
          .AddParameter("ErrorAction", "Stop");
        Invoke(ps);
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
