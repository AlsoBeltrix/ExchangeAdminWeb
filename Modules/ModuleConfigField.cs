namespace ExchangeAdminWeb.Modules;

public enum ConfigFieldType
{
    Text,
    AdGroup,
    AdUser,
    OU
}

public sealed record ModuleConfigField(
    string Key,
    string Label,
    string Description,
    bool Required = true,
    bool IsSecret = false,
    string DefaultValue = "",
    ConfigFieldType FieldType = ConfigFieldType.Text);
