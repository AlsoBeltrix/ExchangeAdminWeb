using System.Reflection;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Every dependency the Defender service asks DI for is registered in Program.cs.
/// </summary>
/// <remarks>
/// A missing registration throws at startup and takes the WHOLE APP down, not just this
/// module - and nothing else in the suite would catch it, because every other test constructs
/// the service through its internal test seam and never touches the container.
/// </remarks>
public class DefenderServiceRegistrationTests
{
    /// <summary>Types the framework supplies, which Program.cs never registers by hand.</summary>
    private static readonly string[] FrameworkSupplied =
    [
        "IHttpClientFactory",
        "ILogger",
        "IServiceProvider",
        "IConfiguration",
    ];

    [Fact]
    public void EveryConstructorDependencyOfTheDefenderServiceIsRegistered()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs"));

        // The PUBLIC constructor is the one DI uses. The internal test seam beside it takes
        // factories and overrides and is deliberately not checked.
        var ctor = typeof(DefenderEndpointDeviceService)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single();

        var missing = new List<string>();
        foreach (var parameter in ctor.GetParameters())
        {
            var name = parameter.ParameterType.Name;
            if (FrameworkSupplied.Any(f => name.StartsWith(f, StringComparison.Ordinal)))
                continue;

            // Matches AddSingleton<T>(), AddScoped<T>(), and the AddSingleton(new T(...)) shape
            // used where a registration needs a constructed instance.
            if (!Regex.IsMatch(program, $@"Add(Singleton|Scoped|Transient)(<{Regex.Escape(name)}>|\(new {Regex.Escape(name)}\()"))
                missing.Add(name);
        }

        Assert.True(missing.Count == 0,
            "DefenderEndpointDeviceService asks DI for types Program.cs does not register, which "
            + "throws at startup and takes the whole application down: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheDefenderServiceItselfIsRegistered()
    {
        // The page injects it. Without this the module 404s at runtime while everything else
        // keeps working, which is the quietest of the startup failures.
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs"));

        Assert.Contains("AddSingleton<DefenderEndpointDeviceService>()", program, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ExchangeAdminWeb.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test base directory.");
    }
}
