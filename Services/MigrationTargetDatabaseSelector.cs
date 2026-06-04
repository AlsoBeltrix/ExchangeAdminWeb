namespace ExchangeAdminWeb.Services;

public static class MigrationTargetDatabaseSelector
{
    public static string[] Resolve(ModuleConfigService moduleConfig, IConfiguration config)
    {
        if (moduleConfig.IsModuleCorrupt("Migration"))
            return Array.Empty<string>();

        var configured = moduleConfig.GetValue("Migration", "OnPremTargetDatabases");
        var moduleValues = Parse(configured);
        if (moduleValues.Length > 0)
            return moduleValues;

        if (!moduleConfig.HasModuleConfigFile("Migration"))
        {
            var arrayValues = config.GetSection("Migration:OnPremTargetDatabases").Get<string[]>() ?? Array.Empty<string>();
            var cleanedArrayValues = Clean(arrayValues);
            if (cleanedArrayValues.Length > 0)
                return cleanedArrayValues;

            var csvValues = Parse(config["Migration:OnPremTargetDatabases"]);
            if (csvValues.Length > 0)
                return csvValues;
        }

        return Array.Empty<string>();
    }

    public static string[] Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : Clean(value.Split(',', ';', '\r', '\n'));

    private static string[] Clean(IEnumerable<string> values) =>
        values.Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
