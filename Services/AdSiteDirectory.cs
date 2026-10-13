using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Reads the forest's subnet-to-site map out of Active Directory.
/// </summary>
/// <remarks>
/// <para>
/// The directory half of the location answer; <see cref="AdSiteSubnetMap"/> is the arithmetic
/// half. Separate because only this half needs a domain, and only the other half can be proved
/// on a machine with no forest.
/// </para>
/// <para>
/// <b>Environment neutrality.</b> The configuration partition is discovered at runtime from
/// RootDSE, exactly as <c>SectionAccessGroupDirectory</c> does. No domain, site, subnet or
/// address range is named here, defaulted, or made configurable - whatever the host's own
/// forest returns is the map.
/// </para>
/// <para>
/// <b>Failure is FATAL to the report, which is the opposite of the hunting enrichment's rule
/// and is deliberate</b> (plan Q10). A hunting failure greys five columns the operator can work
/// without; a site failure means every row's location is unknown, and the whole reason someone
/// opened this page was to find a machine. Rendering 27,000 rows with a silently blank Site
/// column would send them hunting through a report that cannot answer. It throws instead.
/// </para>
/// </remarks>
public sealed class AdSiteDirectory
{
    private readonly Func<ISectionAccessDirectoryCommands> _commandsFactory;

    public AdSiteDirectory(Func<ISectionAccessDirectoryCommands> commandsFactory)
    {
        _commandsFactory = commandsFactory ?? throw new ArgumentNullException(nameof(commandsFactory));
    }

    /// <summary>
    /// Every subnet object in the forest, with the name of the site it is linked to.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The configuration partition could not be read, or the subnet query failed. Both are
    /// fatal: see the type remarks.
    /// </exception>
    public IReadOnlyList<AdSiteSubnetMap.Subnet> ReadSubnets()
    {
        using var commands = _commandsFactory();

        var rootDse = commands.Invoke("Get-ADRootDSE", new Dictionary<string, object?>
        {
            ["ErrorAction"] = "Stop"
        });

        var configNc = rootDse.Rows.FirstOrDefault(o => o is not null)?.Text("configurationNamingContext");
        if (string.IsNullOrWhiteSpace(configNc))
            throw new InvalidOperationException("Could not read the directory's configuration naming context.");

        // One query for every subnet, not one per device. A 27,000-device report against 531
        // subnets is one directory read; per-device lookups would be 27,000.
        var subnets = commands.Invoke("Get-ADObject", new Dictionary<string, object?>
        {
            ["SearchBase"] = $"CN=Subnets,CN=Sites,{configNc}",
            ["LDAPFilter"] = "(objectClass=subnet)",
            ["Properties"] = new[] { "cn", "siteObject" },
            ["ErrorAction"] = "Stop"
        });

        if (subnets.Error is not null)
            throw new InvalidOperationException($"Reading the forest's subnets failed: {subnets.Error}");

        var result = new List<AdSiteSubnetMap.Subnet>();
        foreach (var row in subnets.Rows)
        {
            if (row is null)
                continue;

            var cidr = row.Text("cn");
            if (string.IsNullOrWhiteSpace(cidr))
                continue;

            // An unlinked subnet is KEPT with an empty site, never skipped. Skipping it would
            // let a broader subnet claim the device and name a site it is not in - see
            // AdSiteSubnetMap.FindSite's remarks. 16 of this forest's 531 were unlinked when
            // last measured.
            result.Add(new AdSiteSubnetMap.Subnet(cidr, SiteNameFromDn(row.Text("siteObject"))));
        }

        return result;
    }

    /// <summary>
    /// The site's own name out of its distinguished name, or empty when there is no link.
    /// </summary>
    /// <remarks>
    /// Takes the first RDN's value rather than fetching each site object: the site's <c>cn</c>
    /// IS the first component of its DN, so a second directory round trip per site would buy
    /// nothing. Returns empty rather than the raw DN for an unexpected shape - a DN rendered in
    /// a Site column is noise an operator cannot act on.
    /// </remarks>
    internal static string SiteNameFromDn(string? siteObjectDn)
    {
        if (string.IsNullOrWhiteSpace(siteObjectDn))
            return "";

        var first = siteObjectDn.Split(',', 2)[0];
        var equals = first.IndexOf('=');
        if (equals < 0 || equals == first.Length - 1)
            return "";

        return first[(equals + 1)..].Trim();
    }
}
