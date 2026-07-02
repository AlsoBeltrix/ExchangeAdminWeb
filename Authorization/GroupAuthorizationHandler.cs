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
                _logger.LogWarning("User {User} denied access to {Section} — module {Module} is disabled",
                    userName, requirement.SectionName, module.Id);
                context.Fail(new AuthorizationFailureReason(this, $"Module {module.DisplayName} is currently disabled."));
                return Task.CompletedTask;
            }
        }

        var roleClaims = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        _logger.LogDebug("User {User} has role claims: {Roles}", userName, string.Join(", ", roleClaims));

        var groups = requirement.ResolveDynamically
            ? _sectionAccessService.GetGroupsForSection(requirement.SectionName)
            : requirement.AllowedGroups;

        if (groups.Length == 0)
        {
            if (requirement.SectionName == "Application")
                _logger.LogError("Security:AllowedGroups is empty — denying all access until configured");
            else
                _logger.LogError("SectionAccess:{Section} has no groups configured — denying all access", requirement.SectionName);
            context.Fail(new AuthorizationFailureReason(this, $"No groups configured for {requirement.SectionName}. Contact your administrator."));
            return Task.CompletedTask;
        }

        // Claims-based match goes through the shared pure checker so the live handler and the bulk
        // job runner's off-circuit re-check can never diverge (see GroupMembershipChecker). The
        // IsInRole() checks remain here because they consult the live Windows principal's token
        // roles, which only exist on a circuit — a job worker has only the captured role claims.
        if (GroupMembershipChecker.IsMemberOfAny(roleClaims, groups))
        {
            _logger.LogInformation("User {User} authorized via a section-access group claim", userName);
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        foreach (var allowedGroup in groups)
        {
            var normalizedAllowedGroup = allowedGroup.Contains('\\')
                ? allowedGroup.Split('\\')[1]
                : allowedGroup;

            if (user.IsInRole(allowedGroup) || user.IsInRole(normalizedAllowedGroup))
            {
                _logger.LogInformation("User {User} authorized via group {Group}", userName, allowedGroup);
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
        }

        _logger.LogWarning("User {User} denied access to {Section} — not in groups: {Groups}",
            userName, requirement.SectionName, string.Join(", ", groups));

        context.Fail(new AuthorizationFailureReason(this, $"User {userName} is not a member of any allowed group for {requirement.SectionName}"));
        return Task.CompletedTask;
    }
}
