using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Sweep item 21. Whether Exchange answered a permission write with a warning that it changed nothing - the right was
/// already in place for a grant, already absent for a removal, or the folder permission already at the level set.
/// Exchange treats such a write as a success, with no error, so the app counted it as applied and a bulk run mailed the
/// trustee and the owner that access had been granted or removed when it had not changed.
/// </summary>
/// <remarks>
/// Assumption, not probed against a tenant: the wording of Exchange's warnings. Recalled forms are "The appropriate access
/// control entry is already present on the object ..." (Add-MailboxPermission, Add-RecipientPermission), "Can't remove the
/// access control entry on the object ... because the ACE isn't present" (Remove-MailboxPermission,
/// Remove-RecipientPermission) and "The command completed successfully but no settings of '...' have been modified"
/// (Set-MailboxFolderPermission at the level the entry already holds). A wording this does not recognise reads as a
/// change, which is what the app did before: the only cost of a miss is the mail that was always sent.
/// </remarks>
public static class ExchangeNoChangeWarning
{
    /// <summary>Whether any of <paramref name="warnings"/> says the write changed nothing.</summary>
    public static bool SaysNothingChanged(IEnumerable<string?> warnings) => warnings.Any(Says);

    /// <summary>The warning stream of <paramref name="ps"/>, read as <see cref="SaysNothingChanged(IEnumerable{string?})"/>.</summary>
    public static bool SaysNothingChanged(PowerShell ps) =>
        SaysNothingChanged(ps.Streams.Warning.Select(w => w?.Message));

    private static bool Says(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.Contains("no settings of", StringComparison.OrdinalIgnoreCase)
            && text.Contains("have been modified", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!text.Contains("access control entry", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("ACE", StringComparison.Ordinal))
            return false;

        return text.Contains("already present", StringComparison.OrdinalIgnoreCase)
            || text.Contains("isn't present", StringComparison.OrdinalIgnoreCase)
            || text.Contains("is not present", StringComparison.OrdinalIgnoreCase)
            || text.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }
}
