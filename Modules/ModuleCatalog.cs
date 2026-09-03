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
        // user until it declares its own catalog-backed policy - not merely opened to any
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
            MainPermission = new(
                "Access",
                "ExchangeOnline",
                "Nothing on its own - no page or code checks this permission; the Exchange Online connection settings page is reached through Admin Settings access instead."),
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
            Version = "1.1.0",
            DependsOn = "ExchangeOnline",
            MainPermission = new(
                "Access",
                "MailboxPermissions",
                "Open the module, look up a mailbox, and grant or revoke Full Access and Send As on Exchange Online mailboxes.",
                FailClosed: true),
            GranularPermissions = [
                new("OnPrem", "MailboxPermissionsOnPrem",
                    "Also grant or revoke those permissions when the mailbox lives on the on-premises Exchange servers; without it, on-premises targets are refused and the operator is told to escalate.",
                    FailClosed: true)
            ],
            ConfigFields = [
                new("DelineaSecretId", "On-Prem Exchange Delinea Secret ID", "Secret Server ID for the on-prem Exchange credential used by mailbox permission operations", Required: false),
                new("PreventSelfGrant", "Prevent Self-Grant", "Block users from granting permissions to themselves - applies to all permission operations", Required: false, DefaultValue: "true", FieldType: ConfigFieldType.Boolean)
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
            Version = "1.1.0",
            DependsOn = "ExchangeOnline",
            MainPermission = new(
                "Access",
                "CalendarPermissions",
                "Open the module and set or remove calendar sharing permissions on Exchange Online mailboxes.",
                FailClosed: true),
            GranularPermissions = [
                new("OnPrem", "CalendarPermissionsOnPrem",
                    "Also set or remove calendar permissions when the mailbox lives on the on-premises Exchange servers; without it, on-premises targets are refused and the operator is told to escalate.",
                    FailClosed: true)
            ],
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
            // 1.8.0: CSV export of the migration batch status list (docs/ModuleCsvExport-Plan.md).
            Version = "1.8.0",
            DependsOn = "ExchangeOnline",
            MainPermission = new(
                "Access",
                "MigrationCheck",
                "Open the module, test whether a mailbox is eligible to migrate, and read the migration batch list; no batch is created or changed.",
                FailClosed: true),
            GranularPermissions = [
                new("Create", "MigrationCreate",
                    "Create migration batches for eligible mailboxes, singly or from a bulk list, which starts real mailbox moves.",
                    FailClosed: true),
                new("Manage", "MigrationManage",
                    "Complete, stop, resume and delete existing migration batches, singly or in bulk; deleting a batch cancels the moves in it.",
                    FailClosed: true)
            ],
            ConfigFields = [
                new("HybridEndpoint", "Hybrid Endpoint", "Migration endpoint name", DefaultValue: "hybrid1"),
                new("CloudTargetDeliveryDomain", "Cloud Target Domain", "e.g. contoso.mail.onmicrosoft.com"),
                new("OnPremTargetDeliveryDomain", "On-Prem Target Domain", "e.g. contoso.com"),
                new("OnPremTargetDatabases", "On-Prem Target Databases", "Comma-separated target mailbox databases. Exchange distributes mailboxes across all listed databases in each move-back batch."),
                new("DelineaSecretId", "On-Prem Exchange Delinea Secret ID", "Secret Server ID for the on-prem Exchange credential used by migration eligibility checks", Required: false),
                new("CloudQuotaGB", "Cloud Quota (GB)", "Max size for cloud migration, applied to the primary mailbox and the archive separately. Combined size is not checked.", DefaultValue: "99"),
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
            MainPermission = new(
                "Access",
                "DelegationReport",
                "Open the module and read who holds Full Access, Send As and calendar rights on any mailbox; read-only, nothing is changed.")
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
            Version = "1.4.1",
            DependsOn = "ExchangeOnline",
            MainPermission = new(
                "Access",
                "MessageTrace",
                "Open the module and trace any user's mail, reading senders, recipients, subjects, headers and transport log detail.",
                FailClosed: true),
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
            MainPermission = new(
                "Access",
                "RecipientLookup",
                "Open the module and read mailbox details such as size, quotas, archive state and recipient type; read-only."),
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
            Version = "1.1.0",
            DependsOn = "ExchangeOnline",
            MainPermission = new(
                "Access",
                "OutOfOffice",
                "Open the module and read or change any mailbox's automatic reply state, schedule and reply text.",
                FailClosed: true)
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
            // 1.1.0: unblock now gates the TARGET through the protected-principal check. The module
            // previously re-checked only the operator, so a protected principal could be unblocked.
            // 1.4.0: CSV export of the blocked-sender list (docs/ModuleCsvExport-Plan.md).
            Version = "1.4.0",
            DependsOn = "ExchangeOnline",
            MainPermission = new(
                "Access",
                "BlockedSenders",
                "Open the module and read which accounts Exchange Online has blocked from sending mail for outbound spam; read-only on its own.",
                FailClosed: true),
            GranularPermissions = [
                new("Unblock", "BlockedSendersUnblock",
                    "Unblock a listed account, restoring its ability to send mail before the outbound spam that blocked it has necessarily been dealt with.",
                    FailClosed: true)
            ]
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
            // 2.3.1: the member listing reads the group's member attribute and resolves each
            // member in its own domain - Get-ADGroupMember faulted wholesale on a cross-domain
            // nested member (ADWS GetADGroupMemberFault, found validating nesting on dev).
            // 2.4.0: both write paths protection-check the TARGET GROUP on a full snapshot,
            // with a servicer override (docs/ProtectedGroupWriteTarget-Plan.md).
            // 2.5.0: group search queries the forest global catalog instead of the credential's
            // home domain only, and results show which domain each group lives in.
            // 2.6.0: protected target groups answer at first query - a non-servicer is refused
            // at selection and never sees members; servicers see a Protected badge. The
            // write-path gates stay as the backstop.
            // 2.7.0: a target group named only by sam/name/mail is resolved through the forest
            // global catalog and re-read in its own domain (a foreign-domain group could not be
            // written to at all), and the membership pre-check/read-back asks the group's own
            // domain for its forward member link instead of the member's local back-link.
            // 2.8.0: add and remove write the group's member attribute directly - the
            // Add-/Remove-ADGroupMember form made the cmdlet resolve the MEMBER on the group's
            // DC, which cannot see a member from another forest domain.
            // 2.9.0: bulk remove - a checkbox per member, select-all, one confirmed batch that
            // runs each member through the same per-member handler as the single Remove
            // (per-row authorization, protection, read-back, audit), a per-row outcome table,
            // and one batch summary audit and email (docs/GroupBulkActions-Plan.md S2).
            // 2.10.0: bulk add via paste list - every line resolved against the forest in one
            // batched query per chunk (user or group), a resolution table, and "Add resolved"
            // running each line through the same per-member handler as the single Add
            // (docs/GroupBulkActions-Plan.md S3).
            Version = "2.10.0",
            MainPermission = new(
                "Access",
                "GroupManagement",
                "Open the module and search on-premises Active Directory groups across the forest and read their membership; read-only on its own.",
                FailClosed: true),
            GranularPermissions = [
                new("OnPrem", "GroupManagementOnPrem",
                    "Add and remove members on any on-premises Active Directory group found here - the only permission in this module that writes to the directory.",
                    FailClosed: true)
            ],
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
            Version = "1.3.0",
            MainPermission = new(
                "Access",
                "M365GroupManagement",
                "Open the module and create, rename, delete Microsoft 365 groups and change their members and owners through Graph; deleting a group is the destructive action here.",
                FailClosed: true),
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
            Version = "1.2.0",
            MainPermission = new(
                "Access",
                "Comms10k",
                "Open the module and replace the entire membership of the company-wide broadcast distribution list from an uploaded CSV; anyone absent from the file is removed.",
                FailClosed: true),
            ConfigFields = [
                new("TargetGroupName", "Target Group", "AD group name to manage", FieldType: ConfigFieldType.AdGroup),
                new("DelineaSecretId", "AD Delinea Secret ID", "Secret Server ID for the AD credential used by Comms-10k operations")
            ]
        },
        new()
        {
            Id = "SelfServiceGroups",
            DisplayName = "Self-Service Groups",
            Description = "View and manage membership of the on-premises Active Directory groups you own and are permitted to update.",
            Route = "self-service-groups",
            // v1.1.1: fixed the DACL eligibility read (Get-Acl AD:\ returned an empty .Access here,
            // excluding every group; now reads Get-ADGroup -Properties nTSecurityDescriptor).
            // v1.2.0: member listing (current members shown per group, per-user Remove) + the member
            // add box uses the shared AD user typeahead.
            IconCss = "bi bi-people-fill-nav-menu",
            Category = "Directory & Groups",
            SortOrder = 165,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.4.1: same member-attribute listing fix as GroupManagement 2.3.1 - the
            // Get-ADGroupMember read faulted on a cross-domain nested member.
            // 1.5.0: the shared executor protection-checks the TARGET GROUP after eligibility;
            // a protected group is not a self-service object (pgwt AC4).
            // 1.6.0: 1.5.0's target gate removed (owner ruling 2026-08-31, .agents/decisions.md):
            // owners always edit owned groups here; eligibility already means native AD write
            // rights, so the app-side refusal was inconvenience, not security.
            // 1.7.0: removing a cross-domain nested group works - the listed row's DN rides with
            // its GUID so the member resolves in its OWN domain (it was refused with "could not
            // be resolved right now"), the membership pre-check/read-back reads the group's
            // forward member link in the group's domain, and the write is routed there too.
            // 1.8.0: the write itself sets the group's member attribute - routing it was not
            // enough, because Remove-ADGroupMember resolved the MEMBER on that same DC and
            // failed with "Cannot find an object with identity ... under: 'DC=ad,...'".
            // 1.9.0: bulk remove - a checkbox per removable member, select-all, one confirmed
            // batch (a nested group carries its one-way warning inside the confirmation) that
            // runs each member through the same per-member handler as the single Remove
            // (per-row authorization, eligibility, protection, read-back, audit, affected-member
            // notification), a per-row outcome table, and one batch summary audit and email
            // (docs/GroupBulkActions-Plan.md S4).
            Version = "1.9.0",
            MainPermission = new(
                "Access",
                "SelfServiceGroups",
                "Open the module and add or remove members on only those on-premises Active Directory groups the signed-in operator already owns or has directory write rights to.",
                FailClosed: true),
            ConfigFields = [
                new("DelineaSecretId", "On-Prem AD Delinea Secret ID", "Secret Server ID for the AD credential used to read group ownership/ACLs and write membership")
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
            // 1.1.0: protection now resolves through Exchange. The AD-only lookup reported every
            // cloud-only user as "no AD object" and skipped the check, which for a Graph module is
            // the normal case - so protection was close to inert here.
            Version = "1.2.0",
            MainPermission = new(
                "Access",
                "MfaReset",
                "Open the module and clear a user's registered multi-factor authentication methods, which locks them out of sign-in until they re-register.",
                FailClosed: true),
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
            Version = "1.2.0",
            MainPermission = new(
                "Access",
                "AccountLockoutRemediation",
                "Open the module and read domain controller lockout events to find which machines are locking an account out; investigation only, nothing is changed.",
                FailClosed: true),
            GranularPermissions = [
                new("Logoff", "AccountLockoutRemediationLogoff",
                    "Log the account off the implicated or scoped computers over WinRM, ending its live sessions there along with any unsaved work in them.",
                    FailClosed: true)
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
            Version = "2.5.0",
            DependsOn = "ExchangeOnline",
            MainPermission = new(
                "Access",
                "ConferenceRooms",
                "Open the module and change room metadata, room lists, booking policies, calendar permissions and room-type templates, which decides who may book each room.",
                FailClosed: true),
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
            // 1.1.0: CSV export of the named-location list (docs/ModuleCsvExport-Plan.md).
            Version = "1.1.0",
            MainPermission = new(
                "Access",
                "NamedLocations",
                "Open the module and create, edit or delete the Conditional Access named locations - the IP ranges and countries tenant sign-in policies are evaluated against.",
                FailClosed: true),
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
            Version = "1.2.0",
            MainPermission = new(
                "Access",
                "EmergencyDisable",
                "Open the module and disable a user in on-premises AD and Entra ID, reset their password and revoke their sign-in sessions in one run - the user is locked out immediately.",
                FailClosed: true),
            GranularPermissions = [],
            ConfigFields = [
                new("DelineaSecretId", "AD Delinea Secret ID", "Secret Server ID for the AD credential with account disable and password reset permissions"),
                new("GraphDelineaSecretId", "Graph Delinea Secret ID", "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields"),
                new("NotifySecurityTeam", "Security Team Email", "Email address for immediate notification on disable actions")
            ]
        },
        new()
        {
            Id = "RiskyUsers",
            DisplayName = "Risky Users",
            Description = "Review Microsoft Entra ID Protection risky users and their risk history.",
            Route = "risky-users",
            IconCss = "bi bi-person-fill-nav-menu",
            Category = "Identity & Access",
            SortOrder = 745,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.1.0",
            MainPermission = new(
                "Access",
                "RiskyUsers",
                "Open the module and read which users Entra ID Protection considers risky, with their risk level, state and detection history; read-only.",
                FailClosed: true),
            GranularPermissions = [
                new("Remediate", "RiskyUsersRemediate",
                    "Dismiss a user's risk, or mark them confirmed safe or confirmed compromised, changing the risk state that Conditional Access policies are evaluated against.",
                    FailClosed: true)
            ],
            ConfigFields = [
                new("GraphDelineaSecretId", "Graph App Delinea Secret ID", "Secret Server secret with fields: Tenant ID, Application ID, Client Secret (requires IdentityRiskyUser.Read.All, plus IdentityRiskyUser.ReadWrite.All for remediation). Requires Microsoft Entra ID P2."),
                new("MaxRows", "Max Rows", "Maximum risky users fetched per query (Graph caps at 500)", Required: false, DefaultValue: "500")
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
            // 1.3.0: CSV export of the authorized-server list (docs/ModuleCsvExport-Plan.md).
            Version = "1.3.0",
            MainPermission = new(
                "Access",
                "DhcpAuthorization",
                "Open the module and authorize or deauthorize DHCP servers in Active Directory; deauthorizing a live server stops it issuing address leases.",
                FailClosed: true),
            ConfigFields = [
                new("DelineaSecretId", "Enterprise Admin Delinea Secret ID", "Secret Server ID for the Enterprise Admin credential used for DHCP operations")
            ]
        },
        new()
        {
            Id = "BitLockerRecovery",
            DisplayName = "BitLocker Recovery",
            Description = "Look up BitLocker recovery keys, including keys for machines removed from Active Directory.",
            Route = "bitlocker-recovery",
            // Reused deliberately: a padlock would read better, but no shield or lock class
            // exists in the host CSS today and the package validator rejects an icon class it
            // cannot find. Adding one is a separate change.
            IconCss = "bi bi-gear-fill-nav-menu",
            Category = "Infrastructure",
            SortOrder = 810,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.1.0: mandatory ticket before any search, written on the search and
            // reveal audit events; ValidateTickets per-module validation switch.
            Version = "1.2.0",
            // Fail-closed: a recovery key decrypts an entire disk.
            MainPermission = new(
                "Access",
                "BitLockerRecovery",
                "Open the module and search for and reveal BitLocker recovery keys, each of which decrypts a whole disk; a ticket is required and every search and reveal is audited.",
                FailClosed: true),
            ConfigFields = [
                new(
                    "ArchiveDatabasePath",
                    "Archive Database Path",
                    "Full path to the BitLocker recovery key SQLite database written by the scheduled export. Must be on a local disk, not a UNC path."),
                new(
                    "DelineaSecretId",
                    "AD Reader Delinea Secret ID",
                    "Optional unless live AD fallback is used. Secret Server secret containing the AD account allowed to read msFVE-RecoveryPassword.",
                    Required: false),
                new(
                    "ActiveDirectorySearchBase",
                    "Active Directory Search Base",
                    "Optional DN limiting live BitLocker recovery searches to one AD subtree.",
                    Required: false),
                new(
                    "ActiveDirectoryServer",
                    "Active Directory Server",
                    "Optional domain controller used for live BitLocker recovery searches.",
                    Required: false),
                new(
                    "SearchResultLimit",
                    "Search Result Limit",
                    "Maximum rows returned by one search. Capped at 500.",
                    Required: false,
                    DefaultValue: "50"),
                new(
                    "ValidateTickets",
                    "Validate Tickets Against ServiceNow",
                    "Off: any non-blank ticket number is accepted and recorded as audit metadata. " +
                    "On: the ticket must validate against ServiceNow; while the ServiceNow integration " +
                    "is not enabled on this deployment, On refuses every search rather than silently " +
                    "validating nothing. A ticket is required in both modes.",
                    Required: false,
                    DefaultValue: "false",
                    FieldType: ConfigFieldType.Boolean)
            ]
        },
        new()
        {
            Id = "IntuneDevices",
            DisplayName = "Intune Devices",
            Description = "Search Intune managed devices, view device detail, and delete, retire or wipe a device.",
            Route = "intune-devices",
            // Reused deliberately, same reason as BitLockerRecovery above: no device/laptop icon
            // class exists in wwwroot/app.css today and the package validator rejects an icon
            // class it cannot find. Adding one is a separate change.
            IconCss = "bi bi-gear-fill-nav-menu",
            Category = "Infrastructure",
            SortOrder = 820,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.3.0: the search issues one Graph request per field (device name, UPN, serial) and
            // merges them - a single combined `or` filter returns 200 with an empty result on the
            // dev tenant (docs/IntuneDeviceManagement-Plan.md T2 Revision 2026-09-03).
            Version = "1.3.0",
            // Fail-closed throughout: device inventory is not address-book data (docs/IntuneDeviceManagement-Plan.md).
            MainPermission = new(
                "Access",
                "IntuneDevices",
                "Open the module, search Intune managed devices, and view device detail. Required before any other Intune Devices permission has effect.",
                FailClosed: true),
            GranularPermissions = [
                new("Delete", "IntuneDevicesDelete",
                    "Delete a device's Intune management record. Company data stays on the device until it next checks in; the Entra ID device object is untouched.",
                    FailClosed: true),
                new("Privileged", "IntuneDevicesPrivileged",
                    "Retire (remove company data and management) or Wipe (factory reset) a device. The destructive tier.",
                    FailClosed: true),
                new("EntraDelete", "IntuneDevicesEntraDelete",
                    "Also remove the device's Entra ID directory object via the checkbox beside each action. Backed by a directory-wide Graph scope.",
                    FailClosed: true)
            ],
            ConfigFields = [
                new("GraphDelineaSecretId", "Graph App Delinea Secret ID",
                    "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields"),
                new("SearchResultLimit", "Search Result Limit",
                    "Devices returned per search. Defaults to 50, capped at 500.",
                    Required: false, DefaultValue: "50")
                // Deliberately NO notification or Entra-removal default fields (owner ruling
                // 2026-09-02, .agents/decisions.md): whether to email the affected user, and whether
                // to also remove the Entra ID device object, are decisions the operator running the
                // action makes at that moment - not deployment-wide settings. The checkboxes on the
                // page carry fixed starting states from IntuneDeviceService instead.
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
            Version = "1.1.0",
            MainPermission = new(
                "Access",
                "LicensingUpdates",
                "Open the module and bulk-write the Exchange licensing value (extensionAttribute11) onto every user named in an uploaded CSV, in one run.",
                FailClosed: true),
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
            Version = "1.4.0",
            MainPermission = new(
                "Access",
                "ADAttributeEditor",
                "Open the module and look up an on-premises user; no attribute can be edited until one of the level permissions below is also granted.",
                FailClosed: true),
            GranularPermissions = [
                new("Level1", "ADAttributeEditorLevel1",
                    "Edit the allowlisted attributes marked level 1 on this module's Editable Attributes tab - the least sensitive tier.",
                    FailClosed: true),
                new("Level2", "ADAttributeEditorLevel2",
                    "Edit the allowlisted attributes marked level 1 or level 2; the levels are cumulative, so this includes everything level 1 allows.",
                    FailClosed: true),
                new("Level3", "ADAttributeEditorLevel3",
                    "Edit every allowlisted attribute at any level - the widest directory write this module offers.",
                    FailClosed: true)
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
            // 1.2.0: the protected-principals panel gains the Protected Group Targets list
            // (docs/ProtectedGroupWriteTarget-Plan.md T0) - pgwt-8.
            Version = "1.2.0",
            MainPermission = new(
                "Access",
                "AdminSettings",
                "Nothing on its own - this page answers to the Security:AdminGroups setting in configuration, not to groups listed here.")
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
            Version = "1.1.0",
            MainPermission = new(
                "Access",
                "EventLog",
                "Read the audit trail of every action any operator has taken in any module, including targets, tickets and outcomes from modules the reader cannot otherwise open.",
                FailClosed: true),
            GranularPermissions = [
                new("Undo", "UndoAuditedActions",
                    "Reverse an audited action from the log, which writes the previous value back to the live target; the undo is itself audited.",
                    FailClosed: true)
            ]
        },
        new()
        {
            Id = "AdminBulkJobs",
            DisplayName = "Bulk Jobs",
            Description = "View, cancel and remove background bulk jobs across every module.",
            Route = "admin-bulk-jobs",
            IconCss = "bi bi-list-nested-nav-menu",
            Category = "Administration",
            SortOrder = 920,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.0.0",
            // FailClosed: this page aggregates EVERY module's jobs - submitters, tickets, targets
            // and per-row outcomes across section-access boundaries. That aggregation is exactly
            // what those boundaries exist to prevent leaking, so a failure to evaluate the policy
            // must deny. Same reasoning as AdminEventLog.
            MainPermission = new(
                "Access",
                "AdminBulkJobs",
                "Open the module and read, cancel or remove background bulk jobs from every module, including their submitters, tickets, targets and per-row outcomes.",
                FailClosed: true)
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
