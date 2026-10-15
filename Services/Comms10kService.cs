using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

public class Comms10kService
{
    // Owner-approved single-target/environment-neutrality exception, 2026-10-15.
    internal static readonly Guid TargetGroupGuid = new("87663f48-10a7-44b3-8da3-0a7e4cc3de0b");
    private const string TargetGroupDisplayName = "Comms-10k";

    private readonly ILogger<Comms10kService> _logger;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly IConfiguration _config;

    /// <summary>Short pause between retry attempts of one directory write.</summary>
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(2);

    public Comms10kService(ILogger<Comms10kService> logger, ModuleConfigService moduleConfig, ModuleCredentialService moduleCredentials, IConfiguration config)
    {
        _logger = logger;
        _moduleConfig = moduleConfig;
        _moduleCredentials = moduleCredentials;
        _config = config;
    }

    private bool HasCredentialSecret => int.TryParse(_moduleConfig.GetValue("Comms10k", "DelineaSecretId"), out var id) && id > 0;

    public bool IsConfigured => !_moduleConfig.IsModuleCorrupt("Comms10k") && HasCredentialSecret;

    /// <summary>
    /// Lists the target group's membership: <paramref name="limit"/> rows for the page's preview,
    /// or all of them for the CSV export. <c>TotalCount</c> is always the FULL membership.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both of this method's directory calls used to be the wrong ones, and Preview and
    /// Download CSV simply threw on the real group.</b> It called <c>Get-ADGroupMember</c>, which
    /// expands every member into a full object and is bound by the ADWS
    /// <c>MaxGroupOrMemberEntries</c> cap (default 5,000) - so it raised past that size, which is
    /// exactly the size this module exists to manage. <see cref="ExecuteReplaceAsync"/> had
    /// already been fixed to read the raw <c>member</c> attribute for the same reason; this half
    /// never got the same treatment. Then it issued one <c>Get-ADUser</c> per member on top.
    /// </para>
    /// <para>
    /// <b>The primary-group union is not an optimisation, it is a correctness fix that comes WITH
    /// the switch.</b> The linked <c>member</c> attribute does not carry members whose membership
    /// comes from their primary group; <c>Get-ADGroupMember</c> did. Swapping one for the other
    /// without the union would silently shorten the list. This repository has already fixed the
    /// identical defect once, at <c>GroupManagementService.cs:414-448</c> (lst-2). Listing only -
    /// the write still targets the flat DN list.
    /// </para>
    /// <para>
    /// Fails closed: an unreadable SID or a failed primary-group query throws rather than
    /// returning a shorter list, because a quietly shorter membership is the whole finding.
    /// </para>
    /// </remarks>
    public async Task<Comms10kMemberList> GetMembersAsync(int? limit = null)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Comms-10k AD credentials are not configured in Admin Settings.");

        var creds = await _moduleCredentials.GetCredentialsAsync("Comms10k", "Comms-10k AD membership lookup");
        if (creds is null)
            throw new InvalidOperationException("Cannot connect to AD: credentials unavailable.");

        return await Task.Run(() =>
        {
            var dns = QueryMemberDns(creds.Value);

            var result = new Comms10kMemberList { GroupName = TargetGroupDisplayName, TotalCount = dns.Count };

            // The limit is applied to the DN LIST, before any detail is fetched - that is the
            // point of doing it here. Applying it after resolution would resolve ten thousand
            // members to show fifty.
            var toResolve = limit.HasValue ? dns.Take(limit.Value).ToList() : dns;

            result.Members.AddRange(
                Comms10kMemberResolver.BuildRows(toResolve, QueryMemberDetails(creds.Value, toResolve)));

            return result;
        });
    }

    /// <summary>
    /// Every member DN of the group: the raw <c>member</c> attribute, unioned with the members
    /// who hold it as their primary group. Internal virtual TEST SEAM.
    /// </summary>
    internal virtual IReadOnlyList<string> QueryMemberDns(
        (string username, string password, string domain) creds)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        InvokeAndClear(ps);

        var credential = CreateCredential(creds.username, creds.password, creds.domain);

        return ReadMemberDns(ps, credential);
    }

    // Kept separate from runspace creation so the actual command wiring can run locally in tests.
    internal static IReadOnlyList<string> ReadMemberDns(PowerShell ps, PSCredential credential)
    {
        // Get-ADGroup -Properties member, NOT Get-ADGroupMember: the linked attribute comes back
        // by range retrieval and is not subject to the ADWS MaxGroupOrMemberEntries cap, which is
        // what made this page throw on the real group.
        ps.Streams.Error.Clear();
        ps.AddCommand("Get-ADGroup")
          .AddParameter("Identity", TargetGroupGuid.ToString())
          .AddParameter("Properties", new[] { "member", "SID" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var groupResult = InvokeAndClear(ps);

        ThrowIfErrors(ps, "the group's membership could not be read");
        RequirePinnedTarget(groupResult);

        var dns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (groupResult[0].Properties["member"]?.Value is System.Collections.IEnumerable members)
        {
            foreach (var m in members)
            {
                var dn = m?.ToString();
                if (!string.IsNullOrWhiteSpace(dn) && seen.Add(dn))
                    dns.Add(dn);
            }
        }

        // The primary-group union (lst-2). Fail closed: an unreadable SID is a read error, never
        // a silently narrower list.
        var rid = Comms10kMemberResolver.RidFromSid(groupResult[0].Properties["SID"]?.Value?.ToString());
        if (rid is null)
            throw new InvalidOperationException("The group's membership could not be read: its SID was unreadable.");

        ps.Streams.Error.Clear();
        ps.AddCommand("Get-ADObject")
          .AddParameter("LDAPFilter", $"(primaryGroupID={rid})")
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var primaries = InvokeAndClear(ps);

        if (ps.HadErrors)
        {
            var first = ps.Streams.Error.FirstOrDefault()?.ToString() ?? "unknown error";
            ps.Streams.Error.Clear();
            throw new InvalidOperationException($"The group's primary-group membership could not be read: {first}");
        }

        foreach (var p in primaries)
        {
            var dn = p?.Properties["DistinguishedName"]?.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(dn) && seen.Add(dn))
                dns.Add(dn);
        }

        return dns;
    }

    /// <summary>
    /// Display detail for the given member DNs, one query per batch. Internal virtual TEST SEAM.
    /// </summary>
    /// <remarks>
    /// <b>A batch error here is NOT fatal, and that is the opposite of the address resolver.</b>
    /// There, a failed query means addresses read as not-found and get dropped from a WRITE. Here
    /// the membership is already known - this only decorates it - so a failed detail query costs
    /// display names, not members. <see cref="Comms10kMemberResolver.BuildRows"/> falls back to
    /// each DN's CN, and the row count still equals the member count.
    /// </remarks>
    internal virtual IReadOnlyList<Comms10kMemberResolver.Detail> QueryMemberDetails(
        (string username, string password, string domain) creds, IReadOnlyList<string> distinguishedNames)
    {
        var details = new List<Comms10kMemberResolver.Detail>();
        if (distinguishedNames.Count == 0)
            return details;

        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        InvokeAndClear(ps);

        var credential = CreateCredential(creds.username, creds.password, creds.domain);
        var props = new[] { "sAMAccountName", "mail", "DisplayName" };

        foreach (var batch in Comms10kMemberResolver.Batch(distinguishedNames))
        {
            ps.Streams.Error.Clear();
            ps.AddCommand("Get-ADObject")
              .AddParameter("LDAPFilter", Comms10kMemberResolver.BuildBatchFilter(batch))
              .AddParameter("Properties", props)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var objects = InvokeAndClear(ps);

            if (ps.HadErrors)
            {
                _logger.LogWarning("Comms-10k: a member detail batch failed; those rows fall back to their CN. {Error}",
                    ps.Streams.Error.FirstOrDefault()?.ToString() ?? "unknown error");
                ps.Streams.Error.Clear();
            }

            foreach (var o in objects)
            {
                if (o is null)
                    continue;
                details.Add(new Comms10kMemberResolver.Detail(
                    DistinguishedName: o.Properties["DistinguishedName"]?.Value?.ToString(),
                    SamAccountName: o.Properties["sAMAccountName"]?.Value?.ToString(),
                    Mail: o.Properties["mail"]?.Value?.ToString(),
                    DisplayName: o.Properties["DisplayName"]?.Value?.ToString()));
            }
        }

        return details;
    }

    /// <summary>
    /// Resolves CSV addresses to distinguished names, in batches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A query error aborts the WHOLE resolution and never yields not-found rows.</b> That is
    /// the load-bearing rule here, not the batching. <see cref="ExecuteReplaceAsync"/> removes
    /// everyone absent from the resolved list, so a batch that silently failed would read as
    /// "these 500 were not found" and unsubscribe 500 real people in one click. Mirrors
    /// <c>GroupManagementService.QueryBatchCandidates</c>, which fails closed for the same reason.
    /// </para>
    /// <para>
    /// The decisions live in <see cref="Comms10kAddressResolver"/>, which is pure; this method is
    /// the I/O around it.
    /// </para>
    /// </remarks>
    public virtual async Task<Comms10kResolveResult> ResolveEmailsAsync(List<string> emails)
    {
        if (!IsConfigured)
            return new Comms10kResolveResult { Success = false, Message = "Module not configured." };

        var creds = await _moduleCredentials.GetCredentialsAsync("Comms10k", "Comms-10k AD user resolution");
        if (creds is null)
            return new Comms10kResolveResult { Success = false, Message = "AD credentials unavailable." };

        return await Task.Run(() =>
        {
            IReadOnlyList<Comms10kAddressResolver.Candidate> candidates;
            try
            {
                candidates = QueryBatchCandidates(creds.Value, Comms10kAddressResolver.QueryableAddresses(emails));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Comms-10k address resolution failed.");
                return new Comms10kResolveResult
                {
                    Success = false,
                    Message = $"Address resolution failed and no addresses were resolved: {ex.Message}"
                };
            }

            var outcome = Comms10kAddressResolver.Resolve(emails, candidates);

            return new Comms10kResolveResult
            {
                Success = outcome.Rejections.Count == 0,
                ResolvedMembers = outcome.ResolvedMembers,
                Rejections = outcome.Rejections,
                Message = $"{outcome.ResolvedDns.Count} resolved, {outcome.Rejections.Count} rejected."
            };
        });
    }

    /// <summary>
    /// The live directory query: one <c>Get-ADObject -LDAPFilter</c> per batch, inside ONE
    /// runspace. Internal virtual TEST SEAM, like
    /// <c>GroupManagementService.QueryBatchCandidates</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No <c>-Server</c>.</b> Directory scope is the host's own, discovered at runtime
    /// (architectural invariant 7). The admin module passes a global catalog because it resolves
    /// foreign-domain principals by design; this module subscribes people from one address list
    /// and has never done that.
    /// </para>
    /// <para>
    /// <b>No <c>ResultSetSize</c>.</b> It would truncate the batch silently, and a truncated
    /// answer here is indistinguishable from "not found" - which is the unsubscribe path. It
    /// would also destroy ambiguity detection: a second match dropped by the cap turns an
    /// AMBIGUOUS address into a confidently resolved wrong person.
    /// </para>
    /// <para>
    /// Throws on any batch error, so the caller reports a failed resolution. Never returns a
    /// partial candidate set.
    /// </para>
    /// </remarks>
    internal virtual IReadOnlyList<Comms10kAddressResolver.Candidate> QueryBatchCandidates(
        (string username, string password, string domain) creds,
        IReadOnlyList<string> addresses)
    {
        if (addresses.Count == 0)
            return [];

        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        InvokeAndClear(ps);

        var credential = CreateCredential(creds.username, creds.password, creds.domain);
        return ReadAddressCandidates(ps, credential, addresses);
    }

    /// <summary>The real batch query and identity parsing, also usable with local SDK stand-ins.</summary>
    internal static IReadOnlyList<Comms10kAddressResolver.Candidate> ReadAddressCandidates(
        PowerShell ps, PSCredential credential, IReadOnlyList<string> addresses)
    {
        var candidates = new List<Comms10kAddressResolver.Candidate>();
        var props = new[] { "userPrincipalName", "mail", "objectGUID" };

        foreach (var batch in Comms10kAddressResolver.Batch(addresses))
        {
            ps.Streams.Error.Clear();
            ps.AddCommand("Get-ADObject")
              .AddParameter("LDAPFilter", Comms10kAddressResolver.BuildBatchFilter(batch))
              .AddParameter("Properties", props)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var objects = InvokeAndClear(ps);

            if (ps.HadErrors)
            {
                var first = ps.Streams.Error.FirstOrDefault()?.ToString() ?? "unknown error";
                ps.Streams.Error.Clear();
                throw new InvalidOperationException($"The directory query reported an error: {first}");
            }

            foreach (var o in objects)
            {
                if (o is null)
                    continue;
                if (!Guid.TryParse(o.Properties["objectGUID"]?.Value?.ToString(), out var guid) || guid == Guid.Empty)
                    throw new InvalidOperationException("A directory candidate has no valid, nonempty ObjectGUID; resolution was refused.");
                candidates.Add(new Comms10kAddressResolver.Candidate(
                    ObjectGuid: guid,
                    DistinguishedName: o.Properties["DistinguishedName"]?.Value?.ToString(),
                    UserPrincipalName: o.Properties["userPrincipalName"]?.Value?.ToString(),
                    Mail: o.Properties["mail"]?.Value?.ToString()));
            }
        }

        return candidates;
    }

    public virtual async Task<Comms10kUpdateResult> ExecuteReplaceAsync(
        IReadOnlyList<Comms10kAddressResolver.ResolvedMember> resolvedMembers, string performedBy)
    {
        var memberGuids = resolvedMembers.Select(member => member.ObjectGuid).Distinct().ToArray();
        if (memberGuids.Length == 0 || memberGuids.Contains(Guid.Empty))
            return new Comms10kUpdateResult { Success = false, MembersRequested = memberGuids.Length, MembersWritten = 0, Message = "Cannot replace with an empty member list." };

        var group = TargetGroupDisplayName;
        if (!IsConfigured)
            return new Comms10kUpdateResult { Success = false, MembersRequested = memberGuids.Length, MembersWritten = 0, Message = "Module not configured." };

        var creds = await _moduleCredentials.GetCredentialsAsync("Comms10k", "Comms-10k AD membership replacement");
        if (creds is null)
            return new Comms10kUpdateResult { Success = false, MembersRequested = memberGuids.Length, MembersWritten = 0, Message = "AD credentials unavailable." };

        return await Task.Run(() =>
        {
            var outcome = RunReplace(group, memberGuids, creds.Value);

            _logger.LogInformation(
                "Comms10k replace by {User} on {Group}: {Outcome}, {Requested} requested, {Written} written (acknowledged adds), final {Final}",
                performedBy, group, outcome.Outcome, outcome.MembersRequested?.ToString() ?? "unknown",
                outcome.MembersWritten?.ToString() ?? "unknown",
                outcome.FinalCount?.ToString() ?? "unknown");

            return outcome;
        });
    }

    /// <summary>
    /// Resolve, re-read, then clear-then-fill with a read-back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Replaces are NOT serialised.</b> Owner ruling 2026-09-30
    /// (<c>.agents/decisions.md</c>): the serialisation this sequence was first built with was
    /// never asked for and is removed. Two replaces of the same group running at once can
    /// interleave a clear with the other run's batches and leave the list holding neither
    /// uploaded file. That risk is accepted, not overlooked. What catches it is the read-back:
    /// each run compares the final membership against its own uploaded list, so an interleaved
    /// run reports <c>PartlyApplied</c> with the observed count rather than claiming success,
    /// and re-running one CSV on its own repairs the list.
    /// </para>
    /// <para>
    /// <b>Do not reinstate a lock here without an owner ruling that says so in those words.</b>
    /// The removed design is described in <c>docs/Comms10kBulkResolveScale-Plan.md</c>, which is
    /// superseded on this point.
    /// </para>
    /// </remarks>
    private Comms10kUpdateResult RunReplace(
        string group, IReadOnlyList<Guid> memberGuids, (string username, string password, string domain) creds)
    {
        Comms10kTarget target;
        try
        {
            target = QueryTargetGroup(creds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve the Comms10k target group {Group}", group);
            return Refused(group, memberGuids, $"The target group could not be resolved: {ex.Message}");
        }

        return PerformReplace(group, target, memberGuids, creds);
    }

    /// <summary>
    /// The write itself. Everything that can refuse does so BEFORE the clear command is issued;
    /// from the clear onwards the outcome comes from the read-back and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>One runspace for the whole sequence, and the steps are passed to
    /// <see cref="Comms10kReplaceWriter.ExecuteSequence"/> as delegates rather than being
    /// <c>internal virtual</c> seams of their own.</b> A seam per step, each opening its own
    /// runspace, costs an <c>Import-Module ActiveDirectory</c> per add batch - six extra module
    /// imports at ten thousand members. Order and outcome derivation live in the pure writer;
    /// command cleanup is also exercised through these operations with the real PowerShell SDK
    /// and harmless local functions, without a directory.
    /// </remarks>
    private Comms10kUpdateResult PerformReplace(
        string group, Comms10kTarget target, IReadOnlyList<Guid> memberGuids,
        (string username, string password, string domain) creds)
    {
        var writeSequenceEntered = false;
        try
        {
            using var runspace = CreateReplaceRunspace();
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
            InvokeAndClear(ps);

            var credential = CreateCredential(creds.username, creds.password, creds.domain);

            // Re-read by objectGUID. The Constitution requires a re-read before write where
            // practical, and here it is one cheap query that also proves the object still exists. It
            // verifies the pinned GUID again. Every subsequent operation keeps using that GUID,
            // so a rename, move or replacement at the old DN cannot retarget the write.
            Comms10kTarget confirmed;
            try
            {
                confirmed = ReadTargetByGuid(ps, credential, target.ObjectGuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Comms10k could not re-read {Group} by objectGUID before the write", group);
                return Refused(group, memberGuids,
                    $"The target group could not be re-read immediately before the write: {ex.Message}");
            }

            // A member-attribute clear cannot remove primary-group membership. Enforce the
            // approved set before dispatch, rather than declaring post-write extras acceptable.
            var primaries = ReadPrimaryMemberGuids(ps, credential, confirmed);
            if (primaries.Except(memberGuids).Any())
                return Refused(group, memberGuids,
                    "The group has primary-group members outside the approved set. They cannot be removed by this operation. Correct the membership and validate again.");

            // From entry to the write sequence onwards, even an unexpected exception must
            // preserve uncertainty: clear may have been dispatched before that exception.
            writeSequenceEntered = true;
            return Comms10kReplaceWriter.ExecuteSequence(
                group,
                memberGuids,
                clear: () => WithRetry("clear", () => ClearMembers(ps, credential, confirmed)),
                addBatch: batch => WithRetry("add batch", () => AddMemberBatch(ps, credential, confirmed, batch)),
                readBack: () => ReadBackMembers(ps, credential, confirmed));
        }
        catch (Exception ex) when (!writeSequenceEntered)
        {
            _logger.LogError(ex, "Comms10k write preparation failed for {Group} before any change", group);
            return Refused(group, memberGuids,
                $"Directory write preparation failed before any change: {ex.Message}");
        }
    }

    // Separate creation from Open so local tests can exercise both preparation failure paths.
    internal virtual Runspace CreateReplaceRunspace()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        return RunspaceFactory.CreateRunspace(iss);
    }

    /// <summary>
    /// Runs one directory write, retrying only a transient failure, and logging every attempt.
    /// </summary>
    /// <remarks>
    /// Per OPERATION, never per run: re-running the clear after batches have landed would wipe
    /// them. Every attempt logs, because <c>ADException</c> is a broad base type and a permanent
    /// fault being retried three times must be visible rather than inferred.
    /// </remarks>
    private void WithRetry(string what, Action operation) =>
        Comms10kReplaceWriter.WithRetry(
            operation,
            (attempt, typeName, retryable, ex) => _logger.LogWarning(ex,
                "Comms10k {What} attempt {Attempt} of {Max} failed; type {Type}, retryable {Retryable}: {Message}",
                what, attempt, Comms10kReplaceWriter.MaxAttempts, typeName ?? "unknown", retryable, ex.Message),
            backoff: () => Thread.Sleep(RetryBackoff));

    /// <summary>
    /// Invokes only the queued operation, discarding its command state even when Invoke throws.
    /// </summary>
    /// <remarks>
    /// A retained write could otherwise execute again during its retry or the read-back. Keep
    /// the original exception for retry classification; returned non-terminating errors remain
    /// available for the caller's existing error handling.
    /// </remarks>
    private static System.Collections.ObjectModel.Collection<PSObject> InvokeAndClear(PowerShell ps)
    {
        try
        {
            return ps.Invoke();
        }
        catch
        {
            ps.Streams.Error.Clear();
            throw;
        }
        finally
        {
            ps.Commands.Clear();
        }
    }

    /// <summary>Empties the group's <c>member</c> attribute. One operation, whatever the size.</summary>
    internal static void ClearMembers(PowerShell ps, PSCredential credential, Comms10kTarget target)
    {
        var identity = PinnedIdentity(target.ObjectGuid);
        ps.Streams.Error.Clear();
        ps.AddCommand("Set-ADGroup")
          .AddParameter("Identity", identity)
          .AddParameter("Clear", "member")
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        InvokeAndClear(ps);
        ThrowIfErrors(ps, "the group could not be cleared");
    }

    /// <summary>Adds one batch. <c>-Add</c> on a member already present is a silent no-op, which is what makes a retry safe.</summary>
    internal static void AddMemberBatch(
        PowerShell ps, PSCredential credential, Comms10kTarget target, IReadOnlyList<Guid> batch)
    {
        var identity = PinnedIdentity(target.ObjectGuid);
        if (batch.Count == 0 || batch.Contains(Guid.Empty))
            throw new ArgumentException("Every member add requires a nonempty ObjectGUID.", nameof(batch));
        // MS-ADTS 3.1.1.3.1.2.4 permits GUID-only alternative DNs as modify attribute values.
        // Never append a DN or fall back to one: a reused path must not select another person.
        var values = batch.Select(guid => $"<GUID={guid:D}>").ToArray();
        ps.Streams.Error.Clear();
        ps.AddCommand("Set-ADGroup")
          .AddParameter("Identity", identity)
          .AddParameter("Add", new System.Collections.Hashtable { { "member", values } })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        InvokeAndClear(ps);
        ThrowIfErrors(ps, "a batch of members could not be added");
    }

    /// <summary>
    /// Strict immutable identities for every observed member, including primary membership.
    /// </summary>
    /// <remarks>
    /// A missing or ambiguous DN-to-GUID mapping fails the observation. The display-list
    /// resolver's permissive fallbacks cannot establish who the write actually produced.
    /// </remarks>
    internal static Comms10kReplaceWriter.ReadBack ReadBackMembers(
        PowerShell ps, PSCredential credential, Comms10kTarget target)
    {
        var group = ReadMembershipGroup(ps, credential, target);
        var dns = new List<string>();
        var value = group.Properties["member"]?.Value;
        if (value is string single)
            dns.Add(single);
        else if (value is System.Collections.IEnumerable raw)
        {
            foreach (var member in raw)
            {
                if (member is not string dn || string.IsNullOrWhiteSpace(dn))
                    throw new InvalidOperationException("A member distinguished name was unreadable.");
                dns.Add(dn);
            }
        }
        else if (value is not null)
            throw new InvalidOperationException("The member attribute was unreadable.");

        return new Comms10kReplaceWriter.ReadBack(
            ReadMemberGuids(ps, credential, dns), ReadPrimaryMemberGuids(ps, credential, group));
    }

    internal static IReadOnlyList<Guid> ReadPrimaryMemberGuids(
        PowerShell ps, PSCredential credential, Comms10kTarget target) =>
        ReadPrimaryMemberGuids(ps, credential, ReadMembershipGroup(ps, credential, target));

    private static PSObject ReadMembershipGroup(PowerShell ps, PSCredential credential, Comms10kTarget target)
    {
        var identity = PinnedIdentity(target.ObjectGuid);
        ps.Streams.Error.Clear();
        ps.AddCommand("Get-ADGroup")
          .AddParameter("Identity", identity)
          .AddParameter("Properties", new[] { "member", "SID" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var groupResult = InvokeAndClear(ps);
        ThrowIfErrors(ps, "the membership could not be read back");

        RequirePinnedTarget(groupResult);
        return groupResult[0];
    }

    private static IReadOnlyList<Guid> ReadPrimaryMemberGuids(PowerShell ps, PSCredential credential, PSObject group)
    {
        var rid = Comms10kMemberResolver.RidFromSid(group.Properties["SID"]?.Value?.ToString());
        if (rid is null)
            throw new InvalidOperationException("The group's SID was unreadable, so primary-group identities could not be confirmed.");

        ps.Streams.Error.Clear();
        ps.AddCommand("Get-ADObject")
          .AddParameter("LDAPFilter", $"(primaryGroupID={rid})")
          .AddParameter("Properties", new[] { "objectGUID" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var primaries = InvokeAndClear(ps);
        ThrowIfErrors(ps, "the group's primary-group membership could not be read back");

        var identities = new HashSet<Guid>();
        foreach (var p in primaries)
            identities.Add(RequireMemberGuid(p));
        return identities.ToArray();
    }

    private static IReadOnlyList<Guid> ReadMemberGuids(PowerShell ps, PSCredential credential, IReadOnlyList<string> dns)
    {
        var identities = new HashSet<Guid>();
        foreach (var batch in Comms10kMemberResolver.Batch(dns.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
        {
            ps.Streams.Error.Clear();
            ps.AddCommand("Get-ADObject")
              .AddParameter("LDAPFilter", Comms10kMemberResolver.BuildBatchFilter(batch))
              .AddParameter("Properties", new[] { "distinguishedName", "objectGUID" })
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var found = InvokeAndClear(ps);
            ThrowIfErrors(ps, "member identities could not be read back");

            var requested = new HashSet<string>(batch, StringComparer.OrdinalIgnoreCase);
            var mapped = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in found)
            {
                var dn = member.Properties["DistinguishedName"]?.Value?.ToString();
                var guid = RequireMemberGuid(member);
                if (string.IsNullOrWhiteSpace(dn) || !requested.Contains(dn)
                    || (mapped.TryGetValue(dn, out var previous) && previous != guid))
                    throw new InvalidOperationException("A member identity mapping was missing, unexpected or ambiguous.");
                mapped[dn] = guid;
            }
            if (mapped.Count != requested.Count)
                throw new InvalidOperationException("Not every observed member could be mapped to an ObjectGUID.");
            identities.UnionWith(mapped.Values);
        }
        return identities.ToArray();
    }

    private static Guid RequireMemberGuid(PSObject member)
    {
        var value = member.Properties["ObjectGUID"]?.Value;
        if (!Guid.TryParse(value?.ToString(), out var guid) || guid == Guid.Empty)
            throw new InvalidOperationException("A directory member had a missing or invalid ObjectGUID.");
        return guid;
    }

    /// <summary>Re-reads the target by its objectGUID and returns the identity as it stands now.</summary>
    internal static Comms10kTarget ReadTargetByGuid(PowerShell ps, PSCredential credential, Guid objectGuid)
    {
        var identity = PinnedIdentity(objectGuid);
        ps.Streams.Error.Clear();
        ps.AddCommand("Get-ADGroup")
          .AddParameter("Identity", identity)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var found = InvokeAndClear(ps);
        ThrowIfErrors(ps, "the group could not be re-read");

        return RequirePinnedTarget(found);
    }

    private static string PinnedIdentity(Guid objectGuid)
    {
        if (objectGuid != TargetGroupGuid)
            throw new InvalidOperationException("The target is not the fixed Comms-10k group.");

        return TargetGroupGuid.ToString();
    }

    private static Comms10kTarget RequirePinnedTarget(System.Collections.ObjectModel.Collection<PSObject> found)
    {
        if (found.Count != 1)
            throw new InvalidOperationException($"The fixed Comms-10k group lookup returned {found.Count} groups; exactly one is required.");

        var dn = found[0].Properties["DistinguishedName"]?.Value?.ToString();
        var guidValue = found[0].Properties["ObjectGUID"]?.Value;
        if (guidValue is not Guid guid || guid != TargetGroupGuid)
            throw new InvalidOperationException("The lookup did not return the fixed Comms-10k group.");
        if (string.IsNullOrWhiteSpace(dn))
            throw new InvalidOperationException("The fixed Comms-10k group lookup returned no distinguished name.");

        return new Comms10kTarget(dn, guid);
    }

    /// <summary>
    /// Converts a non-terminating error stream into a throw, preserving the directory's own
    /// exception as the inner one so the retry classifier can see its type.
    /// </summary>
    private static void ThrowIfErrors(PowerShell ps, string what)
    {
        if (!ps.HadErrors) return;

        var record = ps.Streams.Error.FirstOrDefault();
        ps.Streams.Error.Clear();

        if (record?.Exception is not null)
            throw new InvalidOperationException($"{what}: {record.Exception.Message}", record.Exception);

        throw new InvalidOperationException($"{what}: {record?.ToString() ?? "unknown error"}");
    }

    /// <summary>
    /// A refusal that happened before the clear was issued, so the membership is untouched. It
    /// routes through the one procedure that derives outcomes rather than building a result by
    /// hand - deciding outcomes at the error site is the habit that produced three rounds of
    /// contradictions in this plan.
    /// </summary>
    private static Comms10kUpdateResult Refused(string group, IReadOnlyList<Guid> target, string reason)
    {
        return Comms10kReplaceWriter.Derive(
            group, target, reason, observed: null, readBackError: null, writeError: null);
    }

    /// <summary>
    /// Resolves the fixed group GUID to its current distinguished name, once, before
    /// anything is written. Throws if the group cannot be resolved unambiguously. Internal virtual
    /// TEST SEAM.
    /// </summary>
    /// <remarks>
    /// The fixed GUID is the owner-approved target, independent of any obsolete config row.
    /// A failed or mismatched lookup refuses before the write sequence is entered.
    /// </remarks>
    internal virtual Comms10kTarget QueryTargetGroup(
        (string username, string password, string domain) creds)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        InvokeAndClear(ps);

        var credential = CreateCredential(creds.username, creds.password, creds.domain);

        return ReadTargetByGuid(ps, credential, TargetGroupGuid);
    }

    private static PSCredential CreateCredential(string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username : $"{domain}\\{username}";
        var securePassword = new System.Security.SecureString();
        foreach (var c in password) securePassword.AppendChar(c);
        return new PSCredential(fullUsername, securePassword);
    }
}

/// <summary>
/// The resolved immutable identity of the target group. Every directory operation in a replace
/// binds to its pinned ObjectGuid; the distinguished name is descriptive only.
/// </summary>
internal readonly record struct Comms10kTarget(string DistinguishedName, Guid ObjectGuid);

public class Comms10kMember
{
    public string Email { get; set; } = "";
    public string SamAccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public class Comms10kMemberList
{
    public string GroupName { get; set; } = "";
    public int TotalCount { get; set; }
    public List<Comms10kMember> Members { get; set; } = new();
}

public class Comms10kResolveResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public IReadOnlyList<Comms10kAddressResolver.ResolvedMember> ResolvedMembers { get; init; } = [];
    public List<string> ResolvedDns => ResolvedMembers.Select(m => m.DistinguishedName).ToList();

    /// <summary>
    /// Every address that did not resolve, in input order, each with the reason. Owner ruling
    /// 2026-10-12: all of them are reported, and the operator can download them as a CSV.
    /// </summary>
    /// <remarks>
    /// <b>Never truncate this.</b> The view shows a ten-address preview; the list itself is the
    /// whole set, because it is what the errors CSV is written from and what the operator needs
    /// in order to find the rows in the file they uploaded.
    /// </remarks>
    public List<Comms10kAddressResolver.Rejection> Rejections { get; set; } = new();
}

public class Comms10kUpdateResult
{
    /// <summary>
    /// Kept for the audit and notification channels, which are boolean. It is DERIVED from
    /// <see cref="Outcome"/> and is not the thing to branch on: "partly applied" and "could not
    /// confirm" are both false here and mean entirely different things to an operator.
    /// </summary>
    public bool Success { get; set; }

    public string Message { get; set; } = "";

    /// <summary>What actually happened. See <see cref="Comms10kOutcome"/>.</summary>
    public Comms10kOutcome Outcome { get; set; } = Comms10kOutcome.RefusedBeforeAnyChange;

    /// <summary>The resolved set size requested for this replace, or null when it is unavailable.</summary>
    public int? MembersRequested { get; set; }

    /// <summary>
    /// Members covered by a complete sequence of acknowledged adds; zero for a refusal before
    /// dispatch. Null when the write threw or its result was lost. This is never inferred from
    /// final membership, which may include pre-existing or concurrently written members.
    /// </summary>
    public int? MembersWritten { get; set; }

    /// <summary>
    /// The membership observed by the read-back. <b>Null, never a guess</b>, when the read-back
    /// could not be performed - that is the whole point of the could-not-confirm outcome.
    /// </summary>
    public int? FinalCount { get; set; }

    /// <summary>Members a <c>member</c> write cannot evict, because this is their primary group.</summary>
    public List<string> UnremovableMembers { get; set; } = new();
}
