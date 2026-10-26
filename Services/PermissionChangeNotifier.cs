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
    /// aud-a70 (15). What an administrator notification's action carries when the change's outcome is unknown: the subject reads
    /// "FAILED" for anything but a success, and this says the change may have been made all the same.
    /// </summary>
    public const string UnconfirmedSuffix = "_UNCONFIRMED";

    /// <summary>
    /// aud-a70 (15). The notifications of a single change its handler's catch files Undetermined - a write that may have been
    /// made, and so a mutating action administrators are told of (Constitution, Notifications): its action marked
    /// <see cref="UnconfirmedSuffix"/>, not a success, the banner as its detail. kd-44, revising (15): and the affected user
    /// and the owner, told it may have changed (<see cref="NotifyAffectedOfUnconfirmedAsync"/>) - the grant and removal
    /// notices say access changed, which nobody can say here, but a change that may have applied is not left silent to them.
    /// Sent only when <paramref name="filed"/> is Undetermined: a failure before the write, or at the connection, wrote
    /// nothing. A throw is logged against the target, and the result stands.
    /// </summary>
    public static async Task NotifyIfUnconfirmedAsync(AuditResult filed, EmailService email, string performedBy,
        string ipAddress, string action, string target, string user, string permissionType, string ticket, string detail,
        string trusteeLabel, string affectedPermission, bool isGrant, ILogger logger)
    {
        if (filed != AuditResult.Undetermined)
            return;

        try
        {
            await email.SendAdminNotificationAsync(performedBy, ipAddress, action + UnconfirmedSuffix, target, user,
                permissionType, false, ticket, detail);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The administrator notification for {Target} could not be sent; the change stands", target);
        }

        await NotifyAffectedOfUnconfirmedAsync(email, [affectedPermission], trusteeLabel, target, user, performedBy, isGrant, logger);
    }

    /// <summary>
    /// kd-44. Tells the trustee and the owner that a change whose outcome could not be confirmed may have altered access to
    /// <paramref name="ownerTarget"/> (<see cref="EmailService.SendUnconfirmedUserNotificationAsync"/> and its owner twin:
    /// neutral wording - what may have changed, and to contact the IT Service Desk). Nothing is sent when
    /// <paramref name="unconfirmedRights"/> is empty. Each send is guarded on its own: a throw is logged and the other is still
    /// sent, and the result stands.
    /// </summary>
    public static async Task NotifyAffectedOfUnconfirmedAsync(EmailService email, IReadOnlyList<string> unconfirmedRights,
        string trusteeLabel, string ownerTarget, string user, string performedBy, bool isGrant, ILogger logger)
    {
        if (unconfirmedRights.Count == 0)
            return;

        var permType = string.Join("+", unconfirmedRights);

        try
        {
            await email.SendUnconfirmedUserNotificationAsync(user, trusteeLabel, performedBy, permType, isGrant);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The notification to {User} of the unconfirmed change on {Target} could not be sent; the result stands", user, ownerTarget);
        }

        try
        {
            await email.SendUnconfirmedOwnerNotificationAsync(ownerTarget, user, performedBy, permType, isGrant);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The notification to the owner of {Target} of the unconfirmed change could not be sent; the result stands", ownerTarget);
        }
    }

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
