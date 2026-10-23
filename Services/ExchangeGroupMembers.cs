using System.Collections.ObjectModel;
using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The members of an Exchange Online group as the self-grant check asks for them (aud-a52): the group's own, and those of
/// every cloud group nested in it.
/// </summary>
/// <remarks>
/// A Microsoft 365 group's members are its member links (<c>Get-UnifiedGroupLinks -LinkType Members</c>); a distribution
/// group's, a mail-enabled security group's or a room list's are <c>Get-DistributionGroupMember</c>'s answer; a dynamic
/// distribution group's are the ones Exchange calculated from its filter (<c>Get-DynamicDistributionGroupMember</c>,
/// aud-a72 - an empty one is no answer, and nothing here evaluates the filter); every read asks for all of them
/// (<c>-ResultSize Unlimited</c>). No cmdlet follows nesting, so a group among the members is read
/// in its turn - once, by its address - when Exchange calls it cloud-only, and listed but not read when it calls it synced:
/// its whole membership is the on-premises directory's, groups Exchange does not list included, and the caller reads it
/// there. Fail closed: any kind of group not listed here, a nested group without an address or a sync state, more than
/// <see cref="MaxNestedGroups"/> nested groups, or a read that fails throws; nothing here returns a short list for a read
/// that did not complete. The cmdlets' members and properties are as Exchange Online documents them (labelled assumption in
/// the aud-a52 record; no live call is made by any test).
/// </remarks>
internal static class ExchangeGroupMembers
{
    /// <summary>
    /// The most cloud groups one read follows below the group it was asked for (aud-a61); a membership that nests more is
    /// refused (<see cref="ExchangeGroupNestingBoundException"/>), never read short.
    /// </summary>
    /// <remarks>
    /// Exchange documents no nesting limit for distribution groups, so this bound is the check's own: it ends a read that
    /// would not end. Each group is read once, by its address, so a cycle costs one read, and only cloud-only groups count - a
    /// synced one is the directory's to read. 250, five times aud-a52's 50 (which counted the group itself), takes a deep list
    /// of lists while keeping one check to a few hundred pooled reads.
    /// </remarks>
    internal const int MaxNestedGroups = 250;

    // RecipientTypeDetails of a Microsoft 365 group.
    private const string UnifiedGroupKind = "GroupMailbox";

    // RecipientTypeDetails of a dynamic distribution group (aud-a72): its members are the ones Exchange calculated from its filter.
    private const string DynamicGroupKind = "DynamicDistributionGroup";

    // The RecipientTypeDetails Get-DistributionGroupMember expands.
    private static readonly HashSet<string> DistributionGroupKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "MailUniversalDistributionGroup",
        "MailUniversalSecurityGroup",
        "MailNonUniversalGroup",
        "RoomList",
    };

    /// <summary>
    /// Every member of <paramref name="group"/> and of the cloud groups nested in it, in the order read. Throws when the
    /// membership cannot be read in full.
    /// </summary>
    /// <param name="invoke">Runs the pipeline: throws on an error, clears it after (the pool's <c>Invoke</c>).</param>
    internal static IReadOnlyList<ExchangeGroupMember> Read(PowerShell ps, string group, Func<PowerShell, Collection<PSObject>> invoke)
    {
        var identity = group?.Trim();
        if (string.IsNullOrEmpty(identity))
            throw new InvalidOperationException("a blank group cannot be read");

        ps.Commands.Clear();
        ps.AddCommand("Get-Recipient")
          .AddParameter("Identity", identity)
          .AddParameter("ErrorAction", "Stop");
        var found = invoke(ps);
        if (found.Count != 1)
            throw new InvalidOperationException(
                $"Exchange Online returned {found.Count} recipients for '{identity}'; exactly one was expected");
        var root = ToMember(found[0]);

        // Each group read once, by its address: nesting may loop back on itself.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { identity };
        if (root.PrimarySmtpAddress is { } rootAddress)
            visited.Add(rootAddress);

        var members = new List<ExchangeGroupMember>();
        var queue = new Queue<(string Identity, string? Kind)>();
        queue.Enqueue((identity, root.RecipientTypeDetails));
        var nested = 0;
        while (queue.Count > 0)
        {
            var (next, kind) = queue.Dequeue();
            foreach (var row in MembersOf(ps, next, kind, invoke))
            {
                var member = ToMember(row);
                members.Add(member);
                if (!member.IsGroup)
                    continue;

                // Synced or not decides who reads it, so a group whose sync state Exchange did not report is not guessed at.
                if (member.IsDirSynced is null)
                    throw new InvalidOperationException(
                        $"Exchange Online reported no sync state for a group in '{next}' ({member.PrimarySmtpAddress ?? "no address"})");
                // The directory's to read: the caller evaluates it there.
                if (member.IsDirSynced == true)
                    continue;

                var address = member.PrimarySmtpAddress
                    ?? throw new InvalidOperationException($"a group in '{next}' has no primary SMTP address");
                if (!visited.Add(address))
                    continue;
                if (++nested > MaxNestedGroups)
                    throw new ExchangeGroupNestingBoundException(identity, MaxNestedGroups);
                queue.Enqueue((address, member.RecipientTypeDetails));
            }
        }

        return members;
    }

    // One group's members, by the cmdlet its kind takes; a kind neither expands throws.
    private static Collection<PSObject> MembersOf(PowerShell ps, string identity, string? kind, Func<PowerShell, Collection<PSObject>> invoke)
    {
        ps.Commands.Clear();
        if (string.Equals(kind, UnifiedGroupKind, StringComparison.OrdinalIgnoreCase))
        {
            ps.AddCommand("Get-UnifiedGroupLinks")
              .AddParameter("Identity", identity)
              .AddParameter("LinkType", "Members")
              .AddParameter("ResultSize", "Unlimited")
              .AddParameter("ErrorAction", "Stop");
        }
        else if (kind is not null && DistributionGroupKinds.Contains(kind))
        {
            ps.AddCommand("Get-DistributionGroupMember")
              .AddParameter("Identity", identity)
              .AddParameter("ResultSize", "Unlimited")
              .AddParameter("ErrorAction", "Stop");
        }
        else if (string.Equals(kind, DynamicGroupKind, StringComparison.OrdinalIgnoreCase))
        {
            // aud-a72. The members Exchange calculated for the filter; nothing here evaluates it. A calculated membership that
            // is empty cannot be told from one not yet calculated, so it is no answer - never "not a member".
            ps.AddCommand("Get-DynamicDistributionGroupMember")
              .AddParameter("Identity", identity)
              .AddParameter("ResultSize", "Unlimited")
              .AddParameter("ErrorAction", "Stop");
            var calculated = invoke(ps);
            if (calculated.Count == 0)
                throw new InvalidOperationException(
                    $"Exchange Online reports no member of the dynamic distribution group '{identity}', so whether its membership was calculated cannot be told");
            return calculated;
        }
        else
        {
            throw new InvalidOperationException(
                $"the members of '{identity}' cannot be read: Exchange Online reports it as {(kind is null ? "a recipient of no reported kind" : $"a {kind}")}");
        }

        return invoke(ps);
    }

    /// <summary>One row as a member; an empty row is a failed read, never a member skipped.</summary>
    internal static ExchangeGroupMember ToMember(PSObject? row)
    {
        if (row is null)
            throw new InvalidOperationException("Exchange Online returned an empty row");

        return new ExchangeGroupMember(
            Text(row.Properties["PrimarySmtpAddress"]?.Value),
            Text(row.Properties["ExternalDirectoryObjectId"]?.Value),
            Text(row.Properties["RecipientType"]?.Value),
            Text(row.Properties["RecipientTypeDetails"]?.Value),
            AsBool(row.Properties["IsDirSynced"]?.Value));
    }

    private static object? Unwrap(object? value) => value is PSObject wrapped ? wrapped.BaseObject : value;

    private static string? Text(object? value)
    {
        var text = Unwrap(value)?.ToString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool? AsBool(object? value) => Unwrap(value) switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var parsed) => parsed,
        _ => null,
    };
}

/// <summary>
/// aud-a61. A group whose nested cloud groups run past <see cref="ExchangeGroupMembers.MaxNestedGroups"/>: the read stopped
/// there, so its membership was not read whole. The self-grant check refuses on it, naming the bound.
/// </summary>
internal sealed class ExchangeGroupNestingBoundException(string group, int bound)
    : InvalidOperationException($"'{group}' holds more than {bound} nested groups")
{
    /// <summary>The group the read was asked for.</summary>
    public string Group { get; } = group;

    /// <summary>The bound the read ran past.</summary>
    public int Bound { get; } = bound;
}
