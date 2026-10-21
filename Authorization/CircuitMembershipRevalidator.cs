using System.Security.Claims;
using System.Security.Principal;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// One revalidation verdict. <see cref="Reason"/> is null when the circuit may keep its access;
/// <see cref="RemovedGroupSids"/> lists the held consulted groups the account is no longer in.
/// <see cref="Detail"/> is for the audit line and names no group.
/// </summary>
public sealed record MembershipRevalidationResult(
    CircuitRevocationReason? Reason,
    IReadOnlyList<string> RemovedGroupSids,
    string Detail)
{
    public bool IsValid => Reason is null;

    public static MembershipRevalidationResult Valid(string detail) => new(null, Array.Empty<string>(), detail);

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
/// Fail closed, every way: no usable account SID, a compared set that cannot be resolved, a
/// directory that throws, answers nothing, or does not answer within
/// <see cref="CircuitRevalidationOptions.LookupTimeout"/> - each is
/// <see cref="CircuitRevocationReason.LookupFailed"/>. The timeout covers the whole check, name
/// translation and queueing included, and on expiry the lookup's token is cancelled, which stops
/// the running directory command.
/// </para>
/// <para>
/// Throws only <see cref="OperationCanceledException"/>, and only for the caller's own token (the
/// circuit is going away). Everything else is a verdict.
/// </para>
/// </remarks>
public sealed class CircuitMembershipRevalidator
{
    private readonly IOperatorMembershipDirectory _directory;
    private readonly AuthorizationGroupInventory _inventory;
    private readonly CircuitRevalidationOptions _options;

    public CircuitMembershipRevalidator(
        IOperatorMembershipDirectory directory,
        AuthorizationGroupInventory inventory,
        CircuitRevalidationOptions options)
    {
        _directory = directory;
        _inventory = inventory;
        _options = options;
    }

    public async Task<MembershipRevalidationResult> RevalidateAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        // The check can outlive this method - a timeout stops waiting for it, not the check - so the
        // token source is released when the check ends rather than here. Disposing it on return
        // would also stop the CancelAfter timer that tells a still-running command to stop.
        var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lookup.CancelAfter(_options.LookupTimeout);

        // Off the caller's thread: the compared set reads SQLite and may translate names through
        // LSA, which can block, and the circuit-open check must not hold up a circuit's first render.
        var check = Task.Run(() => RevalidateCoreAsync(user, lookup.Token), CancellationToken.None);
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

    private async Task<MembershipRevalidationResult> RevalidateCoreAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value;
        if (!IsAccountSid(userSid))
        {
            return MembershipRevalidationResult.LookupFailed(
                "The signed-in principal carries no usable account SID, so its access cannot be re-checked.");
        }

        var consulted = _inventory.GetConsultedSids();
        var tokenGroups = new HashSet<string>(GroupMembershipChecker.ExtractGroupClaims(user), StringComparer.OrdinalIgnoreCase);
        var held = consulted
            .Where(tokenGroups.Contains)
            .OrderBy(sid => sid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Holding nothing a gate consults, the token grants nothing a revocation could take away.
        if (held.Count == 0)
            return MembershipRevalidationResult.Valid("The principal holds no group an authorization gate consults.");

        var membership = await _directory
            .GetMembershipAsync(userSid!, SectionAccessDirectoryReading.NetBiosFromNTAccount(user.Identity?.Name), cancellationToken)
            .ConfigureAwait(false);

        if (membership?.GroupSids is null)
            return MembershipRevalidationResult.LookupFailed("The directory lookup returned no answer.");

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

        return MembershipRevalidationResult.Valid($"The account still holds all {held.Count} consulted group(s).");
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
}
