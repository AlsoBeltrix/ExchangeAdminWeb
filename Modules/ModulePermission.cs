namespace ExchangeAdminWeb.Modules;

/// <summary>
/// One grantable permission on a module. <paramref name="Description"/> is required and has no
/// default deliberately: it is rendered to operators on the Module Config Access tab, where the
/// alias alone told nobody what they were approving (owner ruling 2026-09-02).
/// </summary>
public sealed record ModulePermission(string Name, string PolicyAlias, string Description, bool FailClosed = false);
