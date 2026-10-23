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
/// but never refused by one: an answer that would refuse is read again. A read that fails forgets the
/// account's answer, and every recorded answer sweeps out the ones that have expired (aud-a29j). The
/// circuit's own checks never use a recorded answer; they read every time.
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

    public CircuitMembershipRevalidator(
        IOperatorMembershipDirectory directory,
        AuthorizationGroupInventory inventory,
        CircuitRevalidationOptions options)
    {
        _directory = directory;
        _inventory = inventory;
        _options = options;
    }

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
        var check = Task.Run(() => RevalidateCoreAsync(user, captured, acceptRecordedAnswer, lookup.Token), CancellationToken.None);
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
            return TimedOut();
        }
        catch (OperationCanceledException)
        {
            return TimedOut();
        }
        catch (Exception ex)
        {
            return MembershipRevalidationResult.LookupFailed($"The access re-check could not be completed: {ex.Message}");
        }
    }

    private async Task<MembershipRevalidationResult> RevalidateCoreAsync(
        ClaimsPrincipal user, IReadOnlySet<AuthorizationGrant> captured, bool acceptRecordedAnswer, CancellationToken cancellationToken)
    {
        var userSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value;
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

        var membership = await ReadDirectoryAsync(userSid!, user, cancellationToken).ConfigureAwait(false);

        if (membership?.GroupSids is null)
            return MembershipRevalidationResult.LookupFailed("The directory lookup returned no answer.");

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
    // check (aud-a29g); a read that fails, answers nothing or is cancelled forgets the account's
    // recorded answer - unless a read that started later recorded one meanwhile - so a directory that
    // has stopped answering does not go on confirming pages from an old answer. The read's START is
    // what is recorded: the answer is at least that fresh, and no fresher can be claimed for it.
    private async Task<OperatorDirectoryMembership?> ReadDirectoryAsync(
        string userSid, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var started = Clock.GetTimestamp();
        OperatorDirectoryMembership? membership;
        try
        {
            membership = await _directory
                .GetMembershipAsync(userSid, SectionAccessDirectoryReading.NetBiosFromNTAccount(user.Identity?.Name), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            Forget(userSid, started);
            throw;
        }

        if (membership?.GroupSids is null)
            Forget(userSid, started);
        else
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
        // here: an expired answer replaced by a newer one in the meantime is left alone.
        foreach (var entry in _answers)
        {
            if (Clock.GetElapsedTime(entry.Value.Started) >= _options.Interval)
                _answers.TryRemove(entry);
        }
    }

    /// <summary>The account SIDs with an answer held right now, for a test to see what is kept.</summary>
    internal IReadOnlyCollection<string> RecordedAccountSids => _answers.Keys.ToArray();

    private void Forget(string userSid, long started)
    {
        if (_answers.TryGetValue(userSid, out var existing) && existing.Started <= started)
            _answers.TryRemove(new KeyValuePair<string, RecordedAnswer>(userSid, existing));
    }

    // The account's recorded answer if its read started less than one interval ago, else nothing.
    private OperatorDirectoryMembership? RecordedAnswerFor(string userSid)
    {
        if (!_answers.TryGetValue(userSid, out var answer))
            return null;

        if (Clock.GetElapsedTime(answer.Started) < _options.Interval)
            return answer.Membership;

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
