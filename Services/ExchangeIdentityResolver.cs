using System.Collections.ObjectModel;
using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

public class ExchangeIdentityResolver : ExchangeServiceBase, IIdentityResolver
{
    // Exchange reports an unknown recipient as a terminating error, not an empty result set.
    // Matching on this fragment is how the repo already separates "no such recipient" from a
    // real failure (PermissionValidator.TryExpandGroupAsync, Services/PermissionValidator.cs:366).
    private const string NotFoundFragment = "couldn't be found";

    public ExchangeIdentityResolver(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<ExchangeIdentityResolver> logger, IConfiguration config)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "") { }

    public async Task<string?> ResolveToObjectIdAsync(string identity)
    {
        try
        {
            // Read-only: safe to retry on a dead pooled session.
            return await RunPooledQueryAsync((ps, tracker) =>
            {
                ps.AddCommand("Get-Recipient")
                  .AddParameter("Identity", identity)
                  .AddParameter("ErrorAction", "Stop");

                var results = Invoke(ps, tracker);
                var recipient = results.FirstOrDefault();
                return recipient?.Properties["ExternalDirectoryObjectId"]?.Value?.ToString();
            }, allowRetry: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve identity for {Identity}", identity);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ResolvedRecipient?> ResolveRecipientAsync(string identity)
    {
        // Deliberately NOT wrapped in a catch-all. Unlike ResolveToObjectIdAsync above, this
        // method's null return is consumed as evidence that the recipient does not exist, and a
        // caller may allow an operation on that basis. Collapsing a failure into null here would
        // let an EXO outage present as an affirmative absence and un-protect a principal. Only a
        // confirmed "not found" returns null; everything else propagates.
        if (!_exoPool.IsConfigured)
            throw new InvalidOperationException("Exchange Online is not configured - cannot resolve recipient.");

        return await RunPooledQueryAsync<ResolvedRecipient?>((ps, tracker) =>
        {
            ps.AddCommand("Get-Recipient")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "Stop");

            Collection<PSObject> results;
            try
            {
                results = Invoke(ps, tracker);
            }
            catch (Exception ex) when (IsRecipientNotFound(ex))
            {
                // Affirmative absence: Exchange answered, and the answer is "no such recipient".
                _logger.LogInformation("Exchange Online reports no recipient for {Identity}", identity);
                return null;
            }

            return MapRecipient(results, identity);
        }, allowRetry: true);
    }

    /// <summary>
    /// True only for Exchange's "no such recipient" terminating error. Every other exception is
    /// a failed lookup and must propagate - see the contract on
    /// <see cref="IIdentityResolver.ResolveRecipientAsync"/>.
    /// </summary>
    internal static bool IsRecipientNotFound(Exception ex)
        => ex.Message.Contains(NotFoundFragment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a Get-Recipient result set to a <see cref="ResolvedRecipient"/>. Separated from the
    /// pooled call so the absence/failure boundary is unit-testable without a live EXO session.
    /// </summary>
    internal static ResolvedRecipient? MapRecipient(Collection<PSObject> results, string identity)
    {
        var recipient = results.FirstOrDefault();
        if (recipient == null)
            return null;

        var primary = recipient.Properties["PrimarySmtpAddress"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(primary))
        {
            // A recipient with no primary address cannot be re-resolved or matched against the
            // protected list. Returning null would report an affirmative absence for a recipient
            // that demonstrably exists - an un-protecting guess. Fail the lookup instead.
            throw new InvalidOperationException(
                $"Exchange Online returned a recipient for '{identity}' with no PrimarySmtpAddress.");
        }

        // aud-a2b hole 3. Sync state decides which rules can apply: a synced recipient is
        // re-resolved in AD so its group, OU and pattern rules run, a cloud-only one is matched
        // against the user rows alone. An unknown state used to read as cloud-only, which is the
        // branch that checks LESS - so the comment that called it conservative was wrong. A
        // recipient whose sync state cannot be read has not been resolved; fail the lookup, the
        // same way a missing primary address does above.
        var synced = IsDirSynced(recipient)
            ?? throw new InvalidOperationException(
                $"Exchange Online returned a recipient for '{identity}' without a readable IsDirSynced.");

        return new ResolvedRecipient(
            PrimarySmtpAddress: primary.Trim(),
            ExternalDirectoryObjectId: recipient.Properties["ExternalDirectoryObjectId"]?.Value?.ToString(),
            RecipientType: recipient.Properties["RecipientType"]?.Value?.ToString(),
            ExistsOnPrem: synced,
            ExchangeGuid: ReadExchangeGuid(recipient));
    }

    /// <summary>
    /// Exchange's mailbox GUID, or null when the recipient has none. Unlike
    /// <see cref="IsDirSynced"/>, null does not fail the lookup: a group or a contact legitimately
    /// has no mailbox GUID, and only the synced-mailbox branch of
    /// <c>ProtectedPrincipalService.ResolveExchangeTargetAsync</c> needs one - where its absence
    /// refuses (aud-a2c). All zeros is Exchange's way of saying "none", so it is read as null;
    /// passing it through would send a search for a zero GUID into Active Directory.
    /// Shared with <see cref="ExchangeServiceBase"/>'s read-back of a validated mailbox (aud-a2d),
    /// so the GUID a write is checked against is parsed exactly as the GUID the check proved.
    /// </summary>
    internal static Guid? ReadExchangeGuid(PSObject recipient)
    {
        var value = recipient.Properties["ExchangeGuid"]?.Value;
        if (value is PSObject wrapped)
            value = wrapped.BaseObject;

        Guid? parsed = value switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var p) => p,
            _ => null
        };

        return parsed == Guid.Empty ? null : parsed;
    }

    /// <summary>
    /// Exchange's directory-synced flag, or null when it is absent or unparseable. Null is not a
    /// third answer the caller may pick a branch for: <see cref="MapRecipient"/> fails the lookup
    /// on it.
    /// </summary>
    private static bool? IsDirSynced(PSObject recipient)
    {
        var value = recipient.Properties["IsDirSynced"]?.Value;
        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }
}
