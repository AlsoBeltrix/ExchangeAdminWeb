using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The runner's authorization check answered by the test: every job allowed until
/// <see cref="Refusal"/> is set. For tests of the runner's own mechanics, whose processors belong to
/// no catalog module, and for tests that never run a row.
/// </summary>
internal sealed class StubJobAuthorization : IJobAuthorization
{
    public JobRefusal? Refusal { get; set; }

    /// <summary>When set, the check itself fails with this.</summary>
    public Exception? Failure { get; set; }

    public JobRefusal? Check(BulkJob job) => Failure is null ? Refusal : throw Failure;
}

/// <summary>
/// The real authorization check over real enablement and section-access stores in a temp directory:
/// one module enabled, one group allowed for its section, and the ways a test takes each away.
/// </summary>
internal sealed class JobAuthorizationStores : IDisposable
{
    public const string GroupSid = "S-1-5-21-100-200-300-4001";

    private readonly TempDir _temp = new();
    private readonly string _moduleId;
    private readonly string _section;
    private readonly SectionAccessRepository _sections;
    private readonly ModuleEnablementRepository _enablement;

    public JobAuthorization Authorization { get; }

    public SectionAccessService Sections { get; }

    public JobAuthorizationStores(string moduleId, string section)
    {
        _moduleId = moduleId;
        _section = section;
        var config = new ConfigurationBuilder().Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        _sections = new SectionAccessRepository(TestConfigStore.Create(_temp.Path));
        _sections.SaveAll(new Dictionary<string, string[]> { [section] = [GroupSid] });
        _enablement = TestConfigStore.CreateModuleEnablement(_temp.Path);
        _enablement.SaveAll(Enabled(true));
        Sections = new SectionAccessService(config, NullLogger<SectionAccessService>.Instance, env, catalog, _sections);
        Authorization = new JobAuthorization(
            catalog,
            new ModuleEnablementService(catalog, env, moduleConfig, _enablement, config, NullLogger<ModuleEnablementService>.Instance),
            Sections,
            NullLogger<JobAuthorization>.Instance);
    }

    /// <summary>The snapshot an enqueuer saves for a submitter the section's one group authorized.</summary>
    public string Snapshot() =>
        new JobAuthorizationSnapshot { Section = _section, RoleClaims = [GroupSid], AuthorizedGroups = [GroupSid] }.ToJson();

    public void DisableModule() => _enablement.SaveAll(Enabled(false));

    /// <summary>The section now allows another group only: the submitter's access is gone.</summary>
    public void RemoveAccess() =>
        _sections.SaveAll(new Dictionary<string, string[]> { [_section] = ["S-1-5-21-100-200-300-4002"] });

    /// <summary>Drops the table behind the enablement store, or the one behind section access, so its next read fails.</summary>
    public void BreakStore(bool enablement)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _temp.DbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = enablement ? "DROP TABLE module_enablement;" : "DROP TABLE section_access;";
        command.ExecuteNonQuery();
    }

    // The module's parent is enabled throughout: only the module's own switch is the test's.
    private Dictionary<string, bool> Enabled(bool module) => new() { ["ExchangeOnline"] = true, [_moduleId] = module };

    public void Dispose() => _temp.Dispose();
}
