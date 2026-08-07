using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Tests for BitLockerRecoveryService.
/// </summary>
/// <remarks>
/// These build a real SQLite archive in a temp file and query it, because the
/// behaviour worth testing here is the fail-closed handling around a real
/// database, not a mock's idea of one. Live AD is represented by a test fake so
/// production code never depends on host stubs.
/// </remarks>
public sealed class BitLockerRecoveryTests : IDisposable
{
    private const string ModuleId = "BitLockerRecovery";

    private readonly string _archivePath =
        Path.Combine(Path.GetTempPath(), $"bl-test-{Guid.NewGuid():N}.db");
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), $"bl-config-{Guid.NewGuid():N}");

    private const string Schema = """
        CREATE TABLE recovery_keys (
            short_computer_name TEXT NOT NULL COLLATE NOCASE,
            recovery_password   TEXT NOT NULL,
            key_guid            TEXT,
            volume_guid         TEXT,
            created_utc         TEXT,
            computer_dn         TEXT,
            first_seen_source   TEXT NOT NULL,
            first_seen_utc      TEXT NOT NULL,
            last_seen_utc       TEXT NOT NULL,
            last_seen_in_ad_utc TEXT,
            PRIMARY KEY (short_computer_name, recovery_password)
        );
        """;

    private void CreateArchive(params (string Machine, string Password, string? KeyId, string? SeenInAd)[] rows)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _archivePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = Schema;
            schema.ExecuteNonQuery();
        }

        foreach (var (machine, password, keyId, seenInAd) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO recovery_keys (
                    short_computer_name, recovery_password, key_guid, created_utc,
                    first_seen_source, first_seen_utc, last_seen_utc, last_seen_in_ad_utc)
                VALUES ($m, $p, $k, '2024-01-01T00:00:00.0000000Z',
                    'Export', '2024-01-01T00:00:00.0000000Z', '2024-01-01T00:00:00.0000000Z', $ad);
                """;
            insert.Parameters.AddWithValue("$m", machine);
            insert.Parameters.AddWithValue("$p", password);
            insert.Parameters.AddWithValue("$k", (object?)keyId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$ad", (object?)seenInAd ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
    }

    private static BitLockerRecoveryService CreateService(
        ModuleConfigService moduleConfig,
        FakeLiveDirectorySearch? liveDirectory = null) =>
        new(
            moduleConfig,
            liveDirectory ?? new FakeLiveDirectorySearch(),
            NullLogger<BitLockerRecoveryService>.Instance);

    private ModuleConfigService CreateArchiveConfig(
        string archivePath,
        params (string Key, string Value)[] extraValues)
    {
        var values = extraValues.ToDictionary(item => item.Key, item => item.Value);
        values["ArchiveDatabasePath"] = archivePath;

        return CreateModuleConfig(values);
    }

    private ModuleConfigService CreateModuleConfig(
        IDictionary<string, string>? values = null,
        bool corruptStore = false)
    {
        Directory.CreateDirectory(_configDir);

        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.ContentRootPath.Returns(_configDir);

        var repository = corruptStore
            ? new ModuleConfigRepository(new ThrowingConfigStore())
            : TestConfigStore.CreateModuleConfig(_configDir);
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(),
            env,
            repository,
            NullLogger<ModuleConfigService>.Instance);

        if (!corruptStore && values is { Count: > 0 })
        {
            moduleConfig.SaveModuleConfig(ModuleId, new Dictionary<string, string>(values));
        }

        return moduleConfig;
    }

    private static BitLockerRecoveryKey LiveKey(
        string machine,
        string password,
        string? keyId = null) => new()
        {
            RowId = 0,
            ComputerName = machine,
            RecoveryPassword = password,
            KeyId = keyId,
            VolumeId = null,
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            FirstSeenSource = "ActiveDirectory",
            LastSeenInAdUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            ResultSource = BitLockerRecoveryKeySource.ActiveDirectory,
        };

    private sealed class FakeLiveDirectorySearch : IBitLockerLiveDirectorySearch
    {
        public BitLockerLiveDirectorySearchResult NameResult { get; set; } =
            BitLockerLiveDirectorySearchResult.Ok([], truncated: false);

        public BitLockerLiveDirectorySearchResult KeyIdResult { get; set; } =
            BitLockerLiveDirectorySearchResult.Ok([], truncated: false);

        public string? LastComputerName { get; private set; }
        public string? LastKeyId { get; private set; }
        public BitLockerRecoveryIdentifier? LastIdentifier { get; private set; }
        public int LastLimit { get; private set; }
        public int ComputerNameSearchCount { get; private set; }
        public int KeyIdSearchCount { get; private set; }

        public Task<BitLockerLiveDirectorySearchResult> SearchByComputerNameAsync(
            string computerName,
            int limit)
        {
            ComputerNameSearchCount++;
            LastComputerName = computerName;
            LastLimit = limit;
            return Task.FromResult(NameResult);
        }

        public Task<BitLockerLiveDirectorySearchResult> SearchByRecoveryIdentifierAsync(
            BitLockerRecoveryIdentifier identifier,
            int limit)
        {
            KeyIdSearchCount++;
            LastIdentifier = identifier;
            LastKeyId = identifier.Value;
            LastLimit = limit;
            return Task.FromResult(KeyIdResult);
        }
    }

    private sealed class ThrowingConfigStore : IConfigStore
    {
        public long GetChangeToken() => throw new InvalidOperationException("store unreadable");

        public T Read<T>(Func<SqliteConnection, T> read) =>
            throw new InvalidOperationException("store unreadable");

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) =>
            throw new InvalidOperationException("store unreadable");

        public void Write(Action<SqliteConnection, SqliteTransaction> write) =>
            throw new InvalidOperationException("store unreadable");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _archivePath, $"{_archivePath}-wal", $"{_archivePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(_configDir))
        {
            try
            {
                Directory.Delete(_configDir, recursive: true);
            }
            catch
            {
                // The host config store can briefly keep SQLite handles alive after a test.
            }
        }
    }

    [Fact]
    public async Task Finds_key_by_computer_name_fragment()
    {
        CreateArchive(("ASHLAP12345", "111111-AAAAAA", null, "2024-06-01T00:00:00.0000000Z"));
        var service = CreateService(CreateArchiveConfig(_archivePath));

        var result = await service.SearchByComputerNameAsync("LAP");

        Assert.True(result.Success);
        Assert.Single(result.Keys);
        Assert.Equal("ASHLAP12345", result.Keys[0].ComputerName);
        Assert.Equal(BitLockerRecoveryKeySource.Archive, result.Keys[0].ResultSource);
    }

    [Fact]
    public async Task Defaults_to_archive_only_by_computer_name()
    {
        CreateArchive(("PC1", "111111-AAAAAA", null, "2024-06-01T00:00:00.0000000Z"));
        var live = new FakeLiveDirectorySearch
        {
            NameResult = BitLockerLiveDirectorySearchResult.Ok(
                [LiveKey("NEWPC", "222222-BBBBBB")],
                truncated: false),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByComputerNameAsync("PC1");

        Assert.True(result.Success);
        Assert.Equal(BitLockerRecoveryKeySource.Archive, Assert.Single(result.Keys).ResultSource);
        Assert.Equal(0, live.ComputerNameSearchCount);
    }

    [Fact]
    public async Task Defaults_to_archive_only_by_key_id()
    {
        CreateArchive(("OLDNAME", "111111-AAAAAA", "7a159302-48bb-435e-9a81-3f1aef9a7a40", null));
        var live = new FakeLiveDirectorySearch
        {
            KeyIdResult = BitLockerLiveDirectorySearchResult.Ok(
                [LiveKey("LIVEPC", "222222-BBBBBB", "7a159302-48bb-435e-9a81-3f1aef9a7a40")],
                truncated: false),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByKeyIdAsync("7A159302");

        Assert.True(result.Success);
        Assert.Equal("OLDNAME", Assert.Single(result.Keys).ComputerName);
        Assert.Equal(0, live.KeyIdSearchCount);
    }

    [Fact]
    public async Task Returns_live_ad_results()
    {
        CreateArchive();
        var live = new FakeLiveDirectorySearch
        {
            NameResult = BitLockerLiveDirectorySearchResult.Ok(
                [LiveKey("NEWPC", "111111-AAAAAA", "7a159302-48bb-435e-9a81-3f1aef9a7a40")],
                truncated: false),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByComputerNameAsync("NEWPC", includeLiveAd: true);

        var key = Assert.Single(result.Keys);
        Assert.True(key.FoundInLiveDirectory);
        Assert.Equal("Live AD", key.StatusLabel);
    }

    [Fact]
    public async Task Prefers_live_ad_duplicate_over_archive_duplicate()
    {
        CreateArchive(("PC1", "111111-AAAAAA", null, "2024-01-01T00:00:00.0000000Z"));
        var live = new FakeLiveDirectorySearch
        {
            NameResult = BitLockerLiveDirectorySearchResult.Ok(
                [LiveKey("PC1", "111111-AAAAAA", "7a159302-48bb-435e-9a81-3f1aef9a7a40")],
                truncated: false),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByComputerNameAsync("PC1", includeLiveAd: true);

        var key = Assert.Single(result.Keys);
        Assert.True(key.FoundInLiveDirectory);
        Assert.Equal("7a159302-48bb-435e-9a81-3f1aef9a7a40", key.KeyId);
    }

    [Fact]
    public async Task Finds_renamed_machine_by_key_id()
    {
        // The key ID is the only identifier that survives a rename.
        CreateArchive(("OLDNAME", "111111-AAAAAA", "7a159302-48bb-435e-9a81-3f1aef9a7a40", null));
        var service = CreateService(CreateArchiveConfig(_archivePath));

        var result = await service.SearchByKeyIdAsync("7A159302-48BB-435E-9A81-3F1AEF9A7A40");

        Assert.True(result.Success);
        Assert.Equal("OLDNAME", Assert.Single(result.Keys).ComputerName);
    }

    [Fact]
    public async Task Finds_renamed_machine_by_short_key_id()
    {
        CreateArchive(("OLDNAME", "111111-AAAAAA", "7a159302-48bb-435e-9a81-3f1aef9a7a40", null));
        var service = CreateService(CreateArchiveConfig(_archivePath));

        var result = await service.SearchByKeyIdAsync("Recovery key ID: 7A159302");

        Assert.True(result.Success);
        Assert.Equal("OLDNAME", Assert.Single(result.Keys).ComputerName);
    }

    [Fact]
    public async Task Finds_machine_by_pasted_recovery_password()
    {
        var password = "111111-222222-333333-444444-555555-666666-777777-888888";
        CreateArchive(("LOCKEDPC", password, null, null));
        var service = CreateService(CreateArchiveConfig(_archivePath));

        var result = await service.SearchByKeyIdAsync($"Recovery key: {password}");

        Assert.True(result.Success);
        Assert.Equal("LOCKEDPC", Assert.Single(result.Keys).ComputerName);
    }

    [Fact]
    public async Task Searches_live_ad_by_normalised_key_id()
    {
        CreateArchive();
        var live = new FakeLiveDirectorySearch
        {
            KeyIdResult = BitLockerLiveDirectorySearchResult.Ok(
                [LiveKey("LIVEPC", "111111-AAAAAA", "7a159302-48bb-435e-9a81-3f1aef9a7a40")],
                truncated: false),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByKeyIdAsync(
            "{7A159302-48BB-435E-9A81-3F1AEF9A7A40}",
            includeLiveAd: true);

        Assert.Equal("7a159302-48bb-435e-9a81-3f1aef9a7a40", live.LastKeyId);
        Assert.Equal(BitLockerRecoveryIdentifierKind.KeyIdPrefix, live.LastIdentifier?.Kind);
        Assert.True(Assert.Single(result.Keys).FoundInLiveDirectory);
    }

    [Fact]
    public async Task Searches_live_ad_by_short_key_id()
    {
        CreateArchive();
        var live = new FakeLiveDirectorySearch
        {
            KeyIdResult = BitLockerLiveDirectorySearchResult.Ok(
                [LiveKey("LIVEPC", "111111-AAAAAA", "7a159302-48bb-435e-9a81-3f1aef9a7a40")],
                truncated: false),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByKeyIdAsync("Recovery key ID: 7A159302", includeLiveAd: true);

        Assert.Equal("7a159302", live.LastKeyId);
        Assert.Equal(BitLockerRecoveryIdentifierKind.KeyIdPrefix, live.LastIdentifier?.Kind);
        Assert.True(Assert.Single(result.Keys).FoundInLiveDirectory);
    }

    [Fact]
    public async Task Accepts_a_key_id_pasted_with_braces()
    {
        CreateArchive(("PC1", "111111-AAAAAA", "7a159302-48bb-435e-9a81-3f1aef9a7a40", null));
        var service = CreateService(CreateArchiveConfig(_archivePath));

        var result = await service.SearchByKeyIdAsync("{7A159302-48BB-435E-9A81-3F1AEF9A7A40}");

        Assert.Single(result.Keys);
    }

    [Fact]
    public async Task Distinguishes_a_key_never_seen_in_the_directory()
    {
        CreateArchive(
            ("LIVEPC", "111111-AAAAAA", null, "2024-06-01T00:00:00.0000000Z"),
            ("DEADPC", "222222-BBBBBB", null, null));
        var service = CreateService(CreateArchiveConfig(_archivePath));

        var live = await service.SearchByComputerNameAsync("LIVEPC");
        var dead = await service.SearchByComputerNameAsync("DEADPC");

        Assert.True(live.Keys[0].EverSeenInDirectory);
        Assert.False(dead.Keys[0].EverSeenInDirectory);
    }

    [Fact]
    public async Task Returns_archive_rows_with_warning_when_requested_live_ad_credentials_are_missing()
    {
        CreateArchive(("PC1", "111111-AAAAAA", null, null));
        var live = new FakeLiveDirectorySearch
        {
            NameResult = BitLockerLiveDirectorySearchResult.Fail(
                "BitLocker Recovery AD credentials are not configured or unavailable."),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByComputerNameAsync("PC1", includeLiveAd: true);

        Assert.True(result.Success);
        Assert.Single(result.Keys);
        Assert.True(result.IsPartial);
        Assert.Contains(result.Warnings, warning => warning.Contains("AD credentials", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Default_archive_search_ignores_live_ad_credential_failures()
    {
        CreateArchive(("PC1", "111111-AAAAAA", null, null));
        var live = new FakeLiveDirectorySearch
        {
            NameResult = BitLockerLiveDirectorySearchResult.Fail(
                "BitLocker Recovery AD credentials are not configured or unavailable."),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByComputerNameAsync("PC1");

        Assert.True(result.Success);
        Assert.Single(result.Keys);
        Assert.Equal(0, live.ComputerNameSearchCount);
    }

    [Fact]
    public async Task Returns_archive_miss_with_warning_when_requested_live_ad_fails()
    {
        CreateArchive();
        var live = new FakeLiveDirectorySearch
        {
            KeyIdResult = BitLockerLiveDirectorySearchResult.Fail(
                "Active Directory recovery keys could not be searched."),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByKeyIdAsync("7A159302", includeLiveAd: true);

        Assert.True(result.Success);
        Assert.Empty(result.Keys);
        Assert.True(result.IsPartial);
        Assert.Contains(result.Warnings, warning => warning.Contains("Active Directory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Returns_archive_rows_with_warning_when_requested_live_ad_passwords_are_unreadable()
    {
        CreateArchive(("PC1", "111111-AAAAAA", null, null));
        var live = new FakeLiveDirectorySearch
        {
            NameResult = BitLockerLiveDirectorySearchResult.Fail(
                "Active Directory returned BitLocker recovery objects, but the configured account could not read recovery passwords."),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByComputerNameAsync("PC1", includeLiveAd: true);

        Assert.True(result.Success);
        Assert.Single(result.Keys);
        Assert.True(result.IsPartial);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("could not read recovery passwords", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preserves_successful_live_ad_warnings_after_merging_archive_results()
    {
        CreateArchive(("PC1", "111111-AAAAAA", null, null));
        var live = new FakeLiveDirectorySearch
        {
            NameResult = BitLockerLiveDirectorySearchResult.Ok(
                [LiveKey("PC2", "222222-BBBBBB", null)],
                truncated: false,
                warnings:
                [
                    "Active Directory returned some BitLocker recovery objects without readable recovery passwords.",
                ]),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByComputerNameAsync("PC", includeLiveAd: true);

        Assert.True(result.Success);
        Assert.Equal(2, result.Keys.Count);
        Assert.True(result.IsPartial);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("without readable recovery passwords", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fails_closed_when_the_archive_path_is_not_configured()
    {
        var service = CreateService(CreateModuleConfig());

        var result = await service.SearchByComputerNameAsync("ANY");

        Assert.False(result.Success);
        Assert.Contains("not configured", result.Error);
        Assert.Empty(result.Keys);
    }

    [Fact]
    public async Task Fails_closed_when_the_archive_file_is_missing()
    {
        // An unreachable archive must not look like a machine with no key.
        var service = CreateService(
            CreateArchiveConfig(Path.Combine(Path.GetTempPath(), "definitely-absent.db")));

        var result = await service.SearchByComputerNameAsync("ANY");

        Assert.False(result.Success);
        Assert.Contains("not reachable", result.Error);
        Assert.DoesNotContain("definitely-absent.db", result.Error);
    }

    [Fact]
    public async Task Fails_closed_when_the_module_config_is_corrupt()
    {
        CreateArchive(("PC1", "111111-AAAAAA", null, null));
        var service = CreateService(CreateModuleConfig(corruptStore: true));

        var result = await service.SearchByComputerNameAsync("PC1");

        Assert.False(result.Success);
        Assert.Empty(result.Keys);
    }

    [Fact]
    public async Task Gives_every_result_row_a_distinct_id()
    {
        // Reveal state is keyed on this. Historical rows can lack key ID and
        // share created timestamps, so keying on those would let one reveal
        // disclose several keys.
        CreateArchive(
            ("SAMEPC", "111111-AAAAAA", null, null),
            ("SAMEPC", "222222-BBBBBB", null, null));
        var live = new FakeLiveDirectorySearch
        {
            NameResult = BitLockerLiveDirectorySearchResult.Ok(
                [LiveKey("SAMEPC", "333333-CCCCCC", null)],
                truncated: false),
        };
        var service = CreateService(CreateArchiveConfig(_archivePath), live);

        var result = await service.SearchByComputerNameAsync("SAMEPC", includeLiveAd: true);

        Assert.Equal(3, result.Keys.Count);
        Assert.Equal(3, result.Keys.Select(k => k.RowId).Distinct().Count());
    }

    [Fact]
    public async Task Gives_archive_only_result_rows_distinct_ids()
    {
        CreateArchive(
            ("SAMEPC", "111111-AAAAAA", null, null),
            ("SAMEPC", "222222-BBBBBB", null, null));
        var service = CreateService(CreateArchiveConfig(_archivePath));

        var result = await service.SearchByComputerNameAsync("SAMEPC");

        Assert.Equal(2, result.Keys.Count);
        Assert.Equal(2, result.Keys.Select(k => k.RowId).Distinct().Count());
    }

    [Fact]
    public async Task Rejects_a_UNC_archive_path()
    {
        // SQLite over SMB reads unreliably under WAL. A reachable share is
        // worse than an unreachable one, because it half-works.
        var service = CreateService(
            CreateArchiveConfig(@"\\server\share\archive.db"));

        var result = await service.SearchByComputerNameAsync("ANY");

        Assert.False(result.Success);
        Assert.Contains("network share", result.Error);
    }

    [Fact]
    public async Task Rejects_an_empty_search_term()
    {
        var service = CreateService(CreateArchiveConfig(_archivePath));

        Assert.False((await service.SearchByComputerNameAsync("")).Success);
        Assert.False((await service.SearchByComputerNameAsync("   ")).Success);
        Assert.False((await service.SearchByKeyIdAsync("")).Success);
    }

    [Fact]
    public async Task Caps_results_at_the_configured_limit()
    {
        var rows = Enumerable.Range(1, 20)
            .Select(i => ($"PC{i:D3}", $"{i:D6}-AAAAAA", (string?)null, (string?)null))
            .ToArray();
        CreateArchive(rows);

        var service = CreateService(CreateArchiveConfig(_archivePath, ("SearchResultLimit", "5")));

        var result = await service.SearchByComputerNameAsync("PC");

        Assert.Equal(5, result.Keys.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Does_not_mark_archive_results_truncated_at_the_exact_limit()
    {
        var rows = Enumerable.Range(1, 5)
            .Select(i => ($"PC{i:D3}", $"{i:D6}-AAAAAA", (string?)null, (string?)null))
            .ToArray();
        CreateArchive(rows);

        var service = CreateService(CreateArchiveConfig(_archivePath, ("SearchResultLimit", "5")));

        var result = await service.SearchByComputerNameAsync("PC");

        Assert.Equal(5, result.Keys.Count);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Caps_results_after_merging_live_and_archive_rows()
    {
        CreateArchive(
            ("PC003", "333333-CCCCCC", null, null),
            ("PC004", "444444-DDDDDD", null, null));
        var config = CreateArchiveConfig(_archivePath, ("SearchResultLimit", "2"));
        var live = new FakeLiveDirectorySearch
        {
            NameResult = BitLockerLiveDirectorySearchResult.Ok(
                [
                    LiveKey("PC001", "111111-AAAAAA"),
                    LiveKey("PC002", "222222-BBBBBB"),
                ],
                truncated: false),
        };
        var service = CreateService(config, live);

        var result = await service.SearchByComputerNameAsync("PC", includeLiveAd: true);

        Assert.Equal(2, result.Keys.Count);
        Assert.All(result.Keys, key => Assert.True(key.FoundInLiveDirectory));
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Ignores_a_non_positive_limit_rather_than_returning_everything()
    {
        // SQLite treats a negative LIMIT as unlimited, which would pull the
        // whole archive into the web process.
        var rows = Enumerable.Range(1, 20)
            .Select(i => ($"PC{i:D3}", $"{i:D6}-AAAAAA", (string?)null, (string?)null))
            .ToArray();
        CreateArchive(rows);

        var service = CreateService(CreateArchiveConfig(_archivePath, ("SearchResultLimit", "-1")));

        var result = await service.SearchByComputerNameAsync("PC");

        Assert.True(result.Success);
        Assert.Equal(20, result.Keys.Count); // default limit of 50, not unlimited
    }

    [Fact]
    public async Task Treats_a_wildcard_character_in_a_name_as_a_literal()
    {
        CreateArchive(
            ("PC100", "111111-AAAAAA", null, null),
            ("PC_00", "222222-BBBBBB", null, null));
        var service = CreateService(CreateArchiveConfig(_archivePath));

        var result = await service.SearchByComputerNameAsync("PC_0");

        Assert.Equal("PC_00", Assert.Single(result.Keys).ComputerName);
    }

    [Fact]
    public async Task Reports_no_keys_without_failing_when_nothing_matches()
    {
        // Distinct from the failure cases above: both required sources were
        // searched, and the machine genuinely has no key.
        CreateArchive(("PC1", "111111-AAAAAA", null, null));
        var service = CreateService(CreateArchiveConfig(_archivePath));

        var result = await service.SearchByComputerNameAsync("NOSUCHMACHINE");

        Assert.True(result.Success);
        Assert.Empty(result.Keys);
        Assert.Null(result.Error);
    }

    // ---- Audit target redaction (blr-1) ----------------------------------------------------
    //
    // The recovery-screen box legitimately accepts a pasted 48-digit recovery key -- the docs
    // invite it and the parser has a branch for it. Auditing that verbatim writes a working
    // disk-decryption key into the audit log, which is durable, separately stored, readable by
    // more people than may reveal a key, and reachable without tripping the RevealRecoveryKey
    // event that exists to record exactly that disclosure.

    [Fact]
    public void AuditTarget_RedactsAPastedRecoveryPassword()
    {
        var password = "111111-222222-333333-444444-555555-666666-777777-888888";

        var target = BitLockerRecovery.AuditSearchTarget(password);

        Assert.DoesNotContain("111111", target, StringComparison.Ordinal);
        Assert.DoesNotContain("888888", target, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditTarget_RedactsARecoveryPasswordEvenWithSurroundingText()
    {
        // The load-bearing case. The parser matches a recovery password anywhere in the input,
        // so a redaction keyed on "the whole box is 48 digits" would pass the test above and
        // still leak the key here -- which is how an operator actually pastes it.
        var target = BitLockerRecovery.AuditSearchTarget(
            "Recovery key: 111111-222222-333333-444444-555555-666666-777777-888888 (from caller)");

        Assert.DoesNotContain("111111", target, StringComparison.Ordinal);
        Assert.DoesNotContain("888888", target, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditTarget_KeepsAFullKeyId()
    {
        // A key ID is an identifier, not a secret, and it is what makes the record useful.
        var target = BitLockerRecovery.AuditSearchTarget("{7A159302-48BB-435E-9A81-3F1AEF9A7A40}");

        Assert.Equal("7a159302-48bb-435e-9a81-3f1aef9a7a40", target);
    }

    [Fact]
    public void AuditTarget_KeepsAShortKeyId()
    {
        var target = BitLockerRecovery.AuditSearchTarget("Recovery key ID: 7A159302");

        Assert.Equal("7a159302", target);
    }

    [Fact]
    public void AuditTarget_KeepsAnUnparseableStringForDiagnostics()
    {
        // Not a key, and the service refuses it. Blanking it would strip the audit record of
        // the diagnostic value it exists for.
        var target = BitLockerRecovery.AuditSearchTarget("  what the caller read out  ");

        Assert.Equal("what the caller read out", target);
    }

    [Fact]
    public void AuditTarget_HandlesEmptyInput()
    {
        Assert.Equal(string.Empty, BitLockerRecovery.AuditSearchTarget(null));
        Assert.Equal(string.Empty, BitLockerRecovery.AuditSearchTarget("   "));
    }
}
