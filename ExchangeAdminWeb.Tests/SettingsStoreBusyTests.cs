using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28v: since aud-a28r a protected-principals save with a changed Secret ID compares it under the settings store's
/// write lock before anything is written. A lock held elsewhere for longer than the store waits failed that compare, and
/// with it the whole save, as a generic "Failed to save" - nothing written, but nothing said about why or that a retry
/// would work.
/// </summary>
/// <remarks>
/// The compiled page through <see cref="SettingsPageHarness"/>, the lock held by a connection of its own on the harness's
/// database file - another writer, as the other instance or another admin's save would be. The store's wait is the real
/// one, about 35 seconds (the factory's busy timeout inside the provider's busy retry), so the first case is slow by
/// design. No directory: the one rule edit is a pattern, which is never looked up.
/// </remarks>
public sealed class SettingsStoreBusyTests : IDisposable
{
    private const string Cfo = "cfo@contoso.com";
    private const string SecretKey = ProtectedPrincipalService.DirectoryReadSecretConfigKey;

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
        try
        {
            await _h.RunAsync(page, "SaveProtectedPrincipals");
        }
        finally
        {
            Release(holder);
        }

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
}
