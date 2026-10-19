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
    /// <summary>
    /// Schema GUID of the <c>Membership</c> property set, which CONTAINS the <c>member</c>
    /// attribute. An ACE scoped to the property set applies to every attribute in it.
    /// </summary>
    /// <remarks>
    /// Added for aud-a4b finding 4: a Deny scoped here was skipped, because the classifier
    /// accepted only the member attribute's own GUID, and a following Allow then authorised the
    /// write. Windows applies the property-set ACE to <c>member</c>.
    /// </remarks>
    public static readonly Guid MembershipPropertySet = new("bc0ac240-79a9-11d0-9020-00c04fc2d4cf");

    public static bool ConveysMemberWrite(int adRights, Guid objectType)
    {
        // An OBJECT-SPECIFIC ace keeps its scope even when the mask is a composite one.
        // aud-a4b finding 5: this returned true for GenericWrite/GenericAll against ANY
        // ObjectType, so an Allow of 0x00020028 scoped to an unrelated attribute passed as
        // though it wrote every property. Expanded rights do not discard the ACE's ObjectType.
        var scoped = objectType != Guid.Empty;
        var appliesToMember = !scoped || objectType == MemberAttribute || objectType == MembershipPropertySet;

        if (!appliesToMember)
            return false;

        if ((adRights & GenericAll) == GenericAll) return true;
        if ((adRights & GenericWrite) == GenericWrite) return true;
        return (adRights & WriteProperty) == WriteProperty;
    }

    /// <summary>One access-control entry, projected to primitives so no ACL type crosses into C#.</summary>
    /// <param name="Sid">The trustee's SID.</param>
    /// <param name="IsDeny">True for a Deny ACE, false for Allow. Anything else is ignored.</param>
    /// <param name="Rights">The ACE's ActiveDirectoryRights value as an int bitmask.</param>
    /// <param name="ObjectType">The ACE's ObjectType GUID; Guid.Empty applies to all properties.</param>
    /// <param name="IsInherited">
    /// True for an ACE inherited from a parent container. Canonical order puts explicit ACEs
    /// first, and the distinction decides outcomes - see <see cref="GrantsMemberWrite"/>.
    /// </param>
    /// <param name="InheritOnly">
    /// True when the ACE carries <c>INHERIT_ONLY_ACE</c>, meaning it does NOT apply to this
    /// object - only to children. aud-a4b finding 3: these were being read as effective
    /// grants.
    /// </param>
    public readonly record struct Entry(
        string? Sid, bool IsDeny, int Rights, Guid ObjectType, bool IsInherited, bool InheritOnly);

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
        IEnumerable<Entry> aces, string callerSid, IReadOnlyCollection<string>? callerGroupSids,
        IReadOnlyCollection<string>? denyOnlySids = null)
    {
        ArgumentNullException.ThrowIfNull(aces);

        // Trustees whose ACEs can GRANT. A deny-only SID is deliberately absent: Windows keeps
        // such memberships in the token for denial purposes only, so letting one satisfy an
        // Allow would invent access (aud-a4b finding 6).
        var granting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(callerSid))
            granting.Add(callerSid);
        foreach (var groupSid in callerGroupSids ?? [])
        {
            if (!string.IsNullOrWhiteSpace(groupSid))
                granting.Add(groupSid);
        }

        // Trustees whose ACEs can DENY - everything above, plus the deny-only memberships.
        var denying = new HashSet<string>(granting, StringComparer.OrdinalIgnoreCase);
        foreach (var denyOnly in denyOnlySids ?? [])
        {
            if (!string.IsNullOrWhiteSpace(denyOnly))
                denying.Add(denyOnly);
        }

        if (granting.Count == 0)
            return false;

        // CANONICAL ORDER, which is what Windows actually does and what aud-a4 got wrong.
        // Explicit ACEs are evaluated before inherited ones, and within each group Deny before
        // Allow; the first decisive match wins. So an explicit Allow DOES defeat an inherited
        // Deny - aud-a4 refused that case and called it fail-closed, which was simply
        // incorrect. Re-sorting here rather than trusting the stored order is deliberate:
        // GetAccessRules guarantees no ordering, and a non-canonical DACL must still be
        // evaluated canonically.
        foreach (var inherited in new[] { false, true })
        {
            foreach (var deny in new[] { true, false })
            {
                foreach (var ace in aces)
                {
                    // INHERIT_ONLY never applies to the object carrying it (finding 3).
                    if (ace.InheritOnly) continue;
                    if (ace.IsInherited != inherited || ace.IsDeny != deny) continue;
                    if (ace.Sid is null) continue;
                    if (!(deny ? denying : granting).Contains(ace.Sid)) continue;
                    if (!ConveysMemberWrite(ace.Rights, ace.ObjectType)) continue;

                    return !deny;
                }
            }
        }

        return false;
    }
}
