using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace ExchangeAdminWeb.Authorization;

public class GroupAuthorizationRequirement : IAuthorizationRequirement
{
    public string[] AllowedGroups { get; }
    public string SectionName { get; }
    public bool ResolveDynamically { get; }

    public GroupAuthorizationRequirement(string[] allowedGroups, string sectionName = "Application")
    {
        AllowedGroups = allowedGroups;
        SectionName = sectionName;
        ResolveDynamically = false;
    }

    public GroupAuthorizationRequirement(string sectionName, bool dynamic)
    {
        AllowedGroups = Array.Empty<string>();
        SectionName = sectionName;
        ResolveDynamically = dynamic;
    }
}

public class GroupAuthorizationHandler : AuthorizationHandler<GroupAuthorizationRequirement>
{
    private readonly ILogger<GroupAuthorizationHandler> _logger;
    private readonly SectionAccessService _sectionAccessService;
    private readonly ModuleCatalog _catalog;
    private readonly ModuleEnablementService _enablement;

    public GroupAuthorizationHandler(
        ILogger<GroupAuthorizationHandler> logger,
        SectionAccessService sectionAccessService,
        ModuleCatalog catalog,
        ModuleEnablementService enablement)
    {
        _logger = logger;
        _sectionAccessService = sectionAccessService;
        _catalog = catalog;
        _enablement = enablement;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GroupAuthorizationRequirement requirement)
    {
        var user = context.User;
        var userName = user.Identity?.Name ?? "Unknown";

        if (requirement.ResolveDynamically)
        {
            var module = _catalog.GetByPolicyAlias(requirement.SectionName);
            if (module != null && !_enablement.IsModuleEnabled(module.Id))
            {
                _logger.LogWarning("User {User} denied access to {Section} - module {Module} is disabled",
                    userName, requirement.SectionName, module.Id);
                context.Fail(new AuthorizationFailureReason(this, $"Module {module.DisplayName} is currently disabled."));
                return Task.CompletedTask;
            }
        }

        // Group SIDs, not role claims. ClaimTypes.Role is empty on every request under Negotiate
        // (measured: 0 of 1687 prod authorizations came through it), while the Windows token
        // carries 333 group SIDs. Only the count is logged - 333 SIDs per request would drown the
        // log and they identify the operator.
        var groupClaims = GroupMembershipChecker.ExtractGroupClaims(user);
        _logger.LogDebug("User {User} carries {Count} group claim(s)", userName, groupClaims.Count);

        var groups = requirement.ResolveDynamically
            ? _sectionAccessService.GetGroupsForSection(requirement.SectionName)
            : requirement.AllowedGroups;

        if (groups.Length == 0)
        {
            if (requirement.SectionName == "Application")
                _logger.LogError("Security:AllowedGroups is empty - denying all access until configured");
            else
                _logger.LogError("SectionAccess:{Section} has no groups configured - denying all access", requirement.SectionName);
            context.Fail(new AuthorizationFailureReason(this, $"No groups configured for {requirement.SectionName}. Contact your administrator."));
            return Task.CompletedTask;
        }

        // Claims-based match goes through the shared pure checker so the live handler and the bulk
        // job runner's off-circuit re-check can never diverge (see GroupMembershipChecker). The
        // IsInRole() check remains as well because it consults the live Windows principal, which
        // only exists on a circuit - a job worker has only the captured claims.
        if (GroupMembershipChecker.IsMemberOfAny(groupClaims, groups))
        {
            _logger.LogInformation("User {User} authorized via a section-access group claim", userName);
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        foreach (var allowedGroup in groups)
        {
            // No DOMAIN\-stripping fallback. It existed to make a bare name match a qualified one,
            // which is exactly what made two same-named groups in different domains
            // indistinguishable. Stored values are SIDs now, and WindowsPrincipal.IsInRole
            // resolves a SID string against the token's SIDs natively - so this compares
            // self-qualifying identifiers with no normalization at all.
            if (user.IsInRole(allowedGroup))
            {
                _logger.LogInformation("User {User} authorized via group {Group}", userName, allowedGroup);
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
        }

        _logger.LogWarning("User {User} denied access to {Section} - not in groups: {Groups}",
            userName, requirement.SectionName, string.Join(", ", groups));

        context.Fail(new AuthorizationFailureReason(this, $"User {userName} is not a member of any allowed group for {requirement.SectionName}"));
        return Task.CompletedTask;
    }
}
