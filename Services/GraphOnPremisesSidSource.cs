using System.Net;
using System.Security.Principal;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

/// <summary>How a read of a synced recipient's on-premises SID ended (docs/ProtectedPrincipalIdentifiers-Plan.md C.2).</summary>
public enum OnPremisesSidOutcome
{
    /// <summary>The source answered with a SID: <see cref="OnPremisesSidAnswer.Sid"/>, canonical.</summary>
    Reported,

    /// <summary>The source answered, and reports no on-premises SID for the recipient. Only this falls to the address path.</summary>
    NoneReported,

    /// <summary>
    /// The source could not be asked, or its answer could not be read: no setting, a credential that could not be read,
    /// a throttled or failed request, an answer that is not a SID. Never "no SID": the check refuses (C.2 step 6).
    /// </summary>
    NotAnswered,
}

/// <summary>One read of a synced recipient's on-premises SID.</summary>
/// <param name="Reason">For <see cref="OnPremisesSidOutcome.NotAnswered"/>, why - for the log; never a secret.</param>
/// <param name="Synced">
/// For <see cref="OnPremisesSidOutcome.NoneReported"/>, what Graph says of the object's sync from on-premises: true when
/// <c>onPremisesSyncEnabled</c> is true; false when it is false or null, which is how Graph reports a cloud-only object;
/// null when the flag was absent or not a boolean, so the question is unanswered.
/// </param>
public sealed record OnPremisesSidAnswer(OnPremisesSidOutcome Outcome, string? Sid = null, string? Reason = null, bool? Synced = null)
{
    public static OnPremisesSidAnswer NotAnswered(string reason) => new(OnPremisesSidOutcome.NotAnswered, Reason: reason);
}

/// <summary>
/// Where the protected-principal check reads the objectSid of the on-premises account a synced Exchange recipient is
/// synced from (docs/ProtectedPrincipalIdentifiers-Plan.md C.2 step 3). Exchange Online does not report it: its
/// <c>Get-User</c> <c>Sid</c> is a cloud-generated SID (plan I.1, the owner's live read).
/// </summary>
public interface IOnPremisesSidSource
{
    /// <summary>The source's name, for the timing line.</summary>
    string Name { get; }

    /// <summary>
    /// The on-premises SID of the directory object <paramref name="externalDirectoryObjectId"/> (Exchange's
    /// ExternalDirectoryObjectId, the Entra object id). Never throws: a read that could not be made is
    /// <see cref="OnPremisesSidOutcome.NotAnswered"/>.
    /// </summary>
    Task<OnPremisesSidAnswer> ReadAsync(string externalDirectoryObjectId);
}

/// <summary>
/// Reads <c>onPremisesSecurityIdentifier</c> from Microsoft Graph (<c>GET /users/{id}</c>) through the protection
/// service's own app-wide credential: the Admin Settings field "Graph Directory Read Credential (Delinea Secret ID)"
/// (<see cref="ProtectedPrincipalService.GraphDirectoryReadSecretConfigKey"/>), whose secret holds an app registration with
/// the application permission <c>User.Read.All</c>. Owner ruling 2026-11-03 (<c>.agents/decisions.md</c>): the field is its
/// own and empty until an admin fills it - nothing is read from any module's Graph setting.
/// </summary>
/// <remarks>
/// <para>
/// Fail closed: no setting, a secret that cannot be read or lacks a field, a token that cannot be had, any status but 200,
/// and a value that is not a SID are each <see cref="OnPremisesSidOutcome.NotAnswered"/>. Only a 200 whose
/// <c>onPremisesSecurityIdentifier</c> is absent or null is <see cref="OnPremisesSidOutcome.NoneReported"/>.
/// </para>
/// <para>
/// The Graph client - its token with it - is kept for the Secret ID it was built from, as each Graph client keeps its
/// token until five minutes before it expires: a check does not fetch the secret and a token every time. A changed
/// Secret ID builds a new one; a 401 or 403, or a token that could not be had, drops it, so a rotated secret is read
/// again on the next check. Nothing from Secret Server is logged (Constitution), only the Secret ID's absence.
/// </para>
/// </remarks>
public sealed class GraphOnPremisesSidSource : IOnPremisesSidSource
{
    private readonly ModuleConfigService _moduleConfig;
    private readonly DelineaService? _delinea;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<GraphOnPremisesSidSource> _logger;
    private readonly object _lock = new();
    private (int SecretId, GraphTokenClient Client)? _client;
    private int _firstReadLogged;

    public GraphOnPremisesSidSource(ModuleConfigService moduleConfig, DelineaService delinea, IHttpClientFactory httpClientFactory,
        ILogger<GraphOnPremisesSidSource> logger)
    {
        _moduleConfig = moduleConfig;
        _delinea = delinea;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Test seam: the source over a stand-in secret store and Graph. Production builds both from DI.</summary>
    internal GraphOnPremisesSidSource(ModuleConfigService moduleConfig, Func<int, Task<Dictionary<string, string>?>> secretFields,
        Func<HttpClient> httpClient, ILogger<GraphOnPremisesSidSource> logger)
    {
        _moduleConfig = moduleConfig;
        _logger = logger;
        SecretFieldsFetch = secretFields;
        HttpClientForTests = httpClient;
    }

    internal Func<int, Task<Dictionary<string, string>?>>? SecretFieldsFetch { get; }
    internal Func<HttpClient>? HttpClientForTests { get; }

    public string Name => "Graph";

    public async Task<OnPremisesSidAnswer> ReadAsync(string externalDirectoryObjectId)
    {
        if (!Guid.TryParse(externalDirectoryObjectId?.Trim(), out var objectId) || objectId == Guid.Empty)
            return NotAnswered("Exchange reported no usable ExternalDirectoryObjectId for the recipient");

        var (client, secretId, why) = await ClientAsync();
        if (client is null)
            return NotAnswered(why!);

        try
        {
            var (doc, status) = await client.GetWithStatusAsync(
                $"/users/{objectId:D}?$select=onPremisesSecurityIdentifier,onPremisesSyncEnabled");
            if (doc is null)
            {
                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    Drop(secretId);
                return NotAnswered($"Graph answered {(int)status} for the recipient's directory object");
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return NotAnswered("Graph's answer for the recipient's directory object could not be read");

                if (!doc.RootElement.TryGetProperty("onPremisesSecurityIdentifier", out var value)
                    || value.ValueKind == JsonValueKind.Null)
                {
                    return NoneReported(doc.RootElement);
                }

                if (value.ValueKind != JsonValueKind.String)
                    return NotAnswered("Graph's onPremisesSecurityIdentifier for the recipient is not text");

                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text))
                    return NoneReported(doc.RootElement);

                var sid = ProtectedPrincipalIdEntry.CanonicalSid(text);
                if (sid is null)
                    return NotAnswered("Graph's onPremisesSecurityIdentifier for the recipient is not a SID");

                LogFirstRead(sid);
                return new OnPremisesSidAnswer(OnPremisesSidOutcome.Reported, sid);
            }
        }
        catch (Exception ex)
        {
            // A token that could not be had throws here (GraphTokenClient), as does a request that failed outright.
            Drop(secretId);
            _logger.LogWarning(ex, "The Graph read of an on-premises SID failed");
            return NotAnswered("the Graph read failed");
        }
    }

    private OnPremisesSidAnswer NotAnswered(string reason) => OnPremisesSidAnswer.NotAnswered(reason);

    private static OnPremisesSidAnswer NoneReported(JsonElement user) =>
        new(OnPremisesSidOutcome.NoneReported, Synced: !user.TryGetProperty("onPremisesSyncEnabled", out var flag)
            ? null
            : flag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False or JsonValueKind.Null => false,
                _ => null,
            });

    /// <summary>
    /// The deploy's check list confirms the attribute is populated in this tenant from this one line: the first SID Graph
    /// reports in this process, by its domain part only - never the account's RID.
    /// </summary>
    private void LogFirstRead(string sid)
    {
        if (Interlocked.Exchange(ref _firstReadLogged, 1) != 0)
            return;

        string domain;
        try
        {
            domain = new SecurityIdentifier(sid).AccountDomainSid?.Value ?? "(not a domain account SID)";
        }
        catch (ArgumentException)
        {
            domain = "(unreadable)";
        }
        _logger.LogInformation(
            "Graph reported an on-premises SID for the first time in this process: onPremisesSecurityIdentifier is populated (domain {DomainSid})",
            domain);
    }

    private async Task<(GraphTokenClient? Client, int SecretId, string? Why)> ClientAsync()
    {
        if (!_moduleConfig.TryGetModuleConfig(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, out var values))
        {
            _logger.LogWarning("The Graph Directory Read Credential (Delinea Secret ID) setting could not be read");
            return (null, 0, "the Graph Directory Read Credential setting could not be read");
        }

        var stored = values.GetValueOrDefault(ProtectedPrincipalService.GraphDirectoryReadSecretConfigKey);
        if (!int.TryParse(stored, out var secretId) || secretId <= 0)
        {
            _logger.LogWarning(
                "The Graph Directory Read Credential (Delinea Secret ID) is not set on Admin Settings (Protected Principals): "
                + "the on-premises SID of a synced recipient cannot be read, so every synced write target is refused");
            return (null, 0, "the Graph Directory Read Credential (Delinea Secret ID) is not set");
        }

        lock (_lock)
        {
            if (_client is { } kept && kept.SecretId == secretId)
                return (kept.Client, secretId, null);
        }

        Dictionary<string, string>? fields;
        try
        {
            fields = SecretFieldsFetch is { } fetch ? await fetch(secretId) : await _delinea!.GetSecretFieldsAsync(secretId);
        }
        catch (Exception)
        {
            fields = null;
        }

        if (fields is null)
        {
            // No secret detail in the message: the Constitution forbids logging PAM responses.
            _logger.LogError("The Graph Directory Read Credential (Secret ID {SecretId}) could not be read from Secret Server", secretId);
            return (null, secretId, "the Graph Directory Read Credential could not be read from Secret Server");
        }

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogError(
                "The Graph Directory Read Credential (Secret ID {SecretId}) is missing one of Tenant ID, Application ID or Client Secret", secretId);
            return (null, secretId, "the Graph Directory Read Credential's secret is missing a field");
        }

        var client = new GraphTokenClient(tenantId, clientId, clientSecret,
            HttpClientForTests is { } http ? http() : _httpClientFactory!.CreateClient("MicrosoftGraph"));
        lock (_lock)
        {
            _client = (secretId, client);
        }
        return (client, secretId, null);
    }

    private void Drop(int secretId)
    {
        lock (_lock)
        {
            if (_client is { } kept && kept.SecretId == secretId)
                _client = null;
        }
    }
}
