namespace ExchangeAdminWeb.Services;

/// <summary>
/// Combines audit records with diagnostic records for display under a total cap, without
/// letting diagnostics push audit records out.
/// </summary>
/// <remarks>
/// Audit finding A20. The Admin Event Log filled one FIFO with all three sources - audit
/// first, then traces, then extended diagnostics - and trimmed it by dequeuing the oldest
/// ENQUEUED entry. Because audit went in first, every diagnostic line past the cap evicted an
/// audit record, whatever its timestamp, and the sort by time happened afterwards on whatever
/// had survived.
///
/// Generic over the entry type purely so this rule can be tested: the page's entry record is
/// private to the page.
/// </remarks>
public static class AuditFirstMerge
{
    /// <summary>
    /// Every audit record, plus the newest diagnostics that fit in the remaining room, sorted
    /// newest first.
    /// </summary>
    /// <remarks>
    /// Audit records are never dropped to make space for a diagnostic. They can still exceed
    /// the cap on their own - that is a real truncation and the caller warns about it - but
    /// what gets dropped then is the oldest audit, not an arbitrary one displaced by a trace.
    /// </remarks>
    public static List<T> Merge<T, TKey>(
        IReadOnlyCollection<T> audit,
        IReadOnlyCollection<T> diagnostics,
        int cap,
        Func<T, TKey> timestamp)
        where TKey : IComparable<TKey>
    {
        var room = Math.Max(0, cap - audit.Count);

        return audit
            .Concat(diagnostics.OrderByDescending(timestamp).Take(room))
            .OrderByDescending(timestamp)
            .ToList();
    }
}
