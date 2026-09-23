using System.Text.Json;

namespace ExchangeAdminWeb.Services;

/// <summary>Why a Cloud Password Reset step refused. These names reach the audit log.</summary>
/// <remarks>
/// The exact string set is a published interface: these events go to Splunk and somebody will
/// alert on them (docs/CloudPasswordReset-Plan.md, "Audit fields, and Splunk"). Renaming a member
/// breaks a dashboard; adding one means adding it to the plan's table in the same commit.
/// </remarks>
public enum CloudPasswordResetRefusal
{
    None = 0,
    SyncedAccount,
    GuestAccount,
    DestinationNoEmployeeId,
    DestinationNoMatch,
    DestinationAmbiguous,
    DestinationNoMailbox,
    DestinationLookupFailed,
    NotificationsDisabled,
    TicketInvalid,
    TicketValidatorUnavailable,
    ProtectedPrincipal,
    ProtectionCheckFailed,
    PermissionDenied,
    GraphReadFailed,
    PasswordPolicyRejected,
    GeneratorFailed,

    /// <summary>
    /// The write was ISSUED and its outcome is unknown. Not a refusal: the password may well have
    /// changed.
    /// </summary>
    /// <remarks>
    /// A transport failure after the PATCH left the client - a timeout, a dropped connection -
    /// means Graph may have applied it and the response never arrived. Reporting that as a
    /// refusal would tell the operator no change was made, which is a claim nothing supports, and
    /// would audit a write that may exist as one that never happened (review finding cpr-8).
    /// </remarks>
    WriteIndeterminate,
}

/// <summary>What the preflight read about the account an operator named.</summary>
public sealed record CloudPasswordTarget(
    string ObjectId,
    string UserPrincipalName,
    string DisplayName,
    bool AccountEnabled,
    bool CloudOnly,
    string? EmployeeId,
    IReadOnlyList<string> DirectoryRoles,
    bool DirectoryRolesReadFailed);

/// <summary>Where a reset's password would be sent, or why it cannot be.</summary>
/// <remarks>
/// <see cref="Address"/> is non-null only when <see cref="Refusal"/> is
/// <see cref="CloudPasswordResetRefusal.None"/>. The two are never both set and never both empty:
/// a destination either resolved or it did not, and there is no third state in which the caller
/// should guess.
/// </remarks>
public sealed record CloudPasswordDestination(
    string? Address,
    string? EmployeeId,
    string? OwnerDisplayName,
    CloudPasswordResetRefusal Refusal)
{
    public bool Resolved => Refusal == CloudPasswordResetRefusal.None && !string.IsNullOrWhiteSpace(Address);
}

/// <summary>The outcome of an attempted reset.</summary>
/// <remarks>
/// <see cref="Password"/> is populated ONLY for a caller that will immediately mail it or reveal
/// it, and is never logged, audited or traced (Constitution, Credential Isolation; plan AC7).
/// </remarks>
public sealed record CloudPasswordResetOutcome(
    bool Success,
    string? Password,
    CloudPasswordResetRefusal Refusal,
    string Message);

/// <summary>
/// Entra ID cloud-only account password reset (docs/CloudPasswordReset-Plan.md).
/// </summary>
/// <remarks>
/// App-only Graph through this module's own Delinea secret, following
/// <see cref="MfaResetService"/>'s bootstrap shape. The write is
/// <c>PATCH /users/{id}</c> with <c>passwordProfile</c>; the documented
/// <c>resetPassword</c> action is delegated-only and structurally unreachable from an app-only
/// caller, which is recorded at length in the plan so it is not "discovered" again.
///
/// **This service has no destination parameter.** The recipient is derived from the target
/// account's employee ID, so there is nothing for a caller to pass and therefore nothing for a
/// future caller to pass wrongly. Owner instruction 2026-09-22.
/// </remarks>
public class CloudPasswordResetService
{
    private readonly ILogger<CloudPasswordResetService> _logger;
    private readonly ModuleConfigService _moduleConfig;
    private readonly DelineaService _delinea;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ADDirectorySearchService _directory;
    private readonly ITicketValidator _tickets;
    private readonly PasswordGenerator _generator;

    public const string ModuleId = "CloudPasswordReset";

    public CloudPasswordResetService(
        ILogger<CloudPasswordResetService> logger,
        ModuleConfigService moduleConfig,
        DelineaService delinea,
        IHttpClientFactory httpClientFactory,
        ADDirectorySearchService directory,
        ITicketValidator tickets,
        PasswordGenerator generator)
    {
        _logger = logger;
        _moduleConfig = moduleConfig;
        _delinea = delinea;
        _httpClientFactory = httpClientFactory;
        _directory = directory;
        _tickets = tickets;
        _generator = generator;
    }

    /// <summary>True when a Graph secret id is configured. Not a claim that it works.</summary>
    public bool IsAvailable
    {
        get
        {
            var secretIdStr = _moduleConfig.GetValue(ModuleId, "GraphDelineaSecretId");
            return int.TryParse(secretIdStr, out var id) && id > 0;
        }
    }

    private async Task<GraphTokenClient?> GetGraphClientAsync()
    {
        var secretIdStr = _moduleConfig.GetValue(ModuleId, "GraphDelineaSecretId");
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
        {
            _logger.LogWarning("CloudPasswordReset has no GraphDelineaSecretId configured");
            return null;
        }

        var fields = await _delinea.GetSecretFieldsAsync(secretId);
        if (fields == null)
        {
            // No secret detail in the message: the Constitution forbids logging PAM responses.
            _logger.LogError("CloudPasswordReset could not read its Graph secret from Secret Server");
            return null;
        }

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogError("CloudPasswordReset Graph secret is missing one of Tenant ID, Application ID or Client Secret");
            return null;
        }

        return new GraphTokenClient(tenantId, clientId, clientSecret, _httpClientFactory.CreateClient("MicrosoftGraph"));
    }

    /// <summary>
    /// Read the named account, and refuse here if it is out of scope.
    /// </summary>
    /// <remarks>
    /// Fail-closed on the sync flag, which is Known Failure Class 3 in this repo: a Graph read
    /// that FAILED must never read as "not synced". Only a definite <c>false</c> (or an absent
    /// property on an account Graph returned successfully, which is how Graph represents
    /// cloud-only) admits the target; an unreadable account refuses.
    /// </remarks>
    public async Task<(CloudPasswordTarget? Target, CloudPasswordResetRefusal Refusal, string Message)>
        ResolveTargetAsync(string userPrincipalName)
    {
        var client = await GetGraphClientAsync();
        if (client is null)
        {
            return (null, CloudPasswordResetRefusal.GraphReadFailed,
                "Cloud Password Reset is not configured, or its Graph credentials could not be read.");
        }

        const string select = "id,displayName,userPrincipalName,accountEnabled,onPremisesSyncEnabled,userType,employeeId";
        var (doc, status) = await client.GetWithStatusAsync(
            $"/users/{Uri.EscapeDataString(userPrincipalName)}?$select={select}");

        if (doc is null)
        {
            _logger.LogError("Graph read for {Upn} failed with {Status}", userPrincipalName, (int)status);
            return (null, CloudPasswordResetRefusal.GraphReadFailed,
                $"Could not read {userPrincipalName} from Entra ID ({(int)status}).");
        }

        using var _ = doc;
        var root = doc.RootElement;

        var syncState = ClassifySyncState(root);

        var userType = root.TryGetProperty("userType", out var typeProp) ? typeProp.GetString() : null;
        var employeeId = root.TryGetProperty("employeeId", out var empProp) ? empProp.GetString() : null;
        var objectId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
        var displayName = root.TryGetProperty("displayName", out var dnProp) ? dnProp.GetString() ?? "" : "";
        var upn = root.TryGetProperty("userPrincipalName", out var upnProp) ? upnProp.GetString() ?? userPrincipalName : userPrincipalName;
        var enabled = !root.TryGetProperty("accountEnabled", out var enProp) || enProp.ValueKind != JsonValueKind.False;

        _logger.LogDebug(
            "CloudPasswordReset preflight for {Upn}: id={ObjectId} syncState={SyncState} userType={UserType} employeeIdPresent={HasEmployeeId}",
            upn, objectId, syncState, userType, !string.IsNullOrWhiteSpace(employeeId));

        if (syncState == SyncState.Synced)
        {
            _logger.LogWarning("CloudPasswordReset refused {Upn}: account is synced from on-premises", upn);
            return (null, CloudPasswordResetRefusal.SyncedAccount,
                "This account is synced from on-premises Active Directory. Reset it there.");
        }

        if (syncState == SyncState.Unknown)
        {
            // Not "cloud-only by default". We asked for the property and did not get a value we
            // understand, so the question is unanswered - and an unanswered sync question on a
            // password reset is a refusal (plan AC1, Known Failure Class 3).
            _logger.LogError("CloudPasswordReset refused {Upn}: onPremisesSyncEnabled was absent or unreadable in the Graph response", upn);
            return (null, CloudPasswordResetRefusal.GraphReadFailed,
                "Entra ID did not report whether this account is synced from on-premises. The reset was refused rather than assume it is cloud-only.");
        }

        if (string.Equals(userType, "Guest", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("CloudPasswordReset refused {Upn}: guest account", upn);
            return (null, CloudPasswordResetRefusal.GuestAccount,
                "Guest accounts are out of scope for this module.");
        }

        var (roles, rolesReadFailed) = await ReadDirectoryRolesAsync(client, objectId);

        return (new CloudPasswordTarget(objectId, upn, displayName, enabled, CloudOnly: true, employeeId, roles, rolesReadFailed),
                CloudPasswordResetRefusal.None, "");
    }

    /// <summary>What the Graph response says about whether the target is mastered on-premises.</summary>
    internal enum SyncState
    {
        /// <summary>Definitely synced from on-premises. Out of scope.</summary>
        Synced,

        /// <summary>Definitely not synced. In scope.</summary>
        CloudOnly,

        /// <summary>The response did not answer the question. Refuse; do not assume either way.</summary>
        Unknown,
    }

    /// <summary>
    /// Read <c>onPremisesSyncEnabled</c> as three states rather than two.
    /// </summary>
    /// <remarks>
    /// **Graph never returns <c>false</c> here.** The property is <c>true</c> for a synced account
    /// and <c>null</c> for one that is not - so a literal reading of the plan's "absent a definite
    /// false, refuse" would refuse this module's entire population. The rule the plan is actually
    /// expressing is that an UNANSWERED question must not be read as a permissive answer, and the
    /// three states below are what that means against the real API:
    ///
    /// - present and <c>true</c>  -> Synced. Refuse: its password is mastered on-premises.
    /// - present and <c>null</c> or <c>false</c> -> CloudOnly. In scope.
    /// - absent, or any other kind -> Unknown. **Refuse.** The property was requested in the
    ///   <c>$select</c>, so its absence means the projection did not happen, not that the account
    ///   is cloud-only.
    ///
    /// The first implementation collapsed Unknown into CloudOnly, which would have admitted a
    /// synced account whenever Graph returned 200 without the property - review finding cpr-4,
    /// severity HIGH.
    /// </remarks>
    internal static SyncState ClassifySyncState(JsonElement root)
    {
        if (!root.TryGetProperty("onPremisesSyncEnabled", out var prop))
            return SyncState.Unknown;

        return prop.ValueKind switch
        {
            JsonValueKind.True => SyncState.Synced,
            JsonValueKind.False => SyncState.CloudOnly,
            JsonValueKind.Null => SyncState.CloudOnly,
            _ => SyncState.Unknown,
        };
    }

    /// <summary>
    /// Active directory-role display names for the preflight panel. Display only, gates nothing.
    /// </summary>
    /// <remarks>
    /// Returns an empty list when the read fails, and says so in the log. That is safe precisely
    /// BECAUSE nothing is gated on it: no permission, refusal or write consults this. If a future
    /// change makes a decision from it, this fallback becomes a fail-open and must change with it.
    ///
    /// PIM trap, recorded in the plan: <c>transitiveMemberOf</c> returns ACTIVE assignments only,
    /// so a PIM-eligible admin who has not activated reads as holding no roles. That costs
    /// accuracy on a panel, nothing more.
    /// </remarks>
    private async Task<(IReadOnlyList<string> Roles, bool ReadFailed)> ReadDirectoryRolesAsync(GraphTokenClient client, string objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId)) return ([], false);

        var (doc, status) = await client.GetWithStatusAsync(
            $"/users/{Uri.EscapeDataString(objectId)}/transitiveMemberOf/microsoft.graph.directoryRole?$select=id,displayName,roleTemplateId");

        if (doc is null)
        {
            // Reported as a FAILED read, not as an empty list. Nothing is gated on roles, so this
            // cannot open a hole - but the panel is what an operator reads before resetting a
            // possible Global Administrator, and "None active" is a claim this call did not earn
            // (review finding cpr-6).
            _logger.LogWarning("Could not read directory roles for {ObjectId} ({Status}); the panel will say so", objectId, (int)status);
            return ([], true);
        }

        using var _ = doc;

        var roles = new List<string>();
        if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.TryGetProperty("displayName", out var name) && name.GetString() is { Length: > 0 } s)
                    roles.Add(s);
            }
        }

        _logger.LogDebug("Target {ObjectId} holds {RoleCount} active directory role(s)", objectId, roles.Count);
        return (roles, false);
    }

    /// <summary>
    /// Work out where this account's password would be sent.
    /// </summary>
    /// <remarks>
    /// The whole of the destination logic, and the reason this module has no address field. Each
    /// failure is its own refusal and none falls back to another destination.
    ///
    /// <see cref="CloudPasswordResetRefusal.DestinationNoMatch"/> and
    /// <see cref="CloudPasswordResetRefusal.DestinationLookupFailed"/> are deliberately distinct:
    /// "nobody carries this id" is a fact about the directory and "the search did not complete"
    /// is a fact about the network. They log at different levels for the same reason.
    /// </remarks>
    public CloudPasswordDestination DeriveDestination(CloudPasswordTarget target)
    {
        var employeeId = target.EmployeeId?.Trim();

        DirectoryValidationResult? lookup = null;
        if (!string.IsNullOrWhiteSpace(employeeId))
            lookup = _directory.FindUserByEmployeeId(employeeId);

        var destination = ClassifyDestination(employeeId, lookup);

        switch (destination.Refusal)
        {
            case CloudPasswordResetRefusal.None:
                _logger.LogDebug("Destination for {Upn} derived from its employee ID", target.UserPrincipalName);
                break;
            case CloudPasswordResetRefusal.DestinationLookupFailed:
                // Error, not Warning: this is the network failing, not an answer about the data.
                _logger.LogError("Destination lookup for {Upn} did not complete; the directory was not reachable", target.UserPrincipalName);
                break;
            default:
                _logger.LogWarning("No destination for {Upn}: {Refusal}", target.UserPrincipalName, destination.Refusal);
                break;
        }

        return destination;
    }

    /// <summary>
    /// The destination decision, as a pure function of the employee ID and what the directory
    /// said about it.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="DeriveDestination"/> so the branch that decides where an admin
    /// credential is sent can be tested exhaustively without a forest, in the same way
    /// <see cref="ADDirectorySearchService.ClassifyOutcome"/> is. A wrong answer here does not
    /// throw; it mails a password to the wrong person.
    ///
    /// Order matters and is not arbitrary:
    /// 1. No employee ID - nothing was asked of the directory.
    /// 2. Lookup unavailable - we could not look, which is not the same as nobody being there.
    /// 3. Not found - we looked and nobody carries it.
    /// 4. Ambiguous - outranks the mailbox check, because with two matches the module does not
    ///    know WHOSE mailbox it would be examining.
    /// 5. No mailbox on the single match.
    /// 6. Resolved.
    /// </remarks>
    internal static CloudPasswordDestination ClassifyDestination(string? employeeId, DirectoryValidationResult? lookup)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return new CloudPasswordDestination(null, null, null, CloudPasswordResetRefusal.DestinationNoEmployeeId);

        var id = employeeId.Trim();

        // A null result means the caller never ran the lookup, which is not evidence of absence.
        if (lookup is null || lookup.Outcome == DirectoryLookupOutcome.Unavailable)
            return new CloudPasswordDestination(null, id, null, CloudPasswordResetRefusal.DestinationLookupFailed);

        if (lookup.Outcome == DirectoryLookupOutcome.NotFound)
            return new CloudPasswordDestination(null, id, null, CloudPasswordResetRefusal.DestinationNoMatch);

        if (lookup.Ambiguous)
            return new CloudPasswordDestination(null, id, null, CloudPasswordResetRefusal.DestinationAmbiguous);

        var owner = lookup.Match;
        if (owner is null || string.IsNullOrWhiteSpace(owner.Email))
            return new CloudPasswordDestination(null, id, owner?.DisplayName, CloudPasswordResetRefusal.DestinationNoMailbox);

        return new CloudPasswordDestination(
            owner.Email.Trim().ToLowerInvariant(), id, owner.DisplayName, CloudPasswordResetRefusal.None);
    }

    /// <summary>Ticket policy gate. Refuses on both Rejected and Unavailable.</summary>
    public async Task<(bool Accepted, CloudPasswordResetRefusal Refusal, string Message)> ValidateTicketAsync(string? ticket)
    {
        var gate = await _tickets.ValidateAsync(ModuleId, ticket);
        if (gate.Accepted)
        {
            _logger.LogDebug("Ticket accepted for a CloudPasswordReset attempt");
            return (true, CloudPasswordResetRefusal.None, "");
        }

        var refusal = gate.Outcome == TicketGateOutcome.Unavailable
            ? CloudPasswordResetRefusal.TicketValidatorUnavailable
            : CloudPasswordResetRefusal.TicketInvalid;

        _logger.LogWarning("CloudPasswordReset ticket gate refused: {Outcome}", gate.Outcome);
        return (false, refusal, gate.Message ?? "The ticket was not accepted.");
    }

    /// <summary>
    /// Generate a password and write it to the account.
    /// </summary>
    /// <param name="objectId">The Entra object id resolved during preflight, not a UPN.</param>
    /// <param name="forceChangeAtNextSignIn">
    /// REQUIRED, with no default. The page owns the default (checked, owner ruling 2026-09-23); a
    /// second default here would be a second place for it to be wrong.
    /// </param>
    /// <remarks>
    /// Bound to the object id rather than the UPN deliberately: a UPN can be renamed between
    /// preflight and write, and the id cannot.
    ///
    /// The returned password is for the caller to mail or reveal. It is never logged here and
    /// must not be logged there.
    /// </remarks>
    public async Task<CloudPasswordResetOutcome> ResetPasswordAsync(string objectId, bool forceChangeAtNextSignIn)
    {
        if (string.IsNullOrWhiteSpace(objectId))
        {
            return new CloudPasswordResetOutcome(false, null, CloudPasswordResetRefusal.GraphReadFailed,
                "No target object id was supplied.");
        }

        var client = await GetGraphClientAsync();
        if (client is null)
        {
            return new CloudPasswordResetOutcome(false, null, CloudPasswordResetRefusal.GraphReadFailed,
                "Cloud Password Reset is not configured, or its Graph credentials could not be read.");
        }

        string password;
        try
        {
            password = _generator.Generate();
        }
        catch (InvalidOperationException ex)
        {
            // The generator refuses rather than degrading. Nothing about the attempts is logged.
            _logger.LogError(ex, "The password generator refused for target {ObjectId}", objectId);
            return new CloudPasswordResetOutcome(false, null, CloudPasswordResetRefusal.GeneratorFailed,
                "A password meeting the required strength could not be generated. No change was made.");
        }

        var body = new
        {
            passwordProfile = new
            {
                password,
                forceChangePasswordNextSignIn = forceChangeAtNextSignIn,
            },
        };

        bool ok;
        System.Net.HttpStatusCode status;
        string? safeError;

        try
        {
            (ok, status, safeError) = await client.PatchWithStatusAsync($"/users/{Uri.EscapeDataString(objectId)}", body);
        }
        catch (Exception ex)
        {
            // THE REQUEST WAS ISSUED AND WE DO NOT KNOW WHAT HAPPENED TO IT. GraphTokenClient does
            // not catch around HttpClient.SendAsync, so a timeout or a dropped connection arrives
            // here - and Graph may have applied the change before the response was lost.
            //
            // Reporting this as a failure would tell the operator "no change was made", which
            // nothing supports, and would audit a write that may exist as one that never happened.
            // The account could be left with a password nobody knows while the record says the
            // reset never ran (review finding cpr-8).
            _logger.LogCritical(ex,
                "The password write for {ObjectId} was issued and its outcome is UNKNOWN. The password may have been changed. Verify the account before retrying",
                objectId);

            return new CloudPasswordResetOutcome(false, null, CloudPasswordResetRefusal.WriteIndeterminate,
                "The password change was sent to Entra ID but no response came back, so it is not known whether it applied. "
                + "The password has been discarded and NOT delivered. Check the account before running this again - "
                + "if the change did apply, nobody currently knows the password.");
        }

        if (ok)
        {
            _logger.LogInformation(
                "Password reset accepted by Entra for {ObjectId}, forceChangePasswordNextSignIn={Force}",
                objectId, forceChangeAtNextSignIn);
            return new CloudPasswordResetOutcome(true, password, CloudPasswordResetRefusal.None, "");
        }

        // A 400 is the tenant's password policy rejecting the value, which is a different thing
        // from a permission or plumbing failure and must never surface as a generic error - still
        // less as a success.
        if (status == System.Net.HttpStatusCode.BadRequest)
        {
            _logger.LogError("Entra rejected the generated password for {ObjectId} on policy grounds", objectId);
            return new CloudPasswordResetOutcome(false, null, CloudPasswordResetRefusal.PasswordPolicyRejected,
                "Entra ID rejected the generated password against the tenant's password policy. No change was made.");
        }

        _logger.LogError("Password reset for {ObjectId} failed with {Status}: {SafeError}", objectId, (int)status, safeError);
        return new CloudPasswordResetOutcome(false, null, CloudPasswordResetRefusal.GraphReadFailed,
            $"The password change failed ({(int)status}). No change was made.");
    }
}
