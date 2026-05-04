using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Components;
using ExchangeAdminWeb.Middleware;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

    var allowedGroups = builder.Configuration.GetSection("Security:AllowedGroups").Get<string[]>() ?? Array.Empty<string>();

    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
        .AddNegotiate();

    builder.Services.AddSingleton<IAuthorizationHandler, GroupAuthorizationHandler>();

    builder.Services.AddAuthorization(options =>
    {
        var groupPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
            .Build();

        options.AddPolicy("GroupPolicy", groupPolicy);
        options.FallbackPolicy = groupPolicy;
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
    builder.Services.AddScoped<IExchangeService, ExchangeService>();
    builder.Services.AddScoped<ClientInfoService>();

    var app = builder.Build();

    // Configure forwarded headers to get real client IP behind IIS
    // Note: With IIS inprocess hosting, RemoteIpAddress should be available directly
    // This is mainly for reverse proxy scenarios
    var forwardedHeadersOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        // Don't require forwarded headers - allow direct connection info too
        RequireHeaderSymmetry = false
    };
    // Clear default networks to allow any proxy
    forwardedHeadersOptions.KnownIPNetworks.Clear();
    forwardedHeadersOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedHeadersOptions);

    app.UsePathBase("/ExchangeAdminWeb");

    if (!app.Environment.IsDevelopment())
        app.UseExceptionHandler("/Error", createScopeForErrors: true);

    app.UseStaticFiles();
    app.UseMiddleware<ClientInfoMiddleware>();
    app.UseAuthentication();
    app.UseAuthorization();
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
