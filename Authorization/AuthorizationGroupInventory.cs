using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// Every grant an authorization gate in this app can grant access through right now - each a
/// group SID and the setting that grants through it - which circuit revalidation compares
/// (aud-a29b, aud-a29d).
/// </summary>
/// <remarks>
/// <para>
/// Re-read on every check, so a grant added after a circuit opened is seen. A grant removed is
/// NOT simply no longer this check's concern: the gate stops honouring it for the next
/// authorization, but a page that already authorized does not ask again. So the provider keeps,
/// per circuit, the grants its circuit was confirmed through, and a captured grant missing from
/// this inventory ends the circuit (aud-a29d). That is also why a grant is a (setting, SID) pair
/// rather than a SID: a group taken out of one section while it stays in another is a removed
/// grant all the same.
/// </para>
/// <para>
/// In: every <c>section_access</c> value across every section - module policies and the
/// <c>ProtectedServicer:</c> keys alike - every module-admin grant, and
/// <c>Security:AllowedGroups</c> / <c>Security:AdminGroups</c>. Stored values pass
/// <see cref="GroupMembershipChecker.UsableSidsOnly"/>, which is how the gates read them. The
/// appsettings lists are not filtered by their gate (review finding sidf-1: they ship as
/// <c>DOMAIN\Name</c>), so a name there is translated to the SID <c>IsInRole</c> would test.
/// While section access is unconfigured, <c>GetGroupsForSection</c> answers every section that is
/// not fail-closed with <c>Security:AllowedGroups</c>, so those SIDs are also a grant of their own
/// (<see cref="UnconfiguredSectionAccessSource"/>), which ends when section access is first saved.
/// </para>
/// <para>
/// A store that cannot be read throws. The gates read an unreadable store as "nothing granted",
/// which denies - right for them, and the wrong way round here, where "nothing granted" would let
/// every circuit pass without a lookup.
/// </para>
/// <para>
/// Only usable domain-account SIDs come out. A well-known or BUILTIN SID is not a directory group
/// <c>tokenGroups</c> can attest to - comparing one would end every session that holds it - and
/// none is a valid section-access subject anyway.
/// </para>
/// <para>
/// Out: the Self-Service Groups DACL gate, which reads arbitrary groups from AD ACLs at the moment
/// of the call; there is no list of them to compare.
/// </para>
/// </remarks>
public sealed class AuthorizationGroupInventory
{
    /// <summary>Prefix of a section-access grant's source; the section key follows.</summary>
    internal const string SectionAccessSource = "SectionAccess:";

    /// <summary>The <c>Security:AllowedGroups</c> fallback while section access is unconfigured. No section key holds '*'.</summary>
    internal const string UnconfiguredSectionAccessSource = "SectionAccess:*";

    /// <summary>Prefix of a module-admin grant's source; the module id follows.</summary>
    internal const string ModuleAdminsSource = "ModuleAdmins:";

    internal const string AllowedGroupsSource = "Security:AllowedGroups";
    internal const string AdminGroupsSource = "Security:AdminGroups";

    private readonly SectionAccessService _sectionAccess;
    private readonly ModuleAdminService _moduleAdmins;
    private readonly IOperatorMembershipDirectory _directory;

    public AuthorizationGroupInventory(
        SectionAccessService sectionAccess,
        ModuleAdminService moduleAdmins,
        IOperatorMembershipDirectory directory)
    {
        _sectionAccess = sectionAccess;
        _moduleAdmins = moduleAdmins;
        _directory = directory;
    }

    /// <summary>Every grant as it stands now. Never an empty answer standing in for a failed read.</summary>
    /// <exception cref="DirectoryUnavailableException">A configured group name could not be translated.</exception>
    /// <exception cref="InvalidOperationException">A grant store could not be read.</exception>
    public AuthorizationInventory Read()
    {
        if (!_sectionAccess.TryGetSectionAccess(out var sectionAccess, out var configured))
            throw new InvalidOperationException("The section-access settings could not be read.");

        if (!_moduleAdmins.TryGetAllGrants(out var moduleAdmins))
            throw new InvalidOperationException("The module-admin settings could not be read.");

        var grants = new HashSet<AuthorizationGrant>();

        foreach (var (section, groups) in sectionAccess)
        {
            foreach (var sid in GroupMembershipChecker.UsableSidsOnly(groups))
                grants.Add(new AuthorizationGrant(SectionAccessSource + section, sid));
        }

        foreach (var (moduleId, groups) in moduleAdmins)
        {
            foreach (var sid in GroupMembershipChecker.UsableSidsOnly(groups))
                grants.Add(new AuthorizationGrant(ModuleAdminsSource + moduleId, sid));
        }

        foreach (var sid in ResolveStaticGroups(_sectionAccess.GetAllowedGroups()))
        {
            grants.Add(new AuthorizationGrant(AllowedGroupsSource, sid));
            if (!configured)
                grants.Add(new AuthorizationGrant(UnconfiguredSectionAccessSource, sid));
        }

        foreach (var sid in ResolveStaticGroups(_sectionAccess.GetAdminGroups()))
            grants.Add(new AuthorizationGrant(AdminGroupsSource, sid));

        return new AuthorizationInventory(grants);
    }

    /// <summary>The distinct SIDs of <see cref="Read"/>: what the directory comparison is about.</summary>
    /// <exception cref="DirectoryUnavailableException">A configured group name could not be translated.</exception>
    /// <exception cref="InvalidOperationException">A grant store could not be read.</exception>
    public IReadOnlySet<string> GetConsultedSids() =>
        Read().Grants.Select(grant => grant.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The usable domain SIDs a static list grants through: SID values as they are, names translated
    // the way IsInRole translates them. A name that maps to nothing grants nothing and is skipped.
    private List<string> ResolveStaticGroups(IEnumerable<string> values)
    {
        var sids = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (SectionAccessGroupIdentity.IsUsableGroupSid(value))
            {
                sids.Add(value);
                continue;
            }

            // SID-shaped but not a usable group (a well-known or BUILTIN SID): nothing to translate.
            if (value.TrimStart().StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                continue;

            var translated = _directory.TranslateGroupName(value);
            if (SectionAccessGroupIdentity.IsUsableGroupSid(translated))
                sids.Add(translated!);
        }

        return sids;
    }
}

/// <summary>
/// One way access can be granted: a group SID and the setting that grants through it - for example
/// <c>SectionAccess:RiskyUsers</c> and a group's SID (aud-a29d).
/// </summary>
/// <remarks>
/// Equal regardless of case, in both parts: section keys are case-insensitive in the store, and a
/// SID is compared the way every gate compares it. Never a group name.
/// </remarks>
public sealed record AuthorizationGrant(string Source, string Sid)
{
    public bool Equals(AuthorizationGrant? other) =>
        other is not null
        && string.Equals(Source, other.Source, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Sid, other.Sid, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Source),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Sid));
}

/// <summary>What <see cref="AuthorizationGroupInventory.Read"/> found: every grant the gates grant through now.</summary>
public sealed record AuthorizationInventory(IReadOnlySet<AuthorizationGrant> Grants);
