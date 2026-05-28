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

            return string.IsNullOrWhiteSpace(version) ? "v0.0.0" : $"v{version}";
        }
    }
}
