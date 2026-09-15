namespace ExchangeAdminWeb.Modules;

/// <summary>
/// The nav categories a module descriptor may declare. Category is display grouping
/// only - it reaches no authorization path (section access is keyed on policy alias) -
/// but a typo silently drops a module out of the primary nav, so every descriptor
/// names one of these constants rather than repeating a string literal.
/// <para>
/// <see cref="Administration"/> is the one category with behavior attached: a module
/// in it renders in the Administration block at the bottom of the sidebar instead of
/// the primary nav.
/// </para>
/// </summary>
public static class ModuleCategories
{
    public const string Exchange = "Exchange";
    public const string DirectoryAndGroups = "Directory & Groups";
    public const string IdentityAndAccess = "Identity & Access";
    public const string Infrastructure = "Infrastructure";
    public const string Administration = "Administration";
}
