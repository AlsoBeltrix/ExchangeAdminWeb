namespace ExchangeAdminWeb.Modules;

public enum ConfigFieldType
{
    Text,
    AdGroup,
    AdUser,
    OU,

    /// <summary>
    /// A true/false setting, rendered as a checkbox - never a text input. A
    /// boolean setting must be impossible to mistype (owner ruling 2026-09-01,
    /// .agents/decisions.md); the stored value is exactly "true" or "false".
    /// </summary>
    Boolean
}

public sealed record ModuleConfigField(
    string Key,
    string Label,
    string Description,
    bool Required = true,
    bool IsSecret = false,
    string DefaultValue = "",
    ConfigFieldType FieldType = ConfigFieldType.Text);
