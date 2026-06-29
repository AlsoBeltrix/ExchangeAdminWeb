using System.Management.Automation;

namespace ExchangeAdminWeb.Models.BlockedSenders;

/// <summary>
/// One blocked-sender entry as returned by <c>Get-BlockedSenderAddress</c>. A blocked sender is a
/// Microsoft 365 account the service has blocked from sending mail (typically after outbound spam).
/// Only the SMTP address is guaranteed present; Reason / blocked date are best-effort, so the
/// mapper tolerates their absence.
/// </summary>
public sealed record BlockedSenderInfo
{
    public required string SenderAddress { get; init; }
    public string? Reason { get; init; }
    public string? BlockedDateRaw { get; init; }

    /// <summary>
    /// Maps a <c>Get-BlockedSenderAddress</c> PSObject to a <see cref="BlockedSenderInfo"/>.
    /// Pure and null-tolerant so it is unit-testable without a live Exchange connection: a row
    /// missing the optional Reason / CreatedDatetime properties maps without throwing. Returns
    /// null only when no usable sender address can be read (such a row is dropped by the caller).
    /// </summary>
    public static BlockedSenderInfo? FromPSObject(PSObject? obj)
    {
        if (obj is null)
            return null;

        var address = PropString(obj, "SenderAddress");
        if (string.IsNullOrWhiteSpace(address))
            return null;

        return new BlockedSenderInfo
        {
            SenderAddress = address,
            Reason = PropString(obj, "Reason"),
            // EXO has used both names across versions; take whichever is present.
            BlockedDateRaw = PropString(obj, "CreatedDatetime") ?? PropString(obj, "BlockedDateTime"),
        };
    }

    private static string? PropString(PSObject obj, string name)
    {
        var value = obj.Properties[name]?.Value;
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
