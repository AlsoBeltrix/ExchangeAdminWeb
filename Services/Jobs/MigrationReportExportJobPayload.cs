namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The serialized payload for a bulk per-mailbox migration report export - what the operator
/// ticked, captured at submit time so a queued job is a real, inspectable record after the
/// browser closes.
/// </summary>
/// <remarks>
/// <para>
/// R31a of docs/MigrationInterfaceRedesign-Plan.md: <b>this cannot run on the circuit.</b> Each
/// report is a <c>Get-MigrationUserStatistics</c> that can take twenty minutes or more, so fifty
/// ticked mailboxes is potentially a whole day. A request handler cannot hold that, and an
/// operator watching a page that appears to do nothing will reload it.
/// </para>
/// <para>
/// The BATCH NAME is part of the payload and not an afterthought. A report is identified by batch
/// plus mailbox (R24c), and a batch removed and recreated under the same name is a different
/// migration - so the job must record which batch it was submitted against rather than resolving
/// "the open batch" later, when the operator has moved on.
/// </para>
/// </remarks>
public sealed class MigrationReportExportJobPayload
{
    public const string ModuleId = "Migration";
    public const string JobType = "Migration_ReportExport";

    /// <summary>The batch the ticked mailboxes belonged to when the job was submitted.</summary>
    public required string BatchName { get; init; }

    /// <summary>The ticked mailbox addresses, in the order the table listed them.</summary>
    public required List<string> EmailAddresses { get; init; }
}
