using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

// docs/DirectoryObjectVerify-Plan.md B.1: the Verify engine. One service, store adapters, two directories. The engine owns
// batching, the forest check, the report and the finding taxonomy; each store's adapter owns how its rows become items, which
// lookup rule applies, the object classes it permits, and which actions each finding offers. Slice V1 ships the engine and the
// protected-principal adapter (A1/A2, ProtectedPrincipalVerifyAdapter); V3/V4 add adapters, not engine changes.

/// <summary>What kind of directory object a stored row names (plan B.1).</summary>
internal enum VerifyObjectKind
{
    User,
    Group,
    OrganizationalUnit,
    GroupTarget,
    SearchBase,
    RecipientGroup,
}

/// <summary>The finding taxonomy (plan B.1).</summary>
internal enum VerifyFindingKind
{
    /// <summary>The object exists, is the right class, and its name is the one stored.</summary>
    Verified,

    /// <summary>The directory answered and holds no such object.</summary>
    Missing,

    /// <summary>The object exists but its canonical name is no longer the one stored; the detail is the current name.</summary>
    LabelDrift,

    /// <summary>The row is stored by name only; the resolution says what it would convert to (plan A2).</summary>
    NotConverted,

    /// <summary>The row names more than one object; the detail lists them.</summary>
    Ambiguous,

    /// <summary>The object is of another class than the row's kind; the detail is its class.</summary>
    WrongClass,

    /// <summary>The directory could not be asked, or did not answer as one object; the detail says why. Never ignorable.</summary>
    Unreadable,

    /// <summary>An admin ignored the underlying finding (plan B.3); <see cref="VerifyFinding.Underlying"/> is it.</summary>
    Ignored,
}

/// <summary>What a not-converted row resolves to (plan B.2, A2): exactly one object, none, several, or the lookup failed.</summary>
internal enum VerifyResolutionKind
{
    Convertible,
    None,
    Several,
    Unreadable,
}

/// <summary>A not-converted row's resolution: its kind, why, and - convertible - the identifier kind and identifier.</summary>
/// <remarks>
/// <see cref="Label"/> is the name a conversion stores beside the identifier where the store keeps one - a section-access
/// group's display name (slice V3); null where the store keeps none.
/// </remarks>
internal sealed record VerifyResolution(
    VerifyResolutionKind Kind, string Detail, string? IdKind = null, string? Identifier = null, string? Label = null);

/// <summary>
/// One stored row to verify (plan B.1): the store it lives in, its row key (the kind within the store), the object kind,
/// its identifier (SID or objectGUID; null when it has none or it cannot be read), its stored label, its value exactly as
/// stored, and the version of the store's snapshot it was read from - which every action's write checks.
/// </summary>
internal sealed record VerifyItem(
    string Store, string RowKey, VerifyObjectKind Kind, string? Identifier, string Label, string StoredValue, string SnapshotVersion)
{
    /// <summary>The identifier, or the stored text when there is none: what an ignore entry names.</summary>
    public string IdentifierOrText => Identifier ?? StoredValue.Trim();
}

/// <summary>One finding about one item. <see cref="Detail"/> is the current name, the class, the matches or the reason.</summary>
internal sealed record VerifyFinding(
    VerifyFindingKind Kind, string? Detail = null, VerifyResolution? Resolution = null, VerifyFinding? Underlying = null,
    VerifyIgnoreEntry? IgnoredBy = null)
{
    public static VerifyFinding Verified() => new(VerifyFindingKind.Verified);
    public static VerifyFinding Missing() => new(VerifyFindingKind.Missing);
    public static VerifyFinding LabelDrift(string current) => new(VerifyFindingKind.LabelDrift, current);
    public static VerifyFinding NotConverted(VerifyResolution resolution) => new(VerifyFindingKind.NotConverted, resolution.Detail, resolution);
    public static VerifyFinding Ambiguous(string matches) => new(VerifyFindingKind.Ambiguous, matches);
    public static VerifyFinding WrongClass(string actual) => new(VerifyFindingKind.WrongClass, actual);
    public static VerifyFinding Unreadable(string reason) => new(VerifyFindingKind.Unreadable, reason);
    public static VerifyFinding Ignored(VerifyFinding underlying, VerifyIgnoreEntry entry) =>
        new(VerifyFindingKind.Ignored, underlying.Detail, underlying.Resolution, underlying, entry);

    /// <summary>What was observed, as an ignore entry records it: "not found" for a missing object, else the detail.</summary>
    public string Observed => Kind == VerifyFindingKind.Missing ? VerifyIgnoreList.NotFoundObserved : Detail ?? string.Empty;
}

/// <summary>The actions a finding offers (plan C.2); which primitive each uses is the store's.</summary>
internal enum VerifyAction
{
    Rename,
    Replace,
    Convert,
    Remove,
    Ignore,
}

/// <summary>One directory object as Verify reads it: the attributes plan B.1 lists.</summary>
internal sealed record VerifyDirectoryObject(
    string DistinguishedName, string? ObjectSid, string? ObjectGuid, string? ObjectClass,
    string? UserPrincipalName, string? Mail, string? SamAccountName);

/// <summary>How an item is looked up (plan B.1).</summary>
internal enum VerifyLookupKind
{
    /// <summary>By objectSid at the global catalog, batched.</summary>
    Sid,

    /// <summary>By objectGUID at the global catalog, batched.</summary>
    Guid,

    /// <summary>By a base read of the DN in its own domain, batched per domain.</summary>
    Dn,

    /// <summary>By the adapter's own rule, in the press's one directory session (<see cref="IVerifyStoreAdapter.ResolveInDirectory"/>).</summary>
    InDirectory,

    /// <summary>By the adapter's own rule outside the directory session - another service, such as Exchange.</summary>
    OutsideDirectory,

    /// <summary>
    /// By the adapter's own resolver outside the directory session, answered as findings rather than objects - a rule whose
    /// answer is a resolution (one group, none, several, or no answer), such as the section-access SID migration's lookup
    /// (<see cref="IVerifyStoreAdapter.ResolveOutsideDirectoryAsync"/>; docs/DirectoryObjectVerify-Plan.md B.2, slice V3).
    /// </summary>
    ResolveOutsideDirectory,

    /// <summary>Not looked up: <see cref="VerifyLookup.Problem"/> says why, and the item is unreadable.</summary>
    None,
}

/// <summary>
/// An item's lookup: its kind, its key (the SID, GUID or DN), and the stored DN the forest check applies to - a DN whose
/// domain is not in the host's forest is unreadable, never missing (F6).
/// </summary>
internal sealed record VerifyLookup(VerifyLookupKind Kind, string? Key = null, string? StoredDn = null, string? Problem = null)
{
    public static VerifyLookup NotLookedUp(string problem) => new(VerifyLookupKind.None, Problem: problem);
}

/// <summary>A typed lookup answer (plan B.1, R4): found (its objects; none is a confirmed absence) or unreadable.</summary>
internal abstract record VerifyLookupResult
{
    private VerifyLookupResult() { }

    /// <summary>The directory answered. An empty list is a CONFIRMED absence - the only way to reach Missing.</summary>
    public sealed record Found(IReadOnlyList<VerifyDirectoryObject> Objects) : VerifyLookupResult;

    /// <summary>The lookup did not complete; nothing about the object is known.</summary>
    public sealed record Unreadable(string Reason) : VerifyLookupResult;

    public static VerifyLookupResult NotFound { get; } = new Found([]);
}

/// <summary>
/// The reads a Verify press makes in its one directory session (plan B.1): the forest check, identifier lookups at the global
/// catalog, base reads by DN, and - through <see cref="IProtectedPrincipalResolveDirectory"/> - the conversion resolver's
/// lookups for rows stored by name. Every member returns the directory's answer or throws; none reports "no rows" for a read
/// that did not complete.
/// </summary>
internal interface IVerifyDirectory : IProtectedPrincipalResolveDirectory
{
    /// <summary>The DNS name of the forest's domain <paramref name="dnsDomain"/>, or null when it is not a domain of this forest.</summary>
    string? ForestDomainServer(string dnsDomain);

    /// <summary>Every object at the global catalog carrying one of <paramref name="sids"/> (at most one batch).</summary>
    IReadOnlyList<VerifyDirectoryObject> FindBySids(IReadOnlyList<string> sids);

    /// <summary>Every object at the global catalog carrying one of <paramref name="guids"/> (at most one batch).</summary>
    IReadOnlyList<VerifyDirectoryObject> FindByGuids(IReadOnlyList<string> guids);

    /// <summary>The objects at <paramref name="dns"/>, read on <paramref name="server"/> (a domain controller of their domain).</summary>
    IReadOnlyList<VerifyDirectoryObject> ReadByDns(IReadOnlyList<string> dns, string server);

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.2, A6 (slice V4): whether <c>Get-ADOrganizationalUnit -Identity</c> finds an
    /// organizational unit at <paramref name="dn"/> on <paramref name="server"/> - the Emergency Disable module's own
    /// precondition. False is a confirmed absence: the AD module's not-found, which it also answers for a container that is not
    /// an OU. Throws on any other failure. A directory that has no such read throws.
    /// </summary>
    bool OrganizationalUnitExists(string dn, string server) =>
        throw new NotSupportedException("this directory cannot read organizational units");

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.2, A5 (slice V4): the forest's domains as the Migration module asks for them
    /// (<see cref="MigrationService.ForestDomains"/>). Throws when they cannot be read.
    /// </summary>
    IReadOnlyList<string> ForestDomainNames() =>
        throw new NotSupportedException("this directory cannot list the forest's domains");

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.2, A5 (slice V4): one excluded-group value through the Migration module's own
    /// per-domain lookup (<see cref="MigrationService.TryResolveExcludedGroup"/>), in each of <paramref name="domains"/>.
    /// </summary>
    MigrationService.ExcludedGroupResolution ResolveExcludedGroup(string configured, IReadOnlyList<string> domains) =>
        throw new NotSupportedException("this directory cannot resolve excluded groups");
}

/// <summary>A press's directory session, or why it could not be had.</summary>
internal sealed record VerifyDirectoryRun<T>(bool Completed, T? Result, string? Unavailable)
{
    public static VerifyDirectoryRun<T> Ran(T result) => new(true, result, null);
    public static VerifyDirectoryRun<T> NotRun(string reason) => new(false, default, reason);
}

/// <summary>
/// Where a press's directory reads run: one session with the directory-read credential, fetched once
/// (<see cref="ProtectedPrincipalService.RunVerifyReadsAsync{T}"/> in production; a stand-in forest in the tests).
/// </summary>
internal interface IVerifyDirectoryAccess
{
    Task<VerifyDirectoryRun<T>> RunAsync<T>(Func<IVerifyDirectory, T> reads, CancellationToken cancellationToken);
}

/// <summary>
/// One store's Verify rules (plan B.1): one adapter per store - A1/A2 share one, A3/A4 one, A5-A8 one each. The engine calls
/// <see cref="LookupFor"/> for every item, runs the engine-owned lookups (SID, GUID, DN) itself, and asks the adapter to
/// <see cref="Compare"/> only a completed answer - an unreadable lookup never reaches an adapter, so no adapter can turn it
/// into Verified or Missing. <see cref="ResolveInDirectory"/> and <see cref="LookUpOutsideDirectoryAsync"/> are for stores
/// whose lookup is their own module's rule; their defaults answer unreadable.
/// </summary>
internal interface IVerifyStoreAdapter
{
    /// <summary>The store this adapter verifies (<see cref="VerifyItem.Store"/>).</summary>
    string Store { get; }

    /// <summary>How <paramref name="item"/> is looked up.</summary>
    VerifyLookup LookupFor(VerifyItem item);

    /// <summary>The finding for a COMPLETED engine lookup (<see cref="VerifyLookupResult.Found"/>, none being a confirmed absence).</summary>
    VerifyFinding Compare(VerifyItem item, VerifyLookupResult.Found result);

    /// <summary>Findings for <paramref name="items"/> (all <see cref="VerifyLookupKind.InDirectory"/>), one per item in order, read in the press's session.</summary>
    IReadOnlyList<VerifyFinding> ResolveInDirectory(IReadOnlyList<VerifyItem> items, IVerifyDirectory directory, CancellationToken cancellationToken) =>
        items.Select(_ => VerifyFinding.Unreadable("this store has no lookup in the directory session")).ToList();

    /// <summary>Lookup answers for <paramref name="items"/> (all <see cref="VerifyLookupKind.OutsideDirectory"/>), one per item in order.</summary>
    Task<IReadOnlyList<VerifyLookupResult>> LookUpOutsideDirectoryAsync(IReadOnlyList<VerifyItem> items, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VerifyLookupResult>>(
            items.Select(_ => (VerifyLookupResult)new VerifyLookupResult.Unreadable("this store has no lookup outside the directory")).ToList());

    /// <summary>
    /// Findings for <paramref name="items"/> (all <see cref="VerifyLookupKind.ResolveOutsideDirectory"/>), one per item in order,
    /// from the store's own resolver outside the directory session. A resolver that could not answer for an item must say so
    /// (unreadable, or not converted with an unreadable resolution) - never verified or missing.
    /// </summary>
    Task<IReadOnlyList<VerifyFinding>> ResolveOutsideDirectoryAsync(IReadOnlyList<VerifyItem> items, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VerifyFinding>>(
            items.Select(_ => VerifyFinding.Unreadable("this store has no resolver outside the directory")).ToList());

    /// <summary>Whether <paramref name="finding"/> on <paramref name="item"/> may be ignored. Never asked for NotConverted or Unreadable.</summary>
    bool CanIgnore(VerifyItem item, VerifyFinding finding);

    /// <summary>The actions <paramref name="finding"/> on <paramref name="item"/> offers.</summary>
    IReadOnlyList<VerifyAction> ActionsFor(VerifyItem item, VerifyFinding finding);
}

/// <summary>One item and its finding.</summary>
internal sealed record VerifyItemResult(VerifyItem Item, VerifyFinding Finding);

/// <summary>
/// What one press found (plan C.1): a finding per item, and the ignores of the verified stores that matched nothing - stale,
/// because what they ignored is no longer what Verify observes.
/// </summary>
internal sealed record VerifyReport(DateTimeOffset At, IReadOnlyList<VerifyItemResult> Results, IReadOnlyList<VerifyIgnoreEntry> StaleIgnores)
{
    public int Total => Results.Count;
    public int Verified => Results.Count(r => r.Finding.Kind == VerifyFindingKind.Verified);
    public int Ignored => Results.Count(r => r.Finding.Kind == VerifyFindingKind.Ignored);
    public int Unreadable => Results.Count(r => r.Finding.Kind == VerifyFindingKind.Unreadable);
    public int Findings => Total - Verified - Ignored - Unreadable;

    /// <summary>"Verified N entries at &lt;time&gt;: M findings, K ignored, J could not be verified."</summary>
    public string Summary(string at) =>
        $"Verified {Total} {(Total == 1 ? "entry" : "entries")} at {at}: {Findings} {(Findings == 1 ? "finding" : "findings")}, "
        + $"{Ignored} ignored, {Unreadable} could not be verified.";
}

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md B.1. Verifies stored directory objects: every item looked up, in one directory session
/// per press, and given exactly one finding.
/// </summary>
/// <remarks>
/// <para><b>One operation.</b> A press is one <see cref="DirectoryReadOperation.BeginCheck"/> - one credential fetch and one
/// timing line - and every Active Directory read runs in one session lent by the protection service's warm pool, as several
/// batched reads: identifier lookups at the global catalog in chunks of <see cref="BatchSize"/>, DN reads in each DN's own
/// domain. Nothing walks a domain (decision 2026-10-30).</para>
/// <para><b>Forest scope (F6).</b> Every stored DN's domain is checked against the host's own forest's crossRefs - discovered
/// at run time, never named (Invariant 7). A DN of a domain the forest does not have is unreadable ("domain is not in this
/// forest"), never missing: the global catalog answers only for this forest, so its silence there proves nothing.</para>
/// <para><b>Unreadable is never Verified or Missing.</b> Missing comes only from a lookup that completed and returned no
/// object; a read that throws, a session that cannot be had, or a domain that cannot be placed makes every item it covers
/// unreadable (Known Failure Class 3). An adapter is never shown an unreadable answer.</para>
/// <para><b>Ignores.</b> An ignore entry turns a matching finding into <see cref="VerifyFindingKind.Ignored"/>, carrying it;
/// a NotConverted or Unreadable finding is never ignored, whatever the list holds (plan B.4).</para>
/// </remarks>
internal sealed class DirectoryObjectVerifier
{
    /// <summary>The press's name in the timing line.</summary>
    internal const string CheckName = "Directory object verify";

    /// <summary>Identifiers per global-catalog read (plan B.1).</summary>
    internal const int BatchSize = 50;

    /// <summary>Why an item whose DN names a domain outside the host's forest is unreadable.</summary>
    internal const string NotInForest = "domain is not in this forest";

    private readonly IVerifyDirectoryAccess _access;
    private readonly Dictionary<string, IVerifyStoreAdapter> _adapters;
    private readonly ILogger _logger;

    public DirectoryObjectVerifier(IVerifyDirectoryAccess access, IEnumerable<IVerifyStoreAdapter> adapters, ILogger<DirectoryObjectVerifier> logger)
    {
        _access = access;
        _adapters = adapters.ToDictionary(a => a.Store, StringComparer.Ordinal);
        _logger = logger;
    }

    /// <summary>The adapter of <paramref name="store"/>.</summary>
    public IVerifyStoreAdapter AdapterFor(string store) =>
        _adapters.TryGetValue(store, out var adapter) ? adapter : throw new InvalidOperationException($"No Verify adapter for store '{store}'.");

    /// <summary>Verifies <paramref name="items"/> with no ignore list.</summary>
    public Task<VerifyReport> VerifyAsync(IReadOnlyList<VerifyItem> items, CancellationToken cancellationToken) =>
        VerifyAsync(items, [], cancellationToken);

    /// <summary>Verifies <paramref name="items"/>, applying <paramref name="ignores"/>.</summary>
    public async Task<VerifyReport> VerifyAsync(
        IReadOnlyList<VerifyItem> items, IReadOnlyList<VerifyIgnoreEntry> ignores, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ignores);

        using var operation = DirectoryReadOperation.BeginCheck(_logger, CheckName, $"{items.Count} stored entries");

        var planned = items.Select((item, index) =>
        {
            var adapter = AdapterFor(item.Store);
            return new Planned(index, item, adapter, adapter.LookupFor(item));
        }).ToList();

        var findings = new VerifyFinding?[items.Count];
        foreach (var p in planned.Where(p => p.Lookup.Kind == VerifyLookupKind.None))
            findings[p.Index] = VerifyFinding.Unreadable(p.Lookup.Problem ?? "the stored entry cannot be looked up");

        var inSession = planned.Where(p => p.Lookup.Kind is not
            (VerifyLookupKind.None or VerifyLookupKind.OutsideDirectory or VerifyLookupKind.ResolveOutsideDirectory)).ToList();
        if (inSession.Count > 0)
        {
            var run = await _access.RunAsync(directory => ReadInSession(inSession, directory, cancellationToken), cancellationToken);
            foreach (var p in inSession)
            {
                findings[p.Index] = run.Completed && run.Result is { } read && read.TryGetValue(p.Index, out var finding)
                    ? finding
                    : VerifyFinding.Unreadable(run.Unavailable ?? "the directory did not answer for this entry");
            }
        }

        foreach (var group in planned.Where(p => p.Lookup.Kind == VerifyLookupKind.OutsideDirectory).GroupBy(p => p.Adapter))
        {
            var batch = group.ToList();
            IReadOnlyList<VerifyLookupResult> answers;
            try
            {
                answers = await group.Key.LookUpOutsideDirectoryAsync(batch.Select(p => p.Item).ToList(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "A Verify lookup outside the directory failed for store {Store}", group.Key.Store);
                answers = batch.Select(_ => (VerifyLookupResult)new VerifyLookupResult.Unreadable($"the lookup failed: {ex.Message}")).ToList();
            }

            for (var i = 0; i < batch.Count; i++)
            {
                findings[batch[i].Index] = i < answers.Count && answers.Count == batch.Count
                    ? Finish(batch[i], answers[i])
                    : VerifyFinding.Unreadable("the lookup did not answer for this entry");
            }
        }

        foreach (var group in planned.Where(p => p.Lookup.Kind == VerifyLookupKind.ResolveOutsideDirectory).GroupBy(p => p.Adapter))
        {
            var batch = group.ToList();
            IReadOnlyList<VerifyFinding> resolved;
            try
            {
                resolved = await group.Key.ResolveOutsideDirectoryAsync(batch.Select(p => p.Item).ToList(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "A Verify resolution outside the directory failed for store {Store}", group.Key.Store);
                resolved = [];
            }

            for (var i = 0; i < batch.Count; i++)
                findings[batch[i].Index] = resolved.Count == batch.Count ? resolved[i] : VerifyFinding.Unreadable("the lookup did not answer for this entry");
        }

        var results = new List<VerifyItemResult>(items.Count);
        var used = new HashSet<VerifyIgnoreEntry>(ReferenceEqualityComparer.Instance);
        foreach (var p in planned)
        {
            var finding = findings[p.Index] ?? VerifyFinding.Unreadable("not looked up");
            if ((finding.Kind is VerifyFindingKind.Missing or VerifyFindingKind.LabelDrift or VerifyFindingKind.WrongClass or VerifyFindingKind.Ambiguous)
                && p.Adapter.CanIgnore(p.Item, finding))
            {
                var entry = ignores.FirstOrDefault(e => VerifyIgnoreList.Matches(
                    e, p.Item.Store, p.Item.RowKey, p.Item.IdentifierOrText, finding.Kind.ToString(), finding.Observed));
                if (entry is not null)
                {
                    used.Add(entry);
                    finding = VerifyFinding.Ignored(finding, entry);
                }
            }
            results.Add(new VerifyItemResult(p.Item, finding));
        }

        var stores = planned.Select(p => p.Item.Store).ToHashSet(StringComparer.Ordinal);
        var stale = ignores.Where(e => stores.Contains(e.Store) && !used.Contains(e)).ToList();
        var report = new VerifyReport(DateTimeOffset.UtcNow, results, stale);

        _logger.LogInformation(
            "Verify of {Total} stored entries: {Verified} verified, {Findings} findings, {Ignored} ignored, {Unreadable} could not be verified",
            report.Total, report.Verified, report.Findings, report.Ignored, report.Unreadable);
        return report;
    }

    private sealed record Planned(int Index, VerifyItem Item, IVerifyStoreAdapter Adapter, VerifyLookup Lookup)
    {
        public string? Server { get; set; }
    }

    // Every read the press makes in its session. A read that throws makes the items it covers unreadable and the others go on.
    private Dictionary<int, VerifyFinding> ReadInSession(IReadOnlyList<Planned> planned, IVerifyDirectory directory, CancellationToken cancellationToken)
    {
        var findings = new Dictionary<int, VerifyFinding>();

        // The forest check, on every stored DN, once per domain (F6).
        var domains = new Dictionary<string, (string? Server, string? Problem)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in planned.Where(p => !string.IsNullOrWhiteSpace(p.Lookup.StoredDn)))
        {
            var domain = ADDirectorySearchService.DnsDomainFromDn(p.Lookup.StoredDn);
            if (domain is null)
            {
                // A DN that names no domain cannot be placed; a DN read needs one. An identifier lookup does not.
                if (p.Lookup.Kind == VerifyLookupKind.Dn)
                    findings[p.Index] = VerifyFinding.Unreadable(ProtectedGroupMembership.DnNamesNoDomain);
                continue;
            }

            if (!domains.TryGetValue(domain, out var answer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    answer = (directory.ForestDomainServer(domain), null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    answer = (null, $"the forest could not be asked about domain '{domain}': {ex.Message}");
                }
                domains[domain] = answer;
            }

            if (answer.Problem is not null)
                findings[p.Index] = VerifyFinding.Unreadable(answer.Problem);
            else if (answer.Server is null)
                findings[p.Index] = VerifyFinding.Unreadable(NotInForest);
            else
                p.Server = answer.Server;
        }

        var open = planned.Where(p => !findings.ContainsKey(p.Index)).ToList();

        LookUpBatched(open.Where(p => p.Lookup.Kind == VerifyLookupKind.Sid).ToList(), directory.FindBySids,
            o => ProtectedPrincipalIdEntry.CanonicalSid(o.ObjectSid), findings, cancellationToken);
        LookUpBatched(open.Where(p => p.Lookup.Kind == VerifyLookupKind.Guid).ToList(), directory.FindByGuids,
            o => ProtectedPrincipalIdEntry.CanonicalGuid(o.ObjectGuid), findings, cancellationToken);

        foreach (var byServer in open.Where(p => p.Lookup.Kind == VerifyLookupKind.Dn).GroupBy(p => p.Server!, StringComparer.OrdinalIgnoreCase))
        {
            var server = byServer.Key;
            LookUpBatched(byServer.ToList(), dns => directory.ReadByDns(dns, server), o => o.DistinguishedName, findings, cancellationToken);
        }

        foreach (var group in open.Where(p => p.Lookup.Kind == VerifyLookupKind.InDirectory).GroupBy(p => p.Adapter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = group.ToList();
            IReadOnlyList<VerifyFinding> resolved;
            try
            {
                resolved = group.Key.ResolveInDirectory(batch.Select(p => p.Item).ToList(), directory, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resolved = [];
                _logger.LogWarning(ex, "A Verify lookup in the directory failed for store {Store}", group.Key.Store);
            }

            for (var i = 0; i < batch.Count; i++)
                findings[batch[i].Index] = resolved.Count == batch.Count ? resolved[i] : VerifyFinding.Unreadable("the lookup did not answer for this entry");
        }

        return findings;
    }

    // Keys looked up in chunks of BatchSize, each key's answer the objects carrying it. A chunk whose read throws is
    // unreadable for every key in it - never a confirmed absence.
    private void LookUpBatched(
        IReadOnlyList<Planned> planned, Func<IReadOnlyList<string>, IReadOnlyList<VerifyDirectoryObject>> read,
        Func<VerifyDirectoryObject, string?> keyOf, Dictionary<int, VerifyFinding> findings, CancellationToken cancellationToken)
    {
        if (planned.Count == 0)
            return;

        var keys = planned.Select(p => p.Lookup.Key!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var answers = new Dictionary<string, VerifyLookupResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in keys.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var found = read(batch);
                foreach (var key in batch)
                    answers[key] = new VerifyLookupResult.Found(
                        found.Where(o => string.Equals(keyOf(o), key, StringComparison.OrdinalIgnoreCase)).ToList());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "A batched Verify read of {Count} entries failed", batch.Length);
                foreach (var key in batch)
                    answers[key] = new VerifyLookupResult.Unreadable($"the directory read failed: {ex.Message}");
            }
        }

        foreach (var p in planned)
            findings[p.Index] = Finish(p, answers[p.Lookup.Key!]);
    }

    private static VerifyFinding Finish(Planned p, VerifyLookupResult answer) => answer switch
    {
        VerifyLookupResult.Found found => p.Adapter.Compare(p.Item, found),
        VerifyLookupResult.Unreadable unreadable => VerifyFinding.Unreadable(unreadable.Reason),
        _ => VerifyFinding.Unreadable("the lookup gave no answer"),
    };
}

/// <summary>The LDAP filters Verify's batched reads use. Pure, so their shape is testable.</summary>
internal static class VerifyFilters
{
    /// <summary>Any of <paramref name="sids"/>: <c>(|(objectSid=S-1-...)...)</c>.</summary>
    public static string AnySid(IEnumerable<string> sids) => "(|" + string.Concat(sids.Select(ADDirectorySearchService.BuildSidFilter)) + ")";

    /// <summary>
    /// Any of <paramref name="guids"/>: <c>(|(objectGUID=\xx\xx...)...)</c> - objectGUID is an octet string, matched by its bytes
    /// in the order the directory stores them (<see cref="Guid.ToByteArray"/>), each escaped.
    /// </summary>
    public static string AnyGuid(IEnumerable<string> guids) =>
        "(|" + string.Concat(guids.Select(g =>
            "(objectGUID=" + string.Concat(Guid.Parse(g).ToByteArray().Select(b => "\\" + b.ToString("x2"))) + ")")) + ")";

    /// <summary>Any of <paramref name="dns"/>, by distinguishedName.</summary>
    public static string AnyDn(IEnumerable<string> dns) => ProtectedGroupMembership.AnyDnFilter(dns);
}

/// <summary>
/// The press's session directory over <see cref="PowerShellProtectedGroupDirectory"/>: Verify's batched reads and the
/// resolver's, each recorded as having thrown when it does, so the session a failed read may have broken is failed and not
/// lent again (<see cref="ProtectedPrincipalService.RunVerifyReadsAsync{T}"/>).
/// </summary>
internal sealed class VerifySessionDirectory(PowerShellProtectedGroupDirectory inner) : IVerifyDirectory
{
    internal PowerShellProtectedGroupDirectory Inner { get; } = inner;

    /// <summary>Whether any read threw.</summary>
    internal bool Faulted { get; private set; }

    private T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch
        {
            Faulted = true;
            throw;
        }
    }

    public string? ForestDomainServer(string dnsDomain) => Read(() => Inner.ForestDomainServer(dnsDomain));

    public IReadOnlyList<VerifyDirectoryObject> FindBySids(IReadOnlyList<string> sids) =>
        Read(() => Inner.FindVerifyObjects(VerifyFilters.AnySid(sids), null));

    public IReadOnlyList<VerifyDirectoryObject> FindByGuids(IReadOnlyList<string> guids) =>
        Read(() => Inner.FindVerifyObjects(VerifyFilters.AnyGuid(guids), null));

    public IReadOnlyList<VerifyDirectoryObject> ReadByDns(IReadOnlyList<string> dns, string server) =>
        Read(() => Inner.FindVerifyObjects(VerifyFilters.AnyDn(dns), server));

    public bool OrganizationalUnitExists(string dn, string server) => Read(() => Inner.OrganizationalUnitExists(dn, server));

    public IReadOnlyList<string> ForestDomainNames() => Read(() => Inner.ForestDomainNames());

    // A lookup the Migration module would have thrown on - a domain that could not be asked - breaks the session as a throw
    // would: it is failed and not lent again, as the module's own check fails it (MigrationService.CheckExcludedGroups).
    public MigrationService.ExcludedGroupResolution ResolveExcludedGroup(string configured, IReadOnlyList<string> domains)
    {
        var answer = Read(() => Inner.ResolveExcludedGroup(configured, domains));
        if (answer is MigrationService.ExcludedGroupResolution.Unreadable)
            Faulted = true;
        return answer;
    }

    public string ResolveDomainServer(string domain) => Read(() => Inner.ResolveDomainServer(domain));

    public IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server) =>
        Read(() => Inner.FindGroups(key, value, server));

    public IReadOnlyList<DirectoryObjectGuid> ReadObjectGuids(IReadOnlyList<string> dns, string server) =>
        Read(() => Inner.ReadObjectGuids(dns, server));

    public IReadOnlyList<DirectoryUserObject> FindUsers(string ldapFilter, string? server) =>
        Read(() => Inner.FindUsers(ldapFilter, server));
}
