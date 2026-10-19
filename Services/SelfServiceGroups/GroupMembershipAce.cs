namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// Pure, AD-free classifier for whether an access-control entry's RIGHTS convey the ability to edit a
/// group's membership (plan docs/SelfServiceGroupManagement-Plan.md section 6.3, list-time eligibility
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
/// <c>ConveysMemberWrite</c> answers only "do these rights, targeting this attribute, convey
/// member-write?", so the same predicate classifies both Allow and Deny ACEs.
///
/// Allow-vs-Deny and trustee matching used to be decided by the live loop in
/// <see cref="SelfServiceGroupService"/>, which needed a domain controller and so was never
/// unit-tested - and that is exactly where audit finding A4 sat. Both now live in
/// <see cref="GroupMembershipAce.GrantsMemberWrite"/>, pure and here, for the same reason the
/// rights classifier is.
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

    /// <summary>One access-control entry, projected to primitives so no ACL type crosses into C#.</summary>
    /// <param name="Sid">The trustee's SID.</param>
    /// <param name="IsDeny">True for a Deny ACE, false for Allow. Anything else is ignored.</param>
    /// <param name="Rights">The ACE's ActiveDirectoryRights value as an int bitmask.</param>
    /// <param name="ObjectType">The ACE's ObjectType GUID; Guid.Empty applies to all properties.</param>
    public readonly record struct Entry(string? Sid, bool IsDeny, int Rights, Guid ObjectType);

    /// <summary>
    /// Whether a DACL grants member-write to a caller, evaluated against their WHOLE token.
    /// </summary>
    /// <remarks>
    /// <b>Audit finding A4.</b> This decision used to live in the live loop in
    /// <see cref="SelfServiceGroupService"/>, which compared each ACE's trustee against the
    /// caller's own SID and skipped everything else - so a Deny carried by a group the caller
    /// belongs to was never seen. An administrator denying member-write to a group and allowing
    /// it to the individual got the allow, and the privileged module credential performed the
    /// write. Windows evaluates a DACL against the whole token.
    ///
    /// It is pure and here, rather than in the service, for the reason the rights classifier
    /// above already is: the live loop needs a domain controller, so nothing in it was testable,
    /// and that is where the defect sat.
    ///
    /// <b>Deny wins over Allow regardless of ACE order.</b> A canonical DACL lists denies first,
    /// but <c>GetAccessRules</c> makes no ordering guarantee, and guessing wrong in this
    /// direction grants access - so every applicable entry is examined before answering. An
    /// entry whose trustee is neither the caller nor one of their groups is not applicable and
    /// is skipped; an entry whose rights do not convey member-write is skipped by
    /// <see cref="ConveysMemberWrite"/>.
    /// </remarks>
    public static bool GrantsMemberWrite(
        IEnumerable<Entry> aces, string callerSid, IReadOnlyCollection<string>? callerGroupSids)
    {
        ArgumentNullException.ThrowIfNull(aces);

        var applicable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(callerSid))
            applicable.Add(callerSid);
        foreach (var groupSid in callerGroupSids ?? [])
        {
            if (!string.IsNullOrWhiteSpace(groupSid))
                applicable.Add(groupSid);
        }

        if (applicable.Count == 0)
            return false;

        var allow = false;
        foreach (var ace in aces)
        {
            if (ace.Sid is null || !applicable.Contains(ace.Sid))
                continue;
            if (!ConveysMemberWrite(ace.Rights, ace.ObjectType))
                continue;

            if (ace.IsDeny)
                return false;
            allow = true;
        }

        return allow;
    }
}
