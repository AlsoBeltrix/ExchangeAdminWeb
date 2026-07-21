using Microsoft.Extensions.Configuration;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Single contract for reading the required audit/operational log root (Audit:LogRoot).
/// There is deliberately no baked-in default: an environment that fails to configure a log
/// root must fail loudly, never silently write audit logs to a path baked into the product
/// (see docs/RemoveHardcodedLogRoot-Plan.md and the Constitution Audit section). The app
/// enforces this at startup (Program.cs) so DI singletons and scoped services can assume the
/// value is present; this helper is the defense-in-depth backstop for any service constructed
/// outside the app host.
/// </summary>
public static class AuditLogRoot
{
    /// <summary>Error text shared by the startup guard and the helper so operators see one message.</summary>
    public const string UnsetMessage =
        "Audit:LogRoot is not configured. Set it to an absolute path outside the deploy folder " +
        "(e.g. in appsettings). The app will not start without it so audit logs are never silently misplaced.";

    /// <summary>Returns the configured log root, or throws InvalidOperationException if unset/blank.</summary>
    public static string Require(IConfiguration config)
    {
        var logRoot = config["Audit:LogRoot"];
        if (string.IsNullOrWhiteSpace(logRoot))
            throw new InvalidOperationException(UnsetMessage);
        return logRoot;
    }
}
