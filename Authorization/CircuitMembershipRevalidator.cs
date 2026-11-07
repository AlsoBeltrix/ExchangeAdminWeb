using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Principal;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// One revalidation verdict. <see cref="Reason"/> is null when the circuit may keep its access;
/// <see cref="RemovedGroupSids"/> lists the held consulted groups the account is no longer in, or
/// the SIDs of the grants that were removed from the app's settings.
/// <see cref="Detail"/> is for the audit line and names no group.
/// </summary>
public sealed record MembershipRevalidationResult(
    CircuitRevocationReason? Reason,
    IReadOnlyList<string> RemovedGroupSids,
    string Detail)
{
    private static readonly IReadOnlySet<AuthorizationGrant> NoGrants = new HashSet<AuthorizationGrant>();

    /// <summary>
    /// On a valid verdict, every grant this check confirmed: the ones it was given and the ones the
    /// token holds now. The provider adds them to what its circuit was confirmed through (aud-a29d).
    /// </summary>
    public IReadOnlySet<AuthorizationGrant> Grants { get; init; } = NoGrants;

    /// <summary>
    /// sweep-c item 15. The fresh directory read failed or timed out and this verdict is the account's last recorded answer,
    /// read within <see cref="CircuitMembershipRevalidator.FallbackWindow"/>, compared as a fresh one would be.
    /// </summary>
    public bool FromRecordedAnswer { get; init; }

    public bool IsValid => Reason is null;

    public static MembershipRevalidationResult Valid(string detail) => new(null, Array.Empty<string>(), detail);

    public static MembershipRevalidationResult Valid(string detail, IReadOnlySet<AuthorizationGrant> grants) =>
        new(null, Array.Empty<string>(), detail) { Grants = grants };

    public static MembershipRevalidationResult LookupFailed(string detail) =>
        new(CircuitRevocationReason.LookupFailed, Array.Empty<string>(), detail);
}

/// <summary>
/// Re-derives a circuit principal's authorization-relevant group membership from the directory and
/// says whether the principal may keep the access its token gives it (aud-a29b).
/// </summary>
/// <remarks>
/// <para>
/// The token is fixed when the circuit opens - under Kerberos, when Windows was issued the ticket -
/// and <c>WindowsPrincipal.IsInRole</c> tests that token, never the directory. So the check takes
/// the consulted groups the token HOLDS and asks whether the account is still in every one of them.
/// </para>
/// <para>
/// Only held consulted SIDs are compared, never the whole token. <c>tokenGroups</c> read from the
/// account's domain and the token a server builds differ by construction - resource-domain
/// domain-local groups, SID history, well-known SIDs - and comparing everything would end sessions
/// over groups no gate reads. Gaining a group never invalidates.
/// </para>
/// <para>
/// The store can move as well as the directory (aud-a29d). The caller passes the grants its circuit
/// was already confirmed through; any of them no longer in the inventory ends the circuit
/// (<see cref="CircuitRevocationReason.GrantRemoved"/>) without a directory read, and the SIDs of
/// the rest are compared with the directory alongside the ones the token holds now - so a captured
/// grant stays compared even when a reconnect brings a token that no longer carries its group.
/// </para>
/// <para>
/// Fail closed, every way: no usable account SID, a compared set that cannot be resolved (a store
/// that cannot be read included), a token carrying a static grant the directory cannot attest to, a
/// directory that throws, answers nothing, or does not answer within
/// <see cref="CircuitRevalidationOptions.LookupTimeout"/> - each is
/// <see cref="CircuitRevocationReason.LookupFailed"/>. The timeout covers the whole check, name
/// translation and queueing included, and on expiry the lookup's token is cancelled, which stops
/// the running directory command.
/// </para>
/// <para>
/// Page requests are checked too (aud-a29g, <see cref="RevalidatePageRequestAsync"/>), by this same
/// check, before the page is prerendered. Every successful directory read is recorded per account,
/// and a page request may be confirmed by a recorded answer whose read started less than
/// <see cref="CircuitRevalidationOptions.Interval"/> ago - the window an open circuit already has -
/// but never refused by one: an answer that would refuse is read again. Every recorded answer sweeps out
/// the ones that have expired (aud-a29j). The circuit's own checks never use a recorded answer in place
/// of a read; they read every time.
/// </para>
/// <para>
/// sweep-c item 15. One failed lookup no longer ends a session. When the fresh read fails, answers
/// nothing or does not answer within <see cref="CircuitRevalidationOptions.LookupTimeout"/>, the
/// account's last recorded answer is used if its read started within <see cref="FallbackWindow"/> -
/// one interval plus two lookup timeouts, so the answer the circuit's own previous check recorded is
/// still there when the next one fails. rev-b5: that window alone does not make two failed checks in a row end a session -
/// with an interval shorter than two lookup timeouts one answer outlives several failed checks - so each verdict that stood
/// in for a failed read says so (<see cref="MembershipRevalidationResult.FromRecordedAnswer"/>), and the session's provider
/// counts them and ends the session at the second in a row; the window stays the outer bound. That answer is
/// compared exactly as a fresh one: it confirms, or it refuses with its own reason - a removal the
/// directory reported is never forgiven. Only with no such answer does the failure stand, and the
/// session ends as before. Every fallback is counted (<see cref="FallbackCount"/>) and logged at
/// Warning with the count. Before, a read that failed forgot the account's answer (aud-a29j), so one
/// two-minute stall in the directory ended every tab that was checked during it.
/// </para>
/// <para>
/// Throws only <see cref="OperationCanceledException"/>, and only for the caller's own token (the
/// circuit is going away, or the browser abandoned the page request). Everything else is a verdict.
/// </para>
/// </remarks>
public sealed class CircuitMembershipRevalidator
{
    private static readonly IReadOnlySet<AuthorizationGrant> NothingCaptured = new HashSet<AuthorizationGrant>();

    private readonly IOperatorMembershipDirectory _directory;
    private readonly AuthorizationGroupInventory _inventory;
    private readonly CircuitRevalidationOptions _options;

    // The directory's latest answer per account SID, and when its read started (aud-a29g). Facts about
    // the account, never verdicts: a page request still recomputes its verdict from the settings as they
    // are now and from its own token. One entry per operator, replaced by every later read; one that has
    // expired is swept out whenever any answer is recorded (aud-a29j), so an operator who never comes
    // back is not kept for the app pool's life.
    private readonly ConcurrentDictionary<string, RecordedAnswer> _answers = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _logger;
    private long _fallbacks;

    public CircuitMembershipRevalidator(
        IOperatorMembershipDirectory directory,
        AuthorizationGroupInventory inventory,
        CircuitRevalidationOptions options,
        ILogger<CircuitMembershipRevalidator>? logger = null)
    {
        _directory = directory;
        _inventory = inventory;
        _options = options;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// sweep-c item 15. How old a recorded answer may be and still stand in for a fresh read that failed: one interval plus
    /// two lookup timeouts. The circuit's loop starts its next check one interval after the last one ended, a check takes
    /// at most one lookup timeout, and the failure is known at most one more later - so the previous check's answer is
    /// always within it, and an answer two checks old never is.
    /// </summary>
    internal TimeSpan FallbackWindow => _options.Interval + _options.LookupTimeout + _options.LookupTimeout;

    /// <summary>sweep-c item 15. How many checks a recorded answer has stood in for since the app started.</summary>
    internal long FallbackCount => Interlocked.Read(ref _fallbacks);

    /// <summary>
    /// The monotonic clock a recorded answer's age is measured on. A test sets its own; nothing else
    /// does (the <see cref="Services.OperatorMembershipDirectory.WorkerScheduler"/> pattern).
    /// </summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The circuit's check: the open check and the interval loop. Always reads the directory.</summary>
    public Task<MembershipRevalidationResult> RevalidateAsync(
        ClaimsPrincipal user, IReadOnlySet<AuthorizationGrant> captured, CancellationToken cancellationToken)
        => CheckAsync(user, captured, acceptRecordedAnswer: false, cancellationToken);

    /// <summary>
    /// The page-request check (aud-a29g): run before a page is rendered, so a page's prerender - its
    /// <c>OnInitialized</c> included - never runs for a principal this check refuses.
    /// </summary>
    /// <remarks>
    /// The circuit's check with nothing captured (there is no circuit yet), so every rule is the same.
    /// The one difference is the recorded answer: one whose read started within the interval may
    /// confirm the request without a directory read, so repeated page loads and enhanced navigations
    /// cost nothing while an account is being read anyway. It never refuses: an answer that would
    /// refuse is read again, and the refusal is the fresh read's. The caller owns the off-switch, as
    /// the circuit's provider does.
    /// </remarks>
    public Task<MembershipRevalidationResult> RevalidatePageRequestAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => CheckAsync(user, NothingCaptured, acceptRecordedAnswer: true, cancellationToken);

    private async Task<MembershipRevalidationResult> CheckAsync(
        ClaimsPrincipal user, IReadOnlySet<AuthorizationGrant> captured, bool acceptRecordedAnswer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(captured);
        cancellationToken.ThrowIfCancellationRequested();

        // The check can outlive this method - a timeout stops waiting for it, not the check - so the
        // token source is released when the check ends rather than here. Disposing it on return
        // would also stop the CancelAfter timer that tells a still-running command to stop.
        var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lookup.CancelAfter(_options.LookupTimeout);

        // Off the caller's thread: the compared set reads SQLite and may translate names through
        // LSA, which can block, and the circuit-open check must not hold up a circuit's first render.
        // sweep-c item 15: what the check had established before it asked the directory, so a timeout here can fall back.
        var state = new CheckState();
        var check = Task.Run(() => RevalidateCoreAsync(user, captured, acceptRecordedAnswer, state, lookup.Token), CancellationToken.None);
        _ = check.ContinueWith(task =>
        {
            _ = task.Exception;
            lookup.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            return await check.WaitAsync(_options.LookupTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (TimeoutException)
        {
            return FallBackOrFail(state, TimedOut());
        }
        catch (OperationCanceledException)
        {
            return FallBackOrFail(state, TimedOut());
        }
        catch (Exception ex)
        {
            return MembershipRevalidationResult.LookupFailed($"The access re-check could not be completed: {ex.Message}");
        }
    }

    /// <summary>
    /// sweep-c item 15. The fresh read failed: the account's last recorded answer from within <see cref="FallbackWindow"/>,
    /// compared as a fresh one would be, or - with none, or before the check reached the directory - the failure itself.
    /// </summary>
    /// <remarks>
    /// Only a directory failure falls back: a check that failed before its read (no usable SID, an unreadable store, a
    /// static grant the directory cannot attest to) never set <see cref="CheckState.Reading"/>. Whichever of the check and
    /// its timeout gets here first counts and logs it; the other's verdict is the same and is not returned.
    /// </remarks>
    private MembershipRevalidationResult FallBackOrFail(CheckState state, MembershipRevalidationResult failure)
    {
        if (!state.Reading || state.UserSid is null || state.Held is null || state.Confirmed is null)
            return failure;

        if (!_answers.TryGetValue(state.UserSid, out var answer))
            return failure;

        var age = Clock.GetElapsedTime(answer.Started);
        if (age >= FallbackWindow)
            return failure;

        var verdict = Compare(answer.Membership, state.Held, state.Confirmed);
        var outcome = verdict with
        {
            Detail = $"{verdict.Detail} (From the answer read {age.TotalSeconds:0} s ago: the fresh lookup failed - {failure.Detail})",
            FromRecordedAnswer = true,
        };

        if (Interlocked.Exchange(ref state.Settled, 1) == 0)
        {
            var count = Interlocked.Increment(ref _fallbacks);
            _logger.LogWarning(
                "The directory lookup for {Account} failed ({Failure}); its access was {Outcome} from the answer read {Age:0} s ago. {Count} lookup(s) have fallen back to a recorded answer since the app started",
                state.AccountName ?? state.UserSid, failure.Detail, outcome.IsValid ? "confirmed" : "refused", age.TotalSeconds, count);
        }

        return outcome;
    }

    /// <summary>sweep-c item 15. What one check had established when it asked the directory.</summary>
    private sealed class CheckState
    {
        public string? UserSid;
        public string? AccountName;
        public IReadOnlyList<string>? Held;
        public IReadOnlySet<AuthorizationGrant>? Confirmed;
        public volatile bool Reading;
        public int Settled;
    }

    private async Task<MembershipRevalidationResult> RevalidateCoreAsync(
        ClaimsPrincipal user, IReadOnlySet<AuthorizationGrant> captured, bool acceptRecordedAnswer, CheckState state,
        CancellationToken cancellationToken)
    {
        var userSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value;
        // kd-90: the name read once, beside the SID, as a string; nothing below reads the principal for it again.
        var accountName = user.Identity?.Name;
        if (!IsAccountSid(userSid))
        {
            return MembershipRevalidationResult.LookupFailed(
                "The signed-in principal carries no usable account SID, so its access cannot be re-checked.");
        }

        // Throws on a store that cannot be read or a name that cannot be translated, which the
        // caller turns into LookupFailed: an unreadable store is never "nothing granted" here.
        var inventory = _inventory.Read();

        // A grant this circuit came through that the app no longer grants. The page it opened may
        // never ask again, so the removal itself ends the circuit; the directory has nothing to add.
        var withdrawn = captured.Where(grant => !inventory.Grants.Contains(grant)).ToList();
        if (withdrawn.Count > 0)
        {
            return new MembershipRevalidationResult(
                CircuitRevocationReason.GrantRemoved,
                withdrawn
                    .Select(grant => grant.Sid)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(sid => sid, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                $"{withdrawn.Count} grant(s) this session's access came through no longer exist in the app's access settings.");
        }

        // What is compared: the grants already captured, plus every grant the token holds now. The
        // captured ones stay in even when this token no longer carries their group - a reconnect's
        // token is not evidence that the circuit stopped using them.
        var tokenGroups = new HashSet<string>(GroupMembershipChecker.ExtractGroupClaims(user), StringComparer.OrdinalIgnoreCase);

        // A static grant the directory cannot attest to (aud-a29e). Startup refuses these while
        // revalidation is on; one that got past cannot be confirmed for a token that carries it, so it
        // is not skipped - it fails closed. The detail names the setting, never the group.
        var unconfirmable = inventory.Unrevalidatable
            .Where(grant => tokenGroups.Contains(grant.Sid))
            .Select(grant => grant.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unconfirmable.Count > 0)
        {
            return MembershipRevalidationResult.LookupFailed(
                "This session's access may come through a configured group that cannot be re-checked "
                + $"against the directory ({string.Join(", ", unconfirmable)}).");
        }

        var confirmed = new HashSet<AuthorizationGrant>(captured);
        confirmed.UnionWith(inventory.Grants.Where(grant => tokenGroups.Contains(grant.Sid)));
        var held = confirmed
            .Select(grant => grant.Sid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(sid => sid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Holding nothing a gate consults, the token grants nothing a revocation could take away.
        if (held.Count == 0)
            return MembershipRevalidationResult.Valid("The principal holds no group an authorization gate consults.");

        // A page request may be confirmed by an answer read within the interval (aud-a29g), never
        // refused by one: a recorded answer that would refuse is read again below, so a refusal is
        // always the directory's current word.
        if (acceptRecordedAnswer && RecordedAnswerFor(userSid!) is { } recorded)
        {
            var fromRecorded = Compare(recorded, held, confirmed);
            if (fromRecorded.IsValid)
                return fromRecorded;
        }

        // sweep-c item 15: from here a failure is the directory's, and may fall back to the account's recorded answer.
        state.UserSid = userSid;
        state.AccountName = accountName;
        state.Held = held;
        state.Confirmed = confirmed;
        state.Reading = true;

        OperatorDirectoryMembership? membership;
        try
        {
            membership = await ReadDirectoryAsync(userSid!, accountName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cancellation is the timeout's or the caller's, and the caller's CheckAsync answers both.
            return FallBackOrFail(state, MembershipRevalidationResult.LookupFailed($"The access re-check could not be completed: {ex.Message}"));
        }

        if (membership?.GroupSids is null)
            return FallBackOrFail(state, MembershipRevalidationResult.LookupFailed("The directory lookup returned no answer."));

        return Compare(membership, held, confirmed);
    }

    private static MembershipRevalidationResult Compare(
        OperatorDirectoryMembership membership, IReadOnlyList<string> held, IReadOnlySet<AuthorizationGrant> confirmed)
    {
        // A disabled account keeps its group memberships, and disabling is the usual way someone
        // leaves: comparing groups alone would let exactly that operator keep working.
        if (!membership.AccountEnabled)
        {
            return new MembershipRevalidationResult(
                CircuitRevocationReason.AccountDisabled, Array.Empty<string>(), "The account is disabled in the directory.");
        }

        var current = new HashSet<string>(membership.GroupSids, StringComparer.OrdinalIgnoreCase);
        var removed = held.Where(sid => !current.Contains(sid)).ToList();
        if (removed.Count > 0)
        {
            return new MembershipRevalidationResult(
                CircuitRevocationReason.MembershipChanged,
                removed,
                $"The account is no longer in {removed.Count} group(s) this session's access was granted through.");
        }

        return MembershipRevalidationResult.Valid($"The account still holds all {held.Count} consulted group(s).", confirmed);
    }

    // One directory read, whoever asked for it. A successful answer is recorded for the page-request
    // check (aud-a29g) and for the fallback (sweep-c item 15). The read's START is what is recorded: the
    // answer is at least that fresh, and no fresher can be claimed for it. sweep-c item 15: a read that
    // fails, answers nothing or is cancelled no longer forgets the account's answer (aud-a29j's rule) -
    // the answer is what a failed read falls back to, and it expires on its own at FallbackWindow, so a
    // directory that has stopped answering stops confirming anything one window after its last answer.
    private async Task<OperatorDirectoryMembership?> ReadDirectoryAsync(
        string userSid, string? accountName, CancellationToken cancellationToken)
    {
        var started = Clock.GetTimestamp();
        var membership = await _directory
            .GetMembershipAsync(userSid, SectionAccessDirectoryReading.NetBiosFromNTAccount(accountName), cancellationToken)
            .ConfigureAwait(false);

        if (membership?.GroupSids is not null)
            Record(userSid, membership, started);

        return membership;
    }

    private void Record(string userSid, OperatorDirectoryMembership membership, long started)
    {
        var answer = new RecordedAnswer(membership, started);
        _answers.AddOrUpdate(userSid, answer, (_, existing) => existing.Started > started ? existing : answer);

        // The sweep (aud-a29j). Here, where the dictionary grows and only after a directory read, so
        // one pass over the operators costs nothing beside the read; the page-request path, which reads
        // the dictionary on every page, stays a single lookup. Instance-guarded, like every removal
        // here: an expired answer replaced by a newer one in the meantime is left alone. sweep-c item 15:
        // kept to the fallback window, the longest any answer is used.
        foreach (var entry in _answers)
        {
            if (Clock.GetElapsedTime(entry.Value.Started) >= FallbackWindow)
                _answers.TryRemove(entry);
        }
    }

    /// <summary>The account SIDs with an answer held right now, for a test to see what is kept.</summary>
    internal IReadOnlyCollection<string> RecordedAccountSids => _answers.Keys.ToArray();

    // The account's recorded answer if its read started less than one interval ago, else nothing - the page-request
    // shortcut, which spares a read. An answer past the fallback window is removed here too.
    private OperatorDirectoryMembership? RecordedAnswerFor(string userSid)
    {
        if (!_answers.TryGetValue(userSid, out var answer))
            return null;

        var age = Clock.GetElapsedTime(answer.Started);
        if (age < _options.Interval)
            return answer.Membership;

        if (age >= FallbackWindow)
            _answers.TryRemove(new KeyValuePair<string, RecordedAnswer>(userSid, answer));
        return null;
    }

    private MembershipRevalidationResult TimedOut() =>
        MembershipRevalidationResult.LookupFailed(
            $"The directory did not answer within {_options.LookupTimeout.TotalSeconds:0} seconds.");

    private static bool IsAccountSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            return new SecurityIdentifier(value).IsAccountSid();
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // A class, not a record: removal compares by reference, so only the very answer that was read is
    // ever removed, never one a later read put in its place.
    private sealed class RecordedAnswer(OperatorDirectoryMembership membership, long started)
    {
        public OperatorDirectoryMembership Membership { get; } = membership;

        public long Started { get; } = started;
    }
}
