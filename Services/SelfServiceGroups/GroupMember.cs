namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// A normalized direct member of an on-prem AD group (plan
/// docs/SelfServiceGroupsMemberListingAndPicker-Plan.md, member listing). Projected from
/// <c>Get-ADGroupMember</c> to primitives so no System.DirectoryServices type crosses into C#.
///
/// <see cref="IsRemovable"/> reflects the first-cut write scope: only USER members can be removed
/// through this self-service path (matching the user-only add/remove constraint of the shipped
/// module, `docs/SelfServiceGroupManagement-Plan.md` section 6.5, codex F7). Non-user members
/// (nested groups, computers, service principals) are listed read-only so the manager sees the full
/// membership but cannot remove them here.
/// </summary>
public sealed record GroupMember
{
    /// <summary>Immutable directory id (objectGUID as a string).</summary>
    public required string ObjectGuid { get; init; }

    /// <summary>Distinguished name, for display.</summary>
    public required string DistinguishedName { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>An identity (sAMAccountName / UPN) the remove path can resolve; empty when unknown.</summary>
    public string Identity { get; init; } = "";

    /// <summary>Human-readable member kind: "User" / "Group" / "Computer" / "Other".</summary>
    public string Kind { get; init; } = "";

    /// <summary>True only for user members - the first-cut removable scope.</summary>
    public bool IsRemovable { get; init; }
}
