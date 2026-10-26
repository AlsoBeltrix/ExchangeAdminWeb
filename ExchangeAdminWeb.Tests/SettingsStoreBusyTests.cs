using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28v: since aud-a28r a protected-principals save with a changed Secret ID compares it under the settings store's
/// write lock before anything is written. A lock held elsewhere for longer than the store waits failed that compare, and
/// with it the whole save, as a generic "Failed to save" - nothing written, but nothing said about why or that a retry
/// would work. aud-a40: every other settings save said the same, and the protected-principals secret's write after the
/// rules said nothing of the store being busy.
/// </summary>
/// <remarks>
/// The compiled page through <see cref="SettingsPageHarness"/>, the lock held by a connection of its own on the harness's
/// database file - another writer, as the other instance or another admin's save would be. The store's wait is the real
/// one, <see cref="SqliteConnectionFactory.BusyTimeout"/> (5 seconds since aud-a41; about 35 before), so the first case is
/// slow by design. No directory: the one rule edit is a pattern, which is never looked up.
/// </remarks>
public sealed class SettingsStoreBusyTests : IDisposable
{
    private const string Cfo = "cfo@contoso.com";
    private const string SecretKey = ProtectedPrincipalService.DirectoryReadSecretConfigKey;
    private const string Module = "MailboxPermissions";
    private const string GroupA = "S-1-5-21-1000-2000-3000-4001";
    private const string GroupB = "S-1-5-21-1000-2000-3000-4002";
    private const string AppId = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static string Status(AdminSettingsPage page) =>
        SettingsPageHarness.Field<string?>(page, typeof(AdminSettingsPage), "statusMessage") ?? "";

    private void SeedRules(params string[] users) =>
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root)).Save(new ProtectedPrincipalData(users, [], [], [], []));

    private void SeedSecret(string value) =>
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(
            ProtectedPrincipalService.ProtectedPrincipalsModuleKey, new Dictionary<string, string> { [SecretKey] = value });

    private string? StoredSecret()
    {
        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root))
            .TryReadModule(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, out var values), "the secret is unreadable");
        return values.GetValueOrDefault(SecretKey);
    }

    private List<string> StoredRules() =>
        TestConfigStore.Create(_h.Root).Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind || ':' || value FROM protected_principal ORDER BY 1;";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add(reader.GetString(0));
            return rows;
        });

    private async Task AddPattern(AdminSettingsPage page, string pattern)
    {
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppNewPattern", pattern);
        await _h.RunAsync(page, "AddPpPattern");
    }

    private static void EnterSecret(AdminSettingsPage page, string value) =>
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "ppDirectoryReadSecretId", value);

    // Another writer: its own connection on the same file, unpooled, holding the write lock until released.
    private SqliteConnection HoldWriteLock()
    {
        var holder = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_h.Root, "exchangeadmin.db"),
            Pooling = false,
        }.ToString());
        holder.Open();
        using var begin = holder.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();
        return holder;
    }

    private static void Release(SqliteConnection holder)
    {
        using (var rollback = holder.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        holder.Dispose();
    }

    [Fact]
    public async Task AdminSettings_ASecretIdCompareThatFindsTheStoreBusy_IsRefused_SaysSo_WritesNothing_AndARetrySaves()
    {
        SeedRules(Cfo);
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        await AddPattern(page, "svc-*");
        EnterSecret(page, "4242");
        var dirty = SettingsPageHarness.Field<AdminPageDirtyState>(page, typeof(AdminSettingsPage), "dirty");

        var holder = HoldWriteLock();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _h.RunAsync(page, "SaveProtectedPrincipals");
        }
        finally
        {
            waited.Stop();
            Release(holder);
        }

        // aud-a41: the documented wait, once - not the provider's 30-second retry around it.
        // aud-a67 item 7: a ceiling that fails that retry loop and nothing else. One second under two busy waits (9 s) is
        // a timing, not a signal, and a loaded host has crossed it with nothing wrong; the wait's shape is pinned
        // structurally by SqliteBusyWaitTests instead.
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(25),
            $"the save waited {waited.Elapsed.TotalSeconds:F2} s for the busy store: the provider's retry loop, not one busy wait");
        Assert.Equal("777", StoredSecret());
        Assert.Equal(new[] { "user:" + Cfo }, StoredRules());
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal("ProtectedPrincipals", Text(record, "section"));
        Assert.Equal("StoreBusy", Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(Status(page), Text(record, "error"));
        Assert.Contains("the settings store was busy", Status(page), StringComparison.Ordinal);
        Assert.Contains("Nothing was written", Status(page), StringComparison.Ordinal);
        Assert.Contains("Try again", Status(page), StringComparison.Ordinal);
        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "statusIsError"));
        Assert.True(dirty.IsDirty);

        // What the message promises: with the store free again, the same page saves - rules and secret, each recorded.
        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal("4242", StoredSecret());
        Assert.Equal(new[] { "sam_pattern:svc-*", "user:" + Cfo }, StoredRules());
        var records = _h.AuditEvents();
        Assert.Equal(3, records.Count);
        Assert.All(records.Skip(1), r => Assert.Equal("Success", Text(r, "result")));
        Assert.False(dirty.IsDirty);
    }

    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(5, 517, true)]
    [InlineData(261, 261, true)]
    [InlineData(6, 6, false)]
    [InlineData(11, 11, false)]
    [InlineData(1, 1, false)]
    public void IsBusy_IsTheBusyResultCode_AndNoOther(int code, int extendedCode, bool busy)
    {
        // 5 is SQLITE_BUSY - a held write lock produced code 5 with extended code 5 - and 517 and 261 are its extended
        // forms (BUSY_SNAPSHOT, BUSY_RECOVERY), whose low byte is the primary code. 6 SQLITE_LOCKED, 11 SQLITE_CORRUPT and
        // 1 SQLITE_ERROR are not another writer holding the lock: a corrupt store must not be told to try again.
        var exception = new SqliteException($"SQLite Error {code}.", code, extendedCode);

        Assert.Equal(busy, ConfigStoreBusy.IsBusy(exception));
    }

    [Fact]
    public void IsBusy_ReadsTheResultCode_NotTheMessage() =>
        Assert.False(ConfigStoreBusy.IsBusy(new InvalidOperationException("SQLite Error 5: 'database is locked'.")));

    // ---- aud-a40: every settings save -----------------------------------------------------------

    // The store's own busy failure, as aud-a28v measured it from a held lock: code 5, extended 5, no inner exception.
    private static SqliteException Busy() => new("SQLite Error 5: 'database is locked'.", 5, 5);

    private static string StatusOf<TPage>(TPage page) =>
        SettingsPageHarness.Field<string?>(page!, typeof(TPage), "statusMessage") ?? "";

    private static string[] Names(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray() : [];

    /// <summary>Exactly one record: a refusal at a busy store under <paramref name="section"/>, its error what the page shows.</summary>
    private JsonElement AssertOneBusyRefusal(string section, string shown)
    {
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(section, Text(record, "section"));
        Assert.Equal(SettingsSaveAudit.StoreBusy, Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(shown, Text(record, "error"));
        return record;
    }

    private static void AssertSaysBusy(string shown)
    {
        Assert.Contains("the settings store was busy", shown, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", shown, StringComparison.Ordinal);
        Assert.Contains("Try again", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLite Error", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminSettings_ARulesWriteThatFindsTheStoreBusy_IsRefused_SaysSo_WritesNothing_AndARetrySaves()
    {
        // The Secret ID unchanged, so no compare: the first write is the rules' own, ReplaceConfig.
        SeedRules(Cfo);
        var page = _h.OpenAdminSettings();
        await AddPattern(page, "svc-*");
        var dirty = SettingsPageHarness.Field<AdminPageDirtyState>(page, typeof(AdminSettingsPage), "dirty");

        var holder = HoldWriteLock();
        try
        {
            await _h.RunAsync(page, "SaveProtectedPrincipals");
        }
        finally
        {
            Release(holder);
        }

        Assert.Equal(new[] { "user:" + Cfo }, StoredRules());
        AssertOneBusyRefusal("ProtectedPrincipals", Status(page));
        AssertSaysBusy(Status(page));
        Assert.True(dirty.IsDirty);

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "sam_pattern:svc-*", "user:" + Cfo }, StoredRules());
        Assert.Equal("Success", Text(_h.AuditEvents()[^1], "result"));
        Assert.False(dirty.IsDirty);
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AConnectionSaveThatFindsTheStoreBusy_IsRefused_SaysSo_WritesNothing_AndARetrySaves()
    {
        var page = _h.OpenExchangeOnlineConfig();
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoAppId", AppId);
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoOrganization", "contoso.onmicrosoft.com");
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "exoCertSubject", "CN=EXO-Automation");
        var before = _h.StoredModuleConfig("ExchangeOnline");

        var holder = HoldWriteLock();
        try
        {
            await _h.RunAsync(page, "SaveExoConfig");
        }
        finally
        {
            Release(holder);
        }

        Assert.Equal(before, _h.StoredModuleConfig("ExchangeOnline"));
        AssertOneBusyRefusal("ExchangeOnlineConfig", StatusOf(page));
        AssertSaysBusy(StatusOf(page));

        await _h.RunAsync(page, "SaveExoConfig");

        Assert.Contains("contoso.onmicrosoft.com", _h.StoredModuleConfig("ExchangeOnline"), StringComparison.Ordinal);
        Assert.Equal("Success", Text(_h.AuditEvents()[^1], "result"));
    }

    [Fact]
    public async Task ModuleConfig_AConfigSaveThatFindsTheStoreBusy_IsRefused_SaysSo_WritesNothing_AndARetrySaves()
    {
        var page = _h.OpenModuleConfig(Module);
        await _h.RunAsync(page, "SetConfigValue", "DelineaSecretId", "4242");
        var before = _h.StoredModuleConfig(Module);
        var dirty = SettingsPageHarness.Field<AdminPageDirtyState>(page, typeof(ModuleConfigPage), "dirty");

        var holder = HoldWriteLock();
        try
        {
            await _h.RunAsync(page, "SaveConfigAsync");
        }
        finally
        {
            Release(holder);
        }

        Assert.Equal(before, _h.StoredModuleConfig(Module));
        AssertOneBusyRefusal($"ModuleConfig:{Module}", StatusOf(page));
        AssertSaysBusy(StatusOf(page));
        Assert.Equal(1, dirty.SectionCount("Configuration"));

        await _h.RunAsync(page, "SaveConfigAsync");

        Assert.Contains("4242", _h.StoredModuleConfig(Module), StringComparison.Ordinal);
        Assert.Equal("Success", Text(_h.AuditEvents()[^1], "result"));
        Assert.Equal(0, dirty.SectionCount("Configuration"));
    }

    [Fact]
    public async Task AdminSettings_AnEnablementSaveThatFindsTheStoreBusy_IsRefusedAsBusy_WritesNothing_AndDoesNotReload()
    {
        var page = _h.OpenAdminSettings();
        var map = SettingsPageHarness.Field<Dictionary<string, bool>>(page, typeof(AdminSettingsPage), "moduleEnablement");
        map[Module] = !map[Module];
        var before = _h.StoredEnablement();
        _h.EnablementStore.BeforeWrite = () => throw Busy();

        await _h.RunAsync(page, "SaveEnablement");

        Assert.Equal(before, _h.StoredEnablement());
        var record = AssertOneBusyRefusal("ModuleEnablement", Status(page));
        Assert.Equal(new[] { $"Module:{Module}" }, Names(record, "sections"));
        AssertSaysBusy(Status(page));
        Assert.Empty(_h.Navigation.Navigations);
    }

    [Fact]
    public async Task AdminSettings_ALogLevelSaveThatFindsTheStoreBusy_IsRefusedAsBusy_AndSaysTheLevelInEffect()
    {
        var page = _h.OpenAdminSettings();
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), "selectedLogLevel", "Warning");
        var before = _h.StoredLogLevel();
        _h.LogLevelStore.BeforeWrite = () => throw Busy();

        await _h.RunAsync(page, "SaveLogLevel");

        Assert.Equal(before, _h.StoredLogLevel());
        AssertOneBusyRefusal("ExtendedLogLevel", Status(page));
        AssertSaysBusy(Status(page));
        Assert.Contains($"the extended log level is still {_h.ExtendedLog.CurrentLevel}", Status(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminSettings_ASecretWriteThatFindsTheStoreBusyAfterTheRulesSaved_IsTheSecretsRefusal_AndSaysTheRulesWereSaved()
    {
        SeedRules(Cfo);
        SeedSecret("777");
        var page = _h.OpenAdminSettings();
        await AddPattern(page, "svc-*");
        await _h.RunAsync(page, "SetPpDirectoryReadSecretId", "4242");
        var dirty = SettingsPageHarness.Field<AdminPageDirtyState>(page, typeof(AdminSettingsPage), "dirty");

        // The compare ahead of the rules is the secret store's first write; armed as the rules' write opens, the busy store
        // meets the next one - the secret's own write, after the rules saved.
        _h.ProtectedStore.BeforeWrite = () => { _h.ModuleConfigStore.BeforeWrite = () => throw Busy(); };

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal(new[] { "sam_pattern:svc-*", "user:" + Cfo }, StoredRules());
        Assert.Equal("777", StoredSecret());
        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        Assert.Equal("Success", Text(records[0], "result"));
        Assert.Equal("ProtectedPrincipals", Text(records[0], "section"));
        Assert.Equal("Failed", Text(records[1], "result"));
        Assert.Equal("ProtectedPrincipals:DirectoryReadSecretId", Text(records[1], "section"));
        Assert.Equal(SettingsSaveAudit.StoreBusy, Text(records[1], "refusedAt"));
        Assert.False(records[1].GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(Status(page), Text(records[1], "error"));
        Assert.Contains("The protection rules were saved and are in effect", Status(page), StringComparison.Ordinal);
        Assert.Contains("the settings store was busy", Status(page), StringComparison.Ordinal);
        Assert.Contains("Save again", Status(page), StringComparison.Ordinal);
        Assert.Equal(1, dirty.SectionCount("Protected Principals"));
    }

    [Fact]
    public async Task ExchangeOnlineConfig_AStatusSaveThatFindsTheStoreBusy_IsRefusedAsBusy_AndWritesNothing()
    {
        var page = _h.OpenExchangeOnlineConfig();
        var enabled = SettingsPageHarness.Field<bool>(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled");
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), "moduleEnabled", !enabled);
        var before = _h.StoredEnablement();
        _h.EnablementStore.BeforeWrite = () => throw Busy();

        await _h.RunAsync(page, "SaveModuleStatus");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOneBusyRefusal("Module:ExchangeOnline", StatusOf(page));
        AssertSaysBusy(StatusOf(page));
    }

    [Fact]
    public async Task ModuleConfig_AStatusSaveThatFindsTheStoreBusy_IsRefusedAsBusy_WritesNothing_AndDoesNotReload()
    {
        var page = _h.OpenModuleConfig(Module);
        var enabled = SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "moduleEnabled");
        await _h.RunAsync(page, "ToggleModuleEnabled", !enabled);
        var before = _h.StoredEnablement();
        _h.EnablementStore.BeforeWrite = () => throw Busy();

        await _h.RunAsync(page, "SaveModuleEnablementAsync");

        Assert.Equal(before, _h.StoredEnablement());
        AssertOneBusyRefusal($"Module:{Module}", StatusOf(page));
        AssertSaysBusy(StatusOf(page));
        Assert.Empty(_h.Navigation.Navigations);
    }

    [Fact]
    public async Task ModuleConfig_AnAccessSaveThatFindsTheStoreBusy_IsRefusedAsBusy_NamingWhatItWouldChange()
    {
        new SectionAccessRepository(TestConfigStore.Create(_h.Root))
            .SaveAll(new Dictionary<string, string[]> { [Module] = [GroupA, GroupB] });
        var page = _h.OpenModuleConfig(Module);
        await _h.RunAsync(page, "RemoveAccessGroup", Module, GroupB);
        var before = _h.StoredSectionAccess();
        _h.SectionStore.BeforeWrite = () => throw Busy();

        await _h.RunAsync(page, "SaveSectionAccessAsync");

        Assert.Equal(before, _h.StoredSectionAccess());
        var record = AssertOneBusyRefusal(Module, StatusOf(page));
        Assert.Equal(new[] { Module }, Names(record, "sections"));
        AssertSaysBusy(StatusOf(page));
    }

    [Fact]
    public async Task ModuleConfig_AnAdminsSaveThatFindsTheStoreBusy_IsRefusedAsBusy_AndWritesNothing()
    {
        new ModuleAdminRepository(TestConfigStore.Create(_h.Root)).SetForModule(Module, [GroupA, GroupB]);
        var page = _h.OpenModuleConfig(Module);
        await _h.RunAsync(page, "RemoveModuleAdmin", GroupB);
        var before = _h.StoredModuleAdmins(Module);
        _h.AdminsStore.BeforeWrite = () => throw Busy();

        await _h.RunAsync(page, "SaveModuleAdminsAsync");

        Assert.Equal(before, _h.StoredModuleAdmins(Module));
        AssertOneBusyRefusal($"ModuleAdmins:{Module}", StatusOf(page));
        AssertSaysBusy(StatusOf(page));
    }

    /// <summary>
    /// aud-a67 item 6, the follow-up aud-a40 left in this service's file: the attribute allowlist save turned any
    /// exception, a busy store included, into "Failed to save: SQLite Error 5: 'database is locked'." and a failure
    /// record. Nothing is written in that case - every decision is made inside the one write a throw rolls back - so it
    /// is the same refusal every other settings save makes.
    /// </summary>
    [Fact]
    public async Task ModuleConfig_AnAllowlistSaveThatFindsTheStoreBusy_IsRefusedAsBusy_AndWritesNothing()
    {
        var page = _h.OpenModuleConfig("ADAttributeEditor", _h.CreateAttributeEditor());
        await _h.RunAsync(page, "AddAttribute");
        await _h.RunAsync(page, "UpdateAttrName", 0, new ChangeEventArgs { Value = "department" });
        await _h.RunAsync(page, "UpdateAttrLabel", 0, new ChangeEventArgs { Value = "Department" });
        var before = new AttributeEditorRepository(TestConfigStore.Create(_h.Root)).ReadAllowlistSnapshot().Rows.Count;
        _h.AllowlistStore.BeforeWrite = () => throw Busy();

        await _h.RunAsync(page, "SaveAllowlistAsync");

        var shown = SettingsPageHarness.Field<string?>(page, typeof(ModuleConfigPage), "allowlistError") ?? "";
        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "allowlistSaved"));
        Assert.Equal(before, new AttributeEditorRepository(TestConfigStore.Create(_h.Root)).ReadAllowlistSnapshot().Rows.Count);
        AssertOneBusyRefusal(ADAttributeEditorService.AllowlistAuditSection, shown);
        AssertSaysBusy(shown);
    }

    [Fact]
    public void LogSettingsSaveException_RecordsABusyStoreAsARefusal_AndAnythingElseAsTheFailureItWasGiven()
    {
        var busy = _h.Audit.LogSettingsSaveException(
            NullLogger.Instance, _h.AdminName, SettingsPageHarness.Ip, "Section:A", Busy(), "Failed to save: A.", ["Module:A"]);
        var custom = _h.Audit.LogSettingsSaveException(
            NullLogger.Instance, _h.AdminName, SettingsPageHarness.Ip, "Section:B", Busy(), "Failed to save: B.", busy: "B was busy.");
        var failed = _h.Audit.LogSettingsSaveException(
            NullLogger.Instance, _h.AdminName, SettingsPageHarness.Ip, "Section:C", new SqliteException("SQLite Error 11.", 11, 11), "Failed to save: C.");

        Assert.Equal(SettingsSaveAudit.StoreBusyMessage(), busy);
        Assert.Equal("B was busy.", custom);
        Assert.Equal("Failed to save: C.", failed);

        var records = _h.AuditEvents();
        Assert.Equal(3, records.Count);
        Assert.Equal(SettingsSaveAudit.StoreBusy, Text(records[0], "refusedAt"));
        Assert.False(records[0].GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(busy, Text(records[0], "error"));
        Assert.Equal(new[] { "Module:A" }, Names(records[0], "sections"));
        Assert.Equal("B was busy.", Text(records[1], "error"));
        Assert.Equal("Failed", Text(records[2], "result"));
        Assert.False(records[2].TryGetProperty("refusedAt", out _));
        Assert.False(records[2].TryGetProperty("mutationAttempted", out _));
        Assert.Equal(failed, Text(records[2], "error"));
    }
}
