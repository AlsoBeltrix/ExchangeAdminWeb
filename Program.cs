using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Components;
using ExchangeAdminWeb.Middleware;
using ExchangeAdminWeb.Modules;
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

    builder.Services.AddSingleton<GraphClientService>();
    builder.Services.AddSingleton<MfaResetService>();

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
