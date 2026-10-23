namespace ExchangeAdminWeb.Services;

/// <summary>
/// What the admin pages tell an administrator about a config store they cannot read: where it is and
/// how it is fixed (aud-a26h). A service's log line or message says the same without the page's reload
/// clause (aud-a26t).
/// </summary>
/// <remarks>
/// These stores have lived in the SQLite config database since the legacy import, but the banners and
/// refusals still told an administrator to fix a <c>config\*.json</c> file nothing reads. The file is
/// named only where its import failed - the one case where it is the thing to fix. One place, so a banner
/// and its save's refusal cannot drift apart again.
/// </remarks>
public static class SettingsStoreText
{
    // The fix itself, once: a page adds its reload clause; a service has no page to reload (aud-a26t).
    private const string DatabaseFix =
        "Correct it as docs/ConfigDbOperations.md describes, or restore a verified backup the same way, with both application pools stopped";

    /// <summary>The fix for a table in the config database.</summary>
    public const string DatabaseRemedy = DatabaseFix + ", then reload this page.";

    /// <summary>The same fix for a service's log line or message, where no page is involved (aud-a26t).</summary>
    public const string ServiceDatabaseRemedy = DatabaseFix + ".";

    private const string LegacyRemedy =
        "Correct or remove the file, then recycle the application pool so the import runs again at start.";

    /// <summary>Why module enablement cannot be read, and its fix.</summary>
    public static string Enablement(bool legacyImportFailed) => legacyImportFailed
        ? "The legacy file config\\modules-enabled.json could not be imported into the config database and is left in place, so every module reads as disabled until it imports. " + LegacyRemedy
        : "The module_enablement table in the config database cannot be read, so every module reads as disabled. " + DatabaseRemedy;

    /// <summary>Why section access cannot be read, and its fix.</summary>
    public static string SectionAccess(bool legacyImportFailed) => legacyImportFailed
        ? "The legacy file config\\sectionaccess.json could not be imported into the config database and is left in place, so every fail-closed module denies access until it imports. " + LegacyRemedy
        : "The section_access table in the config database cannot be read, so every fail-closed module denies access. " + DatabaseRemedy;

    /// <summary>Why one module's configuration cannot be read, and its fix.</summary>
    public static string ModuleConfig(string moduleId) => ModuleConfigUnreadable(moduleId) + DatabaseRemedy;

    /// <summary><see cref="ModuleConfig"/> for a service's log line or message: the same rows and fix, no page to reload (aud-a26t).</summary>
    public static string ServiceModuleConfig(string moduleId) => ModuleConfigUnreadable(moduleId) + ServiceDatabaseRemedy;

    private static string ModuleConfigUnreadable(string moduleId) =>
        $"The {moduleId} rows of the module_config table in the config database cannot be read. ";

    /// <summary>Why the module admin groups cannot be read, and their fix.</summary>
    public static string ModuleAdmins() =>
        "The module_admins table in the config database cannot be read. " + DatabaseRemedy;

    /// <summary>Why the protection rules cannot be read, and their fix.</summary>
    /// <param name="legacyImportFailed">The legacy protected-principals file was not imported.</param>
    /// <param name="exclusionsUnreadable">The Mailbox Permissions module's ExcludedUsers list cannot be read.</param>
    public static string ProtectedPrincipals(bool legacyImportFailed, bool exclusionsUnreadable)
    {
        if (legacyImportFailed)
            return "The legacy file config\\protected-principals.json could not be imported into the config database and is left in place, so every protected-principal check fails closed until it imports. " + LegacyRemedy;

        if (exclusionsUnreadable)
            return "The Mailbox Permissions module's configuration (its ExcludedUsers protection list, in the MailboxPermissions rows of the module_config table) cannot be read, so every protected-principal check fails closed. " + DatabaseRemedy;

        return "The protected_principal table in the config database cannot be read, so every protected-principal check fails closed. " + DatabaseRemedy;
    }
}
