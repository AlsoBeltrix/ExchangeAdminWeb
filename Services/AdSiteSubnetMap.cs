using System.Net;
using System.Net.Sockets;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Maps a device IP address to the Active Directory site whose subnet contains it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the module's answer to the question it exists for.</b> Defender knows a device's
/// IP for essentially every row; it does not know where that is. AD already holds the
/// subnet-to-site map the network team maintains, so the site name is a join away - and a site
/// name is something a person can walk to, which an IP is not.
/// </para>
/// <para>
/// Pure, and separated from the directory read on purpose: the read needs a domain, while
/// longest-prefix matching is arithmetic and has to be provable on any machine. The failure
/// modes here are silent by nature - a wrong prefix length returns the wrong site rather than
/// an error - so they are pinned by tests instead of argued about.
/// </para>
/// <para>
/// <b>Environment neutrality.</b> Nothing here names a domain, a site, a subnet or an address
/// range. The map is whatever the directory returned at runtime.
/// </para>
/// </remarks>
public static class AdSiteSubnetMap
{
    /// <summary>One AD subnet object: its CIDR and the site it belongs to.</summary>
    /// <param name="Cidr">The subnet's <c>cn</c>, which AD stores in CIDR form.</param>
    /// <param name="SiteName">
    /// The site's own <c>cn</c>, or empty when the subnet has no <c>siteObject</c>. An unlinked
    /// subnet is kept rather than dropped: it still matches, and matching it to nothing is the
    /// honest answer, where dropping it would let a BROADER subnet claim the device and name the
    /// wrong site.
    /// </param>
    public sealed record Subnet(string Cidr, string SiteName);

    /// <summary>A subnet parsed into the form matching needs.</summary>
    private sealed record Parsed(byte[] Network, int PrefixLength, AddressFamily Family, string SiteName);

    /// <summary>
    /// The site containing <paramref name="ipAddress"/>, or empty when nothing contains it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Longest prefix wins, which is the whole point.</b> A device inside both a /16 and a
    /// /24 belongs to the /24's site: the /16 is usually a region or a summary route and the /24
    /// is the floor the machine is on. Taking the first match, or the broadest, names a site
    /// that is plausibly right and sometimes wrong - the worst possible outcome for someone
    /// walking to find hardware.
    /// </para>
    /// <para>
    /// <b>Never a nearest match.</b> An address in no subnet returns empty. Returning the
    /// closest subnet would be a guess wearing the costume of an answer, and the operator has no
    /// way to tell it apart from a real one.
    /// </para>
    /// <para>
    /// A subnet whose site is unknown still WINS if it is the longest match, and returns empty.
    /// That is deliberate: the device is in that subnet, and the truthful answer is "that subnet
    /// has no site", not the name of some broader subnet's site.
    /// </para>
    /// </remarks>
    public static string FindSite(string? ipAddress, IReadOnlyList<Subnet> subnets) =>
        FindSiteCancellable(ipAddress, subnets, CancellationToken.None);

    internal static string FindSiteCancellable(string? ipAddress, IReadOnlyList<Subnet> subnets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subnets);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IPAddress.TryParse(ipAddress, out var address))
            return "";

        var addressBytes = address.GetAddressBytes();
        var best = -1;
        var bestSite = "";

        foreach (var subnet in subnets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = Parse(subnet);
            if (parsed is null || parsed.Family != address.AddressFamily)
                continue;

            if (!Contains(parsed, addressBytes))
                continue;

            // Strictly greater: the first of two equally specific matches wins, rather than the
            // last. Two subnets with the same CIDR and different sites is a directory fault, and
            // neither answer is better - what matters is that the result is stable between runs
            // instead of depending on enumeration order.
            if (parsed.PrefixLength > best)
            {
                best = parsed.PrefixLength;
                bestSite = parsed.SiteName;
            }
        }

        // The final MoveNext can coincide with cancellation too. Do not publish a match
        // after the budget expires, even if that was the last subnet or an empty map.
        cancellationToken.ThrowIfCancellationRequested();
        return bestSite;
    }

    /// <summary>Whether a parsed subnet contains the address, compared prefix bit by prefix bit.</summary>
    private static bool Contains(Parsed subnet, byte[] address)
    {
        if (address.Length != subnet.Network.Length)
            return false;

        var fullBytes = subnet.PrefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (address[i] != subnet.Network[i])
                return false;
        }

        // The partial byte, masked to only the bits the prefix covers. Skipping this is the
        // classic off-by-one here: it makes every /25 behave like a /24 and silently merges two
        // sites that share a third octet.
        var remainingBits = subnet.PrefixLength % 8;
        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (address[fullBytes] & mask) == (subnet.Network[fullBytes] & mask);
    }

    /// <summary>
    /// A subnet's CIDR as bytes and a prefix length, or null when it is not parseable.
    /// </summary>
    /// <remarks>
    /// Unparseable subnets are SKIPPED rather than thrown on. One malformed <c>cn</c> in a
    /// directory of hundreds must not cost every other device its site - and an AD subnet object
    /// is created by hand, so a malformed one is a question of when, not whether.
    /// </remarks>
    private static Parsed? Parse(Subnet subnet)
    {
        if (string.IsNullOrWhiteSpace(subnet.Cidr))
            return null;

        var slash = subnet.Cidr.IndexOf('/');
        if (slash <= 0 || slash == subnet.Cidr.Length - 1)
            return null;

        if (!IPAddress.TryParse(subnet.Cidr[..slash], out var network))
            return null;

        if (!int.TryParse(subnet.Cidr[(slash + 1)..], out var prefix))
            return null;

        var bytes = network.GetAddressBytes();
        if (prefix < 0 || prefix > bytes.Length * 8)
            return null;

        return new Parsed(bytes, prefix, network.AddressFamily, subnet.SiteName);
    }
}
