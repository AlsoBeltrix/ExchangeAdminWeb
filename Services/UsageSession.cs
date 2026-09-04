namespace ExchangeAdminWeb.Services;

/// <summary>
/// A throwaway per-circuit identifier, minted when the scope is created and gone when the
/// circuit is (docs/UsageTelemetry-Plan.md, AC4). It is NOT an identity: it is written only to
/// the <c>session</c> column of the anonymous usage store, is never persisted anywhere else,
/// and cannot be resolved back to a person. It exists so a visit can be analysed as a visit -
/// "opened three modules, acted in one" - which per-row counting alone cannot answer.
/// </summary>
public sealed class UsageSession
{
    /// <summary>This circuit's id. Random per scope; no relation to the account or the IP.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The ambient session id for the current inbound circuit activity, or null when there is
    /// no circuit (a background job, startup, an HTTP request outside Blazor).
    /// <see cref="UsageSessionCircuitHandler"/> is the only writer; the singleton
    /// <see cref="UsageTelemetryService"/> is the only reader. It is deliberately a static
    /// <see cref="AsyncLocal{T}"/>: a singleton cannot resolve a scoped service, and the audit
    /// path that records actions runs deep inside the call stack of a circuit activity.
    /// </summary>
    public static readonly AsyncLocal<string?> Current = new();
}
