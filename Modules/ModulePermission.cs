namespace ExchangeAdminWeb.Modules;

/// <summary>
/// One grantable permission on a module. <paramref name="Description"/> is required and has no
/// default deliberately: it is rendered to operators on the Module Config Access tab, where the
/// alias alone told nobody what they were approving (owner ruling 2026-09-02).
/// </summary>
/// <param name="Name">
/// The permission's slot within its module - "Access" for every main permission, and a short verb
/// such as "Search" or "Delete" for a granular one. It is NOT a label: all 29 main permissions are
/// called "Access", so it says nothing on its own. Use <paramref name="DisplayName"/> for text an
/// administrator reads.
/// </param>
/// <param name="PolicyAlias">
/// The authorization policy name AND the section-access storage key. Changing it on a deployed
/// module orphans every group stored against the old value, and because the section-access store
/// is fail-closed, that denies everyone rather than failing open. Treat it as permanent once
/// shipped; rename <paramref name="DisplayName"/> instead.
/// </param>
/// <param name="DisplayName">
/// Optional heading for the Access tab, for a module whose aliases do not read clearly to an
/// administrator (owner ruling 2026-09-22, on "MessageTrace" versus "MessageTraceSearch" - the
/// distinction being granted is header analysis versus trace). Null means the tab shows the alias,
/// which is what every module did before this existed and what most still do. The alias stays on
/// screen either way: it is what appears in the logs and in the fail-closed denial message, so an
/// administrator needs to be able to match one to the other.
/// </param>
public sealed record ModulePermission(
    string Name,
    string PolicyAlias,
    string Description,
    bool FailClosed = false,
    string? DisplayName = null);
