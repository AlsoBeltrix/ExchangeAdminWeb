namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// One stored row's fate under the migration.
/// </summary>
/// <param name="PolicyAlias">The section the row grants access to.</param>
/// <param name="OriginalValue">Exactly what the store held, for the halt report.</param>
/// <param name="Sid">The SID to store. Null when <paramref name="Failure"/> is set.</param>
/// <param name="DisplayName">The friendly name to store for display. Null on failure.</param>
/// <param name="Failure">Why this row could not be converted; null when it converted.</param>
public sealed record RowMigrationResult(
    string PolicyAlias,
    string OriginalValue,
    string? Sid,
    string? DisplayName,
    string? Failure)
{
    public bool Converted => Failure is null;
}

/// <summary>
/// What the migration should do with the store, decided without touching it.
/// </summary>
/// <param name="ShouldWrite">
/// True only when every row converted. A partial write is never proposed.
/// </param>
/// <param name="Rows">Every row's result, converted or not, in input order.</param>
/// <param name="Failures">The subset that could not be converted. Empty when
/// <paramref name="ShouldWrite"/> is true.</param>
/// <param name="AlreadyMigrated">
/// True when every row was already a SID, so the write would be a no-op.
/// </param>
public sealed record SectionAccessMigrationPlan(
    bool ShouldWrite,
    IReadOnlyList<RowMigrationResult> Rows,
    IReadOnlyList<RowMigrationResult> Failures,
    bool AlreadyMigrated);

/// <summary>
/// Decides what the section-access SID migration writes, given the rows and a directory to
/// resolve names against.
/// </summary>
/// <remarks>
/// Separated from the store so the all-or-nothing rule and every failure path are provable without
/// SQLite, and from the directory so they are provable without Active Directory. What remains in
/// the caller is only "run this plan against the database".
///
/// See docs/SectionAccessSidStorage-Plan.md.
/// </remarks>
public static class SectionAccessSidMigrationPlanner
{
    /// <summary>
    /// Builds the migration plan. Resolves each DISTINCT stored value once, however many aliases
    /// reference it - 58 prod rows hold only 18 distinct values, and a directory round-trip per
    /// row would triple the work done while the app is starting.
    /// </summary>
    /// <remarks>
    /// <para><b>All-or-nothing.</b> One unconvertible row stops the whole write. A partial
    /// migration leaves the authorization table in a state no one can reason about - some rows
    /// SIDs, some names - and whichever rows were dropped are access grants that vanished with no
    /// audit trail. Refusing to start is loud; a silently reduced grant is not. The plan records
    /// this as fail-closed behavior rather than policy for an observed case: all 18 prod values
    /// resolve today, so this path guards a future the data does not yet contain.</para>
    ///
    /// <para><b>A failed lookup is not a missing group.</b> If the directory cannot answer, this
    /// throws rather than marking rows unconvertible. Both outcomes leave the store untouched, so
    /// the difference is not in what is written - it is that an outage must not be reported to an
    /// administrator as "these groups do not exist", which would send them to fix data that is
    /// perfectly correct. It also must not be a condition an operator can clear by editing the
    /// store.</para>
    /// </remarks>
    /// <exception cref="DirectoryUnavailableException">
    /// The directory could not be consulted. The caller must leave the store alone and retry on a
    /// later start.
    /// </exception>
    public static SectionAccessMigrationPlan Plan(
        IReadOnlyList<(string PolicyAlias, string GroupValue)> rows,
        ISectionAccessGroupDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(directory);

        var resolvedByValue = new Dictionary<string, RowMigrationResult>(StringComparer.OrdinalIgnoreCase);
        var results = new List<RowMigrationResult>(rows.Count);
        var everySourceWasAlreadyASid = true;

        foreach (var (alias, value) in rows)
        {
            var parsed = SectionAccessGroupIdentity.Parse(value);
            if (parsed.Kind != StoredGroupValueKind.Sid)
                everySourceWasAlreadyASid = false;

            // Cache on the raw value, not the parsed name: "IAM" and "ANALOG\IAM" are different
            // questions to the directory even though they resolve to the same group here.
            if (!resolvedByValue.TryGetValue(value ?? string.Empty, out var resolution))
            {
                resolution = Resolve(parsed, directory);
                resolvedByValue[value ?? string.Empty] = resolution;
            }

            results.Add(resolution with { PolicyAlias = alias, OriginalValue = value ?? string.Empty });
        }

        var failures = results.Where(r => !r.Converted).ToList();

        return new SectionAccessMigrationPlan(
            ShouldWrite: failures.Count == 0 && !everySourceWasAlreadyASid,
            Rows: results,
            Failures: failures,
            AlreadyMigrated: everySourceWasAlreadyASid);
    }

    private static RowMigrationResult Resolve(StoredGroupValue parsed, ISectionAccessGroupDirectory directory)
    {
        if (parsed.Kind == StoredGroupValueKind.Unusable)
            return Failed(parsed.Raw, parsed.RejectionReason ?? "cannot be interpreted");

        if (parsed.Kind == StoredGroupValueKind.Sid)
        {
            // Already migrated. Deliberately NOT re-resolved to refresh the display name: this
            // runs at startup, and making a completed migration depend on the directory again
            // would reintroduce the outage sensitivity the whole design removes.
            return new RowMigrationResult(string.Empty, parsed.Raw, parsed.Sid, null, null);
        }

        // Any DirectoryUnavailableException propagates: see the Plan remarks.
        var matches = directory.FindGroupsByName(parsed.Name!, parsed.NetBiosDomain);

        switch (SectionAccessGroupIdentity.ClassifyMatchCount(matches?.Count ?? 0))
        {
            case GroupResolutionOutcome.NotFound:
                return Failed(parsed.Raw, "no group with that name exists in "
                    + (parsed.NetBiosDomain is null ? "this domain" : parsed.NetBiosDomain));

            case GroupResolutionOutcome.Ambiguous:
                return Failed(parsed.Raw,
                    $"{matches!.Count} groups answer to that name, so it does not identify one group");
        }

        var match = matches![0];

        // The directory is not trusted to return something usable. A group whose SID is a
        // well-known one cannot be resolved to by name in practice, but the refusal is cheap and
        // this is the last point before a value becomes an authorization subject.
        var rejection = SectionAccessGroupIdentity.SidRejectionReason(match.Sid);
        if (rejection is not null)
            return Failed(parsed.Raw, $"resolved to a SID that is {rejection}");

        var display = string.IsNullOrWhiteSpace(match.DisplayName) ? parsed.Name! : match.DisplayName;
        return new RowMigrationResult(string.Empty, parsed.Raw, match.Sid, display, null);
    }

    private static RowMigrationResult Failed(string raw, string reason)
        => new(string.Empty, raw, null, null, reason);

    /// <summary>
    /// The operator-facing explanation of a halt, naming every offending row. Written for a log a
    /// human reads after the app declined to migrate, so it states the consequence as well as the
    /// cause - the failure is silent from the UI's point of view, and the store still holds names.
    /// </summary>
    public static string DescribeFailures(IReadOnlyList<RowMigrationResult> failures)
    {
        if (failures.Count == 0)
            return string.Empty;

        var lines = failures
            .Select(f => $"  {f.PolicyAlias} / '{f.OriginalValue}': {f.Failure}")
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return $"Section-access SID migration halted; {failures.Count} row(s) could not be resolved "
            + "to exactly one group. No rows were changed and access is unaffected. Fix or remove "
            + "these entries on the admin page, then restart:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, lines);
    }
}
