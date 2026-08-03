namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// One group as the directory reports it. <see cref="Sid"/> is the identity; <see cref="DisplayName"/>
/// is for humans only and must never reach an authorization comparison.
/// </summary>
public sealed record DirectoryGroupMatch(string Sid, string DisplayName);

/// <summary>
/// Raised when a group lookup could not be performed - the ActiveDirectory module is missing, a
/// domain controller is unreachable, the NetBIOS domain cannot be mapped, or the query threw.
/// </summary>
/// <remarks>
/// Deliberately an exception rather than an empty result. An empty list means "the directory
/// answered and there is no such group", which is a permanent condition an administrator must fix;
/// a failed lookup is transient and must leave the store untouched for the next startup to retry.
/// A caller that cannot tell them apart will eventually treat an outage as a missing group and
/// delete a live access grant. Same reasoning, and the same shape, as
/// <c>ExchangeIdentityResolver.ResolveRecipientAsync</c>, where a null means an affirmative absence
/// and a failure throws.
/// </remarks>
public sealed class DirectoryUnavailableException : Exception
{
    public DirectoryUnavailableException(string message) : base(message) { }
    public DirectoryUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The narrow directory read the section-access SID migration depends on: find the groups a stored
/// name refers to.
/// </summary>
/// <remarks>
/// A seam rather than a direct dependency for the reason <see cref="IOperatorDirectory"/> documents
/// - <c>ADDirectorySearchService</c> is sealed, and unsealing a live-AD service for test
/// convenience widens it for every other caller. It is a second interface rather than a member on
/// that one because the two answer different questions: <see cref="IOperatorDirectory"/> resolves
/// an already-known SID to a user, this resolves an unknown name to zero, one, or several groups,
/// and the plural return is the whole point.
/// </remarks>
public interface ISectionAccessGroupDirectory
{
    /// <summary>
    /// Finds every group matching <paramref name="name"/> exactly, on
    /// <paramref name="netBiosDomain"/> when given and the app's own domain otherwise.
    /// </summary>
    /// <remarks>
    /// Returns all matches rather than a single best one. Two groups answering to one name is the
    /// exact collision this migration removes, so narrowing here - by picking the first, or the
    /// closest, or the local one - would preserve it silently. The caller decides, and its decision
    /// is to refuse.
    /// </remarks>
    /// <exception cref="DirectoryUnavailableException">The lookup could not be performed.</exception>
    IReadOnlyList<DirectoryGroupMatch> FindGroupsByName(string name, string? netBiosDomain);
}
