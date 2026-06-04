namespace ExchangeAdminWeb.Services;

public class UndoRegistry
{
    private readonly IReadOnlyList<IUndoableModule> _modules;

    public UndoRegistry(IEnumerable<IUndoableModule> modules)
    {
        _modules = modules.ToList();
    }

    public IUndoableModule? FindHandler(Dictionary<string, object?> auditEvent)
    {
        return _modules.FirstOrDefault(m => m.CanUndo(auditEvent));
    }
}
