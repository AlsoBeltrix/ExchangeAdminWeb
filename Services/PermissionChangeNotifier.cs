namespace ExchangeAdminWeb.Services;

/// <summary>
/// Tells the people a permission change actually affected - the trustee and the owner of the
/// mailbox or calendar - that it happened.
/// </summary>
/// <remarks>
/// Audit finding A24. A bulk run wrote a per-row audit record and sent the operator's
/// administrators one summary, and told the affected people nothing. The owner ruling is
/// explicit that mailbox and calendar permission changes notify the affected user; the bulk
/// path was simply never wired to do it, so uploading a CSV was a way to change fifty
/// people's access without any of them hearing about it.
///
/// Shared by the mailbox and calendar processors because the decision is identical and the
/// wording is not: a calendar notice names the target as "<c>someone@example.com
/// (Calendar)</c>" to the trustee while the owner notice uses the bare address.
///
/// Both sends are attempted independently. One failing must not suppress the other, and
/// neither may fail the row: the permission is already applied and already audited, so a
/// mail server being down cannot be allowed to turn an applied change into a reported
/// failure, nor to abandon the rest of the CSV.
/// </remarks>
public static class PermissionChangeNotifier
{
    /// <summary>
    /// Notifies the trustee and the owner, if anything was applied.
    /// </summary>
    /// <param name="appliedRights">
    /// What actually landed. Empty means nothing did, and nobody is told - which is the whole
    /// gate. Callers with a single right pass it only when the operation succeeded; callers
    /// with several pass <c>PermissionResult.AppliedRights</c>, so a partial notifies on the
    /// part that took (audit finding A23).
    /// </param>
    /// <param name="trusteeLabel">How the target is named TO THE TRUSTEE.</param>
    /// <param name="ownerTarget">The mailbox or calendar owner's address.</param>
    public static async Task NotifyAffectedAsync(
        EmailService email,
        IReadOnlyList<string> appliedRights,
        string trusteeLabel,
        string ownerTarget,
        string user,
        string performedBy,
        bool isGrant,
        ILogger logger)
    {
        if (appliedRights.Count == 0)
            return;

        var permType = string.Join("+", appliedRights);

        try
        {
            await email.SendUserNotificationAsync(user, trusteeLabel, performedBy, permType, isGrant);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to notify {User} of a permission change on {Target}", user, ownerTarget);
        }

        try
        {
            await email.SendOwnerNotificationAsync(ownerTarget, user, performedBy, permType, isGrant);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to notify the owner of {Target} of a permission change", ownerTarget);
        }
    }
}
