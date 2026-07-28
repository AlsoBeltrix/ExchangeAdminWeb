namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// Pure, AD-free classifier for a group member's objectClass (plan
/// docs/SelfServiceGroupsMemberListingAndPicker-Plan.md). Kept separate from the live
/// <c>Get-ADGroupMember</c> read so the "which members can be removed here" rule is unit-testable
/// without a domain controller.
///
/// First-cut write scope is USER-ONLY (matching the shipped module's add/remove constraint,
/// `docs/SelfServiceGroupManagement-Plan.md` section 6.5, codex F7): only a plain <c>user</c> is
/// removable. A <c>computer</c> is an AD subclass of <c>user</c> but is NOT a removable user member
/// here, so it is classified as its own kind and is not removable. Everything else (group, service
/// principal, contact, unknown) is listed read-only.
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
    /// True only when the member is a plain user - the first-cut removable scope. A computer (a user
    /// subclass), a group, or any other class is not removable through this self-service path.
    /// </summary>
    public static bool IsRemovable(string? objectClass)
        => KindOf(objectClass) == "User";
}
