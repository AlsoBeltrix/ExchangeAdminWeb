using System.Globalization;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// How often an open circuit's access is re-derived from the directory, and the bounds that keep a
/// typo from either hammering the directory or quietly switching revocation off (aud-a29b).
/// </summary>
/// <remarks>
/// <para>
/// The interval IS the revocation window: an operator removed from a group keeps working for at
/// most this long on a tab that was already open. Every open tab that holds a consulted group reads
/// the directory once per interval, which is what the floor protects.
/// </para>
/// <para>
/// The ceiling exists because <see cref="TimeSpan"/> parsing reads a bare number as DAYS - "5"
/// meant as minutes would become a five-day window that looks configured and does nothing. Both
/// bounds refuse at startup, the <c>ConfigStorePath</c> shape, rather than clamping: a clamped
/// value is a setting nobody chose and nobody can see.
/// </para>
/// <para>
/// <see cref="LookupTimeout"/> is deliberately not a key. It bounds one whole check, queueing
/// behind other circuits' reads included, and a directory that cannot answer in a minute cannot
/// confirm access - which ends the session (fail closed).
/// </para>
/// <para>
/// <see cref="EnabledKey"/> is the operator's off-switch, added so that a revocation the check gets
/// wrong - a directory answer arriving in a shape the code did not expect, or an operator whose
/// access rides a group the read cannot see - can be stopped by configuration and a restart rather
/// than a code change. Off is never silent: startup logs a warning and writes an audit line (see
/// <see cref="MembershipRevalidatingAuthenticationStateProvider.ReportIfDisabled"/>). The interval
/// is still validated when the switch is off, so turning it back on cannot surprise anyone.
/// </para>
/// <para>
/// While the switch is on, the static group lists must be groups revalidation can re-check
/// (<see cref="EnsureStaticGroupsCanBeRevalidated"/>, aud-a29e): turning revalidation off is the
/// documented way to run with a built-in group in them.
/// </para>
/// </remarks>
public static class CircuitRevalidationSettings
{
    /// <summary>The configuration key; a <see cref="TimeSpan"/> written hh:mm:ss.</summary>
    public const string IntervalKey = "Authentication:RevalidationInterval";

    /// <summary>The off-switch key; true or false, true when absent.</summary>
    public const string EnabledKey = "Authentication:RevalidationEnabled";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(1);

    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The interval for <paramref name="configured"/>, or the default when it is absent.</summary>
    /// <exception cref="InvalidOperationException">The value does not parse, or is out of bounds.</exception>
    public static TimeSpan ResolveInterval(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return DefaultInterval;

        if (!TimeSpan.TryParse(configured.Trim(), CultureInfo.InvariantCulture, out var interval))
        {
            throw new InvalidOperationException(
                $"{IntervalKey} is '{configured}', which is not a time span. Use hh:mm:ss, for example 00:05:00.");
        }

        if (interval < MinimumInterval)
        {
            throw new InvalidOperationException(
                $"{IntervalKey} is {interval}, below the minimum of {MinimumInterval}. Every open tab "
                + "reads the directory once per interval; use 00:05:00 unless there is a reason not to.");
        }

        if (interval > MaximumInterval)
        {
            throw new InvalidOperationException(
                $"{IntervalKey} is {interval}, above the maximum of {MaximumInterval}. The interval is how "
                + "long a removed operator keeps working; a bare number such as '5' is read as days.");
        }

        return interval;
    }

    /// <summary>Whether revalidation runs: true unless <paramref name="configured"/> says false.</summary>
    /// <remarks>
    /// Anything other than true or false is refused at startup rather than guessed. Guessing "on"
    /// would silently ignore an operator who meant to switch it off during an incident; guessing
    /// "off" would silently drop revocation for everyone.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The value is neither true nor false.</exception>
    public static bool ResolveEnabled(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return true;

        if (!bool.TryParse(configured.Trim(), out var enabled))
        {
            throw new InvalidOperationException(
                $"{EnabledKey} is '{configured}', which is not true or false.");
        }

        return enabled;
    }

    /// <summary>
    /// Refuses, while revalidation is on, a <c>Security:AllowedGroups</c> or
    /// <c>Security:AdminGroups</c> entry that can grant access but that revalidation cannot re-check
    /// (aud-a29e). Returns one warning per entry whose name could not be translated, so could not be
    /// classified.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The policy handler does not filter the static lists (review finding sidf-1), so
    /// <c>IsInRole</c> grants through <c>BUILTIN\Administrators</c> or <c>S-1-5-32-544</c> for any
    /// token the server built with that alias. <c>tokenGroups</c>, read from the account's domain,
    /// cannot attest to a server's local aliases or to well-known SIDs, so a circuit whose only grant
    /// was such a group used to be skipped by every check - not even a disabled account ended it.
    /// Refusing at startup, with the entry named and the remedy given, is the fail-closed answer that
    /// does not end anyone's session by surprise later.
    /// </para>
    /// <para>
    /// What passes: a usable domain group SID, a name that translates (as <c>IsInRole</c> translates
    /// it) to one, a name that maps to nothing, and a value that is not a SID at all - the last two
    /// grant nothing. What is refused: a SID, or a name translating to a SID, that parses but is not
    /// a usable domain group (<see cref="SectionAccessGroupIdentity.SidRejectionReason"/>).
    /// </para>
    /// <para>
    /// A name whose translation fails cannot be classified here. That is a warning, not a refusal:
    /// the run-time check fails closed on it anyway - a failed translation fails the whole check, and
    /// a translation that later yields a non-domain SID is reported by
    /// <see cref="AuthorizationGroupInventory"/> rather than dropped - and an app that refused to
    /// start whenever a trust was briefly unreachable would be its own outage.
    /// </para>
    /// <para>
    /// With the switch off nothing is translated or refused: there is no revalidation to protect.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">An entry can grant access but cannot be revalidated.</exception>
    public static IReadOnlyList<string> EnsureStaticGroupsCanBeRevalidated(
        bool enabled,
        IReadOnlyList<string>? allowedGroups,
        IReadOnlyList<string>? adminGroups,
        Func<string, string?> translateName)
    {
        ArgumentNullException.ThrowIfNull(translateName);

        var warnings = new List<string>();
        if (!enabled)
            return warnings;

        var lists = new[]
        {
            (Key: AuthorizationGroupInventory.AllowedGroupsSource, Entries: allowedGroups),
            (Key: AuthorizationGroupInventory.AdminGroupsSource, Entries: adminGroups),
        };

        foreach (var (key, entries) in lists)
        {
            foreach (var entry in entries ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                var value = entry.Trim();
                var isName = !value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase);
                string? sid;
                if (isName)
                {
                    try
                    {
                        sid = translateName(value);
                    }
                    catch (DirectoryUnavailableException ex)
                    {
                        warnings.Add(
                            $"{key} contains '{entry}', which could not be translated to a SID at startup "
                            + $"({ex.Message}), so whether it can be re-checked was not confirmed. Until it "
                            + "translates, every session revalidation check fails closed.");
                        continue;
                    }

                    if (sid is null)
                        continue;
                }
                else
                {
                    // SID-shaped values are never names (the SectionAccessGroupIdentity rule). One
                    // that does not parse matches no token and grants nothing.
                    if (!IsParseableSid(value))
                        continue;

                    sid = value;
                }

                var rejection = SectionAccessGroupIdentity.SidRejectionReason(sid);
                if (rejection is null)
                    continue;

                throw new InvalidOperationException(
                    $"{key} contains '{entry}'{(isName ? $", which resolves to {sid}" : string.Empty)}: {rejection}. "
                    + "Windows can grant access through it, but session revalidation cannot re-check it "
                    + "against Active Directory. Use a domain group instead, or set "
                    + $"{EnabledKey} to false to switch revalidation off (README 'Session Revalidation').");
            }
        }

        return warnings;
    }

    private static bool IsParseableSid(string value)
    {
        try
        {
            _ = new System.Security.Principal.SecurityIdentifier(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// The resolved settings, built once at startup. A record rather than the static values so tests
/// can run the real loop on a short interval; the bounds are enforced where configuration is read,
/// by <see cref="CircuitRevalidationSettings.ResolveInterval"/>. <see cref="Enabled"/> defaults to
/// true so nothing that builds options without it can switch revocation off by omission.
/// </summary>
public sealed record CircuitRevalidationOptions(TimeSpan Interval, TimeSpan LookupTimeout, bool Enabled = true);
