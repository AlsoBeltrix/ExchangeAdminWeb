using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Components;
using ExchangeAdminWeb.Middleware;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Serilog;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Section access config is read directly by SectionAccessService (not via IConfiguration)
    // to ensure fail-closed behavior on parse errors and correct override semantics.

    builder.Host.UseSerilog((ctx, services, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

    var allowedGroups = builder.Configuration.GetSection("Security:AllowedGroups").Get<string[]>() ?? Array.Empty<string>();

    var adminGroups = builder.Configuration.GetSection("Security:AdminGroups").Get<string[]>() ?? Array.Empty<string>();
    if (adminGroups.Length == 0)
        Log.Warning("Security:AdminGroups is empty or missing - admin settings page will be inaccessible until configured");

    var catalog = new ModuleCatalog();

    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
        .AddNegotiate();

    builder.Services.AddSingleton(catalog);

    // Config store infrastructure (SqliteConfigStore-Plan Phase A). The DB lives in the
    // persistent, deploy-excluded config/ directory (decision 2026-06-18, Option A), one per
    // environment. The path is derived from the content root - not a new appsettings key and
    // not hardcoded. The factory opens short-lived connections (never a shared singleton
    // connection), so it is safe across the mix of Singleton/Scoped consumers. No existing
    // service reads from the DB yet; Phase B moves the stores over one at a time.
    var configDbPath = Path.Combine(builder.Environment.ContentRootPath, "config", "exchangeadmin.db");
    builder.Services.AddSingleton(new ExchangeAdminWeb.Services.Storage.SqliteConnectionFactory(configDbPath));
    builder.Services.AddSingleton<ExchangeAdminWeb.Services.Storage.ConfigStoreMigrator>();
    builder.Services.AddSingleton<ExchangeAdminWeb.Services.Storage.IConfigStore,
        ExchangeAdminWeb.Services.Storage.SqliteConfigStore>();
    builder.Services.AddSingleton<ExchangeAdminWeb.Services.Storage.AppSettingRepository>();
    builder.Services.AddSingleton<ExchangeAdminWeb.Services.Storage.ModuleAdminRepository>();
    builder.Services.AddSingleton<ExchangeAdminWeb.Services.Storage.ModuleConfigRepository>();
    builder.Services.AddSingleton<ExchangeAdminWeb.Services.Storage.ModuleEnablementRepository>();
    builder.Services.AddSingleton<ExchangeAdminWeb.Services.Storage.SectionAccessRepository>();
    builder.Services.AddSingleton<ExchangeAdminWeb.Services.Storage.ProtectedPrincipalRepository>();
    builder.Services.AddSingleton<ExchangeAdminWeb.Services.Storage.AttributeEditorRepository>();

    // Bulk job runner infrastructure (docs/BulkJobRunner-Plan.md). Durable server-side batches live
    // in a SEPARATE operational SQLite database (exchangeadmin-jobs.db) in the same deploy-excluded
    // config/ directory, distinct from the config DB: job state is environment-local, high-churn and
    // MUST NEVER be promoted dev->prod (owner 2026-07-02). The repository gets its own connection
    // factory pointed at that file. Processors are resolved per-job from a fresh scope via the
    // registry, so the runner stays module-agnostic (no compile-time dependency on any module).
    var jobsDbPath = Path.Combine(builder.Environment.ContentRootPath, "config", "exchangeadmin-jobs.db");
    builder.Services.AddSingleton(new ExchangeAdminWeb.Services.Jobs.BulkJobRepository(
        new ExchangeAdminWeb.Services.Storage.SqliteConnectionFactory(jobsDbPath)));
    builder.Services.AddSingleton(_ => new ExchangeAdminWeb.Services.Jobs.BulkJobProcessorRegistry(
        new KeyValuePair<string, Type>[]
        {
            // module id -> processor type; the type is resolved per-job from a fresh scope.
            new(ExchangeAdminWeb.Services.Jobs.ConferenceRoomBulkProcessor.ModuleName,
                typeof(ExchangeAdminWeb.Services.Jobs.ConferenceRoomBulkProcessor)),
            new(ExchangeAdminWeb.Services.Jobs.MessageTraceDetailJobProcessor.ModuleName,
                typeof(ExchangeAdminWeb.Services.Jobs.MessageTraceDetailJobProcessor)),
        }));
    builder.Services.AddSingleton<ExchangeAdminWeb.Services.Jobs.BulkJobService>();

    builder.Services.AddSingleton<ModuleEnablementService>();
    builder.Services.AddSingleton<SectionAccessService>();
    builder.Services.AddSingleton<IAuthorizationHandler, GroupAuthorizationHandler>();

    builder.Services.AddAuthorization(options =>
    {
        catalog.ConfigureAuthorizationPolicies(options, allowedGroups, adminGroups);
    });

    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddHttpContextAccessor();

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddHttpClient("ServiceNow")
        .ConfigureHttpClient(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

    builder.Services.AddHttpClient("MicrosoftGraph")
        .ConfigureHttpClient(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

    builder.Services.AddSingleton<ModuleConfigService>();
    builder.Services.AddSingleton<ModuleCredentialService>();
    builder.Services.AddSingleton<ModuleAdminService>();
    builder.Services.AddSingleton<MfaResetService>();
    builder.Services.AddSingleton<IntuneDeviceService>();
    builder.Services.AddSingleton<Comms10kService>();
    builder.Services.AddScoped<ConferenceRoomService>();
    // Single protected-principal enforcement point for every ConferenceRooms room-mutating write
    // (page Finder/Type + each bulk row). Guarded-execution: the write runs only through its
    // onAllowed delegate, so the gate cannot be bypassed and is decided before any side effect.
    builder.Services.AddScoped<ConferenceRoomProtectionGate>();
    // The bulk job processor talks to rooms through the narrow IConferenceRoomBulkOperations seam
    // (implemented by ConferenceRoomService) so it is unit-testable without live EXO/AD.
    builder.Services.AddScoped<ExchangeAdminWeb.Services.Jobs.IConferenceRoomBulkOperations>(
        sp => sp.GetRequiredService<ConferenceRoomService>());
    // Bulk job processor for ConferenceRooms (resolved per-job from a fresh scope by the runner).
    builder.Services.AddScoped<ExchangeAdminWeb.Services.Jobs.ConferenceRoomBulkProcessor>();
    builder.Services.AddSingleton<NamedLocationsService>();
    builder.Services.AddSingleton<M365GroupManagementService>();
    // Risky Users read path (docs/RiskyUsersModule-Plan.md, S2). Singleton like the other Graph
    // services: no per-request state, and GraphTokenClient is constructed per operation from the
    // named "MicrosoftGraph" client above.
    builder.Services.AddSingleton<RiskyUsersService>();
    builder.Services.AddSingleton<DhcpAuthorizationService>();
    // BitLocker recovery. Scoped: the service opens a short-lived SQLite connection per query and
    // holds no state between them. Needs no HttpClient, no Graph registration and no Exchange
    // connection -- default searches read the local archive, and the optional live AD fallback
    // uses ModuleCredentialService with a module-specific Delinea secret.
    builder.Services.AddScoped<IBitLockerLiveDirectorySearch, PowerShellBitLockerLiveDirectorySearch>();
    builder.Services.AddScoped<BitLockerRecoveryService>();
    builder.Services.AddScoped<GroupManagementService>();
    builder.Services.AddScoped<ExchangeAdminWeb.Services.SelfServiceGroups.SelfServiceGroupService>();
    builder.Services.AddScoped<ADAttributeEditorService>();
    builder.Services.AddSingleton<ADOrganizationalUnitService>();
    builder.Services.AddSingleton<ADDirectorySearchService>();
    // The operator-email resolver reads the same pooled AD runspace through a one-member seam,
    // so it never reaches the wildcard autocomplete search (OperatorEmailResolution-Plan).
    builder.Services.AddSingleton<IOperatorDirectory>(sp => sp.GetRequiredService<ADDirectorySearchService>());
    builder.Services.AddSingleton<OperatorEmailResolver>();
    // The section-access SID migration gets its OWN directory service rather than reusing the
    // autocomplete one: it must throw when a lookup fails (a migration that reads an outage as
    // "no such group" deletes live access grants), and it must not queue behind the shared
    // 30-second autocomplete lock at startup. See SectionAccessSidStorage-Plan.
    builder.Services.AddSingleton<ExchangeAdminWeb.Authorization.ISectionAccessGroupDirectory,
        SectionAccessGroupDirectory>();
    builder.Services.AddSingleton<SectionAccessSidMigration>();
    builder.Services.AddScoped<EmergencyDisableService>();
    builder.Services.AddScoped<AccountLockoutRemediationService>();
    builder.Services.AddScoped<LicensingUpdatesService>();
    builder.Services.AddScoped<DelegationReportService>();
    builder.Services.AddScoped<OutOfOfficeService>();
    builder.Services.AddScoped<RecipientLookupService>();
    builder.Services.AddScoped<HeaderAnalysisService>();
    builder.Services.AddScoped<MessageTraceService>();
    // The Message Analysis detail-export bulk processor talks to detail through the narrow
    // IMessageTraceDetailSource seam (implemented by MessageTraceService) so it is unit-testable
    // without live EXO/on-prem, and is resolved per-job from a fresh scope by the runner.
    builder.Services.AddScoped<ExchangeAdminWeb.Services.Jobs.IMessageTraceDetailSource>(
        sp => sp.GetRequiredService<MessageTraceService>());
    // Single owner of the export directory, filename convention, and jobId validation, shared by the
    // detail-export writer and the Downloadable Reports page so the two cannot drift apart.
    builder.Services.AddScoped<MessageTraceExportStore>();
    // Page logic for the Downloadable Reports page, kept out of the markup so it is unit-testable
    // (the repo has no bUnit harness).
    builder.Services.AddScoped<MessageTraceExportListing>();
    builder.Services.AddScoped<ExchangeAdminWeb.Services.Jobs.MessageTraceDetailJobProcessor>();
    builder.Services.AddScoped<MailboxPermissionService>();
    builder.Services.AddScoped<CalendarPermissionService>();
    builder.Services.AddScoped<BlockedSenderService>();

    builder.Services.AddSingleton<ExtendedLogService>();
    builder.Services.AddSingleton<JsonlLogService>();
    builder.Services.AddSingleton<OperationTraceService>();
    builder.Services.AddSingleton<AuditService>();
    builder.Services.AddSingleton<EmailService>();
    builder.Services.AddSingleton<ProtectedPrincipalService>();
    builder.Services.AddSingleton<PermissionValidator>();
    builder.Services.AddScoped<BlockedSenderProtectionGate>();
    // SINGLETON, deliberately. PermissionValidator and M365GroupManagementService are singletons
    // and must consult this to authorise servicing; a singleton cannot inject a scoped service,
    // and forcing it creates a captive dependency that outlives its scope. This service is
    // stateless and its only dependencies are SectionAccessService (already a singleton) and a
    // logger, so the scoped registration was wrong rather than load-bearing.
    //
    // Scoped consumers keep working - BlockedSenderProtectionGate above is scoped and injects this
    // one, which is always legal in that direction.
    builder.Services.AddSingleton<ProtectedPrincipalServicerService>();
    builder.Services.AddSingleton<ServiceNowService>();
    builder.Services.AddSingleton<ITicketValidator, TicketValidationService>();
    builder.Services.AddSingleton<DelineaService>();
    builder.Services.AddSingleton<ExoConnectionPool>();
    builder.Services.AddScoped<MigrationService>();
    builder.Services.AddScoped<IIdentityResolver, ExchangeIdentityResolver>();
    builder.Services.AddScoped<ClientInfoService>();
    // Captures IP/user agent into the circuit-scoped ClientInfoService at circuit
    // open, so audit records carry the right per-session IP for the circuit's
    // whole lifetime (the static cache is fallback only).
    builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, ClientInfoCircuitHandler>();

    builder.Services.AddScoped<IUndoableModule, ADAttributeEditorUndoService>();
    builder.Services.AddScoped<UndoRegistry>();

    var app = builder.Build();

    // Ensure the config database schema exists / is current before serving requests
    // (SqliteConfigStore-Plan Phase A). Idempotent: a no-op once the DB is at the target
    // version. Fail fast - a config store that cannot be opened/migrated is not a state we
    // should serve in.
    {
        // Fail fast if the audit/operational log root is not configured. There is no baked-in
        // default (docs/RemoveHardcodedLogRoot-Plan.md): silently misplacing audit logs is worse
        // than a deploy that stops and says why. Runs before any logging service is resolved.
        if (string.IsNullOrWhiteSpace(builder.Configuration["Audit:LogRoot"]))
        {
            Log.Fatal(ExchangeAdminWeb.Services.AuditLogRoot.UnsetMessage);
            throw new InvalidOperationException(ExchangeAdminWeb.Services.AuditLogRoot.UnsetMessage);
        }

        var migrator = app.Services.GetRequiredService<ExchangeAdminWeb.Services.Storage.ConfigStoreMigrator>();
        var schemaVersion = migrator.Migrate();
        Log.Information("Config store schema ready at version {SchemaVersion}", schemaVersion);

        // Startup self-registration (SqliteConfigStore-Plan Section 3d): non-destructively seed
        // enablement rows for any catalog modules missing one (e.g. a newly added module), at
        // their EnabledByDefault. Never overwrites existing rows - the banned destructive
        // startup write stays banned. No-op on a corrupt store.
        var enablement = app.Services.GetRequiredService<ModuleEnablementService>();
        enablement.SeedMissingModules();

        // Convert section-access group names to SIDs (docs/SectionAccessSidStorage-Plan.md).
        // Runs on every start and is idempotent: a row already holding a SID is left alone, so a
        // run deferred by an AD outage simply picks up later. Never throws and never half-writes -
        // this is the table deciding who reaches every module, and it runs before anyone can log
        // in to repair anything, so failing to start would be a worse outage than the ambiguity
        // being fixed. Every failure path leaves the store exactly as it was.
        // Resolve SectionAccessService FIRST. Its constructor performs the one-time import of a
        // legacy config\sectionaccess.json (SectionAccessService.cs, ImportLegacyIfPresent), and
        // because that is a constructor side effect on a lazily-constructed singleton, its timing
        // is otherwise decided by whichever request happens to touch authorization first - i.e.
        // AFTER this migration. On a legacy upgrade that leaves the table holding names for the
        // whole process lifetime, which now means denying everyone configured only through that
        // file until someone restarts. Review finding sid-2.
        _ = app.Services.GetRequiredService<SectionAccessService>();

        var sectionAccessSids = app.Services.GetRequiredService<SectionAccessSidMigration>();
        var sidMigrationStatus = sectionAccessSids.Run();
        Log.Information("Section-access SID migration: {Status}", sidMigrationStatus);

        // One-time repair of the renamed Graph credential key (DelineaSecretId ->
        // GraphDelineaSecretId): moves any value stranded under the old key for Graph modules so
        // the config page (which binds only the new key) shows it. Idempotent, catalog-scoped to
        // Graph modules, non-destructive. See docs/GraphSecretKeyMigration-Plan.md.
        var moduleConfig = app.Services.GetRequiredService<ModuleConfigService>();
        moduleConfig.MigrateGraphSecretKeys();

        // Bulk job runner startup (docs/BulkJobRunner-Plan.md). A DI singleton is not constructed
        // until first resolved, so this explicit call is required for orphan reconciliation to run:
        // it migrates the jobs DB, prunes old terminal jobs, and - the load-bearing rule - flips
        // every non-terminal job (Running OR Queued) to Interrupted. There is no resume; this is a
        // one-shot startup call, NOT a background timer/hosted worker (consistent with the
        // 2026-06-17 no-unattended-worker posture).
        var bulkJobs = app.Services.GetRequiredService<ExchangeAdminWeb.Services.Jobs.BulkJobService>();
        bulkJobs.InitializeAsync();

        // Message Analysis export retention, in the same one-shot startup pass that prunes old job
        // RECORDS above - the records and the files they describe now expire on the same schedule
        // and by the same mechanism.
        //
        // This was documented for months as the job of a host scheduled task that was never
        // created on any host, so nothing enforced the window (docs/AdminBulkJobs-Plan.md Part A).
        // Owner ruled 2026-08-04 that there are and will be no scheduled tasks, so it lives here.
        // Never throws; retention must not be able to stop the app booting.
        using (var retentionScope = app.Services.CreateScope())
        {
            var exports = retentionScope.ServiceProvider.GetRequiredService<MessageTraceExportStore>();
            var retentionLog = retentionScope.ServiceProvider
                .GetRequiredService<ILogger<MessageTraceExportStore>>();
            var removed = exports.PruneExpired(DateTime.UtcNow, retentionLog);
            if (removed > 0)
                Log.Information("Export retention: removed {Count} expired Message Analysis export(s)", removed);
        }
    }

    var pathBase = (builder.Configuration["Application:PathBase"] ?? "/ExchangeAdminWeb").TrimEnd('/');
    if (string.IsNullOrWhiteSpace(pathBase) || pathBase == "/")
        pathBase = "";
    if (pathBase.Length > 0)
        app.UsePathBase(pathBase);

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<ClientInfoMiddleware>();
    app.UseAntiforgery();

    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode()
        .RequireAuthorization();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
