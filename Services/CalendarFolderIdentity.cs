namespace ExchangeAdminWeb.Services;

/// <summary>
/// Builds the <c>mailbox:\Folder</c> identity that the <c>*-MailboxFolderPermission</c> cmdlets
/// take.
/// </summary>
/// <remarks>
/// Extracted from <see cref="ExchangeServiceBase.GetCalendarFolderName"/> so the string
/// construction is testable: the surrounding method calls <c>Get-MailboxFolderStatistics</c>, so
/// nothing could reach this logic without a live Exchange connection, and
/// <see cref="CalendarPermissionService"/> sat at 0% coverage as a result.
///
/// This is worth testing rather than eyeballing because a wrong identity does not fail loudly -
/// it targets a DIFFERENT FOLDER. Granting Reviewer on the wrong folder of the right mailbox is
/// a silent access grant nobody asked for, and the operator is told it succeeded.
///
/// Two properties it has to get right, both learned from live behavior rather than documentation:
///
/// 1. <b>The folder is not always called "Calendar".</b> Exchange returns the folder path in the
///    mailbox owner's language ("\Kalender", "\Calendrier"), so the name must come from the
///    directory rather than be assumed. That is why the caller looks it up at all.
/// 2. <b>Exchange Online may return forward slashes</b> where the cmdlets require backslashes.
/// </remarks>
public static class CalendarFolderIdentity
{
    /// <summary>The path used when the directory returned no folder path at all.</summary>
    public const string DefaultFolderPath = @"\Calendar";

    /// <summary>
    /// Composes the folder identity from a mailbox and the folder path Exchange reported.
    /// </summary>
    /// <param name="mailbox">
    /// The resolved mailbox identity. This is the value the caller already resolved via
    /// <c>ValidateMailbox</c>, not raw operator input.
    /// </param>
    /// <param name="reportedFolderPath">
    /// <c>FolderPath</c> as returned by <c>Get-MailboxFolderStatistics</c>. Null or blank falls
    /// back to <see cref="DefaultFolderPath"/>.
    /// </param>
    /// <remarks>
    /// TWO DELIBERATE DEVIATIONS from the code this was extracted from, both hardening a case the
    /// original mishandled. Neither is reachable in observed behavior - Exchange returns a
    /// leading-backslash path - but both are cheap and the failure they prevent is silent:
    ///
    /// - <b>Blank is treated as absent.</b> The original used <c>?? @"\Calendar"</c>, so a null
    ///   fell back but an EMPTY string did not, yielding <c>mailbox:</c> - the mailbox ROOT.
    ///   Granting calendar rights on the root grants them across the mailbox.
    /// - <b>A missing leading separator is added.</b> The original would emit
    ///   <c>mailbox:Calendar</c>, which the cmdlets reject - loud, so less dangerous than the
    ///   above, but still a failure for no reason.
    /// </remarks>
    public static string Build(string mailbox, string? reportedFolderPath)
    {
        var folderPath = string.IsNullOrWhiteSpace(reportedFolderPath)
            ? DefaultFolderPath
            : reportedFolderPath;

        // Exchange Online may return forward slashes; the cmdlets require backslashes.
        folderPath = folderPath.Replace("/", @"\");

        if (!folderPath.StartsWith('\\'))
            folderPath = @"\" + folderPath;

        return $"{mailbox}:{folderPath}";
    }
}
