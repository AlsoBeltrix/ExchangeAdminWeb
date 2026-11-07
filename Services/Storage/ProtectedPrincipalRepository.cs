using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Repository for the row-per-value <c>protected_principal</c> table (kind, value) plus a single
/// <c>protected_principal_present</c> marker. Backs <see cref="ProtectedPrincipalService"/>.
/// The four config lists (Users / Groups / OrganizationalUnits / SamAccountNamePatterns) are
/// stored as rows keyed by <c>kind</c>; the presence marker preserves the file-world
/// File.Exists distinction (configured-but-empty vs never configured). Row-per-value means one
/// bad entry can't drop the whole config. Sits on <see cref="IConfigStore"/>.
/// </summary>
public sealed class ProtectedPrincipalRepository
{
    public const string KindUser = "user";
    public const string KindGroup = "group";
    public const string KindOu = "ou";
    public const string KindSamPattern = "sam_pattern";

    /// <summary>
    /// Protected write TARGETS (docs/ProtectedGroupWriteTarget-Plan.md T0): a group protected
    /// AS AN OBJECT being written into, never a rule about its members. Stored beside the four
    /// principal-rule kinds and reinterpreting none of them. Value format: "objectGUID|DN".
    /// </summary>
    public const string KindGroupTarget = "group_target";

    /// <summary>
    /// Identifier rows (docs/ProtectedPrincipalIdentifiers-Plan.md A.2): a protected user by objectSid, a protected
    /// group by objectSid, a protected OU by objectGUID. Value format "identifier|label"
    /// (<see cref="ProtectedPrincipalIdEntry"/>), the label being the value an older build stores in the legacy kind.
    /// An older build's reader has no arm for these kinds and ignores them; it keeps enforcing the companion legacy row
    /// the admin page writes beside each one (plan B.1, B.2).
    /// </summary>
    public const string KindUserSid = "user_sid";

    /// <inheritdoc cref="KindUserSid"/>
    public const string KindGroupSid = "group_sid";

    /// <inheritdoc cref="KindUserSid"/>
    public const string KindOuGuid = "ou_guid";

    private readonly IConfigStore _store;

    public ProtectedPrincipalRepository(IConfigStore store) => _store = store;

    /// <summary>A change watcher over this repository's store, for the caching reader that sits on it.</summary>
    public ConfigChangeWatcher CreateChangeWatcher(ILogger? logger = null) => new(_store, logger);

    /// <summary>
    /// Reads the four lists AND the configured flag in a single guarded operation. Returns false
    /// if either read throws (DB-integrity failure / partial schema damage); both out-params are
    /// safe-defaulted so callers fail closed rather than throw.
    /// </summary>
    public bool TryRead(out ProtectedPrincipalData data, out bool configured)
    {
        try
        {
            (data, configured) = _store.Read(connection =>
            {
                var lists = ReadLists(connection);
                var present = ReadConfigured(connection);
                return (lists, present);
            });
            return true;
        }
        catch
        {
            data = ProtectedPrincipalData.Empty;
            configured = false;
            return false;
        }
    }

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.3 (R2). The enforcement load: the rules, the presence marker AND the raw Verify
    /// ignore list (<see cref="VerifyIgnoreList.SettingKey"/>, null when unset) in ONE read transaction, so an ignore and the
    /// rules it is read with are one snapshot. False, everything safe-defaulted, when any of the three reads throws.
    /// </summary>
    public bool TryRead(out ProtectedPrincipalData data, out bool configured, out string? verifyIgnores)
    {
        try
        {
            (data, configured, verifyIgnores) = _store.ReadInTransaction((connection, transaction) =>
            {
                var lists = ReadLists(connection, transaction);
                var present = ReadConfigured(connection, transaction);
                var ignores = AppSettingRepository.ReadValue(connection, transaction, VerifyIgnoreList.SettingKey);
                return (lists, present, ignores);
            });
            return true;
        }
        catch
        {
            data = ProtectedPrincipalData.Empty;
            configured = false;
            verifyIgnores = null;
            return false;
        }
    }

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.1, B.3. What a Verify press reads: the rows, the presence marker and the raw ignore
    /// list in one read transaction. An unreadable store is <see cref="ProtectedPrincipalSnapshot.Unreadable"/> with a null list.
    /// </summary>
    public (ProtectedPrincipalSnapshot Snapshot, string? VerifyIgnores) ReadVerifySnapshot()
    {
        try
        {
            return _store.ReadInTransaction((connection, transaction) =>
            {
                var snapshot = ReadSnapshot(connection, transaction);
                return snapshot.Readable
                    ? (snapshot, AppSettingRepository.ReadValue(connection, transaction, VerifyIgnoreList.SettingKey))
                    : (snapshot, (string?)null);
            });
        }
        catch
        {
            return (ProtectedPrincipalSnapshot.Unreadable, null);
        }
    }

    /// <summary>True if a protected-principals config has been saved (presence marker set).</summary>
    public bool IsConfigured()
    {
        return _store.Read(ReadConfigured);
    }

    /// <summary>
    /// Replaces the entire protected-principal config (clear + insert all four lists) and sets
    /// the presence marker, in one transaction - matching the file version's whole-file save.
    /// </summary>
    /// <remarks>
    /// Internal, and reached in production only from <see cref="ProtectedPrincipalService.SaveConfig"/>, itself an
    /// internal test fixture (aud-a26n): it reads nothing first, and the admin page's save decides under the lock
    /// (<see cref="Replace"/>). WholeStoreWriterTripwireTests refuses another caller.
    /// </remarks>
    internal void Save(ProtectedPrincipalData data)
    {
        _store.Write((connection, transaction) =>
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM protected_principal;";
                delete.ExecuteNonQuery();
            }

            InsertAll(connection, transaction, data);

            MarkPresent(connection, transaction);
        });
    }

    /// <summary>
    /// Reads the rules and replaces them in ONE write transaction (aud-a28j): <paramref name="decide"/>
    /// is shown the rows and the presence marker as they are under the write lock and returns the
    /// rules to store, or null to store nothing - in which case the transaction is rolled back and
    /// nothing, not even the change token, is written.
    /// </summary>
    /// <remarks>
    /// The admin page's save used to replace the table with whatever lists the page held, reading
    /// nothing first, so a page whose load had failed wrote its empty lists over every rule. The decision
    /// is made here, from the rows as they are under the lock <c>SqliteConfigStore.Write</c> takes at
    /// BEGIN IMMEDIATE: dev and prod share this database, and a read made before the write could be
    /// overtaken by the other instance before the replace lands (the aud-a28i and aud-a33b shape). The
    /// rows are read back before the commit so the caller's record names exactly what it left.
    /// </remarks>
    public ProtectedPrincipalReplaceOutcome Replace(Func<ProtectedPrincipalSnapshot, ProtectedPrincipalData?> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        try
        {
            return _store.Write((connection, transaction) =>
            {
                var before = ReadSnapshot(connection, transaction);
                var replacement = decide(before) ?? throw new NothingWritten(before);

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM protected_principal;";
                    delete.ExecuteNonQuery();
                }

                InsertAll(connection, transaction, replacement);
                MarkPresent(connection, transaction);

                // docs/DirectoryObjectVerify-Plan.md B.3, round-3 note: the page's ordinary Remove + Save replaces the whole
                // store, so a removed row's Verify ignores go in this same transaction - never left to apply to a row that
                // later comes back.
                PruneVerifyIgnores(connection, transaction, replacement);

                return new ProtectedPrincipalReplaceOutcome(true, before, ReadSnapshot(connection, transaction));
            });
        }
        catch (NothingWritten nothing)
        {
            return new ProtectedPrincipalReplaceOutcome(false, nothing.Before, null);
        }
    }

    /// <summary>
    /// The rows and the presence marker from one fresh read (aud-a28n); an unreadable store as
    /// <see cref="ProtectedPrincipalSnapshot.Unreadable"/>, never an exception.
    /// </summary>
    public ProtectedPrincipalSnapshot ReadSnapshot()
    {
        try
        {
            return _store.Read(connection => ReadSnapshot(connection, null));
        }
        catch
        {
            return ProtectedPrincipalSnapshot.Unreadable;
        }
    }

    // A read that throws - a damaged or missing table - is a snapshot that says it could not be read: the
    // service refuses on it, and inside the write it must not escape as an exception the page would record
    // as a failed write rather than a refusal.
    private static ProtectedPrincipalSnapshot ReadSnapshot(SqliteConnection connection, SqliteTransaction? transaction)
    {
        try
        {
            return new ProtectedPrincipalSnapshot(
                true, ReadLists(connection, transaction), ReadConfigured(connection, transaction));
        }
        catch
        {
            return ProtectedPrincipalSnapshot.Unreadable;
        }
    }

    // Thrown out of the write when decide stores nothing, because IConfigStore.Write commits - and bumps the
    // change token every cache on both instances watches - whenever its callback returns. An exception is its
    // documented rollback. Caught in Replace; it never leaves this class.
    private sealed class NothingWritten(ProtectedPrincipalSnapshot before) : Exception
    {
        public ProtectedPrincipalSnapshot Before { get; } = before;
    }

    /// <summary>The stored <c>verify_last</c> value (plan C.1), or null; display-only.</summary>
    internal string? ReadVerifyLast() => _store.Read(connection => AppSettingRepository.ReadValue(connection, null, VerifyIgnoreList.LastVerifiedKey));

    /// <summary>
    /// Writes <c>verify_last</c> as <paramref name="update"/> makes it from the stored value: a plain write, compared with
    /// nothing a caller read - the value is display-only and authorizes nothing (plan C.1).
    /// </summary>
    internal void UpdateVerifyLast(Func<string?, string> update) =>
        _store.Write((connection, transaction) =>
        {
            var raw = AppSettingRepository.ReadValue(connection, transaction, VerifyIgnoreList.LastVerifiedKey);
            AppSettingRepository.ReplaceIfEqual(connection, transaction, VerifyIgnoreList.LastVerifiedKey, raw, update(raw));
        });

    // ----- docs/DirectoryObjectVerify-Plan.md B.3, C.2: the Verify actions' write primitives -----
    //
    // Each runs in ONE write transaction (BEGIN IMMEDIATE, so nothing commits between its read and its write), refuses unless
    // the rules are still the version the Verify item was read with and the row it acts on is still stored exactly as read,
    // and - for Remove, Replace and Ignore - compare-and-swaps the ignore list against the value the caller read beside the
    // item. A refusal writes nothing, not even the change token.

    /// <summary>
    /// Remove: deletes <paramref name="row"/> (an identifier row with its companion, unless another identifier row still pairs
    /// with it) AND every Verify ignore that names it, in one transaction.
    /// </summary>
    public VerifyWriteOutcome RemoveWithCompanionAndIgnores(VerifyRowRef row, string expectedVersion, string? expectedIgnores) =>
        WriteVerify(expectedVersion, casIgnores: true, expectedIgnores, null, snapshot =>
        {
            var lists = new RuleLists(snapshot.Data);
            return lists.Remove(row) ? VerifyDecision.Rules(lists.ToData()) : VerifyDecision.Refuse(VerifyWriteStatus.RowChanged);
        });

    /// <summary>
    /// Replace: <paramref name="row"/> leaves (with its companion) and <paramref name="newRow"/> - the picked object's
    /// identifier row, or a target's <c>objectGUID|DN</c> - takes its place in the same kind, with
    /// <paramref name="newCompanion"/> beside it; the old row's Verify ignores go in the same transaction - rev-c2: every one
    /// that names the replaced row, even when the picked object carries the same identifier (an ignore is undone by Replace).
    /// </summary>
    /// <remarks>A legacy row stored by name only (A2) is replaced by an identifier row of the kind it converts into.</remarks>
    public VerifyWriteOutcome ReplaceWithCompanionAndIgnores(
        VerifyRowRef row, string newRow, string? newCompanion, string expectedVersion, string? expectedIgnores) =>
        WriteVerify(expectedVersion, casIgnores: true, expectedIgnores, null, snapshot =>
        {
            var lists = new RuleLists(snapshot.Data);
            if (!lists.Remove(row))
                return VerifyDecision.Refuse(VerifyWriteStatus.RowChanged);
            lists.Add(row.Kind, newRow, newCompanion);
            return VerifyDecision.Rules(lists.ToData());
        }, dropIgnoresOf: row);

    /// <summary>
    /// Rename (B.5): the identifier row of <paramref name="kind"/> carrying <paramref name="identifier"/> gets
    /// <paramref name="newLabel"/>, and its legacy companion's value becomes <paramref name="newLabel"/> - one transaction, so
    /// an older build sharing the database enforces the same object's current name.
    /// </summary>
    public VerifyWriteOutcome RewriteLabel(string kind, string identifier, string newLabel, string expectedVersion) =>
        WriteVerify(expectedVersion, casIgnores: false, null, null, snapshot =>
        {
            var lists = new RuleLists(snapshot.Data);
            return lists.RewriteLabel(kind, identifier, newLabel)
                ? VerifyDecision.Rules(lists.ToData())
                : VerifyDecision.Refuse(VerifyWriteStatus.RowChanged);
        });

    /// <summary>
    /// Ignore (B.3, R2): adds <paramref name="entry"/> to the ignore list, after confirming under the write lock that
    /// <paramref name="row"/> is still stored exactly as Verify read it. Changes no rule.
    /// </summary>
    public VerifyWriteOutcome AddVerifyIgnore(VerifyRowRef row, VerifyIgnoreEntry entry, string expectedVersion, string? expectedIgnores) =>
        WriteVerify(expectedVersion, casIgnores: true, expectedIgnores, entry, snapshot =>
            new RuleLists(snapshot.Data).Contains(row) ? VerifyDecision.Nothing : VerifyDecision.Refuse(VerifyWriteStatus.RowChanged));

    /// <summary>
    /// Convert (C.2): the one legacy row <paramref name="conversion"/> names gets its identifier row, through
    /// <see cref="ConfigDbIdentifiers.ConvertProtectedRow"/> - the primitive <c>db convert-identifiers</c> writes with.
    /// </summary>
    internal VerifyWriteOutcome ConvertRow(ProtectedPrincipalIdConversion conversion, string expectedVersion) =>
        WriteVerify(expectedVersion, casIgnores: false, null, null, snapshot =>
            new RuleLists(snapshot.Data).IsUnpairedLegacy(conversion.LegacyKind, conversion.LegacyValue)
                ? VerifyDecision.Raw((connection, transaction) => ConfigDbIdentifiers.ConvertProtectedRow(connection, transaction, conversion))
                : VerifyDecision.Refuse(VerifyWriteStatus.RowChanged));

    private VerifyWriteOutcome WriteVerify(
        string expectedVersion, bool casIgnores, string? expectedIgnores, VerifyIgnoreEntry? addIgnore,
        Func<ProtectedPrincipalSnapshot, VerifyDecision> decide, VerifyRowRef? dropIgnoresOf = null)
    {
        try
        {
            return _store.Write((connection, transaction) =>
            {
                var before = ReadSnapshot(connection, transaction);
                if (!before.Readable)
                    throw new VerifyRefused(VerifyWriteStatus.StoreUnreadable, before);
                if (!string.Equals(ProtectedPrincipalService.RulesVersion(before), expectedVersion, StringComparison.Ordinal))
                    throw new VerifyRefused(VerifyWriteStatus.VersionChanged, before);

                var decision = decide(before);
                if (decision.Refusal is { } refusal)
                    throw new VerifyRefused(refusal, before);

                if (decision.NewRules is { } rules)
                {
                    using (var delete = connection.CreateCommand())
                    {
                        delete.Transaction = transaction;
                        delete.CommandText = "DELETE FROM protected_principal;";
                        delete.ExecuteNonQuery();
                    }
                    InsertAll(connection, transaction, rules);
                    MarkPresent(connection, transaction);
                }
                decision.RawWrite?.Invoke(connection, transaction);

                var after = ReadSnapshot(connection, transaction);
                var ignoresAfter = AppSettingRepository.ReadValue(connection, transaction, VerifyIgnoreList.SettingKey);
                if (casIgnores)
                {
                    // The list as the caller read it beside the item: a list changed since is refused, whole, and the caller
                    // reads it again (B.3). A list that cannot be read is pruned of nothing, and is never added to.
                    if (!string.Equals(ignoresAfter, expectedIgnores, StringComparison.Ordinal))
                        throw new VerifyRefused(VerifyWriteStatus.IgnoresChanged, before);
                    if (!VerifyIgnoreList.TryParse(expectedIgnores, out var entries))
                    {
                        if (addIgnore is not null)
                            throw new VerifyRefused(VerifyWriteStatus.IgnoresUnreadable, before);
                    }
                    else
                    {
                        // rev-c2: and none that names the row the write took out, whatever took its place.
                        var kept = entries.Where(e => StillNamesARow(e, after.Data) && !NamesRow(e, dropIgnoresOf)).ToList();
                        if (addIgnore is not null)
                            kept.Add(addIgnore);
                        if (kept.Count != entries.Count || addIgnore is not null)
                        {
                            var updated = VerifyIgnoreList.Serialize(kept);
                            if (!AppSettingRepository.ReplaceIfEqual(connection, transaction, VerifyIgnoreList.SettingKey, expectedIgnores, updated))
                                throw new VerifyRefused(VerifyWriteStatus.IgnoresChanged, before);
                            ignoresAfter = updated;
                        }
                    }
                }

                return new VerifyWriteOutcome(
                    VerifyWriteStatus.Written, before.Data, after.Data, ProtectedPrincipalService.RulesVersion(after), ignoresAfter);
            });
        }
        catch (VerifyRefused refused)
        {
            return new VerifyWriteOutcome(refused.Status, refused.Before.Readable ? refused.Before.Data : null, null, null, null);
        }
    }

    // Thrown out of a Verify write to roll it back - the store commits whenever the callback returns. Never leaves this class.
    private sealed class VerifyRefused(VerifyWriteStatus status, ProtectedPrincipalSnapshot before) : Exception
    {
        public VerifyWriteStatus Status { get; } = status;
        public ProtectedPrincipalSnapshot Before { get; } = before;
    }

    private sealed record VerifyDecision(
        VerifyWriteStatus? Refusal, ProtectedPrincipalData? NewRules, Action<SqliteConnection, SqliteTransaction>? RawWrite)
    {
        public static VerifyDecision Nothing { get; } = new(null, null, null);
        public static VerifyDecision Refuse(VerifyWriteStatus status) => new(status, null, null);
        public static VerifyDecision Rules(ProtectedPrincipalData rules) => new(null, rules, null);
        public static VerifyDecision Raw(Action<SqliteConnection, SqliteTransaction> write) => new(null, null, write);
    }

    /// <summary>
    /// What identifies a stored row for the ignore list (B.3): an identifier row by its canonical identifier, a target by its
    /// objectGUID (its DN when it carries none), any other row by its trimmed value. Null for a blank one.
    /// </summary>
    internal static string? RowIdentity(string kind, string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length == 0)
            return null;

        switch (kind)
        {
            case KindUserSid or KindGroupSid:
                var sidEntry = ProtectedPrincipalIdEntry.Parse(v);
                return sidEntry is null ? null : ProtectedPrincipalIdEntry.CanonicalSid(sidEntry.Identifier) ?? sidEntry.Identifier;
            case KindOuGuid:
                var guidEntry = ProtectedPrincipalIdEntry.Parse(v);
                return guidEntry is null ? null : ProtectedPrincipalIdEntry.CanonicalGuid(guidEntry.Identifier) ?? guidEntry.Identifier;
            case KindGroupTarget:
                var target = ProtectedGroupTargetEntry.Parse(v);
                return ProtectedPrincipalIdEntry.CanonicalGuid(target.ObjectGuid) ?? target.ObjectGuid ?? target.DistinguishedName;
            default:
                return v;
        }
    }

    // rev-c2. Whether an ignore names the row a write took out: this store, the row's kind and its identity.
    private static bool NamesRow(VerifyIgnoreEntry entry, VerifyRowRef? row) =>
        row is not null
        && string.Equals(entry.Store, VerifyIgnoreList.ProtectedPrincipalsStore, StringComparison.Ordinal)
        && string.Equals(entry.RowKey, row.Kind, StringComparison.Ordinal)
        && RowIdentity(row.Kind, row.Value) is { } identity
        && string.Equals(identity, entry.IdentifierOrText.Trim(), StringComparison.OrdinalIgnoreCase);

    // An ignore of another store, or one naming a row these rules still hold.
    private static bool StillNamesARow(VerifyIgnoreEntry entry, ProtectedPrincipalData rules) =>
        !string.Equals(entry.Store, VerifyIgnoreList.ProtectedPrincipalsStore, StringComparison.Ordinal)
        || RuleLists.ValuesOf(rules, entry.RowKey).Any(v =>
            string.Equals(RowIdentity(entry.RowKey, v), entry.IdentifierOrText.Trim(), StringComparison.OrdinalIgnoreCase));

    // The whole-store save's cleanup: entries naming a row the replacement no longer holds are dropped. A list that does not
    // parse is left as it is - it ignores nothing, and rewriting it would lose what an admin may still repair.
    private static void PruneVerifyIgnores(SqliteConnection connection, SqliteTransaction transaction, ProtectedPrincipalData replacement)
    {
        var raw = AppSettingRepository.ReadValue(connection, transaction, VerifyIgnoreList.SettingKey);
        if (raw is null || !VerifyIgnoreList.TryParse(raw, out var entries))
            return;

        var kept = entries.Where(e => StillNamesARow(e, replacement)).ToList();
        if (kept.Count != entries.Count)
            AppSettingRepository.ReplaceIfEqual(connection, transaction, VerifyIgnoreList.SettingKey, raw, VerifyIgnoreList.Serialize(kept));
    }

    /// <summary>The eight kinds' rows as lists a Verify write edits, with the identifier/companion rules of the admin page.</summary>
    private sealed class RuleLists
    {
        private readonly Dictionary<string, List<string>> _lists;

        public RuleLists(ProtectedPrincipalData data) => _lists = new(StringComparer.Ordinal)
        {
            [KindUser] = [.. data.Users],
            [KindGroup] = [.. data.Groups],
            [KindOu] = [.. data.OrganizationalUnits],
            [KindSamPattern] = [.. data.SamAccountNamePatterns],
            [KindGroupTarget] = [.. data.GroupTargets],
            [KindUserSid] = [.. data.UserIds],
            [KindGroupSid] = [.. data.GroupIds],
            [KindOuGuid] = [.. data.OuIds],
        };

        public static IEnumerable<string> ValuesOf(ProtectedPrincipalData data, string kind) => kind switch
        {
            KindUser => data.Users,
            KindGroup => data.Groups,
            KindOu => data.OrganizationalUnits,
            KindSamPattern => data.SamAccountNamePatterns,
            KindGroupTarget => data.GroupTargets,
            KindUserSid => data.UserIds,
            KindGroupSid => data.GroupIds,
            KindOuGuid => data.OuIds,
            _ => [],
        };

        private static string? LegacyKindOf(string idKind) => idKind switch
        {
            KindUserSid => KindUser,
            KindGroupSid => KindGroup,
            KindOuGuid => KindOu,
            _ => null,
        };

        private List<string> Of(string kind) =>
            _lists.TryGetValue(kind, out var list) ? list : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a protected-principal kind.");

        public bool Contains(VerifyRowRef row) => _lists.TryGetValue(row.Kind, out var list) && list.Contains(row.Value, StringComparer.Ordinal);

        /// <summary>A legacy row stored exactly so, and no identifier row's companion.</summary>
        public bool IsUnpairedLegacy(string legacyKind, string value)
        {
            var idKind = legacyKind switch { KindUser => KindUserSid, KindGroup => KindGroupSid, KindOu => KindOuGuid, _ => null };
            return idKind is not null
                   && Of(legacyKind).Contains(value, StringComparer.Ordinal)
                   && !ProtectedPrincipalIdEntry.IsCompanion(value, Of(idKind));
        }

        public bool Remove(VerifyRowRef row)
        {
            if (!Contains(row))
                return false;
            if (LegacyKindOf(row.Kind) is { } legacy)
                return ProtectedPrincipalIdEntry.RemoveWithCompanion(Of(row.Kind), Of(legacy), row.Value);
            return Of(row.Kind).Remove(row.Value);
        }

        /// <summary>The replacement row in the kind <paramref name="kind"/> lives in, or - for a legacy row - converts into.</summary>
        public void Add(string kind, string newRow, string? companion)
        {
            var idKind = kind switch { KindUser => KindUserSid, KindGroup => KindGroupSid, KindOu => KindOuGuid, _ => kind };
            if (LegacyKindOf(idKind) is { } legacy)
            {
                ProtectedPrincipalIdEntry.AddWithCompanion(Of(idKind), Of(legacy), newRow, companion);
                return;
            }

            if (idKind == KindGroupTarget)
            {
                // pgwt-6: one group, one target row - a row sharing the new row's GUID goes.
                var guid = ProtectedGroupTargetEntry.Parse(newRow).ObjectGuid;
                if (!string.IsNullOrEmpty(guid))
                    Of(idKind).RemoveAll(x => string.Equals(ProtectedGroupTargetEntry.Parse(x).ObjectGuid, guid, StringComparison.OrdinalIgnoreCase));
            }
            if (!Of(idKind).Contains(newRow, StringComparer.OrdinalIgnoreCase))
                Of(idKind).Add(newRow);
        }

        public bool RewriteLabel(string kind, string identifier, string newLabel)
        {
            var wanted = RowIdentity(kind, kind == KindGroupTarget ? identifier : ProtectedPrincipalIdEntry.Format(identifier, ""));
            var row = Of(kind).FirstOrDefault(r => string.Equals(RowIdentity(kind, r), wanted, StringComparison.OrdinalIgnoreCase));
            if (row is null || string.IsNullOrWhiteSpace(newLabel))
                return false;

            if (kind == KindGroupTarget)
            {
                var guid = ProtectedGroupTargetEntry.Parse(row).ObjectGuid;
                if (string.IsNullOrEmpty(guid))
                    return false;
                Of(kind)[Of(kind).IndexOf(row)] = ProtectedGroupTargetEntry.Format(guid, newLabel.Trim());
                return true;
            }

            var legacy = LegacyKindOf(kind);
            var entry = ProtectedPrincipalIdEntry.Parse(row);
            if (legacy is null || entry is null)
                return false;

            // The old row and its companion leave and the renamed row and its new companion arrive: AddWithCompanion's own
            // rule, so a companion another row still pairs with stays.
            ProtectedPrincipalIdEntry.RemoveWithCompanion(Of(kind), Of(legacy), row);
            ProtectedPrincipalIdEntry.AddWithCompanion(Of(kind), Of(legacy), ProtectedPrincipalIdEntry.Format(entry.Identifier, newLabel), newLabel.Trim());
            return true;
        }

        public ProtectedPrincipalData ToData() =>
            new(Of(KindUser).ToArray(), Of(KindGroup).ToArray(), Of(KindOu).ToArray(), Of(KindSamPattern).ToArray(), Of(KindGroupTarget).ToArray())
            {
                UserIds = Of(KindUserSid).ToArray(),
                GroupIds = Of(KindGroupSid).ToArray(),
                OuIds = Of(KindOuGuid).ToArray(),
            };
    }

    /// <summary>
    /// One-time import only if not yet configured (presence marker absent). Marks present even
    /// for an all-empty config (an existing file with empty lists still counted as configured).
    /// Returns true if it imported.
    /// </summary>
    public bool ImportIfMissing(ProtectedPrincipalData data)
    {
        return _store.Write((connection, transaction) =>
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM protected_principal_present LIMIT 1;";
                if (check.ExecuteScalar() is not null)
                    return false;
            }

            InsertAll(connection, transaction, data);
            MarkPresent(connection, transaction);
            return true;
        });
    }

    /// <summary>
    /// The rows of every kind on <paramref name="connection"/>, inside <paramref name="transaction"/> when one is given: the read
    /// <c>db convert-identifiers</c> and <c>db identifier-status</c> make, which have no <see cref="IConfigStore"/> to read through
    /// (docs/ProtectedPrincipalIdentifiers-Plan.md K.3). Throws when the read fails.
    /// </summary>
    internal static ProtectedPrincipalData ReadRows(SqliteConnection connection, SqliteTransaction? transaction)
        => ReadLists(connection, transaction);

    /// <summary>
    /// <c>db convert-identifiers</c>' write of one conversion: a NEW identifier row, <c>identifier|legacy value</c>, in its
    /// identifier kind beside the legacy row, which is never rewritten and becomes its companion (plan B.1). Inside the verb's
    /// one transaction, which has re-read every row under the write lock first.
    /// </summary>
    internal static void InsertIdentifierRow(SqliteConnection connection, SqliteTransaction transaction, ProtectedPrincipalIdConversion conversion)
    {
        // Never into a legacy kind, where an older build would read "S-1-...|..." as a name (ProtectedPrincipalIdKindsTripwireTests).
        if (conversion.IdKind is not (KindUserSid or KindGroupSid or KindOuGuid))
            throw new ArgumentException($"'{conversion.IdKind}' is not an identifier kind.", nameof(conversion));
        InsertKind(connection, transaction, conversion.IdKind, [ProtectedPrincipalIdEntry.Format(conversion.Identifier, conversion.LegacyValue)]);
    }

    private static ProtectedPrincipalData ReadLists(SqliteConnection connection) => ReadLists(connection, null);

    private static ProtectedPrincipalData ReadLists(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind, value FROM protected_principal;";
        var users = new List<string>();
        var groups = new List<string>();
        var ous = new List<string>();
        var patterns = new List<string>();
        var targets = new List<string>();
        var userIds = new List<string>();
        var groupIds = new List<string>();
        var ouIds = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var kind = reader.GetString(0);
            var value = reader.GetString(1);
            switch (kind)
            {
                case KindUser: users.Add(value); break;
                case KindGroup: groups.Add(value); break;
                case KindOu: ous.Add(value); break;
                case KindSamPattern: patterns.Add(value); break;
                case KindGroupTarget: targets.Add(value); break;
                case KindUserSid: userIds.Add(value); break;
                case KindGroupSid: groupIds.Add(value); break;
                case KindOuGuid: ouIds.Add(value); break;
            }
        }
        return new ProtectedPrincipalData(
            users.ToArray(), groups.ToArray(), ous.ToArray(), patterns.ToArray(), targets.ToArray())
        {
            UserIds = userIds.ToArray(),
            GroupIds = groupIds.ToArray(),
            OuIds = ouIds.ToArray(),
        };
    }

    private static bool ReadConfigured(SqliteConnection connection) => ReadConfigured(connection, null);

    private static bool ReadConfigured(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM protected_principal_present LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    // Every kind, legacy and identifier, each into its own kind: an identifier row is never written into a legacy kind,
    // where an older build would read it as a user, group or OU named "S-1-...|..." (ProtectedPrincipalIdKindsTripwireTests).
    private static void InsertAll(SqliteConnection connection, SqliteTransaction transaction, ProtectedPrincipalData data)
    {
        InsertKind(connection, transaction, KindUser, data.Users);
        InsertKind(connection, transaction, KindGroup, data.Groups);
        InsertKind(connection, transaction, KindOu, data.OrganizationalUnits);
        InsertKind(connection, transaction, KindSamPattern, data.SamAccountNamePatterns);
        InsertKind(connection, transaction, KindGroupTarget, data.GroupTargets);
        InsertKind(connection, transaction, KindUserSid, data.UserIds);
        InsertKind(connection, transaction, KindGroupSid, data.GroupIds);
        InsertKind(connection, transaction, KindOuGuid, data.OuIds);
    }

    private static void InsertKind(SqliteConnection connection, SqliteTransaction transaction, string kind, string[] values)
    {
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO protected_principal (kind, value) VALUES ($kind, $value) " +
                "ON CONFLICT(kind, value) DO NOTHING;";
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }
    }

    private static void MarkPresent(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO protected_principal_present (marker) VALUES (1) ON CONFLICT(marker) DO NOTHING;";
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// The protected-principal lists, repository-shaped (decoupled from the service config type).
/// <paramref name="GroupTargets"/> is the write-target list (kind <c>group_target</c>), a
/// separate rule set from the four principal lists (docs/ProtectedGroupWriteTarget-Plan.md T0).
/// </summary>
public sealed record ProtectedPrincipalData(
    string[] Users,
    string[] Groups,
    string[] OrganizationalUnits,
    string[] SamAccountNamePatterns,
    string[] GroupTargets)
{
    public static ProtectedPrincipalData Empty { get; } = new([], [], [], [], []);

    /// <summary>Identifier rows of kind <c>user_sid</c>, each "objectSid|label" (docs/ProtectedPrincipalIdentifiers-Plan.md A.2).</summary>
    public string[] UserIds { get; init; } = [];

    /// <summary>Identifier rows of kind <c>group_sid</c>, each "objectSid|DN".</summary>
    public string[] GroupIds { get; init; } = [];

    /// <summary>Identifier rows of kind <c>ou_guid</c>, each "objectGUID|DN".</summary>
    public string[] OuIds { get; init; } = [];

    /// <summary>How many rows of all eight kinds these lists hold, a companion legacy row counted as its own row.</summary>
    public int RuleCount =>
        Users.Length + Groups.Length + OrganizationalUnits.Length + SamAccountNamePatterns.Length + GroupTargets.Length
        + UserIds.Length + GroupIds.Length + OuIds.Length;
}

/// <summary>The protected-principal rows and presence marker, read together (aud-a28j).</summary>
/// <param name="Readable">False when the read failed; <paramref name="Data"/> is then empty and means nothing.</param>
public sealed record ProtectedPrincipalSnapshot(bool Readable, ProtectedPrincipalData Data, bool Configured)
{
    public static ProtectedPrincipalSnapshot Unreadable { get; } = new(false, ProtectedPrincipalData.Empty, false);
}

/// <summary>
/// What <see cref="ProtectedPrincipalRepository.Replace"/> did: whether it replaced the rules, the rows
/// it found under the write lock, and - after a replace - the rows it left, read back before the commit.
/// </summary>
public sealed record ProtectedPrincipalReplaceOutcome(
    bool Replaced, ProtectedPrincipalSnapshot Before, ProtectedPrincipalSnapshot? After);

/// <summary>
/// One conversion <c>db convert-identifiers</c> writes (docs/ProtectedPrincipalIdentifiers-Plan.md K.3): the legacy row as the
/// verb read it, and the identifier row it adds beside it, the legacy value its label.
/// </summary>
public sealed record ProtectedPrincipalIdConversion(string LegacyKind, string LegacyValue, string IdKind, string Identifier);

/// <summary>One stored protected-principal row a Verify action acts on: its kind and its value exactly as stored.</summary>
public sealed record VerifyRowRef(string Kind, string Value);

/// <summary>How a Verify action's write ended (docs/DirectoryObjectVerify-Plan.md B.3, C.2). Only <see cref="Written"/> wrote.</summary>
public enum VerifyWriteStatus
{
    /// <summary>The write landed.</summary>
    Written,

    /// <summary>Nothing written: the rows could not be read under the lock.</summary>
    StoreUnreadable,

    /// <summary>Nothing written: the rules are no longer the version the Verify item was read with.</summary>
    VersionChanged,

    /// <summary>Nothing written: the row the action names is no longer stored as Verify read it.</summary>
    RowChanged,

    /// <summary>Nothing written: the ignore list changed since it was read beside the item.</summary>
    IgnoresChanged,

    /// <summary>Nothing written: the ignore list does not parse, so nothing may be added to it.</summary>
    IgnoresUnreadable,
}

/// <summary>
/// What a Verify write did: its status, the rules it found under the lock, and - written - the rules it left, their version
/// and the ignore list as it left it, all read back before the commit.
/// </summary>
public sealed record VerifyWriteOutcome(
    VerifyWriteStatus Status, ProtectedPrincipalData? Before, ProtectedPrincipalData? After, string? AfterVersion, string? AfterIgnores);
