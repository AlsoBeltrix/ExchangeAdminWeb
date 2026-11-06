using System.Text.Json;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// One entry of the Verify ignore list (docs/DirectoryObjectVerify-Plan.md B.3): an admin saw <see cref="Finding"/> for one
/// stored row and chose to keep the row and stop the finding being reported until something changes. Matched on store,
/// row key, identifier or text, finding and what was observed: a group ignored as renamed to X is reported again when its
/// name becomes Y, and a missing object stays ignored until its row is removed or replaced.
/// </summary>
/// <param name="Store">The store the row lives in - <see cref="VerifyIgnoreList.ProtectedPrincipalsStore"/> for A1.</param>
/// <param name="RowKey">The row's kind within the store (a protected-principal kind such as <c>group_sid</c>).</param>
/// <param name="IdentifierOrText">The row's identifier (SID or objectGUID), or its stored text when it has none.</param>
/// <param name="Finding">The finding ignored: <c>Missing</c>, <c>LabelDrift</c>, <c>WrongClass</c> or <c>Ambiguous</c>.</param>
/// <param name="Observed">What Verify observed for it (the current name, the actual class, the matches; "not found").</param>
/// <param name="By">Who ignored it.</param>
/// <param name="At">When, UTC, round-trip format.</param>
public sealed record VerifyIgnoreEntry(
    string Store, string RowKey, string IdentifierOrText, string Finding, string Observed, string By, string At);

/// <summary>
/// The <c>app_setting</c> key <c>verify_ignores</c> (docs/DirectoryObjectVerify-Plan.md B.3): a JSON array of
/// <see cref="VerifyIgnoreEntry"/>. Additive - no DDL - and never touched by an older build. Because an ignore can change
/// enforcement (B.4, slice V2), the list is authorization state: it is written only by compare-and-swap inside the
/// transaction that checks the row it names (<see cref="Storage.AppSettingRepository.ReplaceIfEqual"/>), and a value that
/// does not parse ignores nothing (fail closed).
/// </summary>
internal static class VerifyIgnoreList
{
    /// <summary>The <c>app_setting</c> key holding the list.</summary>
    public const string SettingKey = "verify_ignores";

    /// <summary>
    /// The <c>app_setting</c> key holding the last Verify per store (C.1): display-only, written with a plain
    /// <see cref="Storage.AppSettingRepository.Set"/>.
    /// </summary>
    public const string LastVerifiedKey = "verify_last";

    /// <summary>The store name of the Admin Settings protected principals (A1/A2).</summary>
    public const string ProtectedPrincipalsStore = "ProtectedPrincipals";

    /// <summary>
    /// The store name of Module Configuration's section access (A3): the Access pane's groups and the Protection servicer's,
    /// both <c>section_access</c> rows. An entry's row key is the policy alias (<c>ProtectedServicer:&lt;ModuleId&gt;</c> for the
    /// servicer). Its ignores change only what Verify reports (plan B.4): no authorization path reads the list.
    /// </summary>
    public const string SectionAccessStore = "SectionAccess";

    /// <summary>
    /// The store name of Module Configuration's module admins (A4), <c>module_admins</c> rows; an entry's row key is the module
    /// id. Its ignores change only what Verify reports (plan B.4).
    /// </summary>
    public const string ModuleAdminsStore = "ModuleAdmins";

    /// <summary>
    /// The store name of the Module Configuration settings that hold a directory object as text (A5-A8, slice V4):
    /// <c>module_config</c> values. An entry's row key is the setting key (each verified key belongs to one module,
    /// <see cref="ModuleConfigVerifyAdapter.Settings"/>), its identifier-or-text one entry of the value as the module splits
    /// it. Its ignores change only what Verify reports (plan C.2: display-only); no module reads the list.
    /// </summary>
    public const string ConfigStore = "Config";

    /// <summary>What a <c>Missing</c> finding observes: the directory answered and holds no such object.</summary>
    public const string NotFoundObserved = "not found";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses a stored value. Null or blank is an empty list. False - and an empty list - when the value is not a JSON
    /// array of complete entries: a list that cannot be read ignores nothing.
    /// </summary>
    public static bool TryParse(string? raw, out IReadOnlyList<VerifyIgnoreEntry> entries)
    {
        entries = [];
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        try
        {
            var parsed = JsonSerializer.Deserialize<List<VerifyIgnoreEntry?>>(raw, Json);
            if (parsed is null || parsed.Any(e => e is null || !Complete(e)))
                return false;

            entries = parsed.Select(e => e!).ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The entries <paramref name="raw"/> holds; an empty list and one Warning when it does not parse.</summary>
    public static IReadOnlyList<VerifyIgnoreEntry> ParseOrEmpty(string? raw, ILogger? logger)
    {
        if (TryParse(raw, out var entries))
            return entries;

        logger?.LogWarning("The Verify ignore list ({Key}) could not be read; nothing is ignored until it is repaired", SettingKey);
        return [];
    }

    /// <summary>The stored form of <paramref name="entries"/>.</summary>
    public static string Serialize(IEnumerable<VerifyIgnoreEntry> entries) => JsonSerializer.Serialize(entries.ToList(), Json);

    /// <summary>Whether <paramref name="entry"/> names this row - store, row key and identifier or text.</summary>
    public static bool SameRow(VerifyIgnoreEntry entry, string store, string rowKey, string identifierOrText) =>
        string.Equals(entry.Store, store, StringComparison.Ordinal)
        && string.Equals(entry.RowKey, rowKey, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.IdentifierOrText.Trim(), identifierOrText.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="entry"/> ignores this finding on this row: the row, the finding and what was observed.</summary>
    public static bool Matches(VerifyIgnoreEntry entry, string store, string rowKey, string identifierOrText, string finding, string observed) =>
        SameRow(entry, store, rowKey, identifierOrText)
        && string.Equals(entry.Finding, finding, StringComparison.Ordinal)
        && string.Equals(entry.Observed.Trim(), observed.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Complete(VerifyIgnoreEntry e) =>
        !string.IsNullOrWhiteSpace(e.Store) && !string.IsNullOrWhiteSpace(e.RowKey)
        && !string.IsNullOrWhiteSpace(e.IdentifierOrText) && !string.IsNullOrWhiteSpace(e.Finding)
        && e.Observed is not null && e.By is not null && e.At is not null;
}
