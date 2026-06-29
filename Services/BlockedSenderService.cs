using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Models.BlockedSenders;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Reads and clears Exchange Online blocked senders via <c>Get-BlockedSenderAddress</c> and
/// <c>Remove-BlockedSenderAddress</c>. Cloud-only: both cmdlets exist only in Exchange Online,
/// so there is no on-prem path here. EXO auth is owned by the ExchangeOnline parent module and
/// the shared <see cref="ExoConnectionPool"/>; this service needs no module-specific credential.
/// </summary>
public class BlockedSenderService : ExchangeServiceBase
{
    public BlockedSenderService(
        ExoConnectionPool exoPool,
        DelineaService delineaService,
        ILogger<BlockedSenderService> logger,
        OperationTraceService operationTrace)
        : base(exoPool, delineaService, logger, onPremServerUri: "", moduleCredentials: null, moduleId: "BlockedSenders", operationTrace: operationTrace)
    {
    }

    /// <summary>
    /// Returns the current blocked-sender list from Exchange Online. Read-only, so it is safe to
    /// retry once on a dead pooled session (<c>allowRetry: true</c>).
    /// </summary>
    public Task<IReadOnlyList<BlockedSenderInfo>> GetBlockedSendersAsync()
    {
        return RunPooledQueryAsync<IReadOnlyList<BlockedSenderInfo>>((ps, tracker) =>
        {
            ps.AddCommand("Get-BlockedSenderAddress");
            var results = Invoke(ps, tracker);

            return results
                .Select(BlockedSenderInfo.FromPSObject)
                .Where(s => s is not null)
                .Select(s => s!)
                .OrderBy(s => s.SenderAddress, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, allowRetry: true);
    }

    /// <summary>
    /// Unblocks a single sender by SMTP address via <c>Remove-BlockedSenderAddress</c>. A single
    /// write, so retry-on-dead-session is safe (<c>allowRetry: true</c>): the EXO pool only
    /// replays when the pre-cmdlet check proves the cmdlet never ran.
    /// </summary>
    public Task<PermissionResult> UnblockSenderAsync(string senderAddress, string reason)
    {
        var address = NormalizeAddress(senderAddress);
        if (address is null)
            return Task.FromResult(PermissionResult.Fail("A sender address is required."));

        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Remove-BlockedSenderAddress")
              .AddParameter("SenderAddress", address)
              .AddParameter("ErrorAction", "Stop");
            if (!string.IsNullOrWhiteSpace(reason))
                ps.AddParameter("Reason", reason.Trim());

            Invoke(ps, tracker);
        }, () => ($"Unblocked sender {address}.", null), allowRetry: true);
    }

    /// <summary>
    /// Trims and validates an operator-supplied sender address. Pure and side-effect free so it is
    /// unit-testable without a live connection. Returns null for null/blank input.
    /// </summary>
    public static string? NormalizeAddress(string? senderAddress)
    {
        var trimmed = senderAddress?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
