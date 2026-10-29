namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// kd-58, docs/CancellationDepth-Plan.md A9 (section 13, assumption 8). Whether a job's cancel was asked for - the persisted
/// cancel flag the operator's cancel request sets before it signals the job's token. A processor whose row the token stopped
/// asks it, to tell the operator's cancel from the host stopping: both cancel the token the row was handed, and only the
/// operator's sets the flag. Implemented by <see cref="BulkJobRepository"/>, the flag's store.
/// </summary>
public interface IBulkJobCancelRequests
{
    /// <summary>Whether the operator asked for job <paramref name="jobId"/> to be cancelled.</summary>
    bool IsCancelRequested(string jobId);
}
