namespace ExchangeAdminWeb.Services;

/// <summary>
/// The organizational units the Module Configuration page's OU browser offers, behind a seam (aud-a67 item 10).
/// </summary>
/// <remarks>
/// <see cref="IGroupGrantDirectory"/>'s shape and its reason (aud-a66): <see cref="ADOrganizationalUnitService"/> is
/// sealed, and the page's own test needs to hold an enumeration while the route moves to another module, which a
/// runspace-backed service cannot be made to do without reaching a directory. The implementation is that service,
/// registered as the same singleton, and the page's calls are unchanged.
/// </remarks>
public interface IOrganizationalUnitDirectory
{
    /// <summary>Whether the last enumeration reached the directory.</summary>
    bool IsAvailable { get; }

    /// <summary>Every organizational unit, cached by the implementation; empty when the directory cannot be read.</summary>
    List<OUEntry> GetOrganizationalUnits();

    /// <summary>Drops the cache, so the next enumeration reads the directory again.</summary>
    void InvalidateCache();
}
