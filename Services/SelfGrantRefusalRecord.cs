namespace ExchangeAdminWeb.Services;

internal static class SelfGrantRefusalRecord
{
    internal const string AuditRowNotWritten = "The audit row could not be written.";

    internal static string Banner(string refusal, Func<JsonlWriteOutcome> write, ILogger logger, string target)
    {
        try
        {
            return write() == JsonlWriteOutcome.Written ? refusal : refusal + " " + AuditRowNotWritten;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit for the self-grant refusal on {Target}", target);
            return refusal + " " + AuditRowNotWritten;
        }
    }
}
