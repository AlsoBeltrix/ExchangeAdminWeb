using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Components;
using ExchangeAdminWeb.Middleware;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Serilog;

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
    if (allowedGroups.Length == 0)
        Log.Warning("Security:AllowedGroups is empty or missing — all users will be denied access until configured");

    var adminGroups = builder.Configuration.GetSection("Security:AdminGroups").Get<string[]>() ?? Array.Empty<string>();
    if (adminGroups.Length == 0)
        Log.Warning("Security:AdminGroups is empty or missing — admin settings page will be inaccessible until configured");

    var expectedSections = new[] {
        "MailboxPermissions", "CalendarPermissions", "MigrationCheck",
        "MigrationCreate", "MigrationManage", "DelegationReport",
        "MessageTrace", "RecipientLookup", "OutOfOffice",
        "MailboxPermissionsOnPrem", "CalendarPermissionsOnPrem"
    };

    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
        .AddNegotiate();

    builder.Services.AddSingleton<SectionAccessService>();
    builder.Services.AddSingleton<IAuthorizationHandler, GroupAuthorizationHandler>();

    builder.Services.AddAuthorization(options =>
    {
        var groupPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
            .Build();

        options.AddPolicy("GroupPolicy", groupPolicy);
        options.FallbackPolicy = groupPolicy;

        // Section policies: static base gate + dynamic section gate
        var migrationSubPolicies = new[] { "MigrationCreate", "MigrationManage" };
        foreach (var section in expectedSections.Except(migrationSubPolicies))
        {
            options.AddPolicy(section, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
                .AddRequirements(new GroupAuthorizationRequirement(section, dynamic: true)));
        }

        // Migration hierarchy: base gate + dynamic MigrationCheck + dynamic sub-permission
        options.AddPolicy("MigrationCreate", policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
            .AddRequirements(new GroupAuthorizationRequirement("MigrationCheck", dynamic: true))
            .AddRequirements(new GroupAuthorizationRequirement("MigrationCreate", dynamic: true)));

        options.AddPolicy("MigrationManage", policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
            .AddRequirements(new GroupAuthorizationRequirement("MigrationCheck", dynamic: true))
            .AddRequirements(new GroupAuthorizationRequirement("MigrationManage", dynamic: true)));

        // Admin page: AdminGroups only (no base AllowedGroups gate)
        options.AddPolicy("AdminSettings", policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new GroupAuthorizationRequirement(adminGroups, "AdminSettings")));
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

    builder.Services.AddSingleton<AuditService>();
    builder.Services.AddSingleton<EmailService>();
    builder.Services.AddSingleton<PermissionValidator>();
    builder.Services.AddSingleton<ServiceNowService>();
    builder.Services.AddSingleton<DelineaService>();
    builder.Services.AddSingleton<ExoConnectionPool>();
    builder.Services.AddScoped<IExchangeService, ExchangeService>();
    builder.Services.AddScoped<IIdentityResolver>(sp => (IIdentityResolver)sp.GetRequiredService<IExchangeService>());
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
