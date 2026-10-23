namespace ExchangeAdminWeb.Services;

/// <summary>
/// aud-a73. The result an audit row is filed under, for a caller that knows more than success or failure. Each name is
/// the text the row carries, which the Admin Event Log's Result filter matches.
/// </summary>
/// <remarks>
/// A permission change whose write threw may or may not have been made. Its row said so in its error, "Whether the change
/// was made could not be confirmed", but was filed Failed, so the event log's Undetermined filter (aud-a28z part 5) could
/// not find it and a reviewer reconciling Exchange read a change that may have applied as one that had not.
/// </remarks>
public enum AuditResult
{
    /// <summary>The operation succeeded.</summary>
    Success,

    /// <summary>The operation failed.</summary>
    Failed,

    /// <summary>Whether the operation succeeded could not be confirmed.</summary>
    Undetermined,
}
