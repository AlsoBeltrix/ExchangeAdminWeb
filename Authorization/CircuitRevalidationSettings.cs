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
}

/// <summary>
/// The resolved settings, built once at startup. A record rather than the static values so tests
/// can run the real loop on a short interval; the bounds are enforced where configuration is read,
/// by <see cref="CircuitRevalidationSettings.ResolveInterval"/>. <see cref="Enabled"/> defaults to
/// true so nothing that builds options without it can switch revocation off by omission.
/// </summary>
public sealed record CircuitRevalidationOptions(TimeSpan Interval, TimeSpan LookupTimeout, bool Enabled = true);
