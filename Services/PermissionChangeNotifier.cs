namespace ExchangeAdminWeb.Services;

/// <summary>
/// The administrator notification of a permission change whose outcome is unknown. A bulk run's notices to the people it
/// affected - the trustee and the owner of the mailbox or calendar - are <see cref="PermissionChangeDigest"/>'s.
/// </summary>
/// <remarks>
/// Audit finding A24 wired the bulk runs to tell the trustee and the owner (the owner ruling is explicit that mailbox and
/// calendar permission changes notify the affected user). Sweep item 21 moved that notice from here (once per applied row,
/// from inside the loop) to <see cref="PermissionChangeDigest"/>: one mail per affected person after the loop.
/// </remarks>
public static class PermissionChangeNotifier
{
    /// <summary>
    /// aud-a70 (15). What an administrator notification's action carries when the change's outcome is unknown: the subject reads
    /// "FAILED" for anything but a success, and this says the change may have been made all the same.
    /// </summary>
    public const string UnconfirmedSuffix = "_UNCONFIRMED";

    /// <summary>
    /// aud-a70 (15). The administrator notification of a single change its handler's catch files Undetermined - a write that
    /// may have been made, and so a mutating action administrators are told of (Constitution, Notifications): its action
    /// marked <see cref="UnconfirmedSuffix"/>, not a success, the banner as its detail. Sent only when <paramref name="filed"/>
    /// is Undetermined: a failure before the write, or at the connection, wrote nothing. No user or owner notification - they
    /// say access changed, which nobody can say here. A throw is logged against the target, and the result stands.
    /// kd-44 added a notice to the user and the owner that the change could not be confirmed; the owner's ruling of 2026-10-27
    /// ("remove to affected target, keep to admin", <c>.agents/decisions.md</c>) took it out again: administrators alone.
    /// </summary>
    public static async Task NotifyAdminIfUnconfirmedAsync(AuditResult filed, EmailService email, string performedBy,
        string ipAddress, string action, string target, string user, string permissionType, string ticket, string detail,
        ILogger logger)
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
    }
}

/// <summary>
/// Sweep item 21. What a bulk permission run tells the people it affected: one mail per person, after the loop.
/// </summary>
/// <remarks>
/// The A24 fix notified the trustee and the owner from inside the loop, once per applied row, so a 200-row file could send
/// 400 mails one after another and a trustee granted fifty mailboxes got fifty. The owner's ruling of 2026-11-03
/// (<c>.agents/decisions.md</c>, "Bulk-run notifications"): one digest per affected person after the loop, and no mail for a
/// row Exchange treated as a no-op. Each row's changed rights are recorded as the row completes; <see cref="SendAsync"/>
/// sends, per trustee, one mail listing every mailbox or calendar granted or removed to them in the run, and per owner one
/// mail listing every change on their mailbox or calendar. A person with a single change gets the same mail a single-row
/// change sends. Addresses are grouped case-insensitively. Every send is attempted on its own: one failing does not
/// suppress another, and none can change a row's outcome - each row is already applied and audited.
/// </remarks>
public sealed class PermissionChangeDigest
{
    private readonly Dictionary<string, List<(string Target, string Permission)>> _byTrustee = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string User, string Permission)>> _byOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _trusteeOrder = [];
    private readonly List<string> _ownerOrder = [];

    /// <summary>Records one row's change. Nothing changed (<paramref name="changedRights"/> empty) records nothing.</summary>
    /// <param name="changedRights">What the row changed: applied, and not answered by Exchange as a no-op.</param>
    /// <param name="trusteeLabel">How the target is named to the trustee.</param>
    /// <param name="ownerTarget">The mailbox or calendar owner's address.</param>
    /// <param name="user">The trustee.</param>
    public void Record(IReadOnlyList<string> changedRights, string trusteeLabel, string ownerTarget, string user)
    {
        if (changedRights.Count == 0)
            return;

        var permType = string.Join("+", changedRights);

        if (!_byTrustee.TryGetValue(user, out var granted))
        {
            _byTrustee[user] = granted = [];
            _trusteeOrder.Add(user);
        }
        granted.Add((trusteeLabel, permType));

        if (!_byOwner.TryGetValue(ownerTarget, out var onTheirs))
        {
            _byOwner[ownerTarget] = onTheirs = [];
            _ownerOrder.Add(ownerTarget);
        }
        onTheirs.Add((user, permType));
    }

    /// <summary>Sends one mail per trustee and one per owner recorded. Never throws for a failed send.</summary>
    public async Task SendAsync(EmailService email, string performedBy, bool isGrant, ILogger logger)
    {
        foreach (var user in _trusteeOrder)
        {
            var changes = _byTrustee[user];
            try
            {
                if (changes.Count == 1)
                    await email.SendUserNotificationAsync(user, changes[0].Target, performedBy, changes[0].Permission, isGrant);
                else
                    await email.SendUserDigestNotificationAsync(user, changes, performedBy, isGrant);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to notify {User} of {Count} permission change(s) in a bulk run", user, changes.Count);
            }
        }

        foreach (var owner in _ownerOrder)
        {
            var changes = _byOwner[owner];
            try
            {
                if (changes.Count == 1)
                    await email.SendOwnerNotificationAsync(owner, changes[0].User, performedBy, changes[0].Permission, isGrant);
                else
                    await email.SendOwnerDigestNotificationAsync(owner, changes, performedBy, isGrant);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to notify the owner of {Target} of {Count} permission change(s) in a bulk run", owner, changes.Count);
            }
        }
    }
}
