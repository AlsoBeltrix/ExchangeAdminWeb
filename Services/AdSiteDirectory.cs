using System.Globalization;
using System.Text;
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
/// A failed directory read throws rather than returning a map that could be mistaken for
/// complete. The Defender caller catches that failure, marks Site unavailable and preserves
/// the rest of the device report (owner decision 2026-10-14).
/// </para>
/// </remarks>
public sealed class AdSiteDirectory
{
    private readonly Func<ISectionAccessDirectoryCommands> _commandsFactory;
    private readonly SemaphoreSlim _workerAdmission = new(1, 1);
    internal TaskScheduler WorkerScheduler { get; init; } = TaskScheduler.Default;

    public AdSiteDirectory(Func<ISectionAccessDirectoryCommands> commandsFactory)
    {
        _commandsFactory = commandsFactory ?? throw new ArgumentNullException(nameof(commandsFactory));
    }

    /// <summary>
    /// Every subnet object in the forest, with the name of the site it is linked to.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The configuration partition could not be read, or the subnet query failed.
    /// </exception>
    public IReadOnlyList<AdSiteSubnetMap.Subnet> ReadSubnets()
    {
        using var commands = _commandsFactory();
        return ReadSubnets(commands, CancellationToken.None);
    }

    /// <summary>
    /// Bounds the caller's wait. A canceled native worker retains admission and owns cleanup
    /// until it really finishes; repeated searches cannot accumulate abandoned runspaces.
    /// </summary>
    public async Task<IReadOnlyList<AdSiteSubnetMap.Subnet>> ReadSubnetsAsync(CancellationToken cancellationToken = default)
    {
        await _workerAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        var worker = Task.Factory.StartNew(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var commands = _commandsFactory();
                using var registration = cancellationToken.Register(() =>
                {
                    if (commands is ICancellableDirectoryCommands cancellable)
                        cancellable.RequestStop();
                });
                return ReadSubnets(commands, cancellationToken);
            }
            finally
            {
                // Includes command disposal, which can itself block in native cleanup.
                _workerAdmission.Release();
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, WorkerScheduler);

        // Observe eventual failure even after the response has stopped awaiting the worker.
        _ = worker.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<AdSiteSubnetMap.Subnet> ReadSubnets(
        ISectionAccessDirectoryCommands commands, CancellationToken cancellationToken)
    {
        DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = commands is ICancellableDirectoryCommands cancellable
                ? cancellable.Invoke(command, parameters, cancellationToken)
                : commands.Invoke(command, parameters);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        var rootDse = Invoke("Get-ADRootDSE", new Dictionary<string, object?>
        {
            ["ErrorAction"] = "Stop"
        });

        var configNc = rootDse.Rows.FirstOrDefault(o => o is not null)?.Text("configurationNamingContext");
        if (string.IsNullOrWhiteSpace(configNc))
            throw new InvalidOperationException("Could not read the directory's configuration naming context.");

        // One query for every subnet, not one per device. A 27,000-device report against 531
        // subnets is one directory read; per-device lookups would be 27,000.
        var subnets = Invoke("Get-ADObject", new Dictionary<string, object?>
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

        var equals = siteObjectDn.IndexOf('=');
        if (equals < 0 || siteObjectDn.AsSpan(0, equals).Contains(','))
            return "";

        var name = new StringBuilder();
        for (var i = equals + 1; i < siteObjectDn.Length && siteObjectDn[i] != ','; i++)
        {
            if (siteObjectDn[i] != '\\')
            {
                name.Append(siteObjectDn[i]);
                continue;
            }

            // Consume the escape with its value, so an escaped comma is data and a comma
            // after an escaped backslash still ends the RDN. Hex escapes encode UTF-8 bytes.
            if (++i == siteObjectDn.Length)
                return "";

            if (i + 1 < siteObjectDn.Length && byte.TryParse(siteObjectDn.AsSpan(i, 2),
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var octet))
            {
                var bytes = new List<byte> { octet };
                i += 2;
                while (i + 2 < siteObjectDn.Length && siteObjectDn[i] == '\\'
                    && byte.TryParse(siteObjectDn.AsSpan(i + 1, 2),
                        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out octet))
                {
                    bytes.Add(octet);
                    i += 3;
                }

                name.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                i--;
            }
            else
            {
                name.Append(siteObjectDn[i]);
            }
        }

        return name.ToString().Trim();
    }
}
