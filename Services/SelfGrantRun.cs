using System.Collections.Concurrent;
using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// aud-a61. One run of the self-grant check - a single grant, or every row of a bulk file - and what it has learned of
/// group membership, so a group several rows name is read once.
/// </summary>
/// <remarks>
/// Exchange's members of a cloud-only group, by the operator's Exchange identity and the group's canonical address; the
/// directory's answer for a set of groups, by the operator's DN and the groups' DNs; and the directory-read credential
/// (aud-a60), once. Each is kept as its task, so a read that failed is kept as that failure and every later row is told the
/// same - the aud-a23r <c>DirectoryLookupRun</c> shape. A new run reads again: the public check makes one per grant, so the
/// on-premises write's re-check reads afresh. One <see cref="Lazy{T}"/> per key, so rows run concurrently would still share
/// one read.
/// </remarks>
internal sealed class SelfGrantRun
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<ExchangeGroupMember>?>>> _exchangeMembers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<ProtectedGroupMembership.Result>>> _directoryMembership =
        new(StringComparer.OrdinalIgnoreCase);

    private Lazy<Task<(PSCredential? Credential, string? Problem)>>? _credential;

    /// <summary>Exchange's members of <paramref name="group"/>, as <paramref name="read"/> gives them: read once per run.</summary>
    internal Task<IReadOnlyList<ExchangeGroupMember>?> ExchangeMembersAsync(
        string operatorIdentity, string group, Func<Task<IReadOnlyList<ExchangeGroupMember>?>> read) =>
        _exchangeMembers.GetOrAdd(Key(operatorIdentity, group),
            _ => new Lazy<Task<IReadOnlyList<ExchangeGroupMember>?>>(read)).Value;

    /// <summary>
    /// Whether <paramref name="operatorDn"/> is in any of <paramref name="groups"/>, as <paramref name="evaluate"/> answers:
    /// evaluated once per run for that operator and that set of groups.
    /// </summary>
    internal Task<ProtectedGroupMembership.Result> DirectoryMembershipAsync(
        string operatorDn, IEnumerable<string> groups, Func<Task<ProtectedGroupMembership.Result>> evaluate) =>
        _directoryMembership.GetOrAdd(
            Key(operatorDn, string.Join("\n", groups.Select(group => group.Trim()).Order(StringComparer.OrdinalIgnoreCase))),
            _ => new Lazy<Task<ProtectedGroupMembership.Result>>(evaluate)).Value;

    /// <summary>The directory-read credential, or why it cannot be had, as <paramref name="acquire"/> gives it: once per run.</summary>
    internal Task<(PSCredential? Credential, string? Problem)> DirectoryReadCredentialAsync(
        Func<Task<(PSCredential? Credential, string? Problem)>> acquire)
    {
        var fresh = new Lazy<Task<(PSCredential? Credential, string? Problem)>>(acquire);
        return (Interlocked.CompareExchange(ref _credential, fresh, null) ?? fresh).Value;
    }

    // Who, then what: an address or a DN carries no line break.
    private static string Key(string who, string what) => who.Trim() + "\n" + what.Trim();
}
