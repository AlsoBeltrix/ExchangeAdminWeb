namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// Pure, AD-free classifier for whether an access-control entry's RIGHTS convey the ability to edit a
/// group's membership (plan docs/SelfServiceGroupManagement-Plan.md §6.3, list-time eligibility
/// enforcement). This is the security-critical core of task 2, kept separate from the live ACL read so
/// it is unit-testable without a domain controller.
///
/// The group "Manager can update membership" checkbox grants the managedBy manager an
/// <c>Allow WriteProperty</c> ACE on the <c>member</c> attribute. Classification MUST key on the
/// access-right BITS, not the ACE's ObjectType name: the <c>member</c> attribute's schema GUID is
/// SHARED with the Self-Membership validated write, so a GUID-name lookup cannot tell "manager may
/// edit members" (WriteProperty) apart from "may add self only" (Self). Only the rights bits
/// distinguish them, so only WriteProperty / GenericWrite / GenericAll qualify here; Self (0x08) never
/// does.
///
/// Allow-vs-Deny and trustee-SID matching are decided by the caller (the live loop in
/// <see cref="SelfServiceGroupService"/>): this method answers only "do these rights, targeting this
/// attribute, convey member-write?" so the same predicate classifies both Allow and Deny ACEs.
/// </summary>
public static class GroupMembershipAce
{
    // ActiveDirectoryRights flag values, verified against .NET System.DirectoryServices
    // (System.DirectoryServices.ActiveDirectoryRights). Held as raw ints so this type takes no
    // dependency on the DirectoryServices assembly and stays pure/testable.
    private const int GenericAll = 0x000F01FF;
    private const int GenericWrite = 0x00020028;
    private const int WriteProperty = 0x00000020;

    /// <summary>
    /// Schema GUID of the AD <c>member</c> attribute, verified against the live schema. WriteProperty
    /// on this attribute is exactly what the group "Manager can update membership" checkbox grants.
    /// </summary>
    public static readonly Guid MemberAttribute = new("bf9679c0-0de6-11d0-a285-00aa003049e2");

    /// <summary>
    /// True when the given access rights, targeting the given object type, convey the ability to write
    /// a group's <c>member</c> attribute (i.e. edit its membership). Does NOT consider Allow/Deny or
    /// the trustee - the caller layers those on.
    ///
    /// Qualifies when the rights include GenericAll or GenericWrite (both carry write to all
    /// properties), or WriteProperty scoped either to the <c>member</c> attribute or to all properties
    /// (an empty/zero ObjectType). A WriteProperty ACE scoped to any OTHER single attribute does not
    /// qualify. Self (0x08) is not among the qualifying bits, so a Self-Membership ACE returns false
    /// here even though it shares the <c>member</c> GUID.
    /// </summary>
    /// <param name="adRights">The ACE's ActiveDirectoryRights value, as an int bitmask.</param>
    /// <param name="objectType">The ACE's ObjectType GUID (Guid.Empty = applies to all properties).</param>
    public static bool ConveysMemberWrite(int adRights, Guid objectType)
    {
        if ((adRights & GenericAll) == GenericAll) return true;
        if ((adRights & GenericWrite) == GenericWrite) return true;
        if ((adRights & WriteProperty) == WriteProperty)
            return objectType == Guid.Empty || objectType == MemberAttribute;
        return false;
    }
}
