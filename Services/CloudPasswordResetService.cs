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

/// <summary>
/// What happened to a missing employee ID on one operation. These names reach the audit log as
/// <c>employeeIdFill</c>.
/// </summary>
/// <remarks>
/// A published interface, as <see cref="CloudPasswordResetRefusal"/> is. The closed list is
/// docs/CloudPasswordResetEmployeeId-Plan.md 4.8, and the module plan's "Audit fields, and Splunk"
/// table gains these rows in the same commit as the code that first audits them (that plan's S3).
/// Renaming a member breaks a search; adding one means adding it to both tables.
///
/// <see cref="NotAttempted"/> is the zero value on purpose: a value nobody set reads as "the
/// operation stopped before the lookup", never as an answer about the account.
/// </remarks>
public enum EmployeeIdFillOutcome
{
    /// <summary>The operation stopped before the lookup.</summary>
    NotAttempted = 0,

    /// <summary>The account carries an employee ID, so nothing was looked up or saved.</summary>
    NotNeeded,

    /// <summary>The fill-in is switched off for this module, which is its default.</summary>
    Off,

    /// <summary>The switch holds something other than true or false, or the module's config could not be read. Means off.</summary>
    SettingUnreadable,

    /// <summary>The account's name names nobody in the forest, or nothing was left of it.</summary>
    OwnerNotFound,

    /// <summary>The account's name matches more than one directory user.</summary>
    OwnerAmbiguous,

    /// <summary>A directory search did not complete. Never evidence that the owner is absent.</summary>
    OwnerLookupFailed,

    /// <summary>The owner the name points to carries no employee ID.</summary>
    OwnerHasNoEmployeeId,

    /// <summary>The owner's employee ID is carried by more than one directory user.</summary>
    EmployeeIdShared,

    /// <summary>The owner's employee ID does not lead back to the owner: to nobody, to someone else, or a GUID is missing.</summary>
    EmployeeIdMismatch,

    /// <summary>The owner has no mailbox to send to.</summary>
    OwnerHasNoMailbox,

    /// <summary>Found and verified. Recorded as such when the reset stopped before the save.</summary>
    Planned,

    /// <summary>Entra ID accepted the save.</summary>
    Written,

    /// <summary>The save was refused, or never sent. Nothing was written.</summary>
    WriteRejected,

    /// <summary>The save was sent and no answer came back, so it may have applied.</summary>
    WriteIndeterminate,
}

/// <summary>
/// The fill-in of a missing employee ID: its outcome, the number found, and the directory account
/// it came from.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="EmployeeId">
/// The number the owner's directory account carries, trimmed (<c>employeeIdFillValue</c>). Null
/// until the lookup has found a single owner who carries one.
/// </param>
/// <param name="SourceObjectGuid">
/// The ObjectGUID of the directory account the number was read from (<c>employeeIdFillSource</c>):
/// immutable, the choice <c>targetObjectId</c> makes. Null when no single owner was found, or when
/// that account answered without one.
/// </param>
/// <param name="SourceLogonName">That account's logon name, for the operator to check before confirming.</param>
/// <param name="SourceDisplayName">That account's display name, likewise.</param>
public sealed record EmployeeIdFill(
    EmployeeIdFillOutcome Outcome,
    string? EmployeeId = null,
    string? SourceObjectGuid = null,
    string? SourceLogonName = null,
    string? SourceDisplayName = null);

/// <summary>What became of a save of a found employee ID on the cloud account.</summary>
/// <param name="Outcome">
/// <see cref="EmployeeIdFillOutcome.Written"/>, <see cref="EmployeeIdFillOutcome.WriteRejected"/>
/// or <see cref="EmployeeIdFillOutcome.WriteIndeterminate"/>, and nothing else.
/// </param>
/// <param name="Status">The HTTP status Entra ID answered with; null when nothing was sent or nothing came back.</param>
/// <param name="Message">
/// One sentence for the operator and the record: saved, not saved and why, or not known. It carries
/// a status code at most, never a response body.
/// </param>
public sealed record EmployeeIdSaveResult(EmployeeIdFillOutcome Outcome, System.Net.HttpStatusCode? Status, string Message);

/// <summary>What the preflight read about the account an operator named.</summary>
/// <param name="UserPrincipalNameFromEntra">
/// True only when <see cref="UserPrincipalName"/> is the value Entra ID returned on the read. When
/// the response lacked it, the record carries the text the operator typed instead, and the
/// employee-ID fill-in must not read an owner from that: the name it uses is Entra's, never the
/// operator's (docs/CloudPasswordResetEmployeeId-Plan.md 4.2). False by default, so a record built
/// anywhere else is not taken for Entra's word.
/// </param>
public sealed record CloudPasswordTarget(
    string ObjectId,
    string UserPrincipalName,
    string DisplayName,
    bool AccountEnabled,
    bool CloudOnly,
    string? EmployeeId,
    IReadOnlyList<string> DirectoryRoles,
    bool DirectoryRolesReadFailed,
    bool UserPrincipalNameFromEntra = false);

/// <summary>Where a reset's password would be sent, or why it cannot be.</summary>
/// <remarks>
/// <see cref="Address"/> is non-null only when <see cref="Refusal"/> is
/// <see cref="CloudPasswordResetRefusal.None"/>. The two are never both set and never both empty:
/// a destination either resolved or it did not, and there is no third state in which the caller
/// should guess.
///
/// <see cref="Fill"/> says what happened to a missing employee ID
/// (docs/CloudPasswordResetEmployeeId-Plan.md). <see cref="CloudPasswordResetService.DeriveDestination"/>
/// always sets it; it is null only from <see cref="CloudPasswordResetService.ClassifyDestination"/>,
/// the number-only rule, which knows nothing of the fill-in. A fill-in short of
/// <see cref="EmployeeIdFillOutcome.Planned"/> leaves the rest of the record exactly as a missing
/// number has always left it: <see cref="CloudPasswordResetRefusal.DestinationNoEmployeeId"/>, with no
/// address and no employee ID.
/// </remarks>
public sealed record CloudPasswordDestination(
    string? Address,
    string? EmployeeId,
    string? OwnerDisplayName,
    CloudPasswordResetRefusal Refusal,
    EmployeeIdFill? Fill = null)
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
/// account's employee ID - or, for an account that carries none and with the fill-in switched on,
/// from the employee ID of the owner the account's name proposes, verified by that number - so
/// there is nothing for a caller to pass and therefore nothing for a future caller to pass wrongly.
/// Owner instruction 2026-09-22; the fill-in is docs/CloudPasswordResetEmployeeId-Plan.md.
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

    /// <summary>
    /// The module setting that switches the employee-ID fill-in on. Boolean; absent or blank is
    /// off, and anything but true or false reads as unreadable, which is off too.
    /// </summary>
    public const string FillMissingEmployeeIdKey = "FillMissingEmployeeId";

    /// <summary>
    /// The module setting holding the endings a deployment's cloud admin account names add to the
    /// owner's logon name, comma-separated. It has no default: whoever switches the fill-in on
    /// enters them, because they are a fact about one deployment and the repo names none
    /// (Architectural Invariant 7).
    /// </summary>
    public const string OwnerNameEndingsKey = "OwnerNameEndings";

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
        // Entra's own spelling of the UPN when it returned one; the typed text only when it did not,
        // as before. Which of the two this is travels on the target, because the employee-ID
        // fill-in may read an owner from Entra's name and never from the operator's.
        var entraUpn = root.TryGetProperty("userPrincipalName", out var upnProp) ? upnProp.GetString() : null;
        var upn = entraUpn ?? userPrincipalName;
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

        return (new CloudPasswordTarget(objectId, upn, displayName, enabled, CloudOnly: true, employeeId, roles, rolesReadFailed,
                    UserPrincipalNameFromEntra: !string.IsNullOrWhiteSpace(entraUpn)),
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
    /// The whole of the destination logic, and the reason this module has no address field.
    ///
    /// **For an account that carries an employee ID**, each failure is its own refusal and none
    /// falls back to another destination. The fill-in never runs for such an account and never
    /// replaces its number; its outcome is <see cref="EmployeeIdFillOutcome.NotNeeded"/>.
    ///
    /// **For an account that carries none**, the owner-requested fill-in is the one route to a
    /// destination (docs/CloudPasswordResetEmployeeId-Plan.md 4.2): the account's name proposes an
    /// owner, and that owner's number decides the address under the same exact rule. Anything short
    /// of <see cref="EmployeeIdFillOutcome.Planned"/> leaves the destination exactly where a missing
    /// number has always left it, <see cref="CloudPasswordResetRefusal.DestinationNoEmployeeId"/>,
    /// so the page's existing gates answer it as before: today's refusal, word for word, for an
    /// operator who can open the module but cannot see the password, and today's reveal for one
    /// who can. With the fill-in off, that is all that happens.
    ///
    /// <see cref="CloudPasswordResetRefusal.DestinationNoMatch"/> and
    /// <see cref="CloudPasswordResetRefusal.DestinationLookupFailed"/> are deliberately distinct:
    /// "nobody carries this id" is a fact about the directory and "the search did not complete"
    /// is a fact about the network. They log at different levels for the same reason.
    /// </remarks>
    public CloudPasswordDestination DeriveDestination(CloudPasswordTarget target)
    {
        var employeeId = target.EmployeeId?.Trim();

        CloudPasswordDestination destination;
        if (!string.IsNullOrWhiteSpace(employeeId))
        {
            // The ordinary path, as it was before the fill-in existed: the account's own number
            // decides, and nothing reads the account's name.
            var byNumber = ClassifyDestination(employeeId, _directory.FindUserByEmployeeId(employeeId));
            destination = byNumber with { Fill = new EmployeeIdFill(EmployeeIdFillOutcome.NotNeeded) };
        }
        else
        {
            destination = FillMissingEmployeeId(target);
        }

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
    /// The fill-in for an account that carries no employee ID: the setting, the name rule, the
    /// owner lookup, the number lookup and the classifier, read-only, for this one account.
    /// </summary>
    /// <remarks>
    /// Runs inside <see cref="DeriveDestination"/>, so both of the page's callers already run it on
    /// the thread pool with their progress reported. The setting is read here, at operation time and
    /// never cached, so a change takes effect at the next preflight. Two forest searches at most, for
    /// one name and one number (module plan AC4a), and the second runs only when the first found a
    /// single owner who carries a number. Nothing is written here: the save is
    /// <see cref="SetEmployeeIdAsync"/>, for the confirmed reset alone to call.
    /// </remarks>
    private CloudPasswordDestination FillMissingEmployeeId(CloudPasswordTarget target)
    {
        var setting = ReadFillSetting();

        if (setting.Switch == FillSwitch.Unreadable)
        {
            // Error, and off. A switch nobody can read must not act as if it were on (Known Failure
            // Class 3), and must not pass for one that was never set either.
            _logger.LogError(
                "The {Key} setting for Cloud Password Reset could not be read as true or false ({Value}); the employee ID fill-in is off for {Upn}",
                FillMissingEmployeeIdKey, setting.RawSwitch ?? "the module config could not be read", target.UserPrincipalName);
            return NoEmployeeId(new EmployeeIdFill(EmployeeIdFillOutcome.SettingUnreadable));
        }

        if (setting.Switch == FillSwitch.Off)
        {
            _logger.LogDebug("The employee ID fill-in is off; {Upn} carries no employee ID", target.UserPrincipalName);
            return NoEmployeeId(new EmployeeIdFill(EmployeeIdFillOutcome.Off));
        }

        // The name as Entra returned it on the read, never the operator's typed text.
        var (name, endingRemoved) = target.UserPrincipalNameFromEntra
            ? DeriveOwnerLogonName(target.UserPrincipalName, setting.Endings)
            : ("", null);

        _logger.LogDebug(
            "Employee ID fill-in for {Upn}: name tried {Name}, ending removed {Ending}, name read from Entra {FromEntra}",
            target.UserPrincipalName, name, endingRemoved ?? "(none)", target.UserPrincipalNameFromEntra);

        DirectoryValidationResult? ownerLookup = null;
        DirectoryValidationResult? numberLookup = null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            ownerLookup = _directory.FindUserByLogonName(name);
            var owner = ownerLookup.Match;

            _logger.LogDebug(
                "Owner lookup for {Name}: {Outcome}, ambiguous {Ambiguous}, guid {OwnerGuid}, logon name {LogonName}, employee ID {EmployeeId}",
                name, ownerLookup.Outcome, ownerLookup.Ambiguous, owner?.ObjectGuid, owner?.SamAccountName, owner?.EmployeeId);

            if (ownerLookup is { Outcome: DirectoryLookupOutcome.Found, Ambiguous: false, Match.EmployeeId: { } found }
                && !string.IsNullOrWhiteSpace(found))
            {
                var number = found.Trim();
                numberLookup = _directory.FindUserByEmployeeId(number);
                var carrier = numberLookup.Match;

                _logger.LogDebug(
                    "Number lookup for {EmployeeId}: {Outcome}, ambiguous {Ambiguous}, carrier guid {CarrierGuid}, owner guid {OwnerGuid}, same account {Same}",
                    number, numberLookup.Outcome, numberLookup.Ambiguous, carrier?.ObjectGuid, owner?.ObjectGuid,
                    CanonicalGuid(owner?.ObjectGuid) is { } ownerGuid && ownerGuid == CanonicalGuid(carrier?.ObjectGuid));
            }
        }

        var destination = ClassifyEmployeeIdFill(name, ownerLookup, numberLookup);
        var fill = destination.Fill!;

        switch (fill.Outcome)
        {
            case EmployeeIdFillOutcome.Planned:
                _logger.LogDebug(
                    "Employee ID fill-in planned for {Upn}: {EmployeeId} from directory account {LogonName} ({SourceGuid})",
                    target.UserPrincipalName, fill.EmployeeId, fill.SourceLogonName, fill.SourceObjectGuid);
                break;
            case EmployeeIdFillOutcome.OwnerLookupFailed:
                // Error, not Warning: a search that did not finish is the network, not an answer
                // about the data.
                _logger.LogError(
                    "Employee ID fill-in for {Upn} did not complete: a directory search did not finish", target.UserPrincipalName);
                break;
            default:
                _logger.LogWarning(
                    "Employee ID fill-in for {Upn} found no owner to send to: {Outcome}", target.UserPrincipalName, fill.Outcome);
                break;
        }

        return destination;
    }

    /// <summary>The fill-in switch, read three ways. Unreadable acts as off, and says so.</summary>
    internal enum FillSwitch
    {
        Off,
        On,
        Unreadable,
    }

    /// <summary>The fill-in setting as read for one operation.</summary>
    /// <param name="Switch">On, off, or unreadable.</param>
    /// <param name="Endings">The configured endings; empty when none are, or when the setting is unreadable.</param>
    /// <param name="RawSwitch">What the switch held, trimmed, for the log; null when nothing could be read.</param>
    internal sealed record EmployeeIdFillSetting(FillSwitch Switch, IReadOnlyList<string> Endings, string? RawSwitch);

    /// <summary>Read the fill-in setting now, from this module's config. Never cached.</summary>
    /// <remarks>
    /// One read of the module's config: <see cref="ModuleConfigService.TryGetModuleConfig"/> makes
    /// the test <see cref="ModuleConfigService.IsModuleCorrupt"/> makes and returns the values in the
    /// same call, so the switch is never read from a config the corruption check did not see.
    /// </remarks>
    internal EmployeeIdFillSetting ReadFillSetting()
    {
        var readable = _moduleConfig.TryGetModuleConfig(ModuleId, out var config);
        config.TryGetValue(FillMissingEmployeeIdKey, out var rawSwitch);
        config.TryGetValue(OwnerNameEndingsKey, out var rawEndings);
        return ParseFillSetting(readable, rawSwitch, rawEndings);
    }

    /// <summary>
    /// The fill-in setting as a pure function of what the config held, in the shape of the
    /// <c>ValidateTickets</c> switch (<see cref="TicketValidationService"/>).
    /// </summary>
    /// <remarks>
    /// Fail closed, and never silently. An unreadable config, or a switch that is not true or false,
    /// is <see cref="FillSwitch.Unreadable"/>: off, and logged at Error by the caller. A mistyped
    /// switch must not mean on, and must not pass for one that was never set. Absent or blank is the
    /// default, off, because unset is not a mistype. The endings have no default.
    /// </remarks>
    internal static EmployeeIdFillSetting ParseFillSetting(bool configReadable, string? rawSwitch, string? rawEndings)
    {
        if (!configReadable)
            return new EmployeeIdFillSetting(FillSwitch.Unreadable, [], null);

        if (string.IsNullOrWhiteSpace(rawSwitch))
            return new EmployeeIdFillSetting(FillSwitch.Off, ParseOwnerNameEndings(rawEndings), null);

        if (!bool.TryParse(rawSwitch, out var on))
            return new EmployeeIdFillSetting(FillSwitch.Unreadable, [], rawSwitch.Trim());

        return new EmployeeIdFillSetting(on ? FillSwitch.On : FillSwitch.Off, ParseOwnerNameEndings(rawEndings), rawSwitch.Trim());
    }

    /// <summary>The configured endings: comma-separated, each trimmed, blank entries dropped.</summary>
    internal static IReadOnlyList<string> ParseOwnerNameEndings(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',').Select(ending => ending.Trim()).Where(ending => ending.Length > 0).ToList();

    /// <summary>
    /// The owner's logon name a cloud account's name proposes: the local part of its UPN, less the
    /// longest configured ending it ends with, removed once.
    /// </summary>
    /// <remarks>
    /// The true-up script's rule (docs/CloudPasswordResetEmployeeId-Plan.md section 2, "From the
    /// cloud account") with the endings configured rather than coded. Compared case-insensitively, at
    /// the end only, and once; when two endings match, the longer wins, so an ending that is the tail
    /// of another cannot leave the other's first characters behind. A name with no configured ending
    /// is used whole, and so is every name when no ending is configured. The domain part plays no
    /// part. Nothing else is changed: no punctuation folding, name-order handling or numeric-suffix
    /// rules (J9). A blank result names nobody, and the caller runs no query for it.
    /// </remarks>
    /// <returns>The name, and the ending removed (null when none was) for the log.</returns>
    internal static (string Name, string? EndingRemoved) DeriveOwnerLogonName(string? userPrincipalName, IReadOnlyList<string> endings)
    {
        if (string.IsNullOrWhiteSpace(userPrincipalName))
            return ("", null);

        var at = userPrincipalName.IndexOf('@');
        var local = at >= 0 ? userPrincipalName[..at] : userPrincipalName;

        var ending = endings
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate)
                                && local.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault();

        return ending is null ? (local, null) : (local[..^ending.Length], ending);
    }

    /// <summary>
    /// The fill-in decision, as a pure function of the name and what the two searches said: the
    /// table in docs/CloudPasswordResetEmployeeId-Plan.md 4.2, in its order.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="FillMissingEmployeeId"/> for the reason <see cref="ClassifyDestination"/>
    /// is. A wrong answer here does not throw: it mails an admin password to the wrong person and saves
    /// that person's number on the account, so every later reset goes to them too.
    ///
    /// 1. Name blank: <see cref="EmployeeIdFillOutcome.OwnerNotFound"/>.
    /// 2. Owner search unavailable, or never run: <see cref="EmployeeIdFillOutcome.OwnerLookupFailed"/>.
    ///    An unavailable search is never read as "nobody" (Known Failure Class 3).
    /// 3. Owner search found nobody: OwnerNotFound.
    /// 4. Owner search found several: <see cref="EmployeeIdFillOutcome.OwnerAmbiguous"/>. It outranks
    ///    everything after it: with two owners the module does not know whose number or mailbox it
    ///    would be examining.
    /// 5. The owner carries no employeeID: <see cref="EmployeeIdFillOutcome.OwnerHasNoEmployeeId"/>.
    /// 6-8. The number, classified by the module's existing rule (<see cref="ClassifyDestination"/>):
    ///    unavailable or never run is OwnerLookupFailed; nobody carries it, the directory disagreeing
    ///    with itself, is <see cref="EmployeeIdFillOutcome.EmployeeIdMismatch"/>; several carry it is
    ///    <see cref="EmployeeIdFillOutcome.EmployeeIdShared"/>, ahead of the GUID and mailbox checks.
    /// 9. The single carrier is not the owner by ObjectGUID, or either GUID is missing:
    ///    EmployeeIdMismatch. This is what makes the address come from the number, not from the name.
    /// 10. The carrier has no mailbox: <see cref="EmployeeIdFillOutcome.OwnerHasNoMailbox"/>.
    /// 11. Otherwise <see cref="EmployeeIdFillOutcome.Planned"/>: the address is the number lookup's
    ///    match's mail, trimmed and lowercased by <see cref="ClassifyDestination"/>, and the employee
    ///    ID is the number. Rows 9 to 11 read the NUMBER lookup's match, never the name lookup's.
    ///
    /// Every outcome but Planned returns the destination a missing number has always produced.
    /// </remarks>
    internal static CloudPasswordDestination ClassifyEmployeeIdFill(
        string? ownerName, DirectoryValidationResult? ownerLookup, DirectoryValidationResult? numberLookup)
    {
        // 1.
        if (string.IsNullOrWhiteSpace(ownerName))
            return NoEmployeeId(new EmployeeIdFill(EmployeeIdFillOutcome.OwnerNotFound));

        // 2. A null result means the search was never run, which is not evidence of absence.
        if (ownerLookup is null || ownerLookup.Outcome == DirectoryLookupOutcome.Unavailable)
            return NoEmployeeId(new EmployeeIdFill(EmployeeIdFillOutcome.OwnerLookupFailed));

        // 3.
        if (ownerLookup.Outcome == DirectoryLookupOutcome.NotFound)
            return NoEmployeeId(new EmployeeIdFill(EmployeeIdFillOutcome.OwnerNotFound));

        // 4.
        if (ownerLookup.Ambiguous)
            return NoEmployeeId(new EmployeeIdFill(EmployeeIdFillOutcome.OwnerAmbiguous));

        // A search that says Found and names nobody gave no answer to act on.
        var owner = ownerLookup.Match;
        if (owner is null)
            return NoEmployeeId(new EmployeeIdFill(EmployeeIdFillOutcome.OwnerLookupFailed));

        var ownerGuid = CanonicalGuid(owner.ObjectGuid);

        // 5.
        var number = owner.EmployeeId?.Trim();
        if (string.IsNullOrWhiteSpace(number))
            return NoEmployeeId(FillFrom(EmployeeIdFillOutcome.OwnerHasNoEmployeeId, null, owner, ownerGuid));

        // 6-8, by the module's existing rule for a number.
        var byNumber = ClassifyDestination(number, numberLookup);
        switch (byNumber.Refusal)
        {
            case CloudPasswordResetRefusal.DestinationLookupFailed:
                return NoEmployeeId(FillFrom(EmployeeIdFillOutcome.OwnerLookupFailed, number, owner, ownerGuid));
            case CloudPasswordResetRefusal.DestinationNoMatch:
                return NoEmployeeId(FillFrom(EmployeeIdFillOutcome.EmployeeIdMismatch, number, owner, ownerGuid));
            case CloudPasswordResetRefusal.DestinationAmbiguous:
                return NoEmployeeId(FillFrom(EmployeeIdFillOutcome.EmployeeIdShared, number, owner, ownerGuid));
        }

        // 9. The number must lead back to exactly this owner.
        var carrierGuid = CanonicalGuid(numberLookup?.Match?.ObjectGuid);
        if (ownerGuid is null || carrierGuid is null || ownerGuid != carrierGuid)
            return NoEmployeeId(FillFrom(EmployeeIdFillOutcome.EmployeeIdMismatch, number, owner, ownerGuid));

        // 10.
        if (!byNumber.Resolved)
            return NoEmployeeId(FillFrom(EmployeeIdFillOutcome.OwnerHasNoMailbox, number, owner, ownerGuid));

        // 11.
        return byNumber with { Fill = FillFrom(EmployeeIdFillOutcome.Planned, number, owner, ownerGuid) };
    }

    /// <summary>The destination a missing employee ID has always produced, carrying what the fill-in found.</summary>
    private static CloudPasswordDestination NoEmployeeId(EmployeeIdFill fill) =>
        new(null, null, null, CloudPasswordResetRefusal.DestinationNoEmployeeId, fill);

    private static EmployeeIdFill FillFrom(EmployeeIdFillOutcome outcome, string? number, ADSearchResult owner, string? ownerGuid) =>
        new(outcome, number, ownerGuid, owner.SamAccountName, owner.DisplayName);

    /// <summary>A GUID in one canonical form, or null when there is none to compare.</summary>
    private static string? CanonicalGuid(string? value) =>
        Guid.TryParse(value?.Trim(), out var guid) && guid != Guid.Empty ? guid.ToString("D") : null;

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

    /// <summary>
    /// Save a found employee ID on the cloud account: <c>PATCH /users/{objectId}</c> with
    /// <c>{"employeeId": ...}</c> and nothing else, through this module's own Graph registration.
    /// </summary>
    /// <param name="objectId">The Entra object id from the confirmed reset's fresh read, never a UPN.</param>
    /// <param name="employeeId">The number the fill-in found, sent exactly as given.</param>
    /// <returns>
    /// Written, WriteRejected or WriteIndeterminate. It never throws and never refuses: the reset
    /// goes ahead whatever this returns (docs/CloudPasswordResetEmployeeId-Plan.md J2), and the
    /// result says what became of the number.
    /// </returns>
    /// <remarks>
    /// Bound to the object id for <see cref="ResetPasswordAsync"/>'s reason: a UPN can be renamed
    /// between the read and the write, and the id cannot. There is no read-back (J8): nothing
    /// downstream reads the saved value, and the next reset's fresh read will see it. Never an
    /// overwrite: the caller saves only a number the fill-in planned, for an account whose fresh
    /// read carried none (J7).
    ///
    /// The registration needs a grant to write the attribute that the reset alone does not; the
    /// plan's assumption A1 names <c>User.ReadWrite.All</c>, and the first live save settles it.
    /// Until the registration holds it, every save comes back WriteRejected and the resets still go
    /// out.
    /// </remarks>
    public async Task<EmployeeIdSaveResult> SetEmployeeIdAsync(string objectId, string employeeId)
    {
        if (string.IsNullOrWhiteSpace(objectId) || string.IsNullOrWhiteSpace(employeeId))
        {
            return new EmployeeIdSaveResult(EmployeeIdFillOutcome.WriteRejected, null,
                "The employee ID was not saved: no account or no number was supplied, so nothing was sent.");
        }

        GraphTokenClient? client;
        try
        {
            client = await GetGraphClientAsync();
        }
        catch (Exception ex)
        {
            // Before any request, so the number was certainly not sent.
            _logger.LogError(ex, "The Graph client for the employee ID save on {ObjectId} could not be built", objectId);
            client = null;
        }

        if (client is null)
        {
            return new EmployeeIdSaveResult(EmployeeIdFillOutcome.WriteRejected, null,
                "The employee ID was not saved: Cloud Password Reset is not configured, or its Graph credentials could not be read.");
        }

        return await SendEmployeeIdAsync(client, objectId, employeeId);
    }

    /// <summary>The save itself, over a client the caller built; separate so it runs over a stub handler.</summary>
    /// <remarks>
    /// A throw from the client is <see cref="EmployeeIdFillOutcome.WriteIndeterminate"/>, never
    /// WriteRejected: the cpr-8 lesson <see cref="ResetPasswordAsync"/> records. GraphTokenClient
    /// does not catch around the send, so a timeout or a dropped connection arrives here when the
    /// change may have been applied, and "not saved" would be a claim nothing supports. A
    /// non-success status is WriteRejected, with the status and no response body in the message;
    /// the sanitized Graph error goes to the log alone.
    /// </remarks>
    internal async Task<EmployeeIdSaveResult> SendEmployeeIdAsync(GraphTokenClient client, string objectId, string employeeId)
    {
        // Exactly one property. A second one here would be a second write nobody approved.
        var body = new Dictionary<string, string> { ["employeeId"] = employeeId };

        bool ok;
        System.Net.HttpStatusCode status;
        string? safeError;

        try
        {
            (ok, status, safeError) = await client.PatchWithStatusAsync($"/users/{Uri.EscapeDataString(objectId)}", body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "The employee ID save for {ObjectId} was sent and its outcome is UNKNOWN; check the account's employee ID",
                objectId);
            return new EmployeeIdSaveResult(EmployeeIdFillOutcome.WriteIndeterminate, null,
                "The employee ID was sent to Entra ID but no answer came back, so it is not known whether it was saved.");
        }

        if (ok)
        {
            _logger.LogInformation("Employee ID {EmployeeId} saved on {ObjectId}", employeeId, objectId);
            return new EmployeeIdSaveResult(EmployeeIdFillOutcome.Written, status,
                $"The employee ID {employeeId} was saved on the account.");
        }

        _logger.LogError("Entra refused the employee ID save for {ObjectId} with {Status}: {SafeError}", objectId, (int)status, safeError);
        return new EmployeeIdSaveResult(EmployeeIdFillOutcome.WriteRejected, status,
            $"The employee ID was not saved: Entra ID refused it ({(int)status}).");
    }
}
