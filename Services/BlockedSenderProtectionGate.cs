namespace ExchangeAdminWeb.Services;

/// <summary>
/// What the protected-principal gate decided about an unblock target.
/// </summary>
/// <param name="Denied">True when the unblock must not proceed.</param>
/// <param name="Reason">Operator-facing reason when denied; null when allowed.</param>
/// <param name="AuditDetail">
/// What to record in the audit event. Separate from <paramref name="Reason"/> on purpose: the
/// banner stays generic (an operator does not need to be told which protection rule matched, and
/// enumerating rules to whoever types an address is a small disclosure), while the audit must name
/// the rule so a later reviewer can tell WHY the refusal happened. Falls back to the operator
/// message when there is nothing more specific to say.
/// </param>
public readonly record struct BlockedSenderGateDecision(bool Denied, string? Reason, string? AuditDetail = null)
{
    public static BlockedSenderGateDecision Allow() => new(false, null);

    public static BlockedSenderGateDecision Deny(string reason, string? auditDetail = null) =>
        new(true, reason, auditDetail ?? reason);
}

/// <summary>
/// Decides whether a blocked-sender address may be unblocked, given the protected-principal rules.
/// </summary>
/// <remarks>
/// A dedicated gate rather than <see cref="PermissionValidator.ValidateTargetMailboxAsync"/>,
/// which slice 1 of docs/ProtectedPrincipalGapFix-Plan.md used and which review showed to be the
/// wrong helper here, on two counts:
///
/// **It does not always normalize an alias.** That method only takes the full-resolution path when
/// the protection config contains group, OU, pattern or bare-name user rows
/// (<c>requiresFullResolution</c>, `PermissionValidator.cs:116-118`). A config holding only
/// address-form user rows takes the synthetic-principal branch, where the typed string is compared
/// literally - so a protected principal addressed by a secondary SMTP alias is not matched. That is
/// the very bypass class GAP 4 closed elsewhere, reintroduced by choosing the wrong helper.
///
/// **Its NotFound policy belongs to a different question.** For a mailbox-permission target,
/// "neither directory has this recipient" means a bad address and denying is right. A blocked
/// sender is different: the row came from <c>Get-BlockedSenderAddress</c> and is frequently
/// external, stale, or an address that no longer resolves - that is often WHY it was blocked.
/// Denying every unresolvable address would make the module unable to clear exactly the entries it
/// exists to clear.
///
/// So this gate resolves through Exchange **unconditionally** and applies unblock-specific policy
/// to the outcome. It is a plain service with no runspace of its own, so every branch is testable
/// against a substituted <see cref="ProtectedPrincipalService"/>.
/// </remarks>
public class BlockedSenderProtectionGate
{
    private readonly ProtectedPrincipalService _protectedPrincipals;
    private readonly ILogger<BlockedSenderProtectionGate> _logger;

    public BlockedSenderProtectionGate(
        ProtectedPrincipalService protectedPrincipals,
        ILogger<BlockedSenderProtectionGate> logger)
    {
        _protectedPrincipals = protectedPrincipals;
        _logger = logger;
    }

    /// <summary>Message shown when the target is protected. Public so tests assert the real string.</summary>
    public const string ProtectedMessage =
        "Access denied: this address belongs to a protected principal and cannot be unblocked through this interface.";

    /// <summary>Message shown when protection could not be evaluated.</summary>
    public const string UnavailableMessage =
        "Access denied: the protected-principal check could not be completed. Try again shortly or contact your administrator.";

    /// <summary>Message shown when the address matches more than one directory object.</summary>
    public const string AmbiguousMessage =
        "Access denied: this address is ambiguous - it matches multiple directory objects. Contact your administrator.";

    /// <summary>
    /// Decides whether <paramref name="address"/> may be unblocked.
    /// </summary>
    /// <remarks>
    /// Status policy, stated explicitly because each one means something different here:
    /// <list type="bullet">
    /// <item><c>Resolved</c> - a real principal; run the protection rules against it.</item>
    /// <item><c>Ambiguous</c> - deny. Two objects answer to this address, so protection cannot be
    /// evaluated for the one actually meant.</item>
    /// <item><c>Unavailable</c> - deny. A directory that did not answer is not evidence of absence
    /// (Known Failure Class #3).</item>
    /// <item><c>NotFound</c> - ALLOW. Both directories affirmatively answered "no such recipient",
    /// so there is no principal to protect. This is the deliberate difference from the mailbox
    /// gate, and it is what keeps external and decommissioned senders unblockable.</item>
    /// </list>
    /// </remarks>
    public async Task<BlockedSenderGateDecision> EvaluateAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return BlockedSenderGateDecision.Deny("A sender address is required.");

        // Load FIRST, and never short-circuit on HasCentralConfig. An earlier version returned
        // Allow when there was no central config, which skipped two things that matter:
        //
        //  - the LEGACY MailboxPermissions/ExcludedUsers list, which LoadEffectiveConfig returns
        //    independently of the central store and which is still live protection data. "Never
        //    configured centrally" is exactly the state where the legacy list is the only
        //    protection there is, so short-circuiting there un-protected precisely the deployments
        //    relying on it;
        //  - the corruption check. An unreadable MailboxPermissions config makes LoadEffectiveConfig
        //    fail closed, and returning early meant that failure was never consulted.
        var (config, legacyExclusions, loadError) = _protectedPrincipals.LoadEffectiveConfig();
        if (loadError != null)
        {
            // Fail closed: a config that cannot be READ says nothing about whether this address is
            // protected, so the only safe answer is to refuse.
            _logger.LogWarning("Blocking unblock of {Address} - protection config load failed: {Reason}", address, loadError);
            return BlockedSenderGateDecision.Deny($"Access denied: {loadError}");
        }

        // Only now, with both sources read successfully and both empty, is there provably nothing
        // to protect. Resolving would be a directory round-trip that cannot change the answer.
        var hasCentralRules = config is not null &&
            (config.Users.Length > 0 || config.Groups.Length > 0
             || config.OrganizationalUnits.Length > 0 || config.SamAccountNamePatterns.Length > 0);

        if (!hasCentralRules && legacyExclusions.Length == 0)
            return BlockedSenderGateDecision.Allow();

        ProtectedPrincipalService.ResolutionStatus status;
        ResolvedDirectoryPrincipal? resolved;
        try
        {
            // Unconditionally through Exchange: a blocked sender is very often cloud-only or
            // alias-addressed, and an AD-only resolve reports both as NotFound.
            (resolved, status) = await _protectedPrincipals.ResolveWithExchangeFallbackAsync(address);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blocking unblock of {Address} - resolution threw", address);
            return BlockedSenderGateDecision.Deny(UnavailableMessage);
        }

        switch (status)
        {
            case ProtectedPrincipalService.ResolutionStatus.Ambiguous:
                _logger.LogWarning("Blocking unblock of {Address} - identity is ambiguous", address);
                return BlockedSenderGateDecision.Deny(AmbiguousMessage);

            case ProtectedPrincipalService.ResolutionStatus.Unavailable:
                _logger.LogWarning("Blocking unblock of {Address} - directory unavailable", address);
                return BlockedSenderGateDecision.Deny(UnavailableMessage);

            case ProtectedPrincipalService.ResolutionStatus.NotFound:
                _logger.LogInformation(
                    "No such recipient in Active Directory or Exchange Online for {Address} - nothing to protect, allowing unblock", address);
                return BlockedSenderGateDecision.Allow();
        }

        if (resolved is null)
        {
            // Resolved-with-null is not a state the resolver documents. Refuse rather than guess:
            // an unexpected shape must not become an allow.
            _logger.LogWarning("Blocking unblock of {Address} - resolver reported Resolved with no principal", address);
            return BlockedSenderGateDecision.Deny(UnavailableMessage);
        }

        var check = await _protectedPrincipals.CheckAsync(resolved);

        if (check.CheckFailed)
        {
            // The check RAN but could not evaluate a rule (e.g. group membership needed a directory
            // that did not answer). An unevaluated rule is not a passed rule.
            _logger.LogWarning("Blocking unblock of {Address} - protection check failed: {Reason}", address, check.Reason);
            return BlockedSenderGateDecision.Deny($"Access denied: {check.Reason}");
        }

        if (check.IsProtected)
        {
            var rules = string.Join(", ", check.MatchedRules);
            _logger.LogWarning("Blocking unblock of protected principal {Address} - matched rules: {Rules}", address, rules);

            // Generic banner, specific audit. docs/BlockedSenders.md promises the Event Log names
            // the protection rule, and it must actually do so - a reviewer asking "why was this
            // refused" cannot answer it from the operator's message alone.
            return BlockedSenderGateDecision.Deny(ProtectedMessage, $"Protected principal - matched rules: {rules}");
        }

        return BlockedSenderGateDecision.Allow();
    }
}
