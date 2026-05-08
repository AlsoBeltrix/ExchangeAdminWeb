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

    builder.Host.UseSerilog((ctx, services, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

    var allowedGroups = builder.Configuration.GetSection("Security:AllowedGroups").Get<string[]>() ?? Array.Empty<string>();
    if (allowedGroups.Length == 0)
        Log.Warning("Security:AllowedGroups is empty or missing — all users will be denied access until configured");

    var sectionAccess = builder.Configuration
        .GetSection("Security:SectionAccess")
        .Get<Dictionary<string, string[]>>() ?? new();

    var expectedSections = new[] {
        "MailboxPermissions", "CalendarPermissions", "MigrationCheck",
        "MigrationCreate", "MigrationManage", "DelegationReport",
        "MessageTrace", "RecipientLookup", "OutOfOffice"
    };

    string[] GroupsFor(string section)
    {
        if (sectionAccess.TryGetValue(section, out var groups) && groups.Length > 0)
            return groups;
        Log.Warning("SectionAccess:{Section} is empty — access denied until configured", section);
        return Array.Empty<string>();
    }

    foreach (var missing in expectedSections.Where(s => !sectionAccess.ContainsKey(s)))
        Log.Warning("SectionAccess:{Section} is not configured — access denied until configured", missing);

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

        var migrationSubPolicies = new[] { "MigrationCreate", "MigrationManage" };
        foreach (var section in expectedSections.Except(migrationSubPolicies))
        {
            var sectionGroups = GroupsFor(section);
            options.AddPolicy(section, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
                .AddRequirements(new GroupAuthorizationRequirement(sectionGroups, section)));
        }

        options.AddPolicy("MigrationCreate", policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
            .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("MigrationCheck"), "MigrationCheck"))
            .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("MigrationCreate"), "MigrationCreate")));

        options.AddPolicy("MigrationManage", policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
            .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("MigrationCheck"), "MigrationCheck"))
            .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("MigrationManage"), "MigrationManage")));
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
