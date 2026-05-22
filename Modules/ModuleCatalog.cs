using ExchangeAdminWeb.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace ExchangeAdminWeb.Modules;

public sealed class ModuleCatalog
{
    private readonly IReadOnlyList<AdminModuleDescriptor> _modules;
    private readonly Dictionary<string, AdminModuleDescriptor> _byId;
    private readonly Dictionary<string, AdminModuleDescriptor> _byRoute;
    private readonly Dictionary<string, AdminModuleDescriptor> _byPolicyAlias;

    public ModuleCatalog()
    {
        var modules = RegisterAll();
        Validate(modules);

        _modules = modules;
        _byId = modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        _byRoute = modules.ToDictionary(m => m.Route, StringComparer.OrdinalIgnoreCase);

        _byPolicyAlias = new Dictionary<string, AdminModuleDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules)
        {
            _byPolicyAlias.TryAdd(m.MainPermission.PolicyAlias, m);
            foreach (var gp in m.GranularPermissions)
                _byPolicyAlias.TryAdd(gp.PolicyAlias, m);
        }
    }

    public IReadOnlyList<AdminModuleDescriptor> GetAll() => _modules;
    public IReadOnlyList<AdminModuleDescriptor> GetOrdered() => _modules.OrderBy(m => m.SortOrder).ToList();
    public AdminModuleDescriptor? GetById(string id) => _byId.GetValueOrDefault(id);
    public AdminModuleDescriptor? GetByRoute(string route) => _byRoute.GetValueOrDefault(route);
    public AdminModuleDescriptor? GetByPolicyAlias(string alias) => _byPolicyAlias.GetValueOrDefault(alias);

    public IReadOnlyList<string> GetConfigurablePolicyAliases()
    {
        var result = new List<string>();
        foreach (var m in _modules.Where(m => !m.IsSystemModule).OrderBy(m => m.SortOrder))
        {
            result.Add(m.MainPermission.PolicyAlias);
            foreach (var gp in m.GranularPermissions)
                result.Add(gp.PolicyAlias);
        }
        return result;
    }

    public void ConfigureAuthorizationPolicies(
        AuthorizationOptions options,
        string[] allowedGroups,
        string[] adminGroups)
    {
        var groupPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
            .Build();
        options.AddPolicy("GroupPolicy", groupPolicy);
        options.FallbackPolicy = groupPolicy;

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in _modules.Where(m => m.IsSystemModule))
        {
            var alias = module.MainPermission.PolicyAlias;
            if (!registered.Add(alias)) continue;

            options.AddPolicy(alias, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new GroupAuthorizationRequirement(adminGroups, alias)));
        }

        foreach (var module in _modules.Where(m => !m.IsSystemModule))
        {
            var mainAlias = module.MainPermission.PolicyAlias;
            if (registered.Add(mainAlias))
            {
                options.AddPolicy(mainAlias, policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
                    .AddRequirements(new GroupAuthorizationRequirement(mainAlias, dynamic: true)));
            }

            foreach (var gp in module.GranularPermissions)
            {
                if (!registered.Add(gp.PolicyAlias)) continue;

                options.AddPolicy(gp.PolicyAlias, policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
                    .AddRequirements(new GroupAuthorizationRequirement(mainAlias, dynamic: true))
                    .AddRequirements(new GroupAuthorizationRequirement(gp.PolicyAlias, dynamic: true)));
            }
        }
    }

    private static List<AdminModuleDescriptor> RegisterAll() =>
    [
        new()
        {
            Id = "MailboxPermissions",
            DisplayName = "Mailbox Permissions",
            Description = "Grant or revoke Full Access and Send As permissions on Exchange Online mailboxes.",
            Route = "mailbox-permissions",
            IconCss = "bi bi-person-fill-nav-menu",
            SortOrder = 100,
            EnabledByDefault = true,
            IsSystemModule = false,
            MainPermission = new("Access", "MailboxPermissions"),
            GranularPermissions = [new("OnPrem", "MailboxPermissionsOnPrem", FailClosed: true)]
        },
        new()
        {
            Id = "CalendarPermissions",
            DisplayName = "Calendar Permissions",
            Description = "Set or remove calendar sharing permissions on Exchange Online mailboxes.",
            Route = "calendar-permissions",
            IconCss = "bi bi-calendar-fill-nav-menu",
            SortOrder = 200,
            EnabledByDefault = true,
            IsSystemModule = false,
            MainPermission = new("Access", "CalendarPermissions"),
            GranularPermissions = [new("OnPrem", "CalendarPermissionsOnPrem", FailClosed: true)]
        },
        new()
        {
            Id = "Migration",
            DisplayName = "Exchange Migration",
            Description = "Check migration eligibility and create migration batches for Exchange Online and on-premises mailboxes.",
            Route = "migration",
            IconCss = "bi bi-arrow-left-right-nav-menu",
            SortOrder = 300,
            EnabledByDefault = true,
            IsSystemModule = false,
            MainPermission = new("Access", "MigrationCheck"),
            GranularPermissions = [new("Create", "MigrationCreate"), new("Manage", "MigrationManage")],
            ConfigFields = [
                new("HybridEndpoint", "Hybrid Endpoint", "Migration endpoint name", DefaultValue: "hybrid1"),
                new("CloudTargetDeliveryDomain", "Cloud Target Domain", "e.g. contoso.mail.onmicrosoft.com"),
                new("OnPremTargetDeliveryDomain", "On-Prem Target Domain", "e.g. contoso.com"),
                new("OnPremTargetDAG", "On-Prem Target DAG", "Database availability group name", Required: false),
                new("CloudQuotaGB", "Cloud Quota (GB)", "Max mailbox size for cloud migration", DefaultValue: "100"),
                new("ExcludedADGroups", "Excluded AD Groups", "Comma-separated AD groups excluded from cloud migration", Required: false)
            ]
        },
        new()
        {
            Id = "DelegationReport",
            DisplayName = "Delegation Report",
            Description = "View current mailbox delegation assignments including Full Access, Send As, and Calendar permissions.",
            Route = "delegation-report",
            IconCss = "bi bi-people-fill-nav-menu",
            SortOrder = 400,
            EnabledByDefault = true,
            IsSystemModule = false,
            MainPermission = new("Access", "DelegationReport")
        },
        new()
        {
            Id = "MessageTrace",
            DisplayName = "Message Trace",
            Description = "Search message delivery logs by sender, recipient, date range, and subject.",
            Route = "message-trace",
            IconCss = "bi bi-envelope-fill-nav-menu",
            SortOrder = 500,
            EnabledByDefault = true,
            IsSystemModule = false,
            MainPermission = new("Access", "MessageTrace")
        },
        new()
        {
            Id = "RecipientLookup",
            DisplayName = "Recipient Lookup",
            Description = "Look up mailbox details including size, quotas, archive status, and recipient type.",
            Route = "recipient-lookup",
            IconCss = "bi bi-search-nav-menu",
            SortOrder = 600,
            EnabledByDefault = true,
            IsSystemModule = false,
            MainPermission = new("Access", "RecipientLookup")
        },
        new()
        {
            Id = "OutOfOffice",
            DisplayName = "Out of Office",
            Description = "View or configure automatic reply (out of office) settings for Exchange Online mailboxes.",
            Route = "out-of-office",
            IconCss = "bi bi-clock-fill-nav-menu",
            SortOrder = 700,
            EnabledByDefault = true,
            IsSystemModule = false,
            MainPermission = new("Access", "OutOfOffice")
        },
        new()
        {
            Id = "GroupManagement",
            DisplayName = "Group Management",
            Description = "Search, view membership, and manage distribution lists and security groups.",
            Route = "group-management",
            IconCss = "bi bi-people-fill-nav-menu",
            SortOrder = 150,
            EnabledByDefault = false,
            IsSystemModule = false,
            MainPermission = new("Access", "GroupManagement", FailClosed: true),
            GranularPermissions = [new("OnPrem", "GroupManagementOnPrem", FailClosed: true)],
            ConfigFields = [
                new("GraphTenantId", "Azure AD Tenant ID", "For M365 Group management (same tenant as MFA Reset)", Required: false),
                new("GraphClientId", "Graph App Client ID", "App with Group.ReadWrite.All permission", Required: false),
                new("GraphCredentialTarget", "Graph Credential Vault Target", "PasswordVault resource name", Required: false, DefaultValue: "Graph_GroupManagement")
            ]
        },
        new()
        {
            Id = "Comms10k",
            DisplayName = "Comms-10k",
            Description = "Manage the broadcast distribution list for company-wide communications.",
            Route = "comms-10k",
            IconCss = "bi bi-people-fill-nav-menu",
            SortOrder = 160,
            EnabledByDefault = false,
            IsSystemModule = false,
            MainPermission = new("Access", "Comms10k", FailClosed: true),
            ConfigFields = [
                new("TargetGroupName", "Target Group", "AD group name to manage")
            ]
        },
        new()
        {
            Id = "MfaReset",
            DisplayName = "MFA Reset",
            Description = "Reset multi-factor authentication methods for users, forcing re-registration at next sign-in.",
            Route = "mfa-reset",
            IconCss = "bi bi-person-fill-nav-menu",
            SortOrder = 750,
            EnabledByDefault = false,
            IsSystemModule = false,
            MainPermission = new("Access", "MfaReset", FailClosed: true),
            ConfigFields = [
                new("TenantId", "Azure AD Tenant ID", "Your Entra tenant GUID"),
                new("ClientId", "App Registration Client ID", "Graph API app registration (needs UserAuthenticationMethod.ReadWrite.All)"),
                new("CredentialTarget", "Credential Vault Target", "PasswordVault resource name for client secret", DefaultValue: "Graph_MFAResets")
            ]
        },
        new()
        {
            Id = "ConferenceRooms",
            DisplayName = "Conference Rooms",
            Description = "Configure room lists, metadata, and booking policies for Exchange conference rooms.",
            Route = "conference-rooms",
            IconCss = "bi bi-house-door-fill-nav-menu",
            SortOrder = 350,
            EnabledByDefault = false,
            IsSystemModule = false,
            MainPermission = new("Access", "ConferenceRooms", FailClosed: true),
            ConfigFields = []
        },
        new()
        {
            Id = "DhcpAuthorization",
            DisplayName = "DHCP Authorization",
            Description = "Authorize and deauthorize DHCP servers in Active Directory. Requires Enterprise Admin credentials via Secret Server.",
            Route = "dhcp-authorization",
            IconCss = "bi bi-gear-fill-nav-menu",
            SortOrder = 800,
            EnabledByDefault = false,
            IsSystemModule = false,
            MainPermission = new("Access", "DhcpAuthorization", FailClosed: true),
            ConfigFields = [
                new("DelineaSecretId", "Delinea Secret ID", "Secret Server ID for the Enterprise Admin credential used for DHCP operations")
            ]
        },
        new()
        {
            Id = "AdminSettings",
            DisplayName = "Admin Settings",
            Description = "Configure which AD groups have access to each application section.",
            Route = "admin-settings",
            IconCss = "bi bi-gear-fill-nav-menu",
            SortOrder = 900,
            EnabledByDefault = true,
            IsSystemModule = true,
            MainPermission = new("Access", "AdminSettings")
        },
        new()
        {
            Id = "AdminEventLog",
            DisplayName = "Event Log",
            Description = "View audit trail of all actions performed through this application.",
            Route = "admin-event-log",
            IconCss = "bi bi-gear-fill-nav-menu",
            SortOrder = 910,
            EnabledByDefault = true,
            IsSystemModule = true,
            MainPermission = new("Access", "AdminSettings")
        }
    ];

    private static void Validate(List<AdminModuleDescriptor> modules)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var policyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in modules)
        {
            if (!ids.Add(m.Id))
                throw new InvalidOperationException($"Duplicate module ID: '{m.Id}'");
            if (!routes.Add(m.Route))
                throw new InvalidOperationException($"Duplicate module route: '{m.Route}'");

            if (!policyAliases.Add(m.MainPermission.PolicyAlias) && !m.IsSystemModule)
                throw new InvalidOperationException($"Duplicate policy alias: '{m.MainPermission.PolicyAlias}' in module '{m.Id}'");

            foreach (var gp in m.GranularPermissions)
            {
                if (!policyAliases.Add(gp.PolicyAlias))
                    throw new InvalidOperationException($"Duplicate policy alias: '{gp.PolicyAlias}' in module '{m.Id}'");
            }
        }
    }
}
