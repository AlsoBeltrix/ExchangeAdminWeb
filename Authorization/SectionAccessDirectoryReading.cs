namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// The decisions <see cref="Services.SectionAccessGroupDirectory"/> makes about what the directory
/// returned, separated from the act of asking it.
/// </summary>
/// <remarks>
/// Extracted so these are testable at all. Every path in that service opens a PowerShell runspace
/// and imports the <c>ActiveDirectory</c> module, so nothing could reach this logic without a
/// domain-joined host with RSAT - it sat at 0/115 covered as a result, and grew, which is what
/// pushed the security-critical coverage ratchet below its floor
/// (docs/CoverageRatchetRepair-Plan.md).
///
/// Same move, and the same reason, as <see cref="Services.MailboxPermissionOutcome"/> and
/// <see cref="Services.CalendarFolderIdentity"/> (docs/TestSuiteRemediation-Plan.md D2a): pull out
/// the part where a wrong answer is a SILENT defect, leave the I/O where it is.
///
/// What is deliberately NOT here: the runspace calls, <c>DrainErrors</c>, and the
/// ambiguous-match and missing-SID throws. Those read a live <c>PowerShell</c> object, so faking
/// them means abstracting over PowerShell itself. They remain untested - an accepted gap, recorded
/// in the plan rather than papered over.
///
/// Every member is pure and static: no directory, no runspace, no clock.
/// </remarks>
public static class SectionAccessDirectoryReading
{
    /// <summary>
    /// The DNS root from a crossRef's <c>dnsRoot</c> attribute value, or null when there is none
    /// usable.
    /// </summary>
    /// <remarks>
    /// <c>dnsRoot</c> is multi-valued in the schema, so the value arrives as a string, as a
    /// collection, or as something else entirely depending on how the cmdlet materialised it. The
    /// first entry is the domain's DNS name.
    ///
    /// Getting this wrong is not a crash: it points every subsequent group query at the WRONG
    /// DOMAIN, or at the <c>ToString()</c> of a collection type, and the migration then resolves
    /// names against a directory the operator did not mean. Returning null (rather than a blank or
    /// a garbage string) is what lets the caller fail closed.
    /// </remarks>
    public static string? UnwrapDnsRoot(object? value)
    {
        var dnsRoot = value switch
        {
            string s => s,
            System.Collections.IEnumerable e => e.Cast<object?>().FirstOrDefault()?.ToString(),
            var v => v?.ToString()
        };

        return string.IsNullOrWhiteSpace(dnsRoot) ? null : dnsRoot;
    }

    /// <summary>
    /// The name to show for a group, from the directory attributes in order of preference.
    /// </summary>
    /// <remarks>
    /// sAMAccountName first, which is deliberate and differs from other name handling in this app:
    /// it is the half of <c>DOMAIN\Name</c> that Windows itself uses, so the rendered value matches
    /// what an administrator sees in AD tooling.
    ///
    /// DisplayName is the LAST resort on purpose - it is not unique and need not match the logon
    /// name, so preferring it would show two different groups under one label. That precedence was
    /// previously enforced by nothing.
    ///
    /// <paramref name="fallback"/> is the name that was queried, used when the directory returned
    /// an object with no usable name attribute at all. Blank attributes are treated as absent, not
    /// as answers.
    /// </remarks>
    public static string ChooseBareName(
        string? samAccountName,
        string? name,
        string? displayName,
        string fallback)
    {
        if (!string.IsNullOrWhiteSpace(samAccountName))
            return samAccountName;

        if (!string.IsNullOrWhiteSpace(name))
            return name;

        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName;

        return fallback;
    }

    /// <summary>
    /// Why a matched group's <c>objectSid</c> is unusable, or null when it can be used.
    /// </summary>
    /// <remarks>
    /// The SID is the whole product of this lookup - the display name is decoration. A group whose
    /// SID could not be read cannot become an authorization subject.
    ///
    /// Rejecting rather than skipping is the load-bearing choice. Silently dropping such a row
    /// would UNDERSTATE the match count, and the caller refuses only on ambiguity: two matching
    /// groups where one lost its SID would look like a confident single answer and be migrated as
    /// one. That is a wrong-group grant, arrived at without any error.
    /// </remarks>
    public static string? GroupSidProblem(string? sid, string queriedName) =>
        string.IsNullOrWhiteSpace(sid)
            ? $"A group matching '{queriedName}' returned no readable objectSid."
            : null;

    /// <summary>
    /// Why a partition lookup for <paramref name="netBiosDomain"/> that returned
    /// <paramref name="matchCount"/> crossRefs is unusable, or null when exactly one was returned.
    /// </summary>
    /// <remarks>
    /// Exactly one partition must match. Zero means the NetBIOS name is not a domain in this
    /// forest; more than one means the forest cannot tell the caller which domain was meant. Both
    /// have to stop the migration, because the alternative is querying groups against a domain the
    /// operator did not name and storing whatever SIDs come back.
    ///
    /// The distinct wording matters operationally: "not found" sends an administrator to check the
    /// stored value, "matched N" sends them to check the forest. A single merged message would
    /// send them to the wrong place half the time.
    /// </remarks>
    public static string? PartitionMatchProblem(string netBiosDomain, int matchCount) => matchCount switch
    {
        1 => null,
        0 => $"NetBIOS domain '{netBiosDomain}' matched no forest partition.",
        _ => $"NetBIOS domain '{netBiosDomain}' matched {matchCount} forest partitions; expected exactly one."
    };

    /// <summary>
    /// The NetBIOS domain half of a <c>DOMAIN\Name</c> account string, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The caller passes the result of translating a SID to an <c>NTAccount</c>; this is only the
    /// string split, because the translation itself needs the directory.
    ///
    /// <c>slash &gt; 0</c>, not <c>&gt;= 0</c>, is load-bearing: an account string that BEGINS with
    /// a backslash has no domain half, and treating index 0 as a match would yield an empty domain
    /// and render the group as <c>\Name</c>. Null means "no domain half", which the caller
    /// degrades gracefully around - this decorates a display string and must never fail a lookup
    /// whose real product is the SID.
    /// </remarks>
    public static string? NetBiosFromNTAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
            return null;

        var slash = account.IndexOf('\\');
        return slash > 0 ? account[..slash] : null;
    }
}
