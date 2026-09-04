using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Publishes the circuit's <see cref="UsageSession.Id"/> as the ambient
/// <see cref="UsageSession.Current"/> for the duration of every inbound circuit activity
/// (docs/UsageTelemetry-Plan.md, AC4; review finding ute-3).
/// </summary>
/// <remarks>
/// <see cref="CreateInboundActivityHandler"/> wraps every inbound activity - event handlers,
/// JS interop callbacks - in the same async flow, so anything the activity calls, however
/// deep, observes the value. That is how an action recorded from inside
/// <see cref="AuditService"/> (a singleton, which cannot see a scoped service) still carries
/// the visit it belonged to.
///
/// The id cannot leak out of the activity: an async method's AsyncLocal writes are confined to
/// its own flow and are not visible to whatever invoked it. The prior value is still captured
/// and restored in a finally rather than cleared, so that nothing later in this flow - a nested
/// activity, a continuation - observes a stale id.
/// </remarks>
public sealed class UsageSessionCircuitHandler : CircuitHandler
{
    private readonly UsageSession _session;

    public UsageSessionCircuitHandler(UsageSession session) => _session = session;

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return async context =>
        {
            var prior = UsageSession.Current.Value;
            UsageSession.Current.Value = _session.Id;
            try
            {
                await next(context);
            }
            finally
            {
                UsageSession.Current.Value = prior;
            }
        };
    }
}
