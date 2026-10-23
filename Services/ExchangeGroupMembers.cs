using System.Collections.ObjectModel;
using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

internal static class ExchangeGroupMembers
{
    internal const int MaxGroupsRead = 50;

    private const string UnifiedGroupKind = "GroupMailbox";

    private static readonly HashSet<string> DistributionGroupKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "MailUniversalDistributionGroup",
        "MailUniversalSecurityGroup",
        "MailNonUniversalGroup",
        "RoomList",
    };

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

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { identity };
        if (root.PrimarySmtpAddress is { } rootAddress)
            visited.Add(rootAddress);

        var members = new List<ExchangeGroupMember>();
        var queue = new Queue<(string Identity, string? Kind)>();
        queue.Enqueue((identity, root.RecipientTypeDetails));
        var read = 0;
        while (queue.Count > 0)
        {
            var (next, kind) = queue.Dequeue();
            if (++read > MaxGroupsRead)
                throw new InvalidOperationException($"'{identity}' holds more than {MaxGroupsRead} groups");

            foreach (var row in MembersOf(ps, next, kind, invoke))
            {
                var member = ToMember(row);
                members.Add(member);
                if (!member.IsGroup)
                    continue;

                if (member.IsDirSynced is null)
                    throw new InvalidOperationException(
                        $"Exchange Online reported no sync state for a group in '{next}' ({member.PrimarySmtpAddress ?? "no address"})");
                if (member.IsDirSynced == true)
                    continue;

                var address = member.PrimarySmtpAddress
                    ?? throw new InvalidOperationException($"a group in '{next}' has no primary SMTP address");
                if (visited.Add(address))
                    queue.Enqueue((address, member.RecipientTypeDetails));
            }
        }

        return members;
    }

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
        else
        {
            throw new InvalidOperationException(
                $"the members of '{identity}' cannot be read: Exchange Online reports it as {(kind is null ? "a recipient of no reported kind" : $"a {kind}")}");
        }

        return invoke(ps);
    }

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
