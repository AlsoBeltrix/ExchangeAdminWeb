using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// Every group SID an authorization gate in this app can grant access through right now - the set
/// circuit revalidation compares (aud-a29b).
/// </summary>
/// <remarks>
/// <para>
/// Re-read on every check, so a grant added after a circuit opened is compared too, and one removed
/// is no longer this check's concern (the gate itself has stopped honouring it).
/// </para>
/// <para>
/// In: every <c>section_access</c> value across every section - module policies and the
/// <c>ProtectedServicer:</c> keys alike - every module-admin grant, and
/// <c>Security:AllowedGroups</c> / <c>Security:AdminGroups</c>. Stored values pass
/// <see cref="GroupMembershipChecker.UsableSidsOnly"/>, which is how the gates read them. The
/// appsettings lists are not filtered by their gate (review finding sidf-1: they ship as
/// <c>DOMAIN\Name</c>), so a name there is translated to the SID <c>IsInRole</c> would test.
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

    /// <exception cref="DirectoryUnavailableException">A configured group name could not be translated.</exception>
    public IReadOnlySet<string> GetConsultedSids()
    {
        var stored = _sectionAccess.GetSectionAccess().Values
            .SelectMany(groups => groups ?? Array.Empty<string>())
            .Concat(_moduleAdmins.GetAllGrantedGroups());

        var sids = new HashSet<string>(GroupMembershipChecker.UsableSidsOnly(stored), StringComparer.OrdinalIgnoreCase);

        foreach (var value in _sectionAccess.GetAllowedGroups().Concat(_sectionAccess.GetAdminGroups()))
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
