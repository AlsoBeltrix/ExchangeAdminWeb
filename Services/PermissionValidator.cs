using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography.X509Certificates;

namespace ExchangeAdminWeb.Services;

public class PermissionValidator
{
    private readonly HashSet<string> _excludedUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PermissionValidator> _logger;
    private readonly IConfiguration _config;
    private readonly string[] _configuredExclusions;
    private readonly bool _preventSelfGrant;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    private bool _initialized = false;
    private bool _initFailed = false;
    private DateTime _lastRefresh = DateTime.MinValue;

    public PermissionValidator(IConfiguration config, ILogger<PermissionValidator> logger)
    {
        _logger = logger;
        _config = config;
        _configuredExclusions = config.GetSection("Security:ExcludedUsers").Get<string[]>() ?? Array.Empty<string>();
        _preventSelfGrant = bool.Parse(config["Security:PreventSelfGrant"] ?? "true");
    }

    public async Task<bool> IsUserExcludedAsync(string userIdentity)
    {
        await EnsureInitializedAsync();

        if (_excludedUsers.Contains(userIdentity))
            return true;

        return _excludedUsers.Any(excluded => IdentitiesMatch(excluded, userIdentity));
    }

    public async Task<string?> ValidateTargetMailboxAsync(string targetMailbox)
    {
        await EnsureInitializedAsync();

        if (_initFailed)
        {
            _logger.LogWarning("Blocking operation on {Target} — protected-user list failed to load", targetMailbox);
            return "Access denied: Protected-user list is unavailable. Contact your administrator.";
        }

        if (await IsUserExcludedAsync(targetMailbox))
        {
            _logger.LogWarning("Attempted operation on excluded user: {Target}", targetMailbox);
            return $"Access denied: {targetMailbox} is protected and cannot be modified through this interface.";
        }

        return null;
    }

    public string? ValidateSelfGrant(string currentUser, string affectedUser)
    {
        if (!_preventSelfGrant)
            return null;

        if (IdentitiesMatch(currentUser, affectedUser))
        {
            _logger.LogWarning("User {User} attempted to grant permissions to themselves ({Affected})", currentUser, affectedUser);
            return "Access denied: You cannot grant permissions to yourself.";
        }

        return null;
    }

    public static bool IdentitiesMatch(string identity1, string identity2)
    {
        var names1 = GetNormalizedNames(identity1);
        var names2 = GetNormalizedNames(identity2);
        return names1.Overlaps(names2);
    }

    private static HashSet<string> GetNormalizedNames(string identity)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var username = ExtractUsername(identity);
        names.Add(username);
        names.Add(username.Replace(".", ""));
        if (identity.Contains('@'))
            names.Add(identity.Trim().ToLowerInvariant());
        return names;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized && DateTime.UtcNow - _lastRefresh < CacheLifetime) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized && DateTime.UtcNow - _lastRefresh < CacheLifetime) return;

            _logger.LogInformation("Initializing permission validator with {Count} configured exclusions", _configuredExclusions.Length);

            _excludedUsers.Clear();

            foreach (var entry in _configuredExclusions)
            {
                var trimmed = entry.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                _excludedUsers.Add(trimmed);

                var members = await TryExpandGroupAsync(trimmed);
                foreach (var member in members)
                {
                    _excludedUsers.Add(member);
                    _logger.LogDebug("Excluded (from group {Group}): {Member}", trimmed, member);
                }
            }

            _initFailed = false;
            _initialized = true;
            _lastRefresh = DateTime.UtcNow;
            _logger.LogInformation("Permission validator initialized with {Total} total excluded identities", _excludedUsers.Count);
        }
        catch (Exception ex)
        {
            _excludedUsers.Clear();
            _initFailed = true;
            _logger.LogError(ex, "Failed to initialize permission validator — all operations on protected targets will be blocked until resolved.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<List<string>> TryExpandGroupAsync(string identity)
    {
        var members = new List<string>();

        await Task.Run(() =>
        {
            using var runspace = RunspaceFactory.CreateRunspace();
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            if (!ConnectToExchange(ps))
                throw new InvalidOperationException($"Cannot connect to Exchange Online to expand group '{identity}'");

            try
            {
                ps.AddCommand("Get-Recipient")
                  .AddParameter("Identity", identity)
                  .AddParameter("ErrorAction", "Stop");

                var recipients = Invoke(ps);
                if (recipients.Count == 0)
                    return;

                var recipient = recipients[0];
                var recipientType = recipient.Properties["RecipientTypeDetails"]?.Value?.ToString();

                if (recipientType?.Contains("Group", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogInformation("Expanding group: {Group} (type: {Type})", identity, recipientType);

                    ps.AddCommand("Get-DistributionGroupMember")
                      .AddParameter("Identity", identity)
                      .AddParameter("ResultSize", "Unlimited")
                      .AddParameter("ErrorAction", "Stop");

                    var groupMembers = Invoke(ps);

                    foreach (var member in groupMembers)
                    {
                        var email = member.Properties["PrimarySmtpAddress"]?.Value?.ToString();
                        var upn = member.Properties["UserPrincipalName"]?.Value?.ToString();
                        var sam = member.Properties["SamAccountName"]?.Value?.ToString();

                        if (!string.IsNullOrWhiteSpace(email)) members.Add(email);
                        if (!string.IsNullOrWhiteSpace(upn) && upn != email) members.Add(upn);
                        if (!string.IsNullOrWhiteSpace(sam)) members.Add(sam);
                    }

                    _logger.LogInformation("Expanded group {Group} to {Count} members", identity, members.Count);
                }
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    ps.AddCommand("Disconnect-ExchangeOnline").AddParameter("Confirm", false);
                    ps.Invoke();
                }
                catch { /* best effort */ }
            }
        });

        return members;
    }

    private bool ConnectToExchange(PowerShell ps)
    {
        try
        {
            var appId = _config["ExchangeOnline:AppId"];
            var org = _config["ExchangeOnline:Organization"];
            var certSubject = _config["ExchangeOnline:CertificateSubject"] ?? "CN=EXO-Automation";

            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(org))
            {
                _logger.LogWarning("Exchange Online not configured, cannot expand groups");
                return false;
            }

            var cert = FindCertificate(certSubject);
            if (cert is null)
            {
                _logger.LogWarning("Certificate {Subject} not found, cannot expand groups", certSubject);
                return false;
            }

            ps.AddCommand("Import-Module")
              .AddParameter("Name", "ExchangeOnlineManagement")
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);

            ps.AddCommand("Connect-ExchangeOnline")
              .AddParameter("AppId", appId)
              .AddParameter("CertificateThumbprint", cert.Thumbprint)
              .AddParameter("Organization", org)
              .AddParameter("ShowBanner", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to Exchange Online for group expansion");
            return false;
        }
    }

    private X509Certificate2? FindCertificate(string certSubject)
    {
        var locations = new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser };

        foreach (var location in locations)
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);

            var cert = store.Certificates
                .Find(X509FindType.FindBySubjectDistinguishedName, certSubject, validOnly: false)
                .OfType<X509Certificate2>()
                .Where(c => c.HasPrivateKey)
                .OrderByDescending(c => c.NotBefore)
                .FirstOrDefault();

            if (cert is not null) return cert;
        }

        return null;
    }

    private static Collection<PSObject> Invoke(PowerShell ps)
    {
        var result = ps.Invoke();
        ps.Commands.Clear();
        return result;
    }

    private static string ExtractUsername(string identity)
    {
        if (identity.Contains('\\'))
            return identity.Split('\\')[1];
        if (identity.Contains('@'))
            return identity.Split('@')[0];
        return identity;
    }
}
