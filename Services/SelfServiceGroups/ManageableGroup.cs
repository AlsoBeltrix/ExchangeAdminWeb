namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// A normalized on-prem AD group the signed-in user owns and may be able to manage (plan
/// docs/SelfServiceGroupManagement-Plan.md task 1). "Owns" means the user is the group's
/// <c>managedBy</c> or appears in the Exchange multi-owner <c>msExchCoManagedByLink</c>. Ownership
/// alone does NOT grant management - the fail-closed eligibility rule (task 2) is applied on top
/// before <see cref="CanManageMembers"/> is true.
/// </summary>
public sealed record ManageableGroup
{
    /// <summary>Immutable directory id (objectGUID as a string) - the write target (codex F11).</summary>
    public required string ObjectGuid { get; init; }

    /// <summary>Distinguished name, for display/resolution; never the sole write key.</summary>
    public required string DistinguishedName { get; init; }

    public required string Name { get; init; }
    public string SamAccountName { get; init; } = "";
    public string? Description { get; init; }

    /// <summary>e.g. "Security (Global)" / "Distribution (Universal)".</summary>
    public string GroupType { get; init; } = "";

    /// <summary>Display names of other owners (managedBy + co-managers), excluding the caller.</summary>
    public IReadOnlyList<string> OtherOwners { get; init; } = [];

    /// <summary>True only after the eligibility rule (task 2) clears this group for the caller.</summary>
    public bool CanManageMembers { get; init; }
}
