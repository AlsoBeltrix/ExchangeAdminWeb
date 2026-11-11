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
    /// The diagnostics that fit after every audit record has taken its place, newest first.
    /// </summary>
    /// <remarks>
    /// Audit records are never dropped to make space for a diagnostic. They can still exceed
    /// the cap on their own - a real truncation the caller warns about - and the caller can
    /// report how many diagnostics were dropped, so an operator knows WHICH kind of entry went
    /// missing.
    /// </remarks>
    public static List<T> FitDiagnostics<T, TKey>(
        int auditCount,
        IReadOnlyCollection<T> diagnostics,
        int cap,
        Func<T, TKey> timestamp)
        where TKey : IComparable<TKey> =>
        diagnostics.OrderByDescending(timestamp).Take(Math.Max(0, cap - auditCount)).ToList();
}
