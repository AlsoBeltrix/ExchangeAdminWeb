using System.Security.Claims;
using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Whether an operator may act on a protected principal in a given module.
/// </summary>
/// <param name="Allowed">True when the operation may proceed despite the target being protected.</param>
/// <param name="ServicerGroup">The group that authorised it, for the audit record. Null when not allowed.</param>
public readonly record struct ServicerDecision(bool Allowed, string? ServicerGroup)
{
    public static ServicerDecision Deny() => new(false, null);
    public static ServicerDecision Allow(string group) => new(true, group);
}

/// <summary>
/// Authorises named teams to service protected principals within specific modules.
/// </summary>
/// <remarks>
/// Built for the **executive support team**, who service VIP mailboxes as their ordinary job
/// (owner 2026-08-06). This is routine authorised work, not an emergency override, which is why
/// there is no per-operation confirmation, no mandatory typed reason and no alert on use: ceremony
/// imposed on daily work teaches people to click through it, and an alert that fires constantly is
/// noise rather than signal. These actions audit exactly like every other action, because they ARE
/// like every other action.
///
/// **Per module, never global** (owner direction). A module with no servicer group configured has
/// no bypass at all, so the capability does not exist anywhere until it is deliberately granted
/// somewhere. Membership is evaluated against the module being used: authorisation in Blocked
/// Senders confers nothing in MFA Reset.
///
/// **The servicer group is distinct from the module's access group.** Being able to USE a module
/// must never imply being able to act on protected principals within it, so this reads its own
/// section-access key.
///
/// SCOPE (owner ruling, option (a)): an authorised servicer may act on ANY protected principal
/// within their granted modules. There is no per-principal pairing; the fence is the module list
/// plus group membership.
///
/// **This service never weakens a protection result.** `CheckAsync` still reports a protected
/// target as protected, and every call site that has not opted in still refuses. A module opts in
/// by consulting this service deliberately, which is what keeps the default safe: a module whose
/// author has not thought about servicing does not get it.
/// </remarks>
public class ProtectedPrincipalServicerService
{
    /// <summary>
    /// The section-access key suffix identifying a module's servicer group. A module's key is its
    /// id plus this suffix - e.g. <c>BlockedSendersProtectedServicer</c>.
    /// </summary>
    public const string SectionKeySuffix = "ProtectedServicer";

    private readonly SectionAccessService _sectionAccess;
    private readonly ILogger<ProtectedPrincipalServicerService> _logger;

    public ProtectedPrincipalServicerService(
        SectionAccessService sectionAccess,
        ILogger<ProtectedPrincipalServicerService> logger)
    {
        _sectionAccess = sectionAccess;
        _logger = logger;
    }

    /// <summary>The section-access key holding <paramref name="moduleId"/>'s servicer group(s).</summary>
    public static string SectionKeyFor(string moduleId) => moduleId + SectionKeySuffix;

    /// <summary>
    /// Whether <paramref name="user"/> may service protected principals in
    /// <paramref name="moduleId"/>.
    /// </summary>
    /// <remarks>
    /// Fail-closed throughout. No configured group, no principal, an unreadable store, or any
    /// exception all deny - a servicer capability that defaults on is worse than none, because the
    /// protection it overrides is the app's strongest control.
    /// </remarks>
    public ServicerDecision Evaluate(ClaimsPrincipal? user, string moduleId)
    {
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(moduleId))
            return ServicerDecision.Deny();

        try
        {
            var key = SectionKeyFor(moduleId);
            var groups = _sectionAccess.GetGroupsForSection(key);

            // Absence is not permission. A module with no servicer group configured has no bypass,
            // which is also what makes rollout safe: the capability does not exist until granted.
            if (groups is null || groups.Length == 0)
                return ServicerDecision.Deny();

            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group))
                    continue;

                // IsInRole against a stored SID, matching how every other section-access check
                // compares. The Windows token carries group SIDs, so this needs no directory call.
                if (user.IsInRole(group))
                {
                    _logger.LogInformation(
                        "Protected-principal servicing authorised for {User} in {Module} by {Group}",
                        user.Identity?.Name, moduleId, group);
                    return ServicerDecision.Allow(group);
                }
            }

            return ServicerDecision.Deny();
        }
        catch (Exception ex)
        {
            // Fail closed: an unreadable section-access store says nothing about membership.
            _logger.LogWarning(ex, "Servicer check failed for {Module} - denying", moduleId);
            return ServicerDecision.Deny();
        }
    }
}
