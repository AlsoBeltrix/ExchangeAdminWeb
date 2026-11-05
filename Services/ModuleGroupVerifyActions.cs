using System.Security.Claims;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;

namespace ExchangeAdminWeb.Services;

/// <summary>What one Module Configuration Verify action did: whether it landed, what the admin is told, and the write's outcome.</summary>
internal sealed record ModuleGroupVerifyActionResult(bool Succeeded, string Message, ModuleGroupVerifyWriteOutcome? Outcome = null);

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md C.2 (slice V3). The Fix, Remove and Ignore actions of the Verify results on Module
/// Configuration's Access tab (the module's groups and its Protection servicer groups, A3) and Module Admins tab (A4) - the same
/// shape as <see cref="ProtectedPrincipalVerifyActions"/>.
/// </summary>
/// <remarks>
/// <para><b>Global admin, fresh at every click (R3).</b> Both tabs are global-admin only, so every action - and the press, and the
/// staged Replace - asks the <c>AdminSettings</c> policy for the clicking admin's current principal before it does anything. A
/// module admin who is not a global admin is refused, nothing is written, and the refusal is audited.</para>
/// <para><b>The action must be one the finding offers</b> (<see cref="ModuleGroupVerifyAdapter.ActionsFor"/>): an Ignore of a grant
/// stored by name, or of a value that is not a usable SID, is refused here as well as never offered.</para>
/// <para><b>Direct primitives</b> - Rename (the display name, <see cref="SectionAccessRepository.SaveDisplayNames(IReadOnlyDictionary{string, string})"/>'s
/// write for that SID), Convert (A3 <see cref="SectionAccessRepository.ConvertToSids(IReadOnlyList{SidConversion})"/>'s write for that
/// alias and value; A4 <see cref="ConfigDbIdentifiers.ConvertModuleAdminGrant"/>), Remove (the grant and its ignores, atomically)
/// and Ignore - each check, under the write lock, that the pane's rows are still the version the item was read with, and refuse
/// otherwise. Remove and Ignore compare-and-swap the ignore list; a list changed since is read again and the write tried once
/// more (B.3). <b>Replace</b> goes through the pane's picker and its ordinary save (plan C.2), whose confirmation, compare under
/// the lock, audit rows and ignore cleanup fire as for any edit; here it is only authorized and checked as offered.</para>
/// <para><b>Audit, success and refusal separately</b>, under <c>Verify:SectionAccess</c> or <c>Verify:ModuleAdmins</c>, as
/// <see cref="ProtectedPrincipalVerifyActions"/> does.</para>
/// </remarks>
internal sealed class ModuleGroupVerifyActions
{
    /// <summary>The audit section of every Verify record of <paramref name="store"/>.</summary>
    public static string SectionOf(string store) => "Verify:" + store;

    private readonly SectionAccessService _sectionAccess;
    private readonly ModuleAdminService _moduleAdmins;
    private readonly IAuthorizationService _authorization;
    private readonly AuditService _audit;
    private readonly ILogger _logger;

    public ModuleGroupVerifyActions(
        SectionAccessService sectionAccess, ModuleAdminService moduleAdmins, IAuthorizationService authorization, AuditService audit, ILogger logger)
    {
        _sectionAccess = sectionAccess;
        _moduleAdmins = moduleAdmins;
        _authorization = authorization;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>The adapter that decides what each finding of <paramref name="store"/> offers (no directory needed to answer that).</summary>
    private static ModuleGroupVerifyAdapter AdapterOf(string store) => new(store);

    /// <summary>What a press of <paramref name="scope"/> reads, from one read transaction; null when the store cannot be read.</summary>
    public ModuleGroupVerifyWorkset? ReadForVerify(ModuleGroupVerifyScope scope)
    {
        var snapshot = scope.Store == VerifyIgnoreList.SectionAccessStore
            ? _sectionAccess.ReadForVerify(scope.Aliases)
            : _moduleAdmins.ReadForVerify(scope.ModuleId);
        return snapshot.Readable ? new ModuleGroupVerifyWorkset(scope, snapshot) : null;
    }

    /// <summary>A Verify press: re-authorized as a global admin. False - nothing recorded but the refusal - otherwise.</summary>
    public async Task<bool> AuthorizePressAsync(ClaimsPrincipal user, string performedBy, string ip, ModuleGroupVerifyScope scope) =>
        await RefuseUnlessAuthorizedAsync(user, performedBy, ip, scope.Store, "Verify") is null;

    /// <summary>Records a completed press: one success audit row with the counts, and the pane's last-verified line.</summary>
    public void RecordPress(string performedBy, string ip, ModuleGroupVerifyScope scope, VerifyReport report)
    {
        try
        {
            _audit.LogSettingsChange(performedBy, ip, SectionOf(scope.Store), [], [], new Dictionary<string, object?>
            {
                ["action"] = "Verify",
                ["module"] = scope.ModuleId,
                ["total"] = report.Total,
                ["verified"] = report.Verified,
                ["findings"] = report.Findings,
                ["ignored"] = report.Ignored,
                ["unreadable"] = report.Unreadable,
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Audit failed for a Verify press of {Store}", scope.Store); }

        try
        {
            _sectionAccess.WriteVerifyLast(scope.LastVerifiedKey, new VerifyLastEntry(
                report.At.UtcDateTime.ToString("O"), performedBy, report.Total, report.Findings, report.Ignored, report.Unreadable));
        }
        catch (Exception ex)
        {
            // Display-only: a press whose line could not be stored still verified.
            _logger.LogWarning(ex, "The last-verified line of {Key} could not be stored", scope.LastVerifiedKey);
        }
    }

    /// <summary>The last press of <paramref name="scope"/>, display-only; null when none is stored.</summary>
    public VerifyLastEntry? LastVerified(ModuleGroupVerifyScope scope) =>
        _sectionAccess.ReadVerifyLast().GetValueOrDefault(scope.LastVerifiedKey);

    /// <summary>
    /// Replace (plan C.2: the pane's picker and save path): authorized at the click and checked as offered; nothing is written
    /// here - the page takes the grant out of its list and the admin picks the replacement and saves, through the save's own
    /// checks, audit and ignore cleanup.
    /// </summary>
    public async Task<ModuleGroupVerifyActionResult> AuthorizeReplaceAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleGroupVerifyScope scope, VerifyItem item, VerifyFinding finding)
    {
        if (await RefuseUnlessReadyAsync(user, performedBy, ip, scope, item, finding, VerifyAction.Replace) is { } refusal)
            return refusal;
        return new ModuleGroupVerifyActionResult(true,
            "Pick the replacement in the search under this list and press Save. Until you save, nothing has changed.");
    }

    /// <summary>Rename (LabelDrift): the display name stored for the grant's SID becomes the group's current name. Cosmetic.</summary>
    public Task<ModuleGroupVerifyActionResult> RenameAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleGroupVerifyScope scope, VerifyItem item, VerifyFinding finding) =>
        RunAsync(user, performedBy, ip, scope, item, finding, VerifyAction.Rename, ignoresRaw: null, _ =>
            string.IsNullOrWhiteSpace(finding.Detail)
                ? ModuleGroupVerifyWriteOutcome.Refused(VerifyWriteStatus.RowChanged)
                : scope.Store == VerifyIgnoreList.SectionAccessStore
                    ? _sectionAccess.ApplyVerifyWrite(r => r.RenameDisplayName(
                        scope.Aliases, new GroupGrantRow(item.RowKey, item.StoredValue), item.Label, finding.Detail, item.SnapshotVersion))
                    : _moduleAdmins.ApplyVerifyWrite(r => r.RenameDisplayName(
                        scope.ModuleId, item.StoredValue, item.Label, finding.Detail, item.SnapshotVersion)),
            before: [$"{item.IdentifierOrText}: {item.Label}"], after: [$"{item.IdentifierOrText}: {finding.Detail}"]);

    /// <summary>Convert (NotConverted, convertible): the grant stored by name becomes the SID its store's resolver found.</summary>
    public Task<ModuleGroupVerifyActionResult> ConvertAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleGroupVerifyScope scope, VerifyItem item, VerifyFinding finding) =>
        RunAsync(user, performedBy, ip, scope, item, finding, VerifyAction.Convert, ignoresRaw: null, _ =>
            finding.Resolution is not { Kind: VerifyResolutionKind.Convertible, Identifier: { } sid } resolution
                ? ModuleGroupVerifyWriteOutcome.Refused(VerifyWriteStatus.RowChanged)
                : scope.Store == VerifyIgnoreList.SectionAccessStore
                    ? _sectionAccess.ApplyVerifyWrite(r => r.ConvertOne(
                        scope.Aliases, new SidConversion(item.RowKey, item.StoredValue, sid, resolution.Label), item.SnapshotVersion))
                    : _moduleAdmins.ApplyVerifyWrite(r => r.ConvertOne(
                        new ModuleAdminSidConversion(scope.ModuleId, item.StoredValue, sid), item.SnapshotVersion)));

    /// <summary>Remove: the grant and every ignore that names it, in one transaction.</summary>
    public Task<ModuleGroupVerifyActionResult> RemoveAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleGroupVerifyScope scope, VerifyItem item, VerifyFinding finding, string? ignoresRaw) =>
        RunAsync(user, performedBy, ip, scope, item, finding, VerifyAction.Remove, ignoresRaw, ignores =>
            scope.Store == VerifyIgnoreList.SectionAccessStore
                ? _sectionAccess.ApplyVerifyWrite(r => r.RemoveWithIgnores(
                    scope.Aliases, new GroupGrantRow(item.RowKey, item.StoredValue), item.SnapshotVersion, ignores))
                : _moduleAdmins.ApplyVerifyWrite(r => r.RemoveWithIgnores(scope.ModuleId, item.StoredValue, item.SnapshotVersion, ignores)));

    /// <summary>Ignore: records the finding after checking the grant under the write lock; changes no grant and no authorization.</summary>
    public Task<ModuleGroupVerifyActionResult> IgnoreAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleGroupVerifyScope scope, VerifyItem item, VerifyFinding finding, string? ignoresRaw)
    {
        var entry = new VerifyIgnoreEntry(item.Store, item.RowKey, item.IdentifierOrText, finding.Kind.ToString(), finding.Observed,
            performedBy, DateTime.UtcNow.ToString("O"));
        return RunAsync(user, performedBy, ip, scope, item, finding, VerifyAction.Ignore, ignoresRaw, ignores =>
            scope.Store == VerifyIgnoreList.SectionAccessStore
                ? _sectionAccess.ApplyVerifyWrite(r => r.AddVerifyIgnore(
                    scope.Aliases, new GroupGrantRow(item.RowKey, item.StoredValue), entry, item.SnapshotVersion, ignores))
                : _moduleAdmins.ApplyVerifyWrite(r => r.AddVerifyIgnore(scope.ModuleId, item.StoredValue, entry, item.SnapshotVersion, ignores)));
    }

    private async Task<string?> RefuseUnlessAuthorizedAsync(ClaimsPrincipal user, string performedBy, string ip, string store, string action)
    {
        var section = SectionOf(store);
        AuthorizationResult result;
        try
        {
            result = await _authorization.AuthorizeAsync(user, "AdminSettings");
        }
        catch (Exception ex)
        {
            return _audit.LogSettingsSaveException(_logger, performedBy, ip, section, ex,
                $"Not done: the authorization check for {action} failed ({ex.Message}). Nothing was changed.");
        }

        if (result.Succeeded)
            return null;

        const string message = "Only global admins can act on Verify results here. Nothing was changed.";
        _audit.LogSettingsSaveRefused(_logger, performedBy, ip, section, "Authorization", message);
        return message;
    }

    // Authorized as a global admin, an item of this pane, and an action its finding offers - or the refusal, audited.
    private async Task<ModuleGroupVerifyActionResult?> RefuseUnlessReadyAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleGroupVerifyScope scope, VerifyItem item, VerifyFinding finding, VerifyAction action)
    {
        if (await RefuseUnlessAuthorizedAsync(user, performedBy, ip, scope.Store, action.ToString()) is { } refusal)
            return new ModuleGroupVerifyActionResult(false, refusal);

        var inScope = string.Equals(item.Store, scope.Store, StringComparison.Ordinal)
            && (scope.Store == VerifyIgnoreList.SectionAccessStore
                ? scope.Aliases.Contains(item.RowKey, StringComparer.OrdinalIgnoreCase)
                : string.Equals(item.RowKey, scope.ModuleId, StringComparison.OrdinalIgnoreCase));
        if (!inScope || !AdapterOf(scope.Store).ActionsFor(item, finding).Contains(action))
        {
            var message = $"Not done: {action} is not offered for an entry that is {finding.Kind}. Nothing was changed.";
            _audit.LogSettingsSaveRefused(_logger, performedBy, ip, SectionOf(scope.Store), "NotOffered", message);
            return new ModuleGroupVerifyActionResult(false, message);
        }

        return null;
    }

    private async Task<ModuleGroupVerifyActionResult> RunAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleGroupVerifyScope scope, VerifyItem item, VerifyFinding finding,
        VerifyAction action, string? ignoresRaw, Func<string?, ModuleGroupVerifyWriteOutcome> write,
        string[]? before = null, string[]? after = null)
    {
        if (await RefuseUnlessReadyAsync(user, performedBy, ip, scope, item, finding, action) is { } refusal)
            return refusal;

        var section = SectionOf(scope.Store);
        ModuleGroupVerifyWriteOutcome outcome;
        try
        {
            outcome = await Task.Run(() => write(ignoresRaw));
            if (outcome.Status == VerifyWriteStatus.IgnoresChanged)
            {
                // B.3: the list changed since it was read beside the item. Read it again and try once more; the write re-checks
                // the rows' version and the grant itself.
                var fresh = ReadForVerify(scope)?.IgnoresRaw;
                outcome = await Task.Run(() => write(fresh));
            }
        }
        catch (Exception ex)
        {
            var failure = _audit.LogSettingsSaveException(_logger, performedBy, ip, section, ex, $"Not done: {ex.Message}");
            return new ModuleGroupVerifyActionResult(false, failure);
        }

        if (outcome.Status != VerifyWriteStatus.Written)
        {
            var message = outcome.Status switch
            {
                VerifyWriteStatus.StoreUnreadable => "Not done: the stored groups could not be read under the lock, or section access is not configured. Nothing was changed.",
                VerifyWriteStatus.IgnoresChanged => "Not done: the ignore list changed again while this was being written - by another administrator, or on the other instance. Nothing was changed. Run Verify again.",
                VerifyWriteStatus.IgnoresUnreadable => "Not done: the stored ignore list cannot be read, so nothing can be added to it. Nothing was changed.",
                _ => ProtectedPrincipalVerifyActions.ChangedSinceVerify,
            };
            _audit.LogSettingsSaveRefused(_logger, performedBy, ip, section, outcome.Status.ToString(), message);
            return new ModuleGroupVerifyActionResult(false, message, outcome);
        }

        try
        {
            var extra = new Dictionary<string, object?>
            {
                ["action"] = action.ToString(),
                ["finding"] = finding.Kind.ToString(),
                ["observed"] = finding.Observed,
                ["module"] = scope.ModuleId,
                ["row"] = $"{item.RowKey}:{item.StoredValue}",
            };
            if (action == VerifyAction.Ignore)
                _audit.LogSettingsChange(performedBy, ip, section, [], [$"Ignored {item.RowKey}:{item.IdentifierOrText} {finding.Kind} ({finding.Observed})"], extra);
            else
                _audit.LogSettingsChange(performedBy, ip, section, before ?? Rows(outcome.Before), after ?? Rows(outcome.After), extra);
        }
        catch (Exception ex) { _logger.LogError(ex, "Audit failed for a Verify {Action} of {Store}", action, scope.Store); }

        return new ModuleGroupVerifyActionResult(true, SuccessText(action, item, finding), outcome);
    }

    private static string[] Rows(IReadOnlyList<GroupGrantRow>? rows) => rows?.Select(r => $"{r.RowKey}:{r.Value}").ToArray() ?? [];

    private static string SuccessText(VerifyAction action, VerifyItem item, VerifyFinding finding)
    {
        var shown = item.Label.Length > 0 ? item.Label : item.StoredValue;
        return action switch
        {
            VerifyAction.Rename => $"Renamed '{item.Label}' to '{finding.Detail}'. The grant is unchanged.",
            VerifyAction.Convert => $"Converted '{item.StoredValue}' to {finding.Resolution?.Identifier}. It grants from now on.",
            VerifyAction.Remove => $"Removed '{shown}'. In effect now.",
            VerifyAction.Ignore => $"Ignored '{finding.Kind}' for '{shown}'. This changes only what Verify reports.",
            _ => "Done.",
        };
    }

    /// <summary>
    /// The report after one landed action, as <see cref="ProtectedPrincipalVerifyActions.Rebase"/>: the acted row leaves it unless
    /// it was ignored; every other row the write left stored exactly as read keeps its finding at the write's new version, with the
    /// display name the write left (a renamed SID's other rows whose new name is the current one become verified); any row the
    /// write did not leave leaves the report.
    /// </summary>
    internal static VerifyReport Rebase(VerifyReport report, VerifyItemResult acted, ModuleGroupVerifyWriteOutcome outcome)
    {
        if (outcome.Status != VerifyWriteStatus.Written || outcome.After is not { } after || outcome.AfterVersion is not { } version)
            return report;

        var stored = after.Select(r => (r.RowKey.ToUpperInvariant(), r.Value)).ToHashSet();
        var names = outcome.AfterDisplayNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        VerifyIgnoreList.TryParse(outcome.AfterIgnores, out var ignores);

        var results = new List<VerifyItemResult>(report.Results.Count);
        foreach (var r in report.Results)
        {
            if (!stored.Contains((r.Item.RowKey.ToUpperInvariant(), r.Item.StoredValue)))
                continue;

            var label = names.TryGetValue(r.Item.StoredValue, out var name) ? name : string.Empty;
            var item = r.Item with { SnapshotVersion = version, Label = label };
            if (ReferenceEquals(r, acted))
            {
                var entry = ignores.FirstOrDefault(e => VerifyIgnoreList.Matches(
                    e, item.Store, item.RowKey, item.IdentifierOrText, r.Finding.Kind.ToString(), r.Finding.Observed));
                if (entry is not null)
                    results.Add(new VerifyItemResult(item, VerifyFinding.Ignored(r.Finding, entry)));
                continue;
            }

            var finding = r.Finding.Kind == VerifyFindingKind.LabelDrift
                && string.Equals(label.Trim(), r.Finding.Detail, StringComparison.OrdinalIgnoreCase)
                    ? VerifyFinding.Verified()
                    : r.Finding;
            results.Add(new VerifyItemResult(item, finding));
        }

        return report with { Results = results };
    }
}
