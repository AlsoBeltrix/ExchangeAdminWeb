namespace ExchangeAdminWeb.Services;

/// <summary>
/// The narrow directory read <see cref="OperatorEmailResolver"/> depends on: resolve one
/// authenticated principal, by SID, to its directory record.
/// </summary>
/// <remarks>
/// This interface exists so the resolver is testable without a domain controller.
/// <see cref="ADDirectorySearchService"/> is sealed, so it cannot be substituted directly;
/// the seam is the interface rather than an unsealed class, because unsealing a live-AD
/// service purely for test convenience widens it for every other caller too.
/// <para>
/// Deliberately one member. Identity resolution must never reach the wildcard autocomplete
/// search on the same service -- see <c>docs/OperatorEmailResolution-Plan.md</c> ("Why the
/// SID") for the four failure modes that motivated separating them.
/// </para>
/// </remarks>
public interface IOperatorDirectory
{
    /// <summary>
    /// Look up a single user by their Windows SID. Returns null when the SID does not
    /// resolve, when AD is unavailable, or on any directory error (fail-soft).
    /// </summary>
    /// <param name="sid">A Windows SID string (e.g. "S-1-5-21-..."). Callers validate the
    /// format; implementations pass it to the directory unmodified.</param>
    ADSearchResult? FindUserBySid(string sid);
}
