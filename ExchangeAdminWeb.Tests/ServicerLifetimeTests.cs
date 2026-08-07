using ExchangeAdminWeb.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// <see cref="ProtectedPrincipalServicerService"/> must stay injectable by the SINGLETON services
/// that authorise protected-principal servicing.
/// </summary>
/// <remarks>
/// `PermissionValidator` (Mailbox Permissions, Calendar, Out of Office) and
/// `M365GroupManagementService` are registered as singletons. A singleton cannot depend on a
/// scoped service: the container refuses it under scope validation, and where validation is off it
/// silently captures one scope's instance for the lifetime of the process.
///
/// This service was registered Scoped, which made three planned slices unbuildable - and the
/// obvious workarounds (an ambient principal, a nullable-defaulting one, resolving from the root
/// provider) are precisely the fail-open shapes the plan forbids. Found by grok reviewing the plan
/// as F3, before any of those slices was attempted.
///
/// The check is a real container, built and validated, rather than a source assertion: this repo
/// does not set ValidateOnBuild, so a captive dependency would NOT fail at startup in production -
/// it would just quietly hold a stale scope. That makes it exactly the class of defect a test has
/// to catch, because running the app would not.
/// </remarks>
public class ServicerLifetimeTests
{
    [Fact]
    public void ASingletonCanDependOnTheServicerService()
    {
        // Mirrors the real shape: a singleton consumer resolving the servicer. Validated on build
        // AND on scope, so a lifetime regression fails here instead of shipping.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ProtectedPrincipalServicerService>();
        services.AddSingleton<SingletonConsumer>();

        // A fake for the one dependency, so this test is about LIFETIME and not about wiring up
        // the whole configuration store.
        services.AddSingleton<SectionAccessService>(_ => null!);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var consumer = provider.GetRequiredService<SingletonConsumer>();
        Assert.NotNull(consumer);
    }

    [Fact]
    public void TheServicerService_IsRegisteredAsASingleton()
    {
        // Guards the registration itself. The container test above would still pass if someone
        // registered it Scoped AND moved PermissionValidator to Scoped to match - a change that
        // would silently move an authorization check onto per-request lifetimes. Pinning the
        // registration keeps that a deliberate decision rather than a drift.
        var source = ReadProgram();

        Assert.Contains("AddSingleton<ProtectedPrincipalServicerService>()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddScoped<ProtectedPrincipalServicerService>()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSingletonConsumersOfTheServicer_AreStillSingletons()
    {
        // If either of these becomes scoped later, the singleton requirement above stops being
        // load-bearing and this test should be revisited deliberately rather than left asserting
        // something nobody needs.
        var source = ReadProgram();

        Assert.Contains("AddSingleton<PermissionValidator>()", source, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<M365GroupManagementService>()", source, StringComparison.Ordinal);
    }

    /// <summary>A stand-in for PermissionValidator: a singleton that needs the servicer.</summary>
    private sealed class SingletonConsumer
    {
        public SingletonConsumer(ProtectedPrincipalServicerService servicers) => Servicers = servicers;

        public ProtectedPrincipalServicerService Servicers { get; }
    }

    private static string ReadProgram()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "Program.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate Program.cs from the test base directory.");
    }
}
