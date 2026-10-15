using System.Security.Cryptography;
using System.Text;

namespace ExchangeAdminWeb.Services;

/// <summary>One canonical representation of the people approved for a Comms-10k replace.</summary>
public static class Comms10kMemberSet
{
    private const string Version = "v1:";

    public static string Fingerprint(IEnumerable<Guid> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var identities = members.Distinct().ToArray();
        if (identities.Length == 0 || identities.Contains(Guid.Empty))
            throw new ArgumentException("An approved member set must contain nonempty ObjectGUIDs.", nameof(members));

        var canonical = string.Join('\n', identities.Select(guid => guid.ToString("N"))
            .Order(StringComparer.Ordinal));
        return Version + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool IsSupportedFingerprint(string? fingerprint) =>
        fingerprint is { Length: 67 } && fingerprint.StartsWith(Version, StringComparison.Ordinal)
        && fingerprint.AsSpan(Version.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}

/// <summary>The validation result and the exact inputs that produced it, frozen together.</summary>
internal sealed class Comms10kConfirmation
{
    public IReadOnlyList<string> Emails { get; }
    public string? SourceFileName { get; }
    public string Ticket { get; }
    public string MemberFingerprint { get; }

    public Comms10kConfirmation(IReadOnlyList<string> emails, string? sourceFileName, string ticket,
        IReadOnlyList<Comms10kAddressResolver.ResolvedMember> members)
    {
        Emails = Array.AsReadOnly(emails.ToArray());
        SourceFileName = sourceFileName;
        Ticket = ticket;
        MemberFingerprint = Comms10kMemberSet.Fingerprint(members.Select(member => member.ObjectGuid));
    }
}
