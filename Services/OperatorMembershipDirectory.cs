using System.Collections;
using System.Collections.Concurrent;
using System.Security.Principal;
using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Reads an account's current transitive group SIDs and enabled state from on-prem Active
/// Directory, for circuit revalidation (aud-a29b).
/// </summary>
/// <remarks>
/// <para>
/// <c>Get-ADUser -Identity &lt;SID&gt; -Properties tokenGroups</c>, through the same
/// <see cref="ISectionAccessDirectoryCommands"/> seam and PowerShell runspace the section-access
/// lookup uses - no second directory client. Bound by SID, never by name. <c>tokenGroups</c> is the
/// directory's own transitive expansion (nesting, universal groups, the primary group), computed by
/// a domain controller of the account's domain.
/// </para>
/// <para>
/// Routing: the account's NetBIOS domain is mapped to a DNS root through the forest's
/// <c>CN=Partitions</c> crossRefs (<see cref="SectionAccessGroupDirectory.ResolveDomainServer"/>).
/// Nothing is named, defaulted or configured. A domain outside the host's forest has no crossRef and
/// fails - closed. A mapping is cached once it succeeds and forgotten after any failed read, so a
/// wrong entry costs one failed check, never a stuck one.
/// </para>
/// <para>
/// A fresh runspace per read, at most <see cref="MaxConcurrentLookups"/> at once, and the caller's
/// cancellation stops the running command: the <see cref="AdSiteDirectory"/> shape. A cancelled
/// native worker keeps its slot until it really ends, so abandoned reads cannot pile up.
/// </para>
/// <para>
/// Strict about what counts as an answer: exactly one account, no cmdlet error beside it, an
/// <c>Enabled</c> that reads as a bool and at least one group. Anything else throws, because the
/// caller would otherwise read an empty membership as "removed from everything".
/// </para>
/// <para>
/// Runs under the app pool identity, like the other read-only directory lookups (decisions
/// 2026-07-31). Where a deployment denies that identity <c>tokenGroups</c>, the attribute comes back
/// absent, the read fails and sessions end - closed and visible in the audit, never open.
/// </para>
/// </remarks>
public sealed class OperatorMembershipDirectory : IOperatorMembershipDirectory
{
    internal const int MaxConcurrentLookups = 4;

    private readonly Func<ISectionAccessDirectoryCommands> _commandsFactory;
    private readonly Func<string, string?> _translateName;
    private readonly SemaphoreSlim _admission = new(MaxConcurrentLookups, MaxConcurrentLookups);
    private readonly ConcurrentDictionary<string, string> _domainServers = new(StringComparer.OrdinalIgnoreCase);

    public OperatorMembershipDirectory()
        : this(() => new PowerShellDirectoryCommands(), TranslateWithLsa)
    {
    }

    internal OperatorMembershipDirectory(
        Func<ISectionAccessDirectoryCommands> commandsFactory,
        Func<string, string?> translateName)
    {
        _commandsFactory = commandsFactory ?? throw new ArgumentNullException(nameof(commandsFactory));
        _translateName = translateName ?? throw new ArgumentNullException(nameof(translateName));
    }

    internal TaskScheduler WorkerScheduler { get; init; } = TaskScheduler.Default;

    public async Task<OperatorDirectoryMembership> GetMembershipAsync(
        string userSid, string? netBiosDomain, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userSid))
            throw new DirectoryUnavailableException("A blank account SID reached the membership lookup.");

        await _admission.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                return Read(commands, userSid, netBiosDomain, cancellationToken);
            }
            finally
            {
                _admission.Release();
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, WorkerScheduler);

        _ = worker.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public string? TranslateGroupName(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return null;

        try
        {
            return _translateName(groupName);
        }
        catch (DirectoryUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DirectoryUnavailableException(
                $"A configured group name could not be translated to a SID: {ex.Message}", ex);
        }
    }

    internal static OperatorDirectoryMembership FromAccount(DirectoryObject account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.Value("Enabled") is not bool enabled)
            throw new DirectoryUnavailableException("The account's enabled state could not be read.");

        var groups = ParseSids(account.Value("tokenGroups"));
        if (groups.Count == 0)
        {
            throw new DirectoryUnavailableException(
                "The directory returned no group memberships for the account, which no real account has.");
        }

        return new OperatorDirectoryMembership(groups, enabled);
    }

    /// <summary>
    /// The canonical SIDs in a <c>tokenGroups</c> value, whatever shape the cmdlet produced: a
    /// collection of <see cref="SecurityIdentifier"/>, binary SIDs, strings, or wrapped objects.
    /// Entries that are not canonical SIDs - an SDDL alias such as <c>BA</c> included - are dropped.
    /// </summary>
    internal static IReadOnlySet<string> ParseSids(object? value)
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items(value))
        {
            var sid = AsSid(item);
            if (sid is not null)
                sids.Add(sid);
        }

        return sids;
    }

    private OperatorDirectoryMembership Read(
        ISectionAccessDirectoryCommands commands, string userSid, string? netBiosDomain, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["Identity"] = userSid,
                ["Properties"] = new[] { "tokenGroups" },
                ["ErrorAction"] = "Stop",
            };

            if (netBiosDomain is not null)
                parameters["Server"] = ResolveServer(commands, netBiosDomain);

            cancellationToken.ThrowIfCancellationRequested();
            var result = commands is ICancellableDirectoryCommands cancellable
                ? cancellable.Invoke("Get-ADUser", parameters, cancellationToken)
                : commands.Invoke("Get-ADUser", parameters);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.Error is not null)
                throw new DirectoryUnavailableException($"Reading the signed-in account failed: {result.Error}");

            var accounts = result.Rows.Where(row => row is not null).ToList();
            if (accounts.Count != 1)
            {
                throw new DirectoryUnavailableException(
                    $"The account lookup returned {accounts.Count} objects; exactly one was expected.");
            }

            return FromAccount(accounts[0]!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DirectoryUnavailableException)
        {
            Forget(netBiosDomain);
            throw;
        }
        catch (Exception ex)
        {
            Forget(netBiosDomain);
            throw new DirectoryUnavailableException(
                $"The directory lookup for the signed-in account failed: {ex.Message}", ex);
        }
    }

    private string ResolveServer(ISectionAccessDirectoryCommands commands, string netBiosDomain)
    {
        if (_domainServers.TryGetValue(netBiosDomain, out var known))
            return known;

        var server = SectionAccessGroupDirectory.ResolveDomainServer(commands, netBiosDomain);
        _domainServers[netBiosDomain] = server;
        return server;
    }

    private void Forget(string? netBiosDomain)
    {
        if (netBiosDomain is not null)
            _domainServers.TryRemove(netBiosDomain, out _);
    }

    private static IEnumerable<object?> Items(object? value) => value switch
    {
        null => Array.Empty<object?>(),
        string or SecurityIdentifier or byte[] => new[] { value },
        IEnumerable many => many.Cast<object?>(),
        _ => new[] { value },
    };

    private static string? AsSid(object? item)
    {
        try
        {
            switch (item)
            {
                case SecurityIdentifier sid:
                    return sid.Value;
                case byte[] binary:
                    return new SecurityIdentifier(binary, 0).Value;
                default:
                    var text = item?.ToString();
                    if (string.IsNullOrWhiteSpace(text))
                        return null;

                    var parsed = new SecurityIdentifier(text);
                    return string.Equals(parsed.Value, text, StringComparison.OrdinalIgnoreCase) ? parsed.Value : null;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // NTAccount.Translate is the call WindowsPrincipal.IsInRole(string) makes, so the SID returned is
    // the one IsInRole tests. Not mapped -> null (IsInRole cannot grant through it either). Any other
    // failure propagates, and TranslateGroupName turns it into a lookup failure.
    private static string? TranslateWithLsa(string groupName)
    {
        try
        {
            return ((SecurityIdentifier)new NTAccount(groupName).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
