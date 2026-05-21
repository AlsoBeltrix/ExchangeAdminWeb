namespace ExchangeAdminWeb.Modules;

public sealed record ModuleConfigField(
    string Key,
    string Label,
    string Description,
    bool Required = true,
    bool IsSecret = false,
    string DefaultValue = "");
