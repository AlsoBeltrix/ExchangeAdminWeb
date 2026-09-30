namespace ExchangeAdminWeb.Services;

/// <summary>
/// How much the cloud half of a true-last-logon answer can be trusted, especially when it
/// reports nothing.
/// </summary>
/// <remarks>
/// This is the single most important output of the cloud lookup. A DATE you can trust; an
/// ABSENCE you cannot - <c>signInActivity</c> is a materialized aggregate that under-reports.
/// Measured by the source script on 2026-08-20: of 557 accounts it called dormant, re-checking
/// the next day found 9 with sign-ins.
/// </remarks>
public enum CloudVerification
{
    /// <summary>Neither source answered. Nothing is known.</summary>
    Unverified,

    /// <summary>
    /// <c>signInActivity</c> answered and the raw log did not. A DATE from here is usable; an
    /// ABSENCE is NOT sufficient evidence of dormancy.
    /// </summary>
    ActivityOnly,

    /// <summary>
    /// The raw log answered and <c>signInActivity</c> did not. The log retains only ~30 days,
    /// so an absence here means "nothing in the retained window", never "never".
    /// </summary>
    LogOnly30d,

    /// <summary>Both sources answered. The only state in which an absence is good evidence.</summary>
    LogVerified,
}

/// <summary>One source's answer. <paramref name="Answered"/> and a null date are different facts.</summary>
/// <param name="Answered">
/// Whether the source responded at all. A source that responded and reported no sign-ins has
/// <c>Answered = true</c> with null dates; a source that failed has <c>Answered = false</c>.
/// Collapsing these is how a Graph outage becomes "this account is dormant".
/// </param>
public sealed record CloudSignInAnswer(bool Answered, DateTime? Interactive, DateTime? NonInteractive)
{
    public static CloudSignInAnswer DidNotAnswer => new(false, null, null);
}

/// <summary>The cloud half of a true-last-logon answer.</summary>
public sealed record CloudSignInResult(
    DateTime? Interactive,
    DateTime? NonInteractive,
    CloudVerification Verified)
{
    /// <summary>The later of the two sign-in kinds, which is what "last seen in the cloud" means.</summary>
    public DateTime? Latest =>
        Interactive is { } i && NonInteractive is { } n
            ? (i > n ? i : n)
            : Interactive ?? NonInteractive;

    /// <summary>
    /// True when a source answered and reported no sign-in at all.
    /// </summary>
    /// <remarks>
    /// <b>Read this with <see cref="Verified"/> and never alone.</b> Only
    /// <see cref="CloudVerification.LogVerified"/> makes it good evidence; under
    /// <see cref="CloudVerification.ActivityOnly"/> it is the exact reading that was measured
    /// wrong 9 times in 557.
    /// </remarks>
    public bool NoSignInReported => Verified != CloudVerification.Unverified && Latest == null;
}

/// <summary>
/// Combines the two cloud sources into one answer plus its trust level. Pure, so the rules are
/// testable without a tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither source is a superset of the other</b>, which is why both exist and why the
/// answer is the LATER of the two rather than a preference order:
/// </para>
/// <list type="bullet">
/// <item><c>signInActivity</c> is cheap, keeps dates older than 30 days, lags up to ~6 hours,
/// and can return null for an account that really did sign in.</item>
/// <item>The raw sign-in log is live and authoritative but retains only ~30 days, so it cannot
/// see an older sign-in that <c>signInActivity</c> still remembers.</item>
/// </list>
/// </remarks>
public static class CloudSignInAggregator
{
    /// <summary>Combines the two sources.</summary>
    public static CloudSignInResult Combine(CloudSignInAnswer? activity, CloudSignInAnswer? log)
    {
        activity ??= CloudSignInAnswer.DidNotAnswer;
        log ??= CloudSignInAnswer.DidNotAnswer;

        var verified = (activity.Answered, log.Answered) switch
        {
            (true, true) => CloudVerification.LogVerified,
            (true, false) => CloudVerification.ActivityOnly,
            (false, true) => CloudVerification.LogOnly30d,
            _ => CloudVerification.Unverified,
        };

        return new CloudSignInResult(
            Later(activity.Interactive, log.Interactive),
            Later(activity.NonInteractive, log.NonInteractive),
            verified);
    }

    /// <summary>
    /// The later of two dates, treating null as "this source has nothing", never as a zero
    /// date. A null from one source must not suppress a real date from the other.
    /// </summary>
    private static DateTime? Later(DateTime? a, DateTime? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return a > b ? a : b;
    }
}
