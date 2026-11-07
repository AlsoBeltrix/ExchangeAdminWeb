using System.Security.Authentication;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// rev-a2. Whether a directory read was refused for the directory-read credential itself - the secret rotated under the same
/// Secret ID while <see cref="DelineaService"/> still keeps the old password - noted for the flow that made it, so the flow can
/// drop the kept credential and run once more on a fresh fetch (<c>ProtectedPrincipalService.WithDirectoryReadCredentialAsync</c>).
/// </summary>
/// <remarks>
/// Ambient, as <see cref="DirectoryReadOperation"/> is: a flow begins a watch and every read awaited inside it - on a thread-pool
/// thread too - reports to the same watch, through the reads' own choke points (<see cref="Observe(Exception)"/>). Nothing here
/// decides authorization: a rejected read still fails as it did, and the flow's answer on the retry is the directory's.
/// </remarks>
internal static class DirectoryCredentialRejection
{
    private static readonly AsyncLocal<Watch?> Ambient = new();

    // The AD module's and LDAP's words for a bind the server refused for the account or its password. Matched as substrings,
    // case-insensitively; an unreachable server, a timeout or a denied read is none of these.
    private static readonly string[] RejectionTexts =
    [
        "rejected the client credentials",
        "unknown user name or bad password",
        "user name or password is incorrect",
        "supplied credential is invalid",
        "invalid credentials",
    ];

    /// <summary>One flow's watch: whether any read inside it was refused for its credential.</summary>
    internal sealed class Watch : IDisposable
    {
        private readonly Watch? _outer;
        private int _rejected;

        internal Watch(Watch? outer) => _outer = outer;

        /// <summary>True once a read inside the watch reported a credential rejection.</summary>
        public bool Rejected => Volatile.Read(ref _rejected) != 0;

        internal void Note()
        {
            for (var watch = this; watch is not null; watch = watch._outer)
                Interlocked.Exchange(ref watch._rejected, 1);
        }

        public void Dispose()
        {
            if (ReferenceEquals(Ambient.Value, this))
                Ambient.Value = _outer;
        }
    }

    /// <summary>Begins a watch for the current flow; dispose it where the flow's reads end.</summary>
    internal static Watch Begin()
    {
        var watch = new Watch(Ambient.Value);
        Ambient.Value = watch;
        return watch;
    }

    /// <summary>Reports a failed read: noted on the current flow's watch when it is a credential rejection.</summary>
    internal static void Observe(Exception? ex)
    {
        if (ex is not null && IsRejection(ex))
            Ambient.Value?.Note();
    }

    /// <summary>Reports a read's error text: noted on the current flow's watch when it is a credential rejection.</summary>
    internal static void Observe(string? message)
    {
        if (IsRejection(message))
            Ambient.Value?.Note();
    }

    /// <summary>Whether <paramref name="ex"/>, or any exception inside it, is a bind refused for the credential.</summary>
    internal static bool IsRejection(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is InvalidCredentialException || IsRejection(current.Message))
                return true;
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsRejection))
                return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="message"/> is the directory's refusal of the credential.</summary>
    internal static bool IsRejection(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && RejectionTexts.Any(text => message.Contains(text, StringComparison.OrdinalIgnoreCase));
}
