namespace ExchangeAdminWeb.Services;

/// <summary>Which source produced the true last logon.</summary>
public enum LogonSource
{
    /// <summary>No source produced a date. Read <see cref="TrueLastLogonAnswer"/> to learn why.</summary>
    None,

    /// <summary>A domain controller's <c>lastLogon</c>.</summary>
    OnPrem,

    /// <summary>An interactive cloud sign-in.</summary>
    CloudInteractive,

    /// <summary>A non-interactive cloud sign-in.</summary>
    CloudNonInteractive,
}

/// <summary>
/// The whole answer for one person: the latest logon any source saw, and - just as importantly -
/// which halves were actually asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three outcomes this type exists to keep apart.</b> A UI that renders them the same way
/// is more dangerous than the source script, because a screen looks authoritative:
/// </para>
/// <list type="number">
/// <item>A date. <see cref="LastLogon"/> is set and <see cref="Source"/> names who saw it.</item>
/// <item><b>Nothing was checked.</b> <see cref="NothingWasChecked"/>. This is "we do not know",
/// and it must render as "Not checked" - never as "Never".</item>
/// <item><b>Something was checked and saw no logon.</b> A null date with at least one half
/// checked. How much that is worth depends entirely on the coverage travelling beside it - which
/// DCs answered, and which <see cref="CloudVerification"/> state the cloud half reached.</item>
/// </list>
/// <para>
/// Pure so the rule is unit-testable without a domain or a tenant, and out of the page because
/// this repo has no bUnit harness - anything left in a .razor is reachable only by a source-level
/// tripwire.
/// </para>
/// </remarks>
/// <param name="LastLogon">The latest logon any answering source saw, or null.</param>
/// <param name="Source">Which source held <paramref name="LastLogon"/>.</param>
/// <param name="SourceDetail">
/// The domain controller that held an on-prem answer, or null. Named because a result is only as
/// good as the DC that produced it.
/// </param>
/// <param name="OnPremChecked">
/// Whether the DC sweep produced a result at all. An incomplete sweep still counts as checked -
/// coverage is reported, never required (owner, 2026-09-30) - so this is false only when the
/// sweep could not run.
/// </param>
/// <param name="CloudChecked">Whether either cloud source answered.</param>
public sealed record TrueLastLogonAnswer(
    DateTime? LastLogon,
    LogonSource Source,
    string? SourceDetail,
    bool OnPremChecked,
    bool CloudChecked)
{
    /// <summary>
    /// Neither half answered, so the module knows nothing about this account.
    /// </summary>
    /// <remarks>
    /// <b>This is the state that must never render as "Never".</b> It is the same class of defect
    /// as the migration module's empty state, which reported "this batch contains no mailboxes"
    /// for a batch whose own record said otherwise; here the cost is an active account read as
    /// dormant and disabled.
    /// </remarks>
    public bool NothingWasChecked => !OnPremChecked && !CloudChecked;

    /// <summary>
    /// A source answered and none of them had ever seen this account.
    /// </summary>
    /// <remarks>
    /// Evidence of dormancy only as far as the coverage goes, and the caller must render the
    /// coverage beside it. "No logon on the 34 DCs that answered, 3 did not answer" is the honest
    /// sentence; a bare "never" is the one that gets a live account disabled.
    /// </remarks>
    public bool NoLogonFoundOnWhatWasChecked => !NothingWasChecked && LastLogon == null;
}

/// <summary>
/// Combines the on-prem and cloud halves into the one line the page leads with.
/// </summary>
/// <remarks>
/// <para>
/// <b>The maximum, not a preference order.</b> Neither half is a superset of the other:
/// <c>lastLogon</c> is the only source that sees on-prem-only activity, so a cloud-only answer
/// calls a workstation user dormant; and the cloud sources see sign-ins no DC ever handled. The
/// answer is whichever is later.
/// </para>
/// <para>
/// <b>A half that FAILED is not a half that reported nothing.</b> Both are passed as null here to
/// mean "did not answer", and the two Checked flags carry that fact out to the page so it can say
/// "Not checked" rather than inventing an absence. Collapsing the two is the single worst failure
/// this module can have.
/// </para>
/// </remarks>
public static class TrueLastLogonCombiner
{
    /// <summary>
    /// Combines the two halves.
    /// </summary>
    /// <param name="onPrem">
    /// The DC sweep's result, or null when the sweep could not run. A result with
    /// <see cref="OnPremLogonResult.AnsweredCount"/> of zero is still an answer about coverage and
    /// counts as checked.
    /// </param>
    /// <param name="cloud">
    /// The cloud lookup, or null when it could not run. A lookup whose
    /// <see cref="CloudSignInResult.Verified"/> is <see cref="CloudVerification.Unverified"/> did
    /// not answer, and does NOT count as checked however it arrived here.
    /// </param>
    public static TrueLastLogonAnswer Combine(OnPremLogonResult? onPrem, CloudSignInResult? cloud)
    {
        // Unverified means neither cloud source responded, so an Unverified lookup with nothing
        // in it did NOT check the cloud - treating it as checked would let a total Graph outage
        // render as a half that looked and saw nothing, which is the collapse this whole module
        // exists to prevent.
        //
        // The "|| Latest != null" is not belt and braces, it is a reachable case.
        // CloudSignInService marks the log as having answered only when BOTH its queries did, so
        // an interactive query that returned a real sign-in while the non-interactive one and
        // signInActivity both failed arrives here as Unverified CARRYING A DATE. Without this
        // clause the page would report a date and "Not checked" in the same breath. The date is
        // real; what is missing is the confidence, and Verified already carries that.
        var cloudChecked = cloud != null
            && (cloud.Verified != CloudVerification.Unverified || cloud.Latest != null);

        var candidates = new (DateTime? When, LogonSource Source, string? Detail)[]
        {
            (onPrem?.LastLogon, LogonSource.OnPrem, onPrem?.SourceDomainController),
            (cloud?.Interactive, LogonSource.CloudInteractive, null),
            (cloud?.NonInteractive, LogonSource.CloudNonInteractive, null),
        };

        var best = candidates
            .Where(c => c.When.HasValue)
            .OrderByDescending(c => c.When!.Value)
            .FirstOrDefault();

        return new TrueLastLogonAnswer(
            best.When,
            best.When.HasValue ? best.Source : LogonSource.None,
            best.When.HasValue ? best.Detail : null,
            OnPremChecked: onPrem != null,
            CloudChecked: cloudChecked);
    }
}
