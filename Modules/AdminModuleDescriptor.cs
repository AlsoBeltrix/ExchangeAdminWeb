namespace ExchangeAdminWeb.Modules;

public sealed record AdminModuleDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string Route { get; init; }
    public required string IconCss { get; init; }
    public string Category { get; init; } = "Other";
    public required int SortOrder { get; init; }
    public required bool EnabledByDefault { get; init; }
    public required bool IsSystemModule { get; init; }
    public required ModulePermission MainPermission { get; init; }
    public IReadOnlyList<ModulePermission> GranularPermissions { get; init; } = [];
    public IReadOnlyList<ModuleConfigField> ConfigFields { get; init; } = [];
}
