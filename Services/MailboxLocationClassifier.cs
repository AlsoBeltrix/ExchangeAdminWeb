namespace ExchangeAdminWeb.Services;

public static class MailboxLocationClassifier
{
    public static string ForLookupDisplay(string? recipientTypeDetails)
    {
        if (IsOnPremisesMailbox(recipientTypeDetails))
            return "On-Premises";

        if (IsCloudMailbox(recipientTypeDetails))
            return "Cloud";

        return "Unknown";
    }

    public static string ForOperationRouting(string? recipientTypeDetails)
    {
        if (IsOnPremisesMailbox(recipientTypeDetails))
            return "OnPrem";

        if (IsCloudMailbox(recipientTypeDetails))
            return "Cloud";

        return "Unknown";
    }

    public static bool IsOnPremisesMailbox(string? recipientTypeDetails)
    {
        var type = Normalize(recipientTypeDetails);
        if (string.IsNullOrEmpty(type))
            return false;

        return type.Contains("Remote", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "MailUser", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCloudMailbox(string? recipientTypeDetails)
    {
        var type = Normalize(recipientTypeDetails);
        if (string.IsNullOrEmpty(type) || IsOnPremisesMailbox(type))
            return false;

        return type.Contains("Mailbox", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? recipientTypeDetails)
        => recipientTypeDetails?.Trim() ?? string.Empty;
}