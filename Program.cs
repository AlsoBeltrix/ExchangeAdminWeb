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
        Log.Warning("Security:AdminGroups is empty or missing — admin settings page will be inaccessible until configured");

    var catalog = new ModuleCatalog();

    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
        .AddNegotiate();

    builder.Services.AddSingleton(catalog);
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
    builder.Services.AddSingleton<Comms10kService>();
    builder.Services.AddScoped<ConferenceRoomService>();
    builder.Services.AddSingleton<NamedLocationsService>();
    builder.Services.AddSingleton<M365GroupManagementService>();
    builder.Services.AddSingleton<DhcpAuthorizationService>();
    builder.Services.AddScoped<GroupManagementService>();
    builder.Services.AddScoped<ADAttributeEditorService>();
    builder.Services.AddSingleton<ADOrganizationalUnitService>();
    builder.Services.AddSingleton<ADDirectorySearchService>();
    builder.Services.AddScoped<EmergencyDisableService>();
    builder.Services.AddScoped<TestAccountPoolService>();
    builder.Services.AddScoped<LicensingUpdatesService>();
    builder.Services.AddScoped<DelegationReportService>();
    builder.Services.AddScoped<OutOfOfficeService>();
    builder.Services.AddScoped<RecipientLookupService>();
    builder.Services.AddScoped<HeaderAnalysisService>();
    builder.Services.AddScoped<MessageTraceService>();
    builder.Services.AddScoped<MailboxPermissionService>();
    builder.Services.AddScoped<CalendarPermissionService>();

    builder.Services.AddSingleton<ExtendedLogService>();
    builder.Services.AddSingleton<JsonlLogService>();
    builder.Services.AddSingleton<OperationTraceService>();
    builder.Services.AddSingleton<AuditService>();
    builder.Services.AddSingleton<EmailService>();
    builder.Services.AddSingleton<ProtectedPrincipalService>();
    builder.Services.AddSingleton<PermissionValidator>();
    builder.Services.AddSingleton<ServiceNowService>();
    builder.Services.AddSingleton<DelineaService>();
    builder.Services.AddSingleton<ExoConnectionPool>();
    builder.Services.AddScoped<MigrationService>();
    builder.Services.AddScoped<IIdentityResolver, ExchangeIdentityResolver>();
    builder.Services.AddScoped<ClientInfoService>();

    var app = builder.Build();

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
