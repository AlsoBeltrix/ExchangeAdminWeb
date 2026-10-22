namespace ExchangeAdminWeb.Services;

/// <summary>
/// Which operations the Admin Event Log shows as reversed: those a SUCCESSFUL later record names
/// as its <c>originalOperationId</c>.
/// </summary>
/// <remarks>
/// aud-a26c. The page marked every operation any record named, whatever that record's result. The
/// undo service names the original on every outcome - success, a failed save, an exception - so a
/// reversal that was denied or threw marked its edit "Reversed" and hid its Undo button: the log
/// said a change was retracted while it was still in place, and took away the way to retry.
///
/// Generic over the entry type purely so the rule can be tested: the page's entry record is
/// private to the page (the <see cref="AuditFirstMerge"/> precedent).
/// </remarks>
public static class ReversedOperations
{
    public static HashSet<string> From<T>(
        IEnumerable<T> events,
        Func<T, string?> originalOperationId,
        Func<T, string?> result)
    {
        var reversed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
        {
            var original = originalOperationId(evt);
            if (!string.IsNullOrEmpty(original)
                && string.Equals(result(evt), "Success", StringComparison.OrdinalIgnoreCase))
            {
                reversed.Add(original);
            }
        }

        return reversed;
    }
}
