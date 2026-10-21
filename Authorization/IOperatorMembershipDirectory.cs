namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// What the directory says about an account now: its transitive group SIDs (<c>tokenGroups</c>)
/// and whether it is enabled.
/// </summary>
public sealed record OperatorDirectoryMembership(IReadOnlySet<string> GroupSids, bool AccountEnabled);

/// <summary>
/// The directory reads circuit revalidation (aud-a29b) depends on.
/// </summary>
/// <remarks>
/// A seam so the decisions around it - what counts as removed, what counts as a failure - are
/// provable without a domain controller. Production is <see cref="Services.OperatorMembershipDirectory"/>,
/// over the same <see cref="ISectionAccessDirectoryCommands"/> runspace seam the section-access
/// lookup uses. The name translation sits here as well because the same check needs both: one seam,
/// one fake.
/// </remarks>
public interface IOperatorMembershipDirectory
{
    /// <summary>
    /// The current membership of the account <paramref name="userSid"/>, read from a domain
    /// controller of <paramref name="netBiosDomain"/> when given, of the host's own domain otherwise.
    /// </summary>
    /// <remarks>
    /// Never an empty answer standing in for a failure. An account with no readable group or no
    /// readable enabled state, or a lookup that did not run, throws: an empty membership would read
    /// as "removed from everything" and could not be told apart from an outage.
    /// </remarks>
    /// <exception cref="DirectoryUnavailableException">The lookup could not be performed.</exception>
    Task<OperatorDirectoryMembership> GetMembershipAsync(
        string userSid, string? netBiosDomain, CancellationToken cancellationToken);

    /// <summary>
    /// The SID <c>WindowsPrincipal.IsInRole</c> would test for a configured group NAME, or null
    /// when the name maps to no account.
    /// </summary>
    /// <remarks>
    /// <c>Security:AdminGroups</c> ships as <c>DOMAIN\Name</c>, and the policy handler authorizes it
    /// through <c>IsInRole</c>, which translates the name and tests the token for that SID. This is
    /// the same translation, so the SID compared is the one that granted access. A name that maps to
    /// nothing cannot grant through <c>IsInRole</c> either, so null is an answer, not a failure.
    /// </remarks>
    /// <exception cref="DirectoryUnavailableException">The translation could not be performed.</exception>
    string? TranslateGroupName(string groupName);
}
