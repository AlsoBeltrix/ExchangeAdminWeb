using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Claims;
using CsvHelper;
using CsvHelper.Configuration;

namespace ExchangeAdminWeb.Services;

public sealed record LicensePreviewRow(
    string InputIdentity,
    ResolvedDirectoryPrincipal? Principal,
    string? CurrentValue,
    string ProposedValue,
    string? Error);

public sealed record LicensePreviewResult(
    bool Success,
    string? Error,
    string LicenseType,
    List<LicensePreviewRow> Rows);

public sealed record LicenseApplyRow(
    string InputIdentity,
    string? UserPrincipalName,
    string Status,
    string? PreviousValue,
    string? NewValue,
    string? Error);

public sealed record LicenseBulkResult(
    bool Success,
    string? Error,
    int TotalRows,
    int Succeeded,
    int Unchanged,
    int Protected,
    int Failed,
    List<LicenseApplyRow> Rows);

public class LicensingUpdatesService
{
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ProtectedPrincipalService _protectedPrincipalService;
    private readonly ProtectedPrincipalServicerService _servicers;

    /// <summary>Module id for the servicer grant. Must match the catalog descriptor.</summary>
    private const string ServicerModuleId = "LicensingUpdates";
    private readonly OperationTraceService _operationTrace;
    private readonly AuditService _audit;
    private readonly ILogger<LicensingUpdatesService> _logger;
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);

    private const string TargetAttribute = "extensionAttribute11";

    private static readonly HashSet<string> HeaderIndicators = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "name", "email", "sam", "upn", "identity", "userprincipalname", "samaccountname", "mail"
    };

    public LicensingUpdatesService(
        ModuleCredentialService moduleCredentials,
        ModuleConfigService moduleConfig,
        ProtectedPrincipalService protectedPrincipalService,
        ProtectedPrincipalServicerService servicers,
        OperationTraceService operationTrace,
        AuditService audit,
        ILogger<LicensingUpdatesService> logger)
    {
        _moduleCredentials = moduleCredentials;
        _moduleConfig = moduleConfig;
        _protectedPrincipalService = protectedPrincipalService;
        _servicers = servicers;
        _operationTrace = operationTrace;
        _audit = audit;
        _logger = logger;
    }

    public string[] GetAllowedLicenseTypes()
    {
        var configured = _moduleConfig.GetValue("LicensingUpdates", "AllowedLicenseTypes");
        var raw = string.IsNullOrWhiteSpace(configured) ? "E5,EOP2+SOP2,F3,F3+EOP1" : configured;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<LicensePreviewResult> PreviewCsvAsync(Stream csvStream, string fileName, string licenseType, string performedBy, string ip)
    {
        var allowedTypes = GetAllowedLicenseTypes();
        if (!allowedTypes.Contains(licenseType, StringComparer.OrdinalIgnoreCase))
            return new(false, $"Invalid license type '{licenseType}'. Allowed: {string.Join(", ", allowedTypes)}", licenseType, []);

        var creds = await _moduleCredentials.GetCredentialsAsync("LicensingUpdates", "licensing preview");
        if (creds == null)
            return new(false, "AD credentials unavailable. Configure the module's Delinea Secret ID.", licenseType, []);

        var identities = await ParseCsvAsync(csvStream);
        if (identities.Count == 0)
            return new(false, "CSV contains no valid user rows.", licenseType, []);

        _operationTrace.Step("LicensingPreviewStarted", details: new Dictionary<string, object?>
        {
            ["fileName"] = fileName,
            ["licenseType"] = licenseType,
            ["rowCount"] = identities.Count
        });

        var rows = new List<LicensePreviewRow>();

        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
            return new(false, "AD service is busy. Please try again.", licenseType, []);

        try
        {
            rows = await Task.Run(() => ResolveUsers(identities, licenseType, creds.Value));
        }
        finally
        {
            _adThrottle.Release();
        }

        _operationTrace.Step("LicensingPreviewCompleted", details: new Dictionary<string, object?>
        {
            ["resolved"] = rows.Count(r => r.Principal != null),
            ["errors"] = rows.Count(r => r.Error != null)
        });

        return new(true, null, licenseType, rows);
    }

    /// <param name="actingUser">
    /// REQUIRED and not defaulted: the servicer decision needs a real principal, and a default
    /// would silently make every caller that forgot it unable to service - or invite an ambient
    /// lookup, which attributes a bypass to whoever is on the thread. Null refuses.
    /// </param>
    public async Task<LicenseBulkResult> ApplyCsvAsync(LicensePreviewResult preview, string ticket, string performedBy, string ip, ClaimsPrincipal? actingUser)
    {
        var creds = await _moduleCredentials.GetCredentialsAsync("LicensingUpdates", "licensing apply");
        if (creds == null)
            return new(false, "AD credentials unavailable.", 0, 0, 0, 0, 0, []);

        var applyRows = preview.Rows.Where(r => r.Principal != null && r.Error == null).ToList();
        if (applyRows.Count == 0)
            return new(true, "No valid rows to apply.", preview.Rows.Count, 0, 0, 0, preview.Rows.Count(r => r.Error != null), []);

        using var scope = _operationTrace.BeginOperation(
            module: "LicensingUpdates",
            action: "BulkApply",
            actor: performedBy,
            ipAddress: ip,
            target: $"{applyRows.Count} users",
            ticket: ticket);

        var (protectionResults, servicedNotes, servicedNotesByUpn) =
            await EvaluateProtectionAsync(applyRows, actingUser);

        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
            return new(false, "AD service is busy.", 0, 0, 0, 0, 0, []);

        List<LicenseApplyRow> results;
        try
        {
            results = await Task.Run(() => ApplyChanges(applyRows, preview.LicenseType, creds.Value, protectionResults, servicedNotes));
        }
        finally
        {
            _adThrottle.Release();
        }

        var succeeded = results.Count(r => r.Status == "Success");
        var unchanged = results.Count(r => r.Status == "Unchanged");
        var protectedCount = results.Count(r => r.Status == "Protected");
        var checkFailedCount = results.Count(r => r.Status == "CheckFailed");
        var failed = results.Count(r => r.Status == "Error");

        foreach (var row in results)
        {
            try
            {
                // A serviced override succeeds, so it cannot ride errorDetail - that field is
                // written as null on success and the record of who permitted it would vanish.
                var servicedNote = row.UserPrincipalName != null
                    ? servicedNotesByUpn.GetValueOrDefault(row.UserPrincipalName)
                    : null;

                _audit.LogModuleAction(performedBy, ip, "LicensingUpdates_Update",
                    "LicensingUpdates",
                    $"{row.UserPrincipalName ?? row.InputIdentity} [{row.Status}] {row.PreviousValue ?? "(empty)"} -> {row.NewValue ?? "(empty)"}",
                    row.Status == "Success" || row.Status == "Unchanged",
                    ticketNumber: ticket,
                    errorDetail: row.Error,
                    extra: ProtectedPrincipalServicing.Extra(servicedNote));
            }
            catch (Exception auditEx)
            {
                _logger.LogError(auditEx, "Audit write failed for {User} - AD writes already committed", row.InputIdentity);
            }
        }

        var overallSuccess = checkFailedCount == 0 && failed == 0;
        scope.Complete(overallSuccess, !overallSuccess ? $"{checkFailedCount} check failures, {failed} errors" : null);

        return new(overallSuccess,
            checkFailedCount > 0 ? "Protection system unavailable for some targets." : (failed > 0 ? $"{failed} row(s) failed." : null),
            preview.Rows.Count, succeeded, unchanged, protectedCount + checkFailedCount, failed, results);
    }

    private List<LicensePreviewRow> ResolveUsers(List<string> identities, string licenseType, (string username, string password, string domain) creds)
    {
        var rows = new List<LicensePreviewRow>();

        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        var credential = CreateCredential(creds.username, creds.password, creds.domain);

        foreach (var identity in identities)
        {
            try
            {
                var escaped = ProtectedPrincipalService.EscapeLdapFilter(identity);
                var filter = $"(|(userPrincipalName={escaped})(mail={escaped})(sAMAccountName={escaped}))";

                ps.AddCommand("Get-ADUser")
                  .AddParameter("LDAPFilter", filter)
                  .AddParameter("Properties", new[] { "DisplayName", "UserPrincipalName", "SamAccountName", "mail", "DistinguishedName", "ObjectGUID", TargetAttribute })
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                var users = ps.Invoke();
                ps.Commands.Clear();

                if (users.Count == 0)
                {
                    rows.Add(new(identity, null, null, licenseType, "User not found."));
                    continue;
                }
                if (users.Count > 1)
                {
                    rows.Add(new(identity, null, null, licenseType, $"Ambiguous: matches {users.Count} users."));
                    continue;
                }

                var adUser = users[0];
                var principal = new ResolvedDirectoryPrincipal(
                    Source: "OnPremAD",
                    DisplayName: adUser.Properties["DisplayName"]?.Value?.ToString() ?? adUser.Properties["Name"]?.Value?.ToString() ?? identity,
                    UserPrincipalName: adUser.Properties["UserPrincipalName"]?.Value?.ToString() ?? "",
                    SamAccountName: adUser.Properties["SamAccountName"]?.Value?.ToString(),
                    PrimarySmtpAddress: adUser.Properties["mail"]?.Value?.ToString(),
                    DistinguishedName: adUser.Properties["DistinguishedName"]?.Value?.ToString(),
                    ObjectGuid: adUser.Properties["ObjectGUID"]?.Value?.ToString(),
                    EntraObjectId: null);

                var currentValue = adUser.Properties[TargetAttribute]?.Value?.ToString();
                rows.Add(new(identity, principal, currentValue, licenseType, null));
            }
            catch (Exception ex)
            {
                ps.Commands.Clear();
                rows.Add(new(identity, null, null, licenseType, ex.Message));
                _logger.LogWarning(ex, "Failed to resolve {Identity} for licensing preview", identity);
            }
        }

        return rows;
    }

    private List<LicenseApplyRow> ApplyChanges(
        List<LicensePreviewRow> rows, string licenseType,
        (string username, string password, string domain) creds,
        Dictionary<string, ProtectedPrincipalResult> protectionResults,
        Dictionary<string, string> servicedNotes)
    {
        var results = new List<LicenseApplyRow>();

        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        var credential = CreateCredential(creds.username, creds.password, creds.domain);

        foreach (var row in rows)
        {
            var principal = row.Principal!;
            try
            {
                if (!protectionResults.TryGetValue(PrincipalKey(principal), out var protectionResult))
                    protectionResult = ProtectedPrincipalResult.Failed("Protected-principal check result is unavailable.");
                if (protectionResult.CheckFailed)
                {
                    results.Add(new(row.InputIdentity, principal.UserPrincipalName, "CheckFailed", row.CurrentValue, null, protectionResult.Reason));
                    continue;
                }
                // An authorised servicer proceeds; the note was decided on the request thread above.
                var servicedNote = servicedNotes.GetValueOrDefault(PrincipalKey(principal));
                if (protectionResult.IsProtected && servicedNote == null)
                {
                    results.Add(new(row.InputIdentity, principal.UserPrincipalName, "Protected", row.CurrentValue, null, "Protected principal."));
                    continue;
                }

                ps.AddCommand("Get-ADUser")
                  .AddParameter("Identity", principal.DistinguishedName)
                  .AddParameter("Properties", new[] { TargetAttribute, "ObjectGUID" })
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                var reRead = ps.Invoke();
                ps.Commands.Clear();

                if (reRead.Count == 0)
                {
                    results.Add(new(row.InputIdentity, principal.UserPrincipalName, "Error", null, null, "Object no longer exists."));
                    continue;
                }

                if (!string.IsNullOrEmpty(principal.ObjectGuid))
                {
                    var freshGuid = reRead[0].Properties["ObjectGUID"]?.Value?.ToString();
                    if (!string.Equals(freshGuid, principal.ObjectGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new(row.InputIdentity, principal.UserPrincipalName, "Error", null, null, $"Bound-object mismatch: expected {principal.ObjectGuid}, found {freshGuid}."));
                        continue;
                    }
                }

                var freshValue = reRead[0].Properties[TargetAttribute]?.Value?.ToString();

                if (string.Equals(freshValue, licenseType, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new(row.InputIdentity, principal.UserPrincipalName, "Unchanged", freshValue, licenseType, null));
                    continue;
                }

                ps.AddCommand("Set-ADUser")
                  .AddParameter("Identity", principal.DistinguishedName)
                  .AddParameter("Replace", new Dictionary<string, object> { [TargetAttribute] = licenseType })
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                ps.Commands.Clear();

                if (ps.HadErrors)
                {
                    var err = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Set-ADUser failed.";
                    ps.Streams.Error.Clear();
                    results.Add(new(row.InputIdentity, principal.UserPrincipalName, "Error", freshValue, null, err));
                }
                else
                {
                    results.Add(new(row.InputIdentity, principal.UserPrincipalName, "Success", freshValue, licenseType, null));
                }
            }
            catch (Exception ex)
            {
                ps.Commands.Clear();
                results.Add(new(row.InputIdentity, principal.UserPrincipalName, "Error", row.CurrentValue, null, ex.Message));
                _logger.LogWarning(ex, "Failed to apply license for {Identity}", row.InputIdentity);
            }
        }

        return results;
    }

    /// <summary>
    /// Runs the protection check and the servicer decision for every row, before any AD write.
    /// </summary>
    /// <remarks>
    /// The servicer decision is made HERE, on the request thread beside the protection check, and
    /// never inside <c>ApplyChanges</c>: that runs on a worker thread via <c>Task.Run</c>, where
    /// the acting principal is not on the ambient context and reading one would attribute the
    /// override to whoever owns the thread. Decided per row, so a batch can service one protected
    /// user and still refuse another.
    ///
    /// Two keyed views come back deliberately. The apply loop keys on <c>PrincipalKey</c>, which
    /// prefers ObjectGuid; the audit loop only sees <c>LicenseApplyRow</c>, which carries the UPN.
    /// Looking the notes up by UPN against the PrincipalKey view would silently find nothing and
    /// drop the override from the audit record - the one place it must never be missing.
    ///
    /// Internal as a TEST SEAM (the project already exposes internals to ExchangeAdminWeb.Tests):
    /// ApplyCsvAsync returns at the credential gate before reaching this, so the servicer decision
    /// is not otherwise reachable without a live directory. It is a decision with no side effects.
    /// </remarks>
    internal async Task<(
        Dictionary<string, ProtectedPrincipalResult> protectionResults,
        Dictionary<string, string> servicedNotes,
        Dictionary<string, string> servicedNotesByUpn)> EvaluateProtectionAsync(
        List<LicensePreviewRow> applyRows,
        ClaimsPrincipal? actingUser)
    {
        var protectionResults = new Dictionary<string, ProtectedPrincipalResult>(StringComparer.OrdinalIgnoreCase);
        var servicedNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var servicedNotesByUpn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in applyRows)
        {
            var key = PrincipalKey(row.Principal!);
            var check = await _protectedPrincipalService.CheckAsync(row.Principal!);
            protectionResults[key] = check;

            // Protection is evaluated first and never weakened - this only decides whether THIS
            // operator may act on a user already known to be protected. A failed check is not
            // serviceable: it does not know whether the user is protected, so there is no refusal
            // to override. A null actingUser refuses.
            if (check.IsProtected && !check.CheckFailed)
            {
                var note = ProtectedPrincipalServicing.NoteFor(
                    _servicers, actingUser, ServicerModuleId, check.MatchedRules,
                    qualifier: row.Principal!.UserPrincipalName);

                if (note != null)
                {
                    servicedNotes[key] = note;
                    servicedNotesByUpn[row.Principal!.UserPrincipalName] = note;
                }
            }
        }

        return (protectionResults, servicedNotes, servicedNotesByUpn);
    }

    private static string PrincipalKey(ResolvedDirectoryPrincipal principal) =>
        principal.ObjectGuid ?? principal.DistinguishedName ?? principal.UserPrincipalName;

    internal static List<string> ParseCsv(Stream csvStream)
    {
        var identities = new List<string>();
        using var reader = new StreamReader(csvStream);
        using var csv = CreateCsvReader(reader);
        var isFirstRow = true;

        while (csv.Read())
        {
            var firstCol = csv.GetField(0)?.Trim();
            if (string.IsNullOrWhiteSpace(firstCol)) continue;

            if (isFirstRow)
            {
                isFirstRow = false;
                if (HeaderIndicators.Any(h => string.Equals(firstCol, h, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            identities.Add(firstCol);
        }

        return identities;
    }

    internal static async Task<List<string>> ParseCsvAsync(Stream csvStream)
    {
        var identities = new List<string>();
        using var reader = new StreamReader(csvStream);
        using var csv = CreateCsvReader(reader);
        var isFirstRow = true;

        while (await csv.ReadAsync())
        {
            var firstCol = csv.GetField(0)?.Trim();
            if (string.IsNullOrWhiteSpace(firstCol)) continue;

            if (isFirstRow)
            {
                isFirstRow = false;
                if (HeaderIndicators.Any(h => string.Equals(firstCol, h, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            identities.Add(firstCol);
        }

        return identities;
    }

    private static CsvReader CreateCsvReader(TextReader reader)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            HeaderValidated = null,
            MissingFieldFound = null,
            BadDataFound = null
        };
        return new CsvReader(reader, config);
    }

    private static PSCredential CreateCredential(string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username : $"{domain}\\{username}";
        var securePassword = new System.Security.SecureString();
        foreach (var c in password) securePassword.AppendChar(c);
        return new PSCredential(fullUsername, securePassword);
    }
}
