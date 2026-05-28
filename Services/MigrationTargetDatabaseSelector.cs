namespace ExchangeAdminWeb.Services;

public static class MigrationTargetDatabaseSelector
{
    public static readonly string[] DefaultDatabases =
    [
        "dbg3-2019-east01",
        "dbg8-2019-west02",
        "dbg7-2019-west01",
        "dbg4-2019-east01",
        "dbg2-2019-east01",
        "dbg6-2019-east01",
        "dbg2-2019-east02",
        "dbg5-2019-west01",
        "dbg1-2019-west01",
        "dbg8-2019-east02",
        "dbg6-2019-west02",
        "dbg2-2019-west02",
        "dbg5-2019-east01",
        "dbg4-2019-west01",
        "dbg4-2019-east02",
        "dbg5-2019-east02",
        "dbg2-2019-west01",
        "dbg3-2019-east02",
        "dbg3-2019-west02",
        "dbg6-2019-east02",
        "dbg6-2019-west01",
        "dbg1-2019-west02",
        "dbg7-2019-east01",
        "dbg4-2019-west02",
        "dbg7-2019-east02",
        "dbg1-2019-east01",
        "dbg7-2019-west02",
        "dbg5-2019-west02",
        "dbg1-2019-east02",
        "dbg8-2019-east01",
        "dbg10-2019-east01",
        "dbg9-2019-east01",
        "dbg9-2019-east02",
        "dbg10-2019-east02",
        "dbg8-2019-west01",
        "dbg10-2019-west02",
        "dbg10-2019-west01",
        "dbg3-2019-west01",
        "dbg9-2019-west02",
        "dbg9-2019-west01"
    ];

    public static string DefaultDatabasesCsv => string.Join(", ", DefaultDatabases);

    public static string[] Resolve(ModuleConfigService moduleConfig, IConfiguration config)
    {
        var configured = moduleConfig.GetValue("Migration", "OnPremTargetDatabases");
        if (moduleConfig.IsCorrupt)
            return Array.Empty<string>();

        var moduleValues = Parse(configured);
        if (moduleValues.Length > 0)
            return moduleValues;

        if (!moduleConfig.HasConfigFile)
        {
            var arrayValues = config.GetSection("Migration:OnPremTargetDatabases").Get<string[]>() ?? Array.Empty<string>();
            var cleanedArrayValues = Clean(arrayValues);
            if (cleanedArrayValues.Length > 0)
                return cleanedArrayValues;

            var csvValues = Parse(config["Migration:OnPremTargetDatabases"]);
            if (csvValues.Length > 0)
                return csvValues;
        }

        return DefaultDatabases;
    }

    public static string? PickRandom(IReadOnlyList<string> databases)
    {
        if (databases.Count == 0)
            return null;

        return databases[Random.Shared.Next(databases.Count)];
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
