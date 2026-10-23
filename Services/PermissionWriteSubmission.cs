namespace ExchangeAdminWeb.Services;

/// <summary>
/// aud-a76. A permission write that failed before its command was sent to Exchange: the connection it needed was refused or
/// could not be had - Exchange Online's pool refused the borrow or could not connect, or on-premises Exchange's credentials,
/// throttle, runspace or session could not be had. Nothing was written.
/// </summary>
/// <remarks>
/// Its message is its cause's, restated, so a caller that reads only the message - the bulk loops' error rows - reads what it
/// read before. The cause is its <see cref="Exception.InnerException"/>: <see cref="PermissionChangeFailure.Banner"/> reads the
/// cause's own words from there.
/// </remarks>
public sealed class PermissionWriteNotSubmittedException(Exception cause) : InvalidOperationException(cause.Message, cause)
{
}

/// <summary>
/// aud-a76. Where one permission write's command is handed to Exchange. The write calls <see cref="Submitting"/> immediately
/// before it invokes its first write command; a throw that comes out of <see cref="TrackAsync{T}"/> before then is a
/// <see cref="PermissionWriteNotSubmittedException"/>, and one after it goes out as it is - a write whose outcome is unknown.
/// </summary>
/// <remarks>
/// The pages file a throw at the write step Undetermined (aud-a73, aud-a75), so a borrow the pool refused or a session that
/// could not be opened - before anything was sent - read as a change that may have applied. One instance per write call. The
/// mark is never cleared: a write the pool retries (the Calendar removal) whose first attempt reached its command stays
/// submitted whatever the retry's borrow does, so "nothing was written" never rests on the pool's reading of the error it
/// retried on.
/// </remarks>
internal sealed class PermissionWriteSubmission
{
    private int _submitted;

    /// <summary>The write's first command is about to be invoked: from here on a throw may have written.</summary>
    internal void Submitting() => Volatile.Write(ref _submitted, 1);

    /// <summary>Whether <see cref="Submitting"/> has been called.</summary>
    internal bool Submitted => Volatile.Read(ref _submitted) == 1;

    /// <summary>
    /// The result of <paramref name="step"/> as it is. A throw from it - one before it returns a task included - is a
    /// <see cref="PermissionWriteNotSubmittedException"/> while nothing has been submitted, and goes out as it is once
    /// something has.
    /// </summary>
    internal async Task<T> TrackAsync<T>(Func<Task<T>> step)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (!Submitted)
        {
            throw new PermissionWriteNotSubmittedException(ex);
        }
    }
}
