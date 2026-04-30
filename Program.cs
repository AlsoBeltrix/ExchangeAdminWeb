using ExchangeAdminWeb.Components;
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

    builder.Host.UseSerilog((ctx, services, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

    var allowedGroups = builder.Configuration.GetSection("Security:AllowedGroups").Get<string[]>() ?? Array.Empty<string>();

    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
        .AddNegotiate();

    builder.Services.AddAuthorization(options =>
    {
        if (allowedGroups.Length > 0)
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(allowedGroups)
                .Build();
        }
    });

    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddHttpContextAccessor();

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddSingleton<AuditService>();
    builder.Services.AddSingleton<EmailService>();
    builder.Services.AddSingleton<PermissionValidator>();
    builder.Services.AddScoped<IExchangeService, ExchangeService>();

    var app = builder.Build();

    app.UsePathBase("/ExchangeAdminWeb");

    if (!app.Environment.IsDevelopment())
        app.UseExceptionHandler("/Error", createScopeForErrors: true);

    app.UseStaticFiles();
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
