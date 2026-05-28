using System.Reflection;

namespace ExchangeAdminWeb;

public static class BuildInfo
{
    public static string DisplayVersion
    {
        get
        {
            var version = typeof(BuildInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            var cleanVersion = version?.Split('+', 2)[0];
            return string.IsNullOrWhiteSpace(cleanVersion) ? "v0.0.0" : $"v{cleanVersion}";
        }
    }
}
