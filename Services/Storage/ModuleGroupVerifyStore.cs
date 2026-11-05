using System.Security.Cryptography;
using System.Text;
using ExchangeAdminWeb.Authorization;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// One stored group grant a Module Configuration Verify action names (docs/DirectoryObjectVerify-Plan.md A3/A4): its row key - the
/// policy alias for a <c>section_access</c> row, the module id for a <c>module_admins</c> row - and its value exactly as stored.
/// </summary>
public sealed record GroupGrantRow(string RowKey, string Value);

/// <summary>
/// What a Verify press of one Module Configuration pane reads (plan B.1, B.3), in ONE read transaction: the pane's rows, whether
/// the store is configured (section access's presence marker; module admins have none and are always configured), the
/// section-access display names (the labels A3 and A4 both show), and the raw ignore list.
/// </summary>
internal sealed record ModuleGroupVerifySnapshot(
    bool Readable, bool Configured, IReadOnlyList<GroupGrantRow> Rows, IReadOnlyDictionary<string, string> DisplayNames, string? IgnoresRaw)
{
    /// <summary>The version of <see cref="Rows"/> and <see cref="Configured"/>, which every Verify write checks.</summary>
    public string Version => ModuleGroupVerifyStore.VersionOf(Configured, Rows);

    public static ModuleGroupVerifySnapshot Unreadable { get; } =
        new(false, false, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null);
}

/// <summary>
/// What a Module Configuration Verify write did (plan C.2): its status, the pane's rows under the lock, and - written - the rows
/// it left, their version, the ignore list and the display names as it left them, all read back before the commit.
/// </summary>
internal sealed record ModuleGroupVerifyWriteOutcome(
    VerifyWriteStatus Status,
    IReadOnlyList<GroupGrantRow>? Before,
    IReadOnlyList<GroupGrantRow>? After,
    string? AfterVersion,
    string? AfterIgnores,
    IReadOnlyDictionary<string, string>? AfterDisplayNames)
{
    public static ModuleGroupVerifyWriteOutcome Refused(VerifyWriteStatus status, IReadOnlyList<GroupGrantRow>? before = null) =>
        new(status, before, null, null, null, null);
}

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md B.3, C.2 (slice V3). The write discipline the Module Configuration Verify actions share
/// with the protected principals' (<see cref="ProtectedPrincipalRepository"/>): ONE write transaction that refuses unless the
/// pane's rows are still the version the item was read with, lets the action decide and write, reads the rows back, and - for
/// Remove and Ignore - compare-and-swaps the ignore list against the value read beside the item, pruning the entries of rows
/// the store no longer holds. A refusal writes nothing, not even the change token. And the prune the panes' ordinary saves run
/// in their own transactions (<see cref="Prune"/>).
/// </summary>
internal static class ModuleGroupVerifyStore
{
    /// <summary>A stable version of a pane's rows: their order does not matter, their exact text does.</summary>
    internal static string VersionOf(bool configured, IEnumerable<GroupGrantRow> rows)
    {
        var text = new StringBuilder(configured ? "configured\n" : "unconfigured\n");
        foreach (var row in rows.Select(r => r.RowKey + "\t" + r.Value).Order(StringComparer.Ordinal))
            text.Append(row).Append('\n');
        return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    /// <summary>What identifies a stored grant for the ignore list (B.3): a usable group SID as parsed, else its trimmed text.</summary>
    internal static string RowIdentity(string value) =>
        SectionAccessGroupIdentity.Parse(value) is { Kind: StoredGroupValueKind.Sid, Sid: { } sid } ? sid : value.Trim();

    // An ignore of another store, or one naming a grant the store still holds.
    private static bool StillNamesARow(VerifyIgnoreEntry entry, string store, IReadOnlyList<GroupGrantRow> allRows) =>
        !string.Equals(entry.Store, store, StringComparison.Ordinal)
        || allRows.Any(r =>
            string.Equals(r.RowKey, entry.RowKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(RowIdentity(r.Value), entry.IdentifierOrText.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The ordinary saves' cleanup (B.3, round-3 note): inside the save's own transaction, after its writes, the ignore entries
    /// of <paramref name="store"/> naming a grant the store no longer holds are dropped. A list that does not parse is left as it
    /// is - it ignores nothing, and rewriting it would lose what an admin may still repair.
    /// </summary>
    internal static void Prune(SqliteConnection connection, SqliteTransaction transaction, string store, IReadOnlyList<GroupGrantRow> allRows)
    {
        var raw = AppSettingRepository.ReadValue(connection, transaction, VerifyIgnoreList.SettingKey);
        if (raw is null || !VerifyIgnoreList.TryParse(raw, out var entries))
            return;

        var kept = entries.Where(e => StillNamesARow(e, store, allRows)).ToList();
        if (kept.Count != entries.Count)
            AppSettingRepository.ReplaceIfEqual(connection, transaction, VerifyIgnoreList.SettingKey, raw, VerifyIgnoreList.Serialize(kept));
    }

    /// <summary>The pane's rows as read under the lock, or null when they cannot be read.</summary>
    internal delegate (bool Configured, IReadOnlyList<GroupGrantRow> Rows)? ScopeReader(SqliteConnection connection, SqliteTransaction transaction);

    /// <summary>
    /// One Verify write. <paramref name="decide"/> is shown the pane's rows under the lock once their version is confirmed; it
    /// writes and returns null, or returns the refusal - and then nothing it wrote is kept.
    /// </summary>
    internal static ModuleGroupVerifyWriteOutcome Write(
        IConfigStore configStore,
        string store,
        ScopeReader readScope,
        Func<SqliteConnection, SqliteTransaction, IReadOnlyList<GroupGrantRow>> readAllRows,
        string expectedVersion,
        bool casIgnores,
        string? expectedIgnores,
        VerifyIgnoreEntry? addIgnore,
        Func<SqliteConnection, SqliteTransaction, (bool Configured, IReadOnlyList<GroupGrantRow> Rows), VerifyWriteStatus?> decide)
    {
        try
        {
            return configStore.Write((connection, transaction) =>
            {
                (bool Configured, IReadOnlyList<GroupGrantRow> Rows)? read;
                try
                {
                    read = readScope(connection, transaction);
                }
                catch
                {
                    read = null;
                }

                if (read is not { } before)
                    throw new Refused(VerifyWriteStatus.StoreUnreadable, null);
                if (!string.Equals(VersionOf(before.Configured, before.Rows), expectedVersion, StringComparison.Ordinal))
                    throw new Refused(VerifyWriteStatus.VersionChanged, before.Rows);

                if (decide(connection, transaction, before) is { } refusal)
                    throw new Refused(refusal, before.Rows);

                var after = readScope(connection, transaction) ?? throw new Refused(VerifyWriteStatus.StoreUnreadable, before.Rows);
                var ignoresAfter = AppSettingRepository.ReadValue(connection, transaction, VerifyIgnoreList.SettingKey);
                if (casIgnores)
                {
                    // The list as the caller read it beside the item: a list changed since is refused, whole, and the caller reads
                    // it again (B.3). A list that cannot be read is pruned of nothing, and is never added to.
                    if (!string.Equals(ignoresAfter, expectedIgnores, StringComparison.Ordinal))
                        throw new Refused(VerifyWriteStatus.IgnoresChanged, before.Rows);
                    if (!VerifyIgnoreList.TryParse(expectedIgnores, out var entries))
                    {
                        if (addIgnore is not null)
                            throw new Refused(VerifyWriteStatus.IgnoresUnreadable, before.Rows);
                    }
                    else
                    {
                        var allRows = readAllRows(connection, transaction);
                        var kept = entries.Where(e => StillNamesARow(e, store, allRows)).ToList();
                        if (addIgnore is not null)
                            kept.Add(addIgnore);
                        if (kept.Count != entries.Count || addIgnore is not null)
                        {
                            var updated = VerifyIgnoreList.Serialize(kept);
                            if (!AppSettingRepository.ReplaceIfEqual(connection, transaction, VerifyIgnoreList.SettingKey, expectedIgnores, updated))
                                throw new Refused(VerifyWriteStatus.IgnoresChanged, before.Rows);
                            ignoresAfter = updated;
                        }
                    }
                }

                return new ModuleGroupVerifyWriteOutcome(
                    VerifyWriteStatus.Written, before.Rows, after.Rows, VersionOf(after.Configured, after.Rows), ignoresAfter,
                    SectionAccessRepository.ReadDisplayNames(connection, transaction));
            });
        }
        catch (Refused refused)
        {
            return ModuleGroupVerifyWriteOutcome.Refused(refused.Status, refused.Before);
        }
    }

    // Thrown out of a Verify write to roll it back - the store commits whenever the callback returns. Never leaves this class.
    private sealed class Refused(VerifyWriteStatus status, IReadOnlyList<GroupGrantRow>? before) : Exception
    {
        public VerifyWriteStatus Status { get; } = status;
        public IReadOnlyList<GroupGrantRow>? Before { get; } = before;
    }
}
