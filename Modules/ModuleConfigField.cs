namespace ExchangeAdminWeb.Modules;

public enum ConfigFieldType
{
    Text,

    /// <summary>
    /// An Active Directory group. A single value is picked with the group search; a list (a field with a
    /// <see cref="ModuleConfigField.ListSeparator"/>) is typed as text, because picking would replace the whole list.
    /// </summary>
    AdGroup,

    /// <summary>The distinguished name of an organizational unit - an OU only, never another container.</summary>
    OU,

    /// <summary>
    /// A true/false setting, rendered as a checkbox - never a text input. A
    /// boolean setting must be impossible to mistype (owner ruling 2026-09-01,
    /// .agents/decisions.md); the stored value is exactly "true" or "false".
    /// </summary>
    Boolean,

    /// <summary>
    /// The distinguished name of a directory search base: any object a search can start at - an OU, a container or a
    /// domain root - not OU-only (docs/DirectoryObjectVerify-Plan.md C.3). Text, with the OU browser offered.
    /// </summary>
    SearchBase,

    /// <summary>
    /// The address of an Exchange recipient that is a group (docs/DirectoryObjectVerify-Plan.md C.3): text, checked by
    /// Verify against Exchange rather than Active Directory.
    /// </summary>
    RecipientGroup,
}

/// <param name="ListSeparator">
/// A field that holds a list: the character the module splits its value at (docs/DirectoryObjectVerify-Plan.md C.3) - each
/// entry is verified, and removed, on its own. Null for a single value. Editor and Verify only; the stored value and the
/// module's own parsing are unchanged.
/// </param>
public sealed record ModuleConfigField(
    string Key,
    string Label,
    string Description,
    bool Required = true,
    bool IsSecret = false,
    string DefaultValue = "",
    ConfigFieldType FieldType = ConfigFieldType.Text,
    char? ListSeparator = null);
