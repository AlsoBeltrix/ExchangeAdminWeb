namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// Pure, AD-free classifier for a group member's objectClass (plan
/// docs/SelfServiceGroupsMemberListingAndPicker-Plan.md). Kept separate from the live
/// <c>Get-ADGroupMember</c> read so the "which members can be removed here" rule is unit-testable
/// without a domain controller.
///
/// Write scope (nesting plan S4, D2): a plain <c>user</c> and a nested <c>group</c> are removable
/// through this self-service path - group removal happens behind the page's inline
/// warning-and-confirm step, and group ADDS remain excluded entirely (D1, IT Support Desk only).
/// A <c>computer</c> is an AD subclass of <c>user</c> but is NOT removable here, so it is
/// classified as its own kind. Everything else (service principal, contact, unknown) is listed
/// read-only.
/// </summary>
public static class GroupMemberClassifier
{
    /// <summary>
    /// Maps a raw AD objectClass string to a human-readable member kind.
    /// </summary>
    public static string KindOf(string? objectClass)
    {
        if (string.IsNullOrWhiteSpace(objectClass))
            return "Other";

        return objectClass.Trim().ToLowerInvariant() switch
        {
            "user" => "User",
            "computer" => "Computer",
            "group" => "Group",
            _ => "Other",
        };
    }

    /// <summary>
    /// True when the member is a plain user or a nested group - the classes the self-service
    /// REMOVE path may act on (nesting plan S4, D2). A computer (a user subclass) and every other
    /// class stay read-only through this path.
    /// </summary>
    public static bool IsRemovable(string? objectClass)
        => KindOf(objectClass) is "User" or "Group";
}
