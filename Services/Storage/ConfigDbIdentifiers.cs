using ExchangeAdminWeb.Authorization;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// The two identifier verbs of the app's <c>db</c> mode (docs/ProtectedPrincipalIdentifiers-Plan.md K.3 point 1):
/// <c>db convert-identifiers &lt;db&gt; [--plan]</c> and <c>db identifier-status &lt;db&gt;</c>. Run by hand, once, against a
/// copy of the settings database and then the live one, before the deploy that needs it (K.4); the deploy pipeline does not
/// call either.
/// </summary>
/// <remarks>
/// <para><b>All or nothing.</b> The conversion reads every protected user, group and OU row with no identifier companion and
/// every module-admin grant that is not a usable SID, and looks each up once (<see cref="ProtectedPrincipalIdentifierResolver"/>:
/// B.4 step 3's rules for rows, B.6's for grants; exactly one object, well-known SIDs refused). If any lookup finds none or
/// several objects, or could not run, nothing is written and every offending row is named. Otherwise every conversion is
/// written in ONE transaction, under the write lock, after every row has been read again there: if the work is not exactly
/// what was resolved - another writer (an older build's save on the shared database) changed a row in between - nothing is
/// written and the verb says to run it again.</para>
/// <para><b>What it writes.</b> A protected-principal row gets a NEW identifier row beside it (<c>user_sid</c>/<c>group_sid</c>/
/// <c>ou_guid</c>, value <c>id|legacy value</c>); the legacy row is never rewritten (B.1). A module-admin name grant is replaced
/// in place by the group's SID (B.6). The change token moves with the write, so a running instance re-reads.</para>
/// <para><b>Plan.</b> <c>--plan</c> reports the same and the would-be writes, and never opens the database for writing.</para>
/// <para><b>Idempotent.</b> A converted row has its companion and a converted grant is a SID, so a second run finds nothing to
/// do and says so.</para>
/// <para><b>Output</b> (the <see cref="ConfigDbCli"/> contract, extended): on exit 0 the report is on stdout; on exit 2 every
/// line - the offending rows, or the status lines with a row still stored by name - is on stderr, ending in an
/// <c>error:</c> line, so stdout stays meaningful only after exit 0.</para>
/// </remarks>
internal static class ConfigDbIdentifiers
{
    internal const string ModuleAdminKind = "module-admin";

    /// <summary>The work one read found: rows and grants not yet stored by identifier.</summary>
    private sealed record Work(IReadOnlyList<ProtectedPrincipalLegacyRow> Rows, IReadOnlyList<(string ModuleId, string Grant)> Grants)
    {
        public bool IsEmpty => Rows.Count == 0 && Grants.Count == 0;

        public bool SameAs(Work other) =>
            Rows.Select(Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(other.Rows.Select(Key).OrderBy(k => k, StringComparer.Ordinal))
            && Grants.Select(Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(other.Grants.Select(Key).OrderBy(k => k, StringComparer.Ordinal));

        private static string Key(ProtectedPrincipalLegacyRow r) => r.Kind + "\n" + r.Value;
        private static string Key((string ModuleId, string Grant) g) => g.ModuleId + "\n" + g.Grant;
    }

    /// <summary><c>db convert-identifiers</c>. Returns the exit code.</summary>
    internal static int Convert(
        string dbPath, bool plan, Func<IProtectedPrincipalResolveDirectory> openDirectory, TextWriter stdout, TextWriter stderr)
    {
        var path = Path.GetFullPath(dbPath);
        if (!File.Exists(path))
            return Fail(stderr, [], $"Database not found: {path}");

        Work work;
        using (var connection = ConfigDbMaintenance.OpenReadOnly(path))
            work = ReadWork(connection, null);

        if (work.IsEmpty)
        {
            stdout.WriteLine("nothing to convert: every protected user, group and OU has its identifier and every module-admin grant is a SID");
            return ConfigDbCli.ExitOk;
        }

        var (rows, grants, offending) = Resolve(work, openDirectory);
        if (offending.Count > 0)
            return Fail(stderr, offending,
                $"{offending.Count} of {work.Rows.Count + work.Grants.Count} {(offending.Count == 1 ? "entry does" : "entries do")} not resolve to exactly one object; nothing was written. "
                + "Fix or remove each in the old build's Admin Settings or Module Configuration, then run this again.");

        var lines = rows.Select(c => $"{c.LegacyKind}: {c.LegacyValue} -> {c.Identifier}")
            .Concat(grants.Select(g => $"{ModuleAdminKind} {g.ModuleId}: {g.Name} -> {g.Sid}"))
            .ToList();

        if (plan)
        {
            stdout.WriteLine("plan: nothing written; a real run would convert:");
            foreach (var line in lines)
                stdout.WriteLine(line);
            stdout.WriteLine($"would convert {lines.Count}");
            return ConfigDbCli.ExitOk;
        }

        try
        {
            var store = new SqliteConfigStore(new SqliteConnectionFactory(path, mustExist: true));
            store.Write((connection, transaction) =>
            {
                // Read again under the write lock: the lookups ran with no lock held, and dev and prod share this database.
                if (!ReadWork(connection, transaction).SameAs(work))
                    throw new ConfigDbMaintenanceException(
                        "The protected principals or module-admin grants changed while the lookups ran; nothing was written. Run this again.");

                foreach (var conversion in rows)
                    ConvertProtectedRow(connection, transaction, conversion);
                foreach (var conversion in grants)
                    ConvertModuleAdminGrant(connection, transaction, conversion);
            });
        }
        catch (Exception ex)
        {
            // One transaction: a throw rolled it back whole.
            return Fail(stderr, [], ex.Message);
        }

        foreach (var line in lines)
            stdout.WriteLine(line);
        stdout.WriteLine($"converted {lines.Count}");
        return ConfigDbCli.ExitOk;
    }

    /// <summary>
    /// The write of ONE protected-principal row's conversion, inside the caller's write transaction, which has read the row
    /// under the write lock first: a NEW identifier row, <c>identifier|legacy value</c>, beside the legacy row, which is never
    /// rewritten and becomes its companion (plan B.1). The whole-workset <see cref="Convert"/> writes every row with it, and the
    /// Admin Settings Verify pane's Convert one row (docs/DirectoryObjectVerify-Plan.md C.2).
    /// </summary>
    internal static void ConvertProtectedRow(SqliteConnection connection, SqliteTransaction transaction, ProtectedPrincipalIdConversion conversion) =>
        ProtectedPrincipalRepository.InsertIdentifierRow(connection, transaction, conversion);

    /// <summary>
    /// The write of ONE module-admin name grant's conversion, inside the caller's write transaction: the grant replaced in place
    /// by the group's SID (plan B.6). The whole-workset <see cref="Convert"/> writes every grant with it.
    /// </summary>
    internal static void ConvertModuleAdminGrant(SqliteConnection connection, SqliteTransaction transaction, ModuleAdminSidConversion conversion) =>
        ModuleAdminRepository.ReplaceNameWithSid(connection, transaction, conversion);

    /// <summary><c>db identifier-status</c>. Read-only. Returns the exit code: 2 while any row is <c>legacy</c> or <c>grant-name</c>.</summary>
    internal static int Status(string dbPath, TextWriter stdout, TextWriter stderr)
    {
        var path = Path.GetFullPath(dbPath);
        if (!File.Exists(path))
            return Fail(stderr, [], $"Database not found: {path}");

        ProtectedPrincipalData data;
        Dictionary<string, string[]> admins;
        string? ignores;
        using (var connection = ConfigDbMaintenance.OpenReadOnly(path))
        {
            data = ProtectedPrincipalRepository.ReadRows(connection, null);
            admins = ModuleAdminRepository.ReadAll(connection, null);
            ignores = AppSettingRepository.ReadValue(connection, null, VerifyIgnoreList.SettingKey);
        }

        var lines = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["converted"] = 0,
            ["identifier"] = 0,
            ["legacy"] = 0,
            ["grant-sid"] = 0,
            ["grant-name"] = 0,
        };
        void Line(string state, string text)
        {
            counts[state]++;
            lines.Add($"{state} {text}");
        }

        foreach (var (legacyKind, idKind, legacy, ids) in Kinds(data))
        {
            var legacyValues = legacy.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            foreach (var id in ids.Where(v => !string.IsNullOrWhiteSpace(v)).Order(StringComparer.OrdinalIgnoreCase))
            {
                var label = ProtectedPrincipalIdEntry.Parse(id)?.Label ?? "";
                var paired = label.Length > 0 && legacyValues.Any(v => string.Equals(v.Trim(), label, StringComparison.OrdinalIgnoreCase));
                Line(paired ? "converted" : "identifier", $"{idKind}: {id}");
            }
            foreach (var value in ProtectedPrincipalIdEntry.Unpaired(legacyValues, ids).Order(StringComparer.OrdinalIgnoreCase))
                Line("legacy", $"{legacyKind}: {value}");
        }

        foreach (var (moduleId, grants) in admins.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var grant in grants.Order(StringComparer.OrdinalIgnoreCase))
                Line(SectionAccessGroupIdentity.IsUsableGroupSid(grant) ? "grant-sid" : "grant-name", $"{moduleId}: {grant}");
        }

        var summary = string.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key}"));
        var stillByName = counts["legacy"] + counts["grant-name"];
        if (stillByName > 0)
        {
            lines.Add(summary);
            lines.AddRange(IgnoreLines(ignores));
            return Fail(stderr, lines,
                $"{stillByName} {(stillByName == 1 ? "entry is" : "entries are")} still stored by name; run db convert-identifiers");
        }

        foreach (var line in lines)
            stdout.WriteLine(line);
        stdout.WriteLine(summary);
        foreach (var line in IgnoreLines(ignores))
            stdout.WriteLine(line);
        return ConfigDbCli.ExitOk;
    }

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.3: the Verify ignore list, read-only, at the end of the status report - one
    /// <c>ignored</c> line per entry and a count, or one line saying the stored list cannot be read (it then ignores
    /// nothing). Nothing when no list is stored or it is empty, so the report of a database without ignores is unchanged.
    /// </summary>
    private static IEnumerable<string> IgnoreLines(string? raw)
    {
        if (!VerifyIgnoreList.TryParse(raw, out var entries))
        {
            yield return $"verify ignores: the stored list ({VerifyIgnoreList.SettingKey}) cannot be read, so nothing is ignored";
            yield break;
        }

        if (entries.Count == 0)
            yield break;

        foreach (var e in entries.OrderBy(e => e.Store, StringComparer.Ordinal).ThenBy(e => e.RowKey, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.IdentifierOrText, StringComparer.OrdinalIgnoreCase))
            yield return $"ignored {e.Store} {e.RowKey}: {e.IdentifierOrText} - {e.Finding} ({e.Observed}) by {e.By} at {e.At}";
        yield return $"{entries.Count} verify {(entries.Count == 1 ? "ignore" : "ignores")}";
    }

    private static IEnumerable<(string LegacyKind, string IdKind, string[] Legacy, string[] Ids)> Kinds(ProtectedPrincipalData data)
    {
        yield return (ProtectedPrincipalRepository.KindUser, ProtectedPrincipalRepository.KindUserSid, data.Users, data.UserIds);
        yield return (ProtectedPrincipalRepository.KindGroup, ProtectedPrincipalRepository.KindGroupSid, data.Groups, data.GroupIds);
        yield return (ProtectedPrincipalRepository.KindOu, ProtectedPrincipalRepository.KindOuGuid, data.OrganizationalUnits, data.OuIds);
    }

    /// <summary>The rows with no identifier companion and the grants that are not usable SIDs, as the store holds them.</summary>
    private static Work ReadWork(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var data = ProtectedPrincipalRepository.ReadRows(connection, transaction);
        var rows = Kinds(data)
            .SelectMany(k => ProtectedPrincipalIdEntry.Unpaired(k.Legacy.Where(v => !string.IsNullOrWhiteSpace(v)), k.Ids)
                .Select(v => new ProtectedPrincipalLegacyRow(k.LegacyKind, v)))
            .ToList();

        var grants = ModuleAdminRepository.ReadAll(connection, transaction)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(kv => kv.Value
                .Where(g => !string.IsNullOrWhiteSpace(g) && !SectionAccessGroupIdentity.IsUsableGroupSid(g))
                .Select(g => (kv.Key, g)))
            .ToList();

        return new Work(rows, grants);
    }

    /// <summary>
    /// Every lookup, once, in one directory session as the invoking user. A grant value several modules hold is one lookup. A
    /// directory that cannot be opened at all leaves every row offending, "directory unavailable".
    /// </summary>
    private static (List<ProtectedPrincipalIdConversion> Rows, List<ModuleAdminSidConversion> Grants, List<string> Offending) Resolve(
        Work work, Func<IProtectedPrincipalResolveDirectory> openDirectory)
    {
        var distinctGrants = work.Grants.Select(g => g.Grant).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyList<ProtectedPrincipalRowResolution> rowResults;
        IReadOnlyList<ProtectedPrincipalRowResolution> grantResults;
        IProtectedPrincipalResolveDirectory? directory = null;
        try
        {
            directory = openDirectory();
            rowResults = ProtectedPrincipalIdentifierResolver.Resolve(work.Rows, directory, CancellationToken.None);
            grantResults = ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants(distinctGrants, directory, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var why = $"directory unavailable: {ex.Message}";
            rowResults = work.Rows.Select(r => ProtectedPrincipalRowResolution.Left(r, ProtectedPrincipalLeftReason.DirectoryUnavailable, why)).ToList();
            grantResults = distinctGrants.Select(g => ProtectedPrincipalRowResolution.Left(
                new ProtectedPrincipalLegacyRow(ProtectedPrincipalRepository.KindGroup, g), ProtectedPrincipalLeftReason.DirectoryUnavailable, why)).ToList();
        }
        finally
        {
            (directory as IDisposable)?.Dispose();
        }

        var offending = new List<string>();
        var rows = new List<ProtectedPrincipalIdConversion>();
        foreach (var r in rowResults)
        {
            if (r.Identifier is null)
                offending.Add($"{r.Row.Kind}: {r.Row.Value} - {r.Detail}");
            else
                rows.Add(new ProtectedPrincipalIdConversion(r.Row.Kind, r.Row.Value, r.IdKind, r.Identifier));
        }

        var byValue = new Dictionary<string, ProtectedPrincipalRowResolution>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in grantResults)
            byValue.TryAdd(r.Row.Value, r);

        var grants = new List<ModuleAdminSidConversion>();
        foreach (var (moduleId, grant) in work.Grants)
        {
            var r = byValue[grant];
            if (r.Identifier is null)
                offending.Add($"{ModuleAdminKind} {moduleId}: {grant} - {r.Detail}");
            else
                grants.Add(new ModuleAdminSidConversion(moduleId, grant, r.Identifier));
        }

        return (rows, grants, offending);
    }

    private static int Fail(TextWriter stderr, IReadOnlyList<string> lines, string error)
    {
        foreach (var line in lines)
            stderr.WriteLine(line);
        stderr.WriteLine($"error: {error}");
        return ConfigDbCli.ExitFailure;
    }
}
