using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Management.Automation;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

public class PermissionValidator
{
    private ImmutableHashSet<string> _excludedUsers = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
    private ImmutableHashSet<string> _excludedObjectIds = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PermissionValidator> _logger;
    private readonly IConfiguration _config;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ExoConnectionPool _exoPool;
    private readonly ProtectedPrincipalService _protectedPrincipalService;
    private readonly ProtectedPrincipalServicerService _servicers;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    // A load that failed is retried this soon rather than served for CacheLifetime. Failing closed
    // is the rule; failing closed for half an hour after Exchange has recovered is not (aud-a2b).
    internal static readonly TimeSpan FailedLoadRetry = TimeSpan.FromMinutes(1);

    /// <summary>Test seam: the clock the cache lifetime is measured against.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    private bool _initialized = false;
    private bool _initFailed = false;
    private DateTime _lastRefresh = DateTime.MinValue;

    // Cross-process freshness on the shared config database (docs/SharedConfigDb-Plan.md AC4):
    // the exclusions derive from MailboxPermissions module config, which the other instance can
    // save. The token recorded as of the load is checked before the 30-minute lifetime.
    private readonly ConfigChangeWatcher _changeWatcher;
    private long _loadedToken = ConfigChangeWatcher.Unknown;

    public PermissionValidator(IConfiguration config, ModuleConfigService moduleConfig, ExoConnectionPool exoPool, ProtectedPrincipalService protectedPrincipalService, ProtectedPrincipalServicerService servicers, ILogger<PermissionValidator> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _moduleConfig = moduleConfig;
        _exoPool = exoPool;
        _protectedPrincipalService = protectedPrincipalService;
        _servicers = servicers;
        _scopeFactory = scopeFactory;
        _changeWatcher = moduleConfig.CreateChangeWatcher(logger);

        moduleConfig.ConfigSaved += moduleId =>
        {
            if (moduleId == "MailboxPermissions")
            {
                InvalidateCache();
                _protectedPrincipalService.InvalidateCache();
            }
        };
    }

    // Exclusions come only from the MailboxPermissions/ExcludedUsers module config.
    // The appsettings Security:ExcludedUsers fallback was retired 2026-07-28: it was
    // invisible to the Protected Principals admin UI, so principals could be blocked
    // with no visible cause. False when the rows cannot be read: never an empty list
    // standing in for one that was not read (aud-a28s; the central check's read of the
    // same list is ProtectedPrincipalService.TryGetLegacyExclusions, aud-a28p).
    private bool TryGetConfiguredExclusions(out string[] exclusions)
    {
        if (!_moduleConfig.TryGetModuleConfig("MailboxPermissions", out var values))
        {
            exclusions = [];
            return false;
        }

        var excluded = values.GetValueOrDefault("ExcludedUsers");
        exclusions = string.IsNullOrEmpty(excluded)
            ? []
            : excluded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return true;
    }

    // aud-a28t. One read of the Mailbox Permissions rows, fail-closed. Appsettings Security:PreventSelfGrant is the
    // upgrade fallback for a setting the module config does not hold (absent or blank) - the Constitution allows it only
    // then - so rows that cannot be read, and a value that is neither true nor false, refuse self-grants rather than ask
    // appsettings, whose answer may be false.
    private bool GetPreventSelfGrant()
    {
        if (!_moduleConfig.TryGetModuleConfig("MailboxPermissions", out var values))
        {
            _logger.LogError("The self-grant setting cannot be read - self-grants are refused until it can be. {Store}",
                SettingsStoreText.ServiceModuleConfig("MailboxPermissions"));
            return true;
        }

        var val = values.GetValueOrDefault("PreventSelfGrant");
        if (string.IsNullOrWhiteSpace(val))
            return PreventSelfGrantFromAppsettings();

        if (bool.TryParse(val, out var result))
            return result;

        _logger.LogWarning("The self-grant setting holds '{Value}', which is neither true nor false - self-grants are refused until it is corrected", val);
        return true;
    }

    // aud-a28y. The upgrade fallback for a module config that holds no value. Absent or blank is the default, on; a value that
    // is neither true nor false refuses self-grants, as a stored one does (aud-a28t), and is named - it used to throw from
    // bool.Parse on every check, sync and async.
    private bool PreventSelfGrantFromAppsettings()
    {
        var fallback = _config["Security:PreventSelfGrant"];
        if (string.IsNullOrWhiteSpace(fallback))
            return true;

        if (bool.TryParse(fallback, out var result))
            return result;

        _logger.LogWarning("The appsettings value Security:PreventSelfGrant is '{Value}', which is neither true nor false - self-grants are refused until it is corrected", fallback);
        return true;
    }

    public void InvalidateCache()
    {
        _lastRefresh = DateTime.MinValue;
    }

    /// <summary>Test seam: the watcher this validator consults before trusting its cache.</summary>
    internal ConfigChangeWatcher ChangeWatcher => _changeWatcher;

    /// <summary>
    /// True unless the legacy excluded-user list provably does not cover this identity. A list
    /// that could not be loaded, or a target Exchange could not be asked about, counts as covered:
    /// that is the fail-closed reading, and <see cref="ValidateTargetMailboxAsync"/> tells the
    /// two apart for the operator.
    /// </summary>
    public async Task<bool> IsUserExcludedAsync(string userIdentity)
        => (await CheckExclusionAsync(userIdentity)).Verdict != ExclusionVerdict.NotExcluded;

    private enum ExclusionVerdict { NotExcluded, Excluded, Unavailable }

    /// <summary>
    /// The legacy list's verdict, and the recipient it put to Exchange when it asked
    /// (<paramref name="LookedUp"/>) - null then meaning Exchange has no such recipient. aud-a2d
    /// binds the write to that recipient rather than asking Exchange a second time.
    /// </summary>
    private readonly record struct ExclusionCheck(ExclusionVerdict Verdict, bool LookedUp, ResolvedRecipient? Recipient);

    /// <remarks>
    /// aud-a2b hole 1. The string comparisons alone cannot see a SECONDARY address of an excluded
    /// mailbox, so the target is also put to Exchange, which names the recipient it belongs to.
    /// That used to be skipped whenever the load had obtained no object ids - which is exactly
    /// what a failed load produced - and a failed target lookup fell through to "not excluded".
    /// Both now refuse: an excluded entry or a target Exchange could not be asked about is a
    /// question this list cannot answer, not a negative answer.
    /// </remarks>
    private async Task<ExclusionCheck> CheckExclusionAsync(string userIdentity)
    {
        await EnsureInitializedAsync();

        if (_initFailed)
            return new(ExclusionVerdict.Unavailable, false, null);

        if (_excludedUsers.Contains(userIdentity)
            || _excludedUsers.Any(excluded => IdentitiesMatch(excluded, userIdentity)))
            return new(ExclusionVerdict.Excluded, false, null);

        // Nothing is excluded, so no address of anything can be.
        if (_excludedUsers.IsEmpty)
            return new(ExclusionVerdict.NotExcluded, false, null);

        // Throws when Exchange could not answer; null only when it affirmatively has no such
        // recipient, which leaves the string comparisons above as the whole answer.
        var (answered, recipient) = await LookUpRecipientAsync(userIdentity, "the excluded-user check");
        if (!answered)
            return new(ExclusionVerdict.Unavailable, false, null);

        if (recipient is null)
            return new(ExclusionVerdict.NotExcluded, true, null);

        var canonical = recipient.PrimarySmtpAddress;
        if (_excludedUsers.Contains(canonical)
            || _excludedUsers.Any(excluded => IdentitiesMatch(excluded, canonical))
            || (!string.IsNullOrWhiteSpace(recipient.ExternalDirectoryObjectId)
                && _excludedObjectIds.Contains(recipient.ExternalDirectoryObjectId)))
            return new(ExclusionVerdict.Excluded, true, recipient);

        return new(ExclusionVerdict.NotExcluded, true, recipient);
    }

    /// <summary>
    /// Puts <paramref name="identity"/> to Exchange. <c>answered</c> is false when the question
    /// could not be put - no resolver, or a lookup that threw - and the recipient is then
    /// meaningless; a null recipient with <c>answered</c> true is Exchange's "no such recipient".
    /// </summary>
    private async Task<(bool answered, ResolvedRecipient? recipient)> LookUpRecipientAsync(string identity, string purpose)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resolver = scope.ServiceProvider.GetService<IIdentityResolver>();
            if (resolver == null)
            {
                _logger.LogWarning("No identity resolver - cannot put {Identity} to Exchange for {Purpose}; failing closed", identity, purpose);
                return (false, null);
            }

            return (true, await resolver.ResolveRecipientAsync(identity));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exchange could not resolve {Identity} for {Purpose} - failing closed", identity, purpose);
            return (false, null);
        }
    }

    /// <summary>
    /// The protected-principal verdict for a target: an error message to refuse with, or the
    /// mailbox the write must be bound to - plus the servicer note when an authorised operator
    /// overrode a refusal.
    /// </summary>
    /// <remarks>
    /// A record rather than a bare string so a caller cannot take the allow and drop the reason.
    /// <see cref="ServicedNote"/> is non-null only when the target WAS protected and this operator
    /// is an authorised servicer, and it must reach the audit event's <c>extra</c> - the action
    /// succeeds, so the error channel would discard it.
    ///
    /// aud-a2d. <see cref="Mailbox"/> is the identity the check validated, and the only identity a
    /// write may use: the typed string is the operator's question, this is the answer. There is no
    /// public constructor, so an allow cannot be built without a mailbox - every allow carries one
    /// and every refusal carries none.
    /// </remarks>
    public sealed record TargetValidation
    {
        private TargetValidation(string? error, string? servicedNote, ValidatedMailbox? mailbox)
        {
            Error = error;
            ServicedNote = servicedNote;
            Mailbox = mailbox;
        }

        /// <summary>The refusal to show the operator and audit, or null to proceed.</summary>
        public string? Error { get; }

        /// <summary>Set only when an authorised servicer overrode a protected-principal refusal.</summary>
        public string? ServicedNote { get; }

        /// <summary>The mailbox the write is bound to. Non-null exactly when <see cref="Error"/> is null.</summary>
        public ValidatedMailbox? Mailbox { get; }

        public static TargetValidation Allow(ValidatedMailbox mailbox) =>
            new(null, null, mailbox ?? throw new ArgumentNullException(nameof(mailbox)));

        public static TargetValidation Deny(string error) => new(error, null, null);

        public static TargetValidation Serviced(string note, ValidatedMailbox mailbox) =>
            new(null, note, mailbox ?? throw new ArgumentNullException(nameof(mailbox)));
    }

    /// <summary>
    /// Back-compat overload for callers with no servicer concept. Never services: it passes no
    /// principal, and a null principal refuses.
    /// </summary>
    public async Task<string?> ValidateTargetMailboxAsync(string targetMailbox)
        => (await ValidateTargetMailboxAsync(targetMailbox, actingUser: null, moduleId: null)).Error;

    /// <param name="actingUser">The operator, for the servicer decision. Null refuses.</param>
    /// <param name="moduleId">
    /// The MODULE the caller is acting as. Required alongside a principal and never defaulted:
    /// this one method serves Mailbox Permissions, Calendar and Out of Office, and a shared or
    /// borrowed id would let a grant in one authorise the others - collapsing the per-module
    /// boundary the whole design rests on.
    /// </param>
    public async Task<TargetValidation> ValidateTargetMailboxAsync(
        string targetMailbox,
        System.Security.Claims.ClaimsPrincipal? actingUser,
        string? moduleId)
    {
        // aud-a2d. The mailbox the central check resolved, when it resolved one: the write is bound
        // to it, so it is kept rather than re-derived from the string at the end.
        var centralResolved = false;
        ValidatedMailbox? centralMailbox = null;

        if (_protectedPrincipalService.HasCentralConfig)
        {
            var (cfg, _, loadError) = _protectedPrincipalService.LoadEffectiveConfig();

            if (loadError != null)
            {
                _logger.LogWarning("Blocking operation on {Target} - protected-principal config load failed: {Reason}", targetMailbox, loadError);
                return TargetValidation.Deny($"Access denied: {loadError}");
            }

            // Audit finding A2. This used to skip resolution whenever the configuration held
            // only address-form users, on the reasoning that an address can be compared as a
            // string. It cannot: a mailbox has more than one address. With `vip@x.com`
            // protected, an operator targeting a SECONDARY alias of that same mailbox built a
            // principal straight from the alias, compared alias against vip, found no match,
            // and modified the protected mailbox through its other name. Mailbox Permissions,
            // Calendar Permissions and Out of Office all reach this method.
            //
            // The only way to know which mailbox an address belongs to is to ask the directory,
            // so the directory is always asked. The check and the thing being checked must name
            // one object.
            //
            // THE COST IS DELIBERATE AND IT IS A BEHAVIOUR CHANGE: with an address-only
            // configuration and the directory unreachable, this now denies where it previously
            // proceeded. That is the same rule A3 applies to the group check - an unanswerable
            // question is not a negative answer - and the alternative is a protection control
            // that quietly stops protecting during an outage.
            bool requiresFullResolution = cfg != null;

            ResolvedDirectoryPrincipal principal;

            if (requiresFullResolution)
            {
                // aud-a2b hole 2. The write hands targetMailbox to EXCHANGE, so the object checked
                // must be the one Exchange will resolve it to - asked of Exchange first, with AD
                // required to agree. The AD-first resolution stopped at an AD match, which could be
                // an unrelated account sharing the string with the mailbox the write then hit.
                var (resolved, status) = await _protectedPrincipalService.ResolveExchangeTargetAsync(targetMailbox);

                if (status == ProtectedPrincipalService.ResolutionStatus.Ambiguous)
                {
                    _logger.LogWarning(
                        "Blocking operation on {Target} - identity is ambiguous in Active Directory",
                        targetMailbox);
                    return TargetValidation.Deny("Access denied: This identity is ambiguous - it matches multiple directory objects. Contact your administrator.");
                }

                if (status == ProtectedPrincipalService.ResolutionStatus.Unavailable)
                {
                    // aud-a2b diagnostics: since aud-a2 every configuration shape resolves, so the
                    // old wording - that Group/OU/Pattern rules were configured - was false for an
                    // address-only configuration and sent support to the wrong place.
                    _logger.LogWarning(
                        "Blocking operation on {Target} - the directories could not establish which object it is "
                        + "(unreachable, or Active Directory and Exchange disagree)",
                        targetMailbox);
                    return TargetValidation.Deny("Access denied: Protected-principal identity resolution is unavailable. Contact your administrator.");
                }

                if (status == ProtectedPrincipalService.ResolutionStatus.NotFound)
                {
                    // Both directories answered and neither has this recipient. Naming that is the
                    // point of the change: the old blanket message read like an outage and sent
                    // support chasing the wrong problem when the real cause was a bad address.
                    _logger.LogWarning(
                        "Blocking operation on {Target} - not found in Active Directory or Exchange Online",
                        targetMailbox);
                    return TargetValidation.Deny($"Access denied: '{targetMailbox}' was not found in Active Directory or Exchange Online. Check the address.");
                }

                principal = resolved!;
                centralResolved = true;
                centralMailbox = ValidatedMailbox.From(resolved);
            }
            else
            {
                principal = new ResolvedDirectoryPrincipal(
                    Source: "PermissionValidator",
                    DisplayName: targetMailbox,
                    UserPrincipalName: targetMailbox,
                    SamAccountName: null,
                    PrimarySmtpAddress: targetMailbox.Contains('@') ? targetMailbox : null,
                    DistinguishedName: null,
                    ObjectGuid: null,
                    EntraObjectId: null);
            }

            var result = await _protectedPrincipalService.CheckAsync(principal);
            if (result.CheckFailed)
            {
                _logger.LogWarning("Blocking operation on {Target} - protected-principal check failed: {Reason}", targetMailbox, result.Reason);
                return TargetValidation.Deny($"Access denied: {result.Reason}");
            }
            if (result.IsProtected)
            {
                // An authorised servicer for THIS module may proceed. moduleId is passed by the
                // caller and never defaulted, so a Mailbox grant cannot authorise Calendar or
                // Out of Office.
                var note = ServicerNote(actingUser, moduleId, result.MatchedRules);
                if (note is not null)
                    return await BindWriteTargetAsync(targetMailbox, note, centralResolved, centralMailbox, legacy: default);

                _logger.LogWarning("Attempted operation on protected principal: {Target} (rules: {Rules})", targetMailbox, string.Join(", ", result.MatchedRules));
                return TargetValidation.Deny($"Access denied: {targetMailbox} is protected and cannot be modified through this interface.");
            }

            // Regression caught reviewing aud-a2, and it is the mirror image of the bug that
            // slice fixed. MatchesIdentity compares a configured value against the RESOLVED
            // principal's six fields - UPN, primary SMTP, sAMAccountName, DN, objectGUID,
            // Entra id - and a mailbox's SECONDARY addresses are not among them. Before always
            // resolving, a protected entry that was itself a secondary alias matched because
            // the principal was built from the typed string. After, it matches nothing.
            //
            // So the typed identity stays a candidate. Resolution ADDS the identity the
            // operator may be hiding behind; it must not REMOVE the one they typed. Only runs
            // when the resolved check came back clean, so the common path still costs one
            // check, and only when the typed value is not already one of the resolved fields.
            if (!TypedValueIsRedundant(targetMailbox, principal))
            {
                var literal = new ResolvedDirectoryPrincipal(
                    Source: "PermissionValidator:typed",
                    DisplayName: targetMailbox,
                    UserPrincipalName: targetMailbox,
                    SamAccountName: null,
                    PrimarySmtpAddress: targetMailbox.Contains('@') ? targetMailbox : null,
                    DistinguishedName: null,
                    ObjectGuid: null,
                    EntraObjectId: null);

                var typedResult = await _protectedPrincipalService.CheckAsync(literal);
                if (typedResult.CheckFailed)
                {
                    _logger.LogWarning(
                        "Blocking operation on {Target} - protected-principal check failed for the typed identity: {Reason}",
                        targetMailbox, typedResult.Reason);
                    return TargetValidation.Deny($"Access denied: {typedResult.Reason}");
                }
                if (typedResult.IsProtected)
                {
                    var typedNote = ServicerNote(actingUser, moduleId, typedResult.MatchedRules);
                    if (typedNote is not null)
                        return await BindWriteTargetAsync(targetMailbox, typedNote, centralResolved, centralMailbox, legacy: default);

                    _logger.LogWarning(
                        "Attempted operation on protected principal by the address typed: {Target} (rules: {Rules})",
                        targetMailbox, string.Join(", ", typedResult.MatchedRules));
                    return TargetValidation.Deny($"Access denied: {targetMailbox} is protected and cannot be modified through this interface.");
                }
            }
        }

        await EnsureInitializedAsync();

        if (_initFailed)
        {
            _logger.LogWarning("Blocking operation on {Target} - protected-user list failed to load", targetMailbox);
            return TargetValidation.Deny("Access denied: Protected-user list is unavailable. Contact your administrator.");
        }

        var exclusion = await CheckExclusionAsync(targetMailbox);
        if (exclusion.Verdict == ExclusionVerdict.Unavailable)
        {
            _logger.LogWarning("Blocking operation on {Target} - could not establish whether the excluded-user list covers it", targetMailbox);
            return TargetValidation.Deny("Access denied: The protected-user list could not be checked for this mailbox. Contact your administrator.");
        }

        if (exclusion.Verdict == ExclusionVerdict.Excluded)
        {
            // The legacy excluded-user list. It protects the same principals by a different
            // mechanism, so a servicer grant must apply here too - otherwise whether an operator
            // can service a VIP would depend on WHICH list happens to name them, which is
            // invisible from the UI and impossible to reason about.
            var note = ServicerNote(actingUser, moduleId, [$"excluded-user list: {targetMailbox}"]);
            if (note is not null)
                return await BindWriteTargetAsync(targetMailbox, note, centralResolved, centralMailbox, exclusion);

            _logger.LogWarning("Attempted operation on excluded user: {Target}", targetMailbox);
            return TargetValidation.Deny($"Access denied: {targetMailbox} is protected and cannot be modified through this interface.");
        }

        return await BindWriteTargetAsync(targetMailbox, servicedNote: null, centralResolved, centralMailbox, exclusion);
    }

    /// <summary>
    /// Turns a verdict that allows into an allow bound to the mailbox the check validated, or a
    /// refusal when there is no such mailbox (aud-a2d).
    /// </summary>
    /// <remarks>
    /// Every write used to hand the operator's string back to Exchange to be resolved again, so
    /// the object checked and the object written could differ. The write now takes the identity
    /// returned here, and there is no allow without one:
    /// - central check ran: the mailbox it resolved. When the legacy list also asked Exchange and
    ///   was answered with a different mailbox, the check looked at two objects - refused.
    /// - no central check: the recipient the legacy list put to Exchange, or - when the list did
    ///   not ask (nothing configured, or a match by string) - one Exchange lookup now.
    /// - no mailbox GUID on what was found (no recipient; a contact, group or mail user without a
    ///   mailbox), or Exchange not answering: refused. The write would have handed the string to
    ///   Exchange and let it decide; that is what this removes.
    /// Protected and failed verdicts never reach here, so they keep their own messages.
    /// </remarks>
    private async Task<TargetValidation> BindWriteTargetAsync(
        string targetMailbox,
        string? servicedNote,
        bool centralResolved,
        ValidatedMailbox? centralMailbox,
        ExclusionCheck legacy)
    {
        ValidatedMailbox? mailbox;
        if (centralResolved)
        {
            mailbox = centralMailbox;
            if (mailbox is not null && legacy.LookedUp
                && ValidatedMailbox.From(legacy.Recipient)?.ExchangeGuid != mailbox.ExchangeGuid)
            {
                _logger.LogWarning(
                    "Blocking operation on {Target} - the protected-principal check resolved mailbox {Mailbox} and the "
                    + "excluded-user list was answered with a different one; failing closed",
                    targetMailbox, mailbox.Identity);
                return TargetValidation.Deny(
                    $"Access denied: '{targetMailbox}' resolved to two different mailboxes while it was being checked. "
                    + "Try again, and contact your administrator if this persists.");
            }
        }
        else
        {
            var recipient = legacy.Recipient;
            if (!legacy.LookedUp)
            {
                var (answered, found) = await LookUpRecipientAsync(targetMailbox, "binding the write to a mailbox");
                if (!answered)
                {
                    _logger.LogWarning("Blocking operation on {Target} - Exchange could not say which mailbox it is", targetMailbox);
                    return TargetValidation.Deny(
                        "Access denied: The target mailbox could not be identified in Exchange Online. Contact your administrator.");
                }
                recipient = found;
            }
            mailbox = ValidatedMailbox.From(recipient);
        }

        if (mailbox is null)
        {
            _logger.LogWarning(
                "Blocking operation on {Target} - Exchange reports no mailbox for it, so the write has nothing to be bound to",
                targetMailbox);
            return TargetValidation.Deny(NoMailboxMessage(targetMailbox));
        }

        return servicedNote is null
            ? TargetValidation.Allow(mailbox)
            : TargetValidation.Serviced(servicedNote, mailbox);
    }

    /// <summary>The refusal for a target with no mailbox the write could be bound to.</summary>
    internal static string NoMailboxMessage(string targetMailbox) =>
        $"Access denied: Exchange Online reports no mailbox for '{targetMailbox}', so the change cannot be bound to one. Check the address.";

    /// <summary>
    /// True when the operator's typed value is already one of the resolved principal's
    /// identifiers, so checking it a second time would ask the same question twice.
    /// </summary>
    private static bool TypedValueIsRedundant(string typed, ResolvedDirectoryPrincipal principal)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return true;

        foreach (var candidate in new[]
                 {
                     principal.UserPrincipalName,
                     principal.PrimarySmtpAddress,
                     principal.SamAccountName,
                     principal.DistinguishedName,
                     principal.ObjectGuid,
                     principal.EntraObjectId
                 })
        {
            if (!string.IsNullOrEmpty(candidate) &&
                string.Equals(typed, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The servicer note for this operator in this module, or null to refuse.
    /// </summary>
    /// <remarks>
    /// Refuses outright when <paramref name="moduleId"/> is absent. That is what makes the
    /// back-compat overload safe: it supplies neither principal nor module, so it can never
    /// service - and a future caller that forgets the module id gets the refusing behaviour rather
    /// than a borrowed grant.
    /// </remarks>
    private string? ServicerNote(
        System.Security.Claims.ClaimsPrincipal? actingUser,
        string? moduleId,
        IEnumerable<string> matchedRules)
    {
        if (actingUser is null || string.IsNullOrWhiteSpace(moduleId))
            return null;

        return ProtectedPrincipalServicing.NoteFor(_servicers, actingUser, moduleId, matchedRules);
    }

    public string? ValidateSelfGrant(string currentUser, string affectedUser)
    {
        if (!GetPreventSelfGrant())
            return null;

        if (IdentitiesMatch(currentUser, affectedUser))
        {
            _logger.LogWarning("User {User} attempted to grant permissions to themselves ({Affected})", currentUser, affectedUser);
            return "Access denied: You cannot grant permissions to yourself.";
        }

        return null;
    }

    public async Task<string?> ValidateSelfGrantAsync(string currentUser, string affectedUser)
    {
        if (!GetPreventSelfGrant())
            return null;

        if (IdentitiesMatch(currentUser, affectedUser))
        {
            _logger.LogWarning("User {User} attempted to grant permissions to themselves ({Affected})", currentUser, affectedUser);
            return "Access denied: You cannot grant permissions to yourself.";
        }

        // aud-a28y. The string match cannot see an alias of the operator's own mailbox, so Exchange is asked who each side
        // is. That lookup used to answer a failure as "no recipient", and the comparison was skipped: the grant was allowed
        // exactly while Exchange could not answer - unreachable, not configured, its config unreadable. A lookup that could
        // not run now refuses, naming who could not be resolved. Exchange's own "no such recipient" is an answer, not a
        // failure: there is nothing to compare and the string match is the whole answer, as for the excluded-user check.
        var (actorAnswered, actor) = await LookUpRecipientAsync(currentUser, "the self-grant check");
        if (!actorAnswered)
            return SelfGrantCheckIncomplete(currentUser);

        var (granteeAnswered, grantee) = await LookUpRecipientAsync(affectedUser, "the self-grant check");
        if (!granteeAnswered)
            return SelfGrantCheckIncomplete(affectedUser);

        if (actor is not null && grantee is not null && SameRecipient(actor, grantee))
        {
            _logger.LogWarning("User {User} attempted to grant permissions to themselves ({Affected}) - Exchange Online names both {Recipient}", currentUser, affectedUser, actor.PrimarySmtpAddress);
            return "Access denied: You cannot grant permissions to yourself.";
        }

        return null;
    }

    /// <summary>The refusal when the self-grant check could not put <paramref name="identity"/> to Exchange (aud-a28y).</summary>
    internal static string SelfGrantCheckIncomplete(string identity) =>
        $"Access denied: The self-grant check could not be completed: Exchange Online could not resolve '{identity}'; nothing was written. Contact your administrator.";

    /// <summary>
    /// One recipient: a shared directory object id, or the same primary SMTP address - Exchange's own name for the
    /// recipient, unique in the tenant, which answers when an object id is missing. Two blank ids are no id, not a shared one.
    /// </summary>
    private static bool SameRecipient(ResolvedRecipient a, ResolvedRecipient b) =>
        (!string.IsNullOrWhiteSpace(a.ExternalDirectoryObjectId)
            && string.Equals(a.ExternalDirectoryObjectId, b.ExternalDirectoryObjectId, StringComparison.OrdinalIgnoreCase))
        || string.Equals(a.PrimarySmtpAddress, b.PrimarySmtpAddress, StringComparison.OrdinalIgnoreCase);

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

    private bool CacheIsFresh()
        => _initialized
            && !_changeWatcher.HasChangedSince(_loadedToken)
            && UtcNow() - _lastRefresh < (_initFailed ? FailedLoadRetry : CacheLifetime);

    private async Task EnsureInitializedAsync()
    {
        if (CacheIsFresh()) return;

        await _initLock.WaitAsync();
        // Token BEFORE the load (scd-2): a write racing the reads below moves the stored token
        // past this value and is caught on the next check. Recorded on every exit path that
        // stamps _lastRefresh, including the fail-closed ones, so a fix saved by the other
        // instance is picked up without a recycle.
        var token = ConfigChangeWatcher.Unknown;
        try
        {
            if (CacheIsFresh()) return;

            token = _changeWatcher.CurrentToken();

            // aud-a28s. One read is both the corruption probe and the list. A presence read and a
            // probe followed by a second, fail-open read of the list let a failure after the probe
            // read as "no protected users", and every listed mailbox could be written for the cache
            // lifetime. No presence read either: a module never configured has no rows and reads as
            // an empty list, and rows that cannot be read are refused whether or not the module was
            // ever configured.
            if (!TryGetConfiguredExclusions(out var exclusions))
            {
                _excludedUsers = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
                _excludedObjectIds = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
                _initFailed = true;
                _initialized = true;
                _lastRefresh = UtcNow();
                _loadedToken = token;
                // There is no file to fix: the rows and their fix, in the admin pages' words without their reload clause - no
                // page is involved in a service's log line (aud-a26q, aud-a26t).
                _logger.LogError("The protected-user list cannot be loaded - every protected-target operation is refused; the load is retried after {Retry}. {Store}",
                    FailedLoadRetry, SettingsStoreText.ServiceModuleConfig("MailboxPermissions"));
                return;
            }

            var configuredExclusions = exclusions
                .Select(e => e.Trim())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToList();
            _logger.LogInformation("Initializing permission validator with {Count} configured exclusions", configuredExclusions.Count);

            var newExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in configuredExclusions)
            {
                newExcluded.Add(entry);

                var members = await TryExpandGroupAsync(entry);
                foreach (var member in members)
                {
                    newExcluded.Add(member);
                    _logger.LogDebug("Excluded (from group {Group}): {Member}", entry, member);
                }
            }

            // aud-a2b hole 1. Each configured entry is resolved through Exchange so a SECONDARY
            // address of an excluded mailbox can be recognised later by its canonical address and
            // object id. This used to run through a lookup that swallowed every failure, and a
            // load that obtained nothing was accepted as complete - the target check then fell
            // back to comparing strings, and an alias passed for up to the cache lifetime.
            //
            // ResolveRecipientAsync throws when Exchange could not answer, and that throw is NOT
            // caught here: it reaches the catch below, which fails the whole load closed. Null is
            // Exchange affirmatively having no such recipient, which leaves the entry a literal.
            // Group members are not each sent to Exchange: the expansion already returned their
            // primary addresses, which is what the target check compares.
            var newObjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (configuredExclusions.Count > 0)
            {
                using var scope = _scopeFactory.CreateScope();
                var resolver = scope.ServiceProvider.GetService<IIdentityResolver>()
                    ?? throw new InvalidOperationException(
                        "No identity resolver is registered, so excluded users cannot be resolved.");

                foreach (var entry in configuredExclusions)
                {
                    var recipient = await resolver.ResolveRecipientAsync(entry);
                    if (recipient is null)
                        continue;

                    newExcluded.Add(recipient.PrimarySmtpAddress);
                    if (!string.IsNullOrWhiteSpace(recipient.ExternalDirectoryObjectId))
                        newObjectIds.Add(recipient.ExternalDirectoryObjectId);
                }
            }

            _excludedUsers = newExcluded.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            _excludedObjectIds = newObjectIds.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            _initFailed = false;
            _initialized = true;
            _lastRefresh = UtcNow();
            _loadedToken = token;
            _logger.LogInformation("Permission validator initialized with {Total} total excluded identities", _excludedUsers.Count);
        }
        catch (Exception ex)
        {
            _excludedUsers = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
            _excludedObjectIds = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
            _initFailed = true;
            _initialized = true;
            _lastRefresh = UtcNow();
            _loadedToken = token;
            _logger.LogError(ex, "Failed to initialize permission validator - operations on protected targets are blocked; the load is retried after {Retry}.", FailedLoadRetry);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<List<string>> TryExpandGroupAsync(string identity)
    {
        var members = new List<string>();

        // aud-a28t. A config that cannot be read is not one that says "not configured": taking it for that kept a
        // protected group as a bare name, every member unprotected for the cache lifetime, while the resolver's later
        // read of the same config answered. The throw fails the whole load closed instead (EnsureInitializedAsync's
        // catch), retried after FailedLoadRetry. Exchange Online read as not configured still keeps the entry a literal.
        if (!_exoPool.TryGetIsConfigured(out var exchangeConfigured))
            throw new InvalidOperationException(
                $"Exchange Online's module enablement or its module_config rows could not be read, so the excluded entry '{identity}' cannot be expanded into its members.");

        if (!exchangeConfigured)
        {
            _logger.LogWarning("Exchange Online not configured - group '{Identity}' kept as literal match only; group members are not individually protected", identity);
            return members;
        }

        // Read-only (Get-Recipient + Get-DistributionGroupMember): retry-eligible. The pool helper
        // owns borrow/return/discard and the one-shot retry on a dead session; on a NON-connection
        // throw it discards (this delegate uses raw ps.Invoke() and can't guarantee a clean
        // pipeline, matching the prior manual Discard). The "couldn't be found = keep as literal
        // match" rule stays here as a normal (non-connection) success.
        return await _exoPool.RunWithRetryAsync(pooled => Task.Run(() =>
        {
            var found = new List<string>();
            var ps = pooled.PowerShell;

            ps.AddCommand("Get-Recipient")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "Stop");

            Collection<PSObject> recipients;
            try
            {
                recipients = ps.Invoke();
                if (ps.HadErrors)
                {
                    var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Unknown EXO error";
                    ps.Streams.Error.Clear();
                    ps.Commands.Clear();
                    throw new InvalidOperationException($"EXO error resolving '{identity}': {errMsg}");
                }
                ps.Commands.Clear();
            }
            catch (Exception ex) when (ex.Message.Contains("couldn't be found"))
            {
                ps.Commands.Clear();
                _logger.LogInformation("Excluded entry '{Identity}' not found in EXO - kept as literal match", identity);
                return new PooledOutcome<List<string>>(found, false);
            }

            if (recipients.Count == 0)
                return new PooledOutcome<List<string>>(found, false);

            var recipient = recipients[0];
            var recipientType = recipient.Properties["RecipientTypeDetails"]?.Value?.ToString();

            if (recipientType?.Contains("Group", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("Expanding group: {Group} (type: {Type})", identity, recipientType);

                ps.AddCommand("Get-DistributionGroupMember")
                  .AddParameter("Identity", identity)
                  .AddParameter("ResultSize", "Unlimited")
                  .AddParameter("ErrorAction", "Stop");

                var groupMembers = ps.Invoke();
                if (ps.HadErrors)
                {
                    var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Unknown EXO error";
                    ps.Streams.Error.Clear();
                    ps.Commands.Clear();
                    throw new InvalidOperationException($"EXO error expanding group '{identity}': {errMsg}");
                }
                ps.Commands.Clear();

                foreach (var member in groupMembers)
                {
                    var email = member.Properties["PrimarySmtpAddress"]?.Value?.ToString();
                    var upn = member.Properties["UserPrincipalName"]?.Value?.ToString();
                    var sam = member.Properties["SamAccountName"]?.Value?.ToString();

                    if (!string.IsNullOrWhiteSpace(email)) found.Add(email);
                    if (!string.IsNullOrWhiteSpace(upn) && upn != email) found.Add(upn);
                    if (!string.IsNullOrWhiteSpace(sam)) found.Add(sam);
                }

                _logger.LogInformation("Expanded group {Group} to {Count} members", identity, found.Count);
            }

            return new PooledOutcome<List<string>>(found, false);
        }), allowRetry: true, PoolFailurePolicy.Discard);
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
