using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// Custom authorization handler that checks if user is member of allowed AD groups
/// Handles both DOMAIN\GroupName and GroupName formats
/// </summary>
public class GroupAuthorizationRequirement : IAuthorizationRequirement
{
    public string[] AllowedGroups { get; }

    public GroupAuthorizationRequirement(string[] allowedGroups)
    {
        AllowedGroups = allowedGroups;
    }
}

public class GroupAuthorizationHandler : AuthorizationHandler<GroupAuthorizationRequirement>
{
    private readonly ILogger<GroupAuthorizationHandler> _logger;

    public GroupAuthorizationHandler(ILogger<GroupAuthorizationHandler> logger)
    {
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GroupAuthorizationRequirement requirement)
    {
        var user = context.User;
        var userName = user.Identity?.Name ?? "Unknown";

        // Log all role claims for debugging
        var roleClaims = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        _logger.LogDebug("User {User} has role claims: {Roles}", userName, string.Join(", ", roleClaims));

        if (requirement.AllowedGroups.Length == 0)
        {
            _logger.LogError("No allowed groups configured (Security:AllowedGroups) — denying all access. Configure at least one AD group.");
            context.Fail(new AuthorizationFailureReason(this, "No allowed groups configured. Contact your administrator."));
            return Task.CompletedTask;
        }

        // Check if user has any of the allowed groups
        foreach (var allowedGroup in requirement.AllowedGroups)
        {
            // Normalize the allowed group name (remove DOMAIN\ prefix if present)
            var normalizedAllowedGroup = allowedGroup.Contains('\\')
                ? allowedGroup.Split('\\')[1]
                : allowedGroup;

            // Check if user has this role (with or without domain prefix)
            var hasRole = user.IsInRole(allowedGroup) // Try exact match first
                || user.IsInRole(normalizedAllowedGroup) // Try without domain
                || roleClaims.Any(r => r.Equals(allowedGroup, StringComparison.OrdinalIgnoreCase))
                || roleClaims.Any(r => r.Equals(normalizedAllowedGroup, StringComparison.OrdinalIgnoreCase));

            if (hasRole)
            {
                _logger.LogInformation("User {User} authorized via group {Group}", userName, allowedGroup);
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
        }

        _logger.LogWarning("User {User} DENIED ACCESS - not member of allowed groups: {Groups}",
            userName, string.Join(", ", requirement.AllowedGroups));

        context.Fail(new AuthorizationFailureReason(this, $"User {userName} is not a member of any allowed group"));
        return Task.CompletedTask;
    }
}
