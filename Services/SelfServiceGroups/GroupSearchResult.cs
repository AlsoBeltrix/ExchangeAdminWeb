namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// Outcome of the on-demand single-group search (plan docs/SelfServiceGroupManagement-Plan.md §6.3).
/// Exactly one of the two states holds: <see cref="Group"/> is non-null when the caller can manage the
/// named group; otherwise <see cref="Message"/> carries the user-facing "not found or not manageable"
/// text (deliberately not distinguishing the two, so the search cannot enumerate the directory).
/// </summary>
/// <param name="Group">The manageable group, or null when the search yields nothing the caller may edit.</param>
/// <param name="Message">The user-facing message when <paramref name="Group"/> is null; null on success.</param>
public sealed record GroupSearchResult(ManageableGroup? Group, string? Message);
