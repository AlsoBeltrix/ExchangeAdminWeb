namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// Maps a module id to the <see cref="IBulkJobProcessor"/> that handles its jobs. Registered as a
/// singleton listing the processor types; the runner resolves the concrete processor from a fresh
/// DI scope per job (so scoped module services are constructed and disposed with the job).
///
/// This keeps the runner module-agnostic: it never references ConferenceRoomService (or any module)
/// directly — it looks the processor up by the job's <see cref="BulkJob.ModuleId"/>.
/// </summary>
public sealed class BulkJobProcessorRegistry
{
    private readonly Dictionary<string, Type> _byModuleId;

    public BulkJobProcessorRegistry(IEnumerable<KeyValuePair<string, Type>> registrations)
    {
        _byModuleId = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var (moduleId, type) in registrations)
            _byModuleId[moduleId] = type;
    }

    /// <summary>The processor implementation type for a module id, or null if none is registered.</summary>
    public Type? GetProcessorType(string moduleId) =>
        _byModuleId.TryGetValue(moduleId, out var type) ? type : null;

    public bool IsRegistered(string moduleId) => _byModuleId.ContainsKey(moduleId);
}
