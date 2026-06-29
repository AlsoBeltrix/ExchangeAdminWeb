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
        foreach (var m in _modules.Where(m => !m.IsSystemModule && !m.IsConfigOnly).OrderBy(m => m.SortOrder))
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

        // Fallback policy for endpoints that declare NO authorization metadata.
        // True deny-by-default: an undeclared endpoint (a future health check, download,
        // or minimal API added without an [Authorize] attribute) is blocked for EVERY
        // user until it declares its own catalog-backed policy — not merely opened to any
        // authenticated user. Do NOT reuse groupPolicy here either: that would silently
        // subject undeclared endpoints to the legacy app-wide AllowedGroups gate the
        // Constitution removed. An endpoint that needs access must declare its own policy.
        //
        // The Blazor component + SignalR hub endpoints are exempt because
        // MapRazorComponents<App>().RequireAuthorization() (Program.cs) stamps the default
        // policy onto them, so this fallback never applies to them. Static assets are
        // served by UseStaticFiles() before UseAuthorization() and never reach this check.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(_ => false)
            .Build();

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
                    .AddRequirements(new GroupAuthorizationRequirement(mainAlias, dynamic: true)));
            }

            foreach (var gp in module.GranularPermissions)
            {
                if (!registered.Add(gp.PolicyAlias)) continue;

                options.AddPolicy(gp.PolicyAlias, policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new GroupAuthorizationRequirement(mainAlias, dynamic: true))
                    .AddRequirements(new GroupAuthorizationRequirement(gp.PolicyAlias, dynamic: true)));
            }
        }
    }

    private static List<AdminModuleDescriptor> RegisterAll() =>
    [
        new()
        {
            Id = "ExchangeOnline",
            DisplayName = "Exchange Online",
            Description = "Exchange Online PowerShell connection. Required by all Exchange-dependent modules.",
            Route = "exchange-online-config",
            IconCss = "bi bi-cloud-fill-nav-menu",
            Category = "Exchange",
            SortOrder = 50,
            EnabledByDefault = false,
            IsSystemModule = false,
            IsConfigOnly = true,
            Version = "1.0.1",
            MainPermission = new("Access", "ExchangeOnline"),
            ConfigFields = [
                new("AppId", "App Registration ID (GUID)", "Azure AD app registration for EXO PowerShell"),
                new("Organization", "Organization", "e.g. contoso.onmicrosoft.com"),
                new("CertificateSubject", "Certificate Subject", "e.g. CN=EXO-Automation", DefaultValue: "CN=EXO-Automation")
            ]
        },
        new()
        {
            Id = "MailboxPermissions",
            DisplayName = "Mailbox Permissions",
            Description = "Grant or revoke Full Access and Send As permissions on Exchange Online mailboxes.",
            Route = "mailbox-permissions",
            IconCss = "bi bi-person-fill-nav-menu",
            Category = "Exchange",
            SortOrder = 100,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.0.3",
            DependsOn = "ExchangeOnline",
            MainPermission = new("Access", "MailboxPermissions", FailClosed: true),
            GranularPermissions = [new("OnPrem", "MailboxPermissionsOnPrem", FailClosed: true)],
            ConfigFields = [
                new("DelineaSecretId", "On-Prem Exchange Delinea Secret ID", "Secret Server ID for the on-prem Exchange credential used by mailbox permission operations", Required: false),
                new("PreventSelfGrant", "Prevent Self-Grant", "Block users from granting permissions to themselves — applies to all permission operations (true/false)", Required: false, DefaultValue: "true")
            ]
        },
        new()
        {
            Id = "CalendarPermissions",
            DisplayName = "Calendar",
            Description = "Set or remove calendar sharing permissions on Exchange Online mailboxes.",
            Route = "calendar-permissions",
            IconCss = "bi bi-calendar-fill-nav-menu",
            Category = "Exchange",
            SortOrder = 200,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.0.2",
            DependsOn = "ExchangeOnline",
            MainPermission = new("Access", "CalendarPermissions", FailClosed: true),
            GranularPermissions = [new("OnPrem", "CalendarPermissionsOnPrem", FailClosed: true)],
            ConfigFields = [
                new("DelineaSecretId", "On-Prem Exchange Delinea Secret ID", "Secret Server ID for the on-prem Exchange credential used by calendar permission operations", Required: false)
            ]
        },
        new()
        {
            Id = "Migration",
            DisplayName = "Exchange Migration",
            Description = "Check migration eligibility and create migration batches for Exchange Online and on-premises mailboxes.",
            Route = "migration",
            IconCss = "bi bi-arrow-left-right-nav-menu",
            Category = "Exchange",
            SortOrder = 300,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.1.3",
            DependsOn = "ExchangeOnline",
            MainPermission = new("Access", "MigrationCheck", FailClosed: true),
            GranularPermissions = [new("Create", "MigrationCreate", FailClosed: true), new("Manage", "MigrationManage", FailClosed: true)],
            ConfigFields = [
                new("HybridEndpoint", "Hybrid Endpoint", "Migration endpoint name", DefaultValue: "hybrid1"),
                new("CloudTargetDeliveryDomain", "Cloud Target Domain", "e.g. contoso.mail.onmicrosoft.com"),
                new("OnPremTargetDeliveryDomain", "On-Prem Target Domain", "e.g. contoso.com"),
                new("OnPremTargetDatabases", "On-Prem Target Databases", "Comma-separated target mailbox databases. Exchange distributes mailboxes across all listed databases in each move-back batch."),
                new("DelineaSecretId", "On-Prem Exchange Delinea Secret ID", "Secret Server ID for the on-prem Exchange credential used by migration eligibility checks", Required: false),
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
            Category = "Exchange",
            SortOrder = 400,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.0.1",
            DependsOn = "ExchangeOnline",
            MainPermission = new("Access", "DelegationReport")
        },
        new()
        {
            Id = "MessageTrace",
            DisplayName = "Message Analysis",
            Description = "Analyze message headers and trace delivery through Exchange Online and on-premises transport logs.",
            Route = "message-analysis",
            IconCss = "bi bi-envelope-fill-nav-menu",
            Category = "Exchange",
            SortOrder = 500,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.1.1",
            DependsOn = "ExchangeOnline",
            MainPermission = new("Access", "MessageTrace", FailClosed: true),
            ConfigFields = [
                new("DelineaSecretId", "On-Prem Exchange Delinea Secret ID", "Secret Server ID for the on-prem Exchange credential used by message tracking", Required: false)
            ]
        },
        new()
        {
            Id = "RecipientLookup",
            DisplayName = "Recipient Lookup",
            Description = "Look up mailbox details including size, quotas, archive status, and recipient type.",
            Route = "recipient-lookup",
            IconCss = "bi bi-search-nav-menu",
            Category = "Exchange",
            SortOrder = 600,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.0.2",
            DependsOn = "ExchangeOnline",
            MainPermission = new("Access", "RecipientLookup"),
            ConfigFields = [
                new("DelineaSecretId", "On-Prem Exchange Delinea Secret ID", "Secret Server ID for the on-prem Exchange credential used by recipient lookup", Required: false)
            ]
        },
        new()
        {
            Id = "OutOfOffice",
            DisplayName = "Out of Office",
            Description = "View or configure automatic reply (out of office) settings for Exchange Online mailboxes.",
            Route = "out-of-office",
            IconCss = "bi bi-clock-fill-nav-menu",
            Category = "Exchange",
            SortOrder = 700,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.0.2",
            DependsOn = "ExchangeOnline",
            MainPermission = new("Access", "OutOfOffice", FailClosed: true)
        },
        new()
        {
            Id = "BlockedSenders",
            DisplayName = "Blocked Senders",
            Description = "View and unblock Exchange Online blocked senders (accounts blocked from sending mail for outbound spam).",
            Route = "blocked-senders",
            IconCss = "bi bi-envelope-fill-nav-menu",
            Category = "Exchange",
            SortOrder = 650,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.0.0",
            DependsOn = "ExchangeOnline",
            MainPermission = new("Access", "BlockedSenders", FailClosed: true),
            GranularPermissions = [new("Unblock", "BlockedSendersUnblock", FailClosed: true)]
        },
        new()
        {
            Id = "GroupManagement",
            DisplayName = "AD Group Management",
            Description = "Search, view membership, and manage on-premises Active Directory groups.",
            Route = "group-management",
            IconCss = "bi bi-people-fill-nav-menu",
            Category = "Directory & Groups",
            SortOrder = 150,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "2.0.3",
            MainPermission = new("Access", "GroupManagement", FailClosed: true),
            GranularPermissions = [new("OnPrem", "GroupManagementOnPrem", FailClosed: true)],
            ConfigFields = [
                new("DelineaSecretId", "On-Prem AD Delinea Secret ID", "Secret Server ID for the AD credential used by group membership operations", Required: false)
            ]
        },
        new()
        {
            Id = "M365GroupManagement",
            DisplayName = "M365 Group Management",
            Description = "Create, modify, and delete Microsoft 365 groups and manage their members and owners via Graph API.",
            Route = "m365-group-management",
            IconCss = "bi bi-people-fill-nav-menu",
            Category = "Directory & Groups",
            SortOrder = 155,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.1.0",
            MainPermission = new("Access", "M365GroupManagement", FailClosed: true),
            ConfigFields = [
                new("GraphDelineaSecretId", "Graph App Delinea Secret ID", "Secret Server secret with fields: Tenant ID, Application ID, Client Secret (requires Group.ReadWrite.All)")
            ]
        },
        new()
        {
            Id = "Comms10k",
            DisplayName = "Comms-10k",
            Description = "Manage the broadcast distribution list for company-wide communications.",
            Route = "comms-10k",
            IconCss = "bi bi-people-fill-nav-menu",
            Category = "Directory & Groups",
            SortOrder = 160,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.0.3",
            MainPermission = new("Access", "Comms10k", FailClosed: true),
            ConfigFields = [
                new("TargetGroupName", "Target Group", "AD group name to manage", FieldType: ConfigFieldType.AdGroup),
                new("DelineaSecretId", "AD Delinea Secret ID", "Secret Server ID for the AD credential used by Comms-10k operations")
            ]
        },
        new()
        {
            Id = "MfaReset",
            DisplayName = "MFA Reset",
            Description = "Reset multi-factor authentication methods for users, forcing re-registration at next sign-in.",
            Route = "mfa-reset",
            IconCss = "bi bi-person-fill-nav-menu",
            Category = "Identity & Access",
            SortOrder = 750,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.0.3",
            MainPermission = new("Access", "MfaReset", FailClosed: true),
            ConfigFields = [
                new("GraphDelineaSecretId", "Graph App Delinea Secret ID", "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields")
            ]
        },
        new()
        {
            Id = "AccountLockoutRemediation",
            DisplayName = "Account Lockout Remediation",
            Description = "Identify account lockout source machines and log selected accounts off from implicated or scoped domain computers.",
            Route = "account-lockout-remediation",
            IconCss = "bi bi-person-fill-nav-menu",
            Category = "Identity & Access",
            SortOrder = 780,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.0.0",
            MainPermission = new("Access", "AccountLockoutRemediation", FailClosed: true),
            GranularPermissions = [
                new("Logoff", "AccountLockoutRemediationLogoff", FailClosed: true)
            ],
            ConfigFields = [
                new("DelineaSecretId", "AD Delinea Secret ID", "Secret Server ID for the AD credential used to read lockout events, query computer sessions, and log off target sessions"),
                new("DefaultThrottleLimit", "Default Throttle Limit", "Default WinRM fan-out throttle limit. Valid range: 1-256.", Required: false, DefaultValue: "32"),
                new("MaxSweepTargets", "Maximum Sweep Targets", "Maximum computers allowed in a scoped sweep. Use 0 for no module limit.", Required: false, DefaultValue: "10000")
            ]
        },
        new()
        {
            Id = "ConferenceRooms",
            DisplayName = "Conference Rooms",
            Description = "Configure room lists, metadata, booking policies, calendar permissions, and room type templates for Exchange conference rooms.",
            Route = "conference-rooms",
            IconCss = "bi bi-calendar-fill-nav-menu",
            Category = "Exchange",
            SortOrder = 350,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "2.0.11",
            DependsOn = "ExchangeOnline",
            MainPermission = new("Access", "ConferenceRooms", FailClosed: true),
            ConfigFields = [
                new("DelineaSecretId", "AD Delinea Secret ID", "Secret Server ID for the on-prem AD credential used to write dir-synced room attributes (City/State/Country) via Set-ADUser during Room Finder apply"),
                new("DefaultArbiterGroup", "Default Arbiter Group", "Default group with editor permissions on room calendars (e.g. room-admins@example.com)"),
                new("ExecConfCoordinatorsGroup", "Exec Conf Coordinators Group", "Group for executive conference coordinators (e.g. exec-coordinators@example.com)"),
                new("ConfExecAdminsGroup", "Conf Exec Admins Group", "Executive conference admins group (e.g. exec-admins@example.com)"),
                new("ConfExecVPsGroup", "Conf Exec VPs Group", "Executive VP booking group (e.g. exec-vps@example.com)"),
                new("ConfAdminsGroup", "Conf Admins Group", "General conference admins group for restricted rooms (e.g. conf-admins@example.com)"),
                new("ConfCEOGroup", "CEO Room Group", "Group for CEO room booking (e.g. ceo-room@example.com)"),
                new("ConfExceptionGroup", "Exception Room Group", "Group for exception room booking (e.g. exception-room@example.com)"),
                new("ADGTAdminsGroup", "ADGT Meeting Room Admins", "ADGT site-specific admins group (e.g. adgt-admins@example.com)"),
                new("RestrictedMailTip", "Restricted Room MailTip", "Default mail tip for restricted rooms. Leave blank for built-in default."),
                new("ExecMailTip", "Executive Room MailTip", "Mail tip for executive rooms. Leave blank for built-in default."),
                new("RestrictedContactEmail", "Restricted Contact Email", "Contact email shown in restricted room responses (e.g. conf-admins@example.com)"),
                new("ExecContactEmail", "Exec Contact Email", "Contact email shown in exec room responses (e.g. exec-admins@example.com)"),
                new("ADGTContactEmail", "ADGT Contact Email", "Contact email for ADGT restricted rooms (e.g. adgt-admins@example.com)")
            ]
        },
        new()
        {
            Id = "NamedLocations",
            DisplayName = "Named Locations",
            Description = "Manage Entra ID Conditional Access named locations (IP ranges and country/region lists).",
            Route = "named-locations",
            IconCss = "bi bi-geo-alt-fill-nav-menu",
            Category = "Identity & Access",
            SortOrder = 790,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.0.3",
            MainPermission = new("Access", "NamedLocations", FailClosed: true),
            ConfigFields = [
                new("GraphDelineaSecretId", "Graph App Delinea Secret ID", "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields (requires Policy.ReadWrite.ConditionalAccess)")
            ]
        },
        new()
        {
            Id = "EmergencyDisable",
            DisplayName = "Emergency Disable",
            Description = "Rapidly disable a compromised user account across on-prem AD and Entra ID with session revocation.",
            Route = "emergency-disable",
            IconCss = "bi bi-person-fill-nav-menu",
            Category = "Identity & Access",
            SortOrder = 740,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.0.5",
            MainPermission = new("Access", "EmergencyDisable", FailClosed: true),
            GranularPermissions = [],
            ConfigFields = [
                new("DelineaSecretId", "AD Delinea Secret ID", "Secret Server ID for the AD credential with account disable and password reset permissions"),
                new("GraphDelineaSecretId", "Graph Delinea Secret ID", "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields"),
                new("NotifySecurityTeam", "Security Team Email", "Email address for immediate notification on disable actions")
            ]
        },
        new()
        {
            Id = "DhcpAuthorization",
            DisplayName = "DHCP Authorization",
            Description = "Authorize and deauthorize DHCP servers in Active Directory. Requires Enterprise Admin credentials via Secret Server.",
            Route = "dhcp-authorization",
            IconCss = "bi bi-gear-fill-nav-menu",
            Category = "Infrastructure",
            SortOrder = 800,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.2.3",
            MainPermission = new("Access", "DhcpAuthorization", FailClosed: true),
            ConfigFields = [
                new("DelineaSecretId", "Enterprise Admin Delinea Secret ID", "Secret Server ID for the Enterprise Admin credential used for DHCP operations")
            ]
        },
        new()
        {
            Id = "LicensingUpdates",
            DisplayName = "Licensing Updates",
            Description = "Bulk update Exchange licensing SKU assignments (extensionAttribute11) via CSV upload.",
            Route = "licensing-updates",
            IconCss = "bi bi-list-nested-nav-menu",
            Category = "Exchange",
            SortOrder = 450,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.0.2",
            MainPermission = new("Access", "LicensingUpdates", FailClosed: true),
            GranularPermissions = [],
            ConfigFields = [
                new("DelineaSecretId", "AD Delinea Secret ID", "Secret Server ID for the AD credential used to write extensionAttribute11"),
                new("AllowedLicenseTypes", "Allowed License Types", "Comma-separated valid license values", Required: false, DefaultValue: "E5,EOP2+SOP2,F3,F3+EOP1")
            ]
        },
        new()
        {
            Id = "ADAttributeEditor",
            DisplayName = "AD Attribute Editor",
            Description = "View and edit allowlisted Active Directory attributes for on-premises user accounts.",
            Route = "ad-attribute-editor",
            IconCss = "bi bi-person-fill-nav-menu",
            Category = "Directory & Groups",
            SortOrder = 170,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.3.5",
            MainPermission = new("Access", "ADAttributeEditor", FailClosed: true),
            GranularPermissions = [
                new("Level1", "ADAttributeEditorLevel1", FailClosed: true),
                new("Level2", "ADAttributeEditorLevel2", FailClosed: true),
                new("Level3", "ADAttributeEditorLevel3", FailClosed: true)
            ],
            ConfigFields = [
                new("DelineaSecretId", "AD Delinea Secret ID", "Secret Server ID for the AD credential used by attribute read/write operations"),
                new("DefaultSearchBase", "Allowed Search Bases", "Optional semicolon-separated OU DNs that limit which users can be edited (e.g. OU=Users,DC=ad,DC=contoso,DC=com;OU=Contractors,DC=ad,DC=contoso,DC=com)", Required: false)
            ]
        },
        new()
        {
            Id = "AdminSettings",
            DisplayName = "Admin Settings",
            Description = "Configure which AD groups have access to each application section.",
            Route = "admin-settings",
            IconCss = "bi bi-gear-fill-nav-menu",
            Category = "Administration",
            SortOrder = 900,
            EnabledByDefault = true,
            IsSystemModule = true,
            Version = "1.0.1",
            MainPermission = new("Access", "AdminSettings")
        },
        new()
        {
            Id = "AdminEventLog",
            DisplayName = "Event Log",
            Description = "View audit trail of all actions performed through this application.",
            Route = "admin-event-log",
            IconCss = "bi bi-gear-fill-nav-menu",
            Category = "Administration",
            SortOrder = 910,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.0.3",
            MainPermission = new("Access", "EventLog", FailClosed: true),
            GranularPermissions = [new("Undo", "UndoAuditedActions", FailClosed: true)]
        }
    ];

    private static void Validate(List<AdminModuleDescriptor> modules)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var policyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, AdminModuleDescriptor>(StringComparer.OrdinalIgnoreCase);

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

            byId[m.Id] = m;
        }

        // Validate dependency references
        foreach (var m in modules)
        {
            if (m.DependsOn == null) continue;

            if (string.Equals(m.DependsOn, m.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Module '{m.Id}' has a self-dependency.");

            if (!byId.ContainsKey(m.DependsOn))
                throw new InvalidOperationException($"Module '{m.Id}' depends on unknown module '{m.DependsOn}'.");

            // Detect cycles: walk the DependsOn chain
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { m.Id };
            var current = m.DependsOn;
            while (current != null)
            {
                if (!visited.Add(current))
                    throw new InvalidOperationException($"Dependency cycle detected involving module '{m.Id}'.");
                current = byId.TryGetValue(current, out var parent) ? parent.DependsOn : null;
            }
        }
    }
}
