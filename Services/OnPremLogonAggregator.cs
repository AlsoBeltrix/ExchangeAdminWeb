namespace ExchangeAdminWeb.Services;

/// <summary>One domain controller's answer for one user's <c>lastLogon</c>.</summary>
/// <param name="DomainController">The DC that was asked.</param>
/// <param name="LastLogon">
/// The logon this DC recorded, or null when it recorded none. Null and "the DC did not answer"
/// are DIFFERENT and are never both represented here - a DC that failed carries
/// <paramref name="Error"/>.
/// </param>
/// <param name="Error">Why this DC produced no answer, or null when it answered.</param>
public sealed record DomainControllerLogon(string DomainController, DateTime? LastLogon, string? Error);

/// <summary>
/// The on-prem half of a true-last-logon answer.
/// </summary>
/// <param name="LastLogon">
/// The most recent logon any ANSWERING domain controller recorded, or null when every one of
/// them recorded none.
/// </param>
/// <param name="SourceDomainController">Which DC held <paramref name="LastLogon"/>.</param>
/// <param name="Skipped">
/// Every DC that did not answer, with the reason. **Named, never counted.** See
/// <see cref="OnPremLogonAggregator"/> for why this is the load-bearing field.
/// </param>
/// <param name="AnsweredCount">How many DCs actually answered.</param>
public sealed record OnPremLogonResult(
    DateTime? LastLogon,
    string? SourceDomainController,
    IReadOnlyList<DomainControllerLogon> Skipped,
    int AnsweredCount)
{
    /// <summary>
    /// True only when EVERY domain controller answered. When false the date below is a floor,
    /// not the answer: a DC that was not asked can hold a more recent logon.
    /// </summary>
    public bool EveryDomainControllerAnswered => Skipped.Count == 0;

    /// <summary>
    /// True when every DC answered AND none of them had ever seen this user. The ONLY
    /// combination that supports "this account has not logged on to the domain".
    /// </summary>
    public bool ConfirmedNeverOnPrem => EveryDomainControllerAnswered && AnsweredCount > 0 && LastLogon == null;
}

/// <summary>
/// Turns per-DC answers into the on-prem result. Pure, so the rule that actually matters is
/// unit-testable without a domain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every DC has to be asked.</b> <c>lastLogon</c> does not replicate: each DC records
/// only the logons it personally handled and tells no other DC. Ask one and the answer can be
/// months stale. The true value is the MAXIMUM across all of them. (<c>lastLogonTimestamp</c>
/// does replicate but is deliberately imprecise - up to 14 days by design - so it cannot answer
/// "did this person log on last Tuesday".)
/// </para>
/// <para>
/// <b>Why a skipped DC is not a detail.</b> A result computed from 30 of 37 DCs is a different
/// claim from one computed from all 37, and the difference is invisible in the date itself. The
/// source script is emphatic about this and it is right: a DC that was not queried can hide a
/// more recent logon. So an unanswered DC is carried by NAME into the result and
/// <see cref="OnPremLogonResult.EveryDomainControllerAnswered"/> goes false. Reporting a count,
/// or worse dropping them, turns "we do not know" into "they never logged on" - which is the
/// direction that gets an active account disabled.
/// </para>
/// </remarks>
public static class OnPremLogonAggregator
{
    /// <summary>
    /// Partitions <paramref name="answers"/> into the newest logon and the DCs that failed.
    /// </summary>
    public static OnPremLogonResult Aggregate(IEnumerable<DomainControllerLogon>? answers)
    {
        if (answers == null)
            return new OnPremLogonResult(null, null, [], 0);

        DateTime? newest = null;
        string? source = null;
        var skipped = new List<DomainControllerLogon>();
        var answered = 0;

        foreach (var answer in answers)
        {
            if (answer == null)
                continue;

            // A DC that errored is SKIPPED even if it also carried a date - the date cannot be
            // trusted to be that DC's final word, and counting it as an answer would let a
            // partial result claim completeness.
            if (answer.Error != null)
            {
                skipped.Add(answer);
                continue;
            }

            answered++;

            // A lastLogon of zero/default means "this DC has never seen them", which is a real
            // answer and not a missing one. Only a LATER date replaces the running maximum.
            if (answer.LastLogon is { } when && (newest == null || when > newest))
            {
                newest = when;
                source = answer.DomainController;
            }
        }

        return new OnPremLogonResult(newest, source, skipped, answered);
    }
}
