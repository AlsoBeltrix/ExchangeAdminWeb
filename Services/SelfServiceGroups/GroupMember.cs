namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// A normalized direct member of an on-prem AD group (plan
/// docs/SelfServiceGroupsMemberListingAndPicker-Plan.md, member listing). Projected from
/// <c>Get-ADGroupMember</c> to primitives so no System.DirectoryServices type crosses into C#.
///
/// <see cref="IsRemovable"/> reflects the write scope (nesting plan S4): USER and GROUP members
/// can be removed through this self-service path - a group behind the page's inline
/// warning-and-confirm step (D2), and group ADDS stay excluded entirely (D1). Computers, service
/// principals and other classes are listed read-only so the manager sees the full membership but
/// cannot remove them here.
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

    /// <summary>True for user and group members - the classes the remove path may act on (S4).</summary>
    public bool IsRemovable { get; init; }
}
