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
    // Display order is alphabetical by display name, case-insensitive and culture-independent.
    // OrdinalIgnoreCase is deliberate over a culture-sensitive comparison: it gives the same
    // order on every host and locale, which a hand-maintained integer used to guarantee by
    // accident. No display name begins with a digit or punctuation today.
    public IReadOnlyList<AdminModuleDescriptor> GetOrdered() =>
        _modules.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    public AdminModuleDescriptor? GetById(string id) => _byId.GetValueOrDefault(id);
    public AdminModuleDescriptor? GetByRoute(string route) => _byRoute.GetValueOrDefault(route);
    public AdminModuleDescriptor? GetByPolicyAlias(string alias) => _byPolicyAlias.GetValueOrDefault(alias);

    public IReadOnlyList<string> GetConfigurablePolicyAliases()
    {
        var result = new List<string>();
        // Same ordering rule as GetOrdered(), so the two stay consistent; each module's main
        // alias still comes immediately before its own granular aliases.
        foreach (var m in _modules
            .Where(m => !m.IsSystemModule && !m.IsConfigOnly)
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
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
            Category = ModuleCategories.Exchange,
            EnabledByDefault = false,
            IsSystemModule = false,
            IsConfigOnly = true,
            Version = "1.0.2",
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
            Category = ModuleCategories.Exchange,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.2.0",
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
            Category = ModuleCategories.Exchange,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.2.0",
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
            Category = ModuleCategories.Exchange,
            EnabledByDefault = true,
            IsSystemModule = false,
            // 1.8.1: the open migration report is cleared on every batch-user reload
            // 1.8.2: that close now happens after the reload lands, not before it, so a report
            // opened mid-reload cannot survive it (msr-1, docs/MigrationStaleReport-Plan.md).
            // 1.8.3: a failed search no longer leaves loadingBatchUsers set for the life of the
            // circuit (docs/MigrationButtonGating-Plan.md, prerequisite to the button gate).
            // 1.9.0: every control on the page is gated on one in-flight predicate, so a click is
            // never accepted unless it will definitively execute (docs/MigrationButtonGating-Plan.md).
            // 1.9.1: the search box and the staged-ticket box carry that predicate too, so Enter can
            // no longer reach a search or a destructive confirmation that the button beside it is
            // refusing (docs/ClickGatingAudit-Plan.md; a disabled input fires no keydown).
            // 1.10.0: the open batch is addressable as /migration?batch=<name>, so browser Back, a
            // refresh and a circuit reconnect restore it instead of collapsing the page
            // (docs/MigrationInterfaceRedesign-Plan.md S1).
            // 1.10.1: making the URL an entry point let two batch-user loads overlap, so the one
            // that lost the race could publish one batch opened over another batch's mailboxes.
            // A generation counter discards it (mir-1, openreview of S1).
            // 1.11.0: two panes. Batches left, the open batch's mailboxes right, each with its own
            // scroll region and the batch list paged 40 at a time, so opening a batch no longer
            // injects a second table into the first and no view renders the whole catalogue
            // (docs/MigrationInterfaceRedesign-Plan.md S2).
            // 1.12.0: one concept, selection. The open batch is derived from what is ticked rather
            // than held beside it, batch operations moved to a toolbar at the top of the batch pane
            // (Complete and Stop among them, back from the per-row buttons S2 removed), and a
            // multi-batch selection gets its own paged pane
            // (docs/MigrationInterfaceRedesign-Plan.md S3).
            // 1.13.0: the outcome preview. Before the ticket is typed, every ticked batch says
            // whether the staged action will run on it or skip it and why, Confirm carries the
            // eligible count, and the picked action is highlighted
            // (docs/MigrationInterfaceRedesign-Plan.md S4, R12).
            // 1.14.0: the mailbox table gets the filter, sort and paging it never had, both over
            // the whole set rather than the rendered page, so a 2000-mailbox batch no longer
            // renders 2000 rows over the circuit
            // (docs/MigrationInterfaceRedesign-Plan.md S5 step 1, R20, R21).
            //
            // 1.14.0 SHIPPED IN 2a0dd6a WITHOUT THIS LINE. The bump was made and then clobbered
            // before it was staged: two sessions share this working tree, and the other one
            // rewrote this file from its own copy between the edit and the commit. That commit's
            // message claims the bump it does not contain. Recorded rather than rewritten -
            // history rewrites need explicit authority, and the version is correct again below.
            //
            // 1.15.0: mailbox checkboxes, a mailbox action bar that reaches its own planner
            // exactly as the batch bar reaches its own, and ticked mailboxes pinned above an
            // OTHER MAILBOXES divider with their own pager, so a selection is never hidden by the
            // filter (S5 step 2b, R5a, R5b, R7, R10, R11, R12).
            // 1.16.0: the per-mailbox report is a dialog that KEEPS what it fetched. Dismissing it
            // and discarding it are now different operations, so closing no longer guarantees
            // another twenty-minute Get-MigrationUserStatistics, and the text has an Export button
            // (docs/MigrationInterfaceRedesign-Plan.md S6, R24, R24a, R24c, R24e).
            // 1.17.0: batch names can be filtered, which is a different control from the person
            // search that jumps to a mailbox in any batch; all three empty states say which one
            // applies; and the CSV button names the scope it exports
            // (docs/MigrationInterfaceRedesign-Plan.md S6, R26, R28, R29).
            // 1.18.0: a bulk action reports counts in the banner and the reason on the row that
            // refused. It used to join every refusal and every skip into one alert, which over
            // fifty batches is an error log in a banner (owner, 2026-09-28)
            // (docs/MigrationInterfaceRedesign-Plan.md S6, R25).
            // 1.18.1: "complete now" and "complete at T" stop being the same value. CompleteAfter
            // is read as the timestamp it is instead of a boolean, and scheduling refuses a past
            // time rather than cutting mailboxes over immediately
            // (docs/MigrationInterfaceRedesign-Plan.md S8).
            // 1.19.0: tick mailboxes and Export reports fetches each one in the background, then
            // Download reports zips what is held - one text file per report, with a manifest
            // naming any that expired rather than dropping them silently
            // (docs/MigrationInterfaceRedesign-Plan.md S7, R31).
            // 1.20.0: scheduled completion is reachable. S8 built the service method and stopped
            // there, so until now queue item 12 existed only in C# - which is exactly the
            // "buried" R13 forbids, and it survived every slice being marked done. The batch
            // action bar also became a single Actions menu: six labelled buttons could not fit on
            // one line, and the fifth already wrapped (owner, 2026-09-29, on a screenshot -
            // one line, uniform controls, and colour reserved for Delete)
            // (docs/MigrationInterfaceRedesign-Plan.md R13, queue items 12 and 14).
            // 1.20.1: a batch whose every mailbox was ticked rendered as empty. The pinned rows
            // (R5a) sit inside the table, and the empty state short-circuiting that table counted
            // only the UNPINNED rows - so select-all, or any one-mailbox batch, blanked the pane
            // and announced "No mailbox in this batch matches" with no filter set. It hid exactly
            // the rows the operator had selected, which is the R11 failure pinning exists to
            // prevent. Found on dev; a test had pinned the faulty condition and read as coverage.
            // 1.20.2: the batch-name filter hid ticked batches from the selection pane. R5
            // forbids pinning selected batches into the left list BECAUSE the right pane shows
            // them, so that pane is the only surface a ticked batch appears on - and it read the
            // filtered list. Four ticked, type in the filter, and the header still said "4
            // batches selected" over however many survived, with the rest still armed. Same
            // shape as 1.20.1, found by sweeping for it; the mailbox side had been written
            // correctly and the batch side had no equivalent test.
            // 1.20.3: R12 says both action bars are identical, and converting only the batch
            // bar to a menu broke that rule in the act of satisfying the owner. The mailbox bar
            // still had EIGHT controls in five colours and the same flex-wrap - the layout just
            // rejected on the other pane. Both are now one Actions (n) menu with one red item.
            // Nothing had ever checked the "identical" clause, which is how they came apart.
            // 1.20.4: the batch list had no Total column. R16 named Batch/Synced/Failed/Status
            // and the table was built to that list exactly - while the sort dropdown offered
            // "Total" and ordered by it, so the list could be sorted by a column that was not on
            // screen, and Synced and Failed were fractions with no denominator. Added to both
            // batch views; R16 amended. Owner caught it on a dev screenshot.
            // 1.20.5: migration direction shows on every batch row as an inline SVG inside the
            // name cell (owner, 2026-09-29: icons in the list, and do not clog the columns). No
            // seventh column, so the batch name keeps its width and R15 holds. Labelled with a
            // title and an aria-label, because an icon is where R17 is easiest to break.
            // 1.20.6: read-only operators saw every batch row one column left of its
            // heading. The header drew an unconditional spacer for the tick-box column while
            // the row only drew a tick box when canManage, so without MigrationManage the name
            // sat under the blank and Failed under a heading that was not rendered.
            // Pre-existing; invisible to everyone who built it, because they all had the
            // permission.
            // 1.20.7: the batch list gave its width to the wrong columns. Three fixed count
            // columns held single digits while a thirty-character address was cut to 115px.
            // Synced and Total are now one cell as the fraction they always were, the direction
            // icon has its own narrow track instead of being a passenger in the name cell, and
            // the name takes what is left (owner, 2026-09-29: "batch names are cutoff ... not
            // enough space where it is actually useful").
            // 1.20.8: the selection pane header lost a cell in 1.20.7. Moving the direction
            // icon into its own track added a cell to the ROWS of that pane and not to its header,
            // so every heading there sat one column left of its data - the same defect 1.20.6
            // fixed in the list beside it, reintroduced by the change that fixed it. The
            // alignment test now counts header cells against declared grid tracks for BOTH
            // batch grids instead of one.
            // 1.20.9: the HEADERS were setting the column widths. "SYNCED/TOTAL" is twelve
            // characters over data that reads "0/0" - nine wasted character widths taken from
            // the batch name. Short labels with the full name on hover, and the count tracks
            // shrink to fit the numbers rather than the headings (owner, 2026-09-29).
            // 1.21.0: both batch lists are HTML tables instead of CSS grids. The grid needed
            // a width for every column and the header had to supply a cell per track by hand -
            // which produced two header-shift defects and four wrong width guesses in a day.
            // A table sizes each column to its own widest cell across all rows, so there is no
            // width to get wrong and a header cannot misalign against its body. The mailbox
            // table beside it always worked this way and had none of those faults.
            // 1.21.1: the table refactor left the abbreviated Sync/Fail headers without their
            // dotted-underline hover affordance - the rule still named the deleted
            // mig-batch-head class. Found by the codex review, not by the gates: a dead CSS
            // selector compiles and breaks nothing visible except the thing it was doing.
            // 1.22.0: four defects the owner found on dev in one sitting.
            // - Get-MigrationUser -BatchId returned nothing for batches that had users, so the
            //   pane said "This batch contains no mailboxes" for a batch whose own record said
            //   it held one. Piped from Get-MigrationBatch instead, which is the form that
            //   works. THIS WAS THE REAL BUG behind the empty mailbox pane.
            // - That empty state now distinguishes an empty batch from the batch record and the
            //   user list DISAGREEING, instead of reporting the second as the first.
            // - Total and Synced are separate columns again with real headers. They were merged
            //   into "0/1" to save width under the old hardcoded grid; the owner then had to
            //   ask what 0/1 meant. A table column costs only its content, so the hack bought
            //   nothing once the grid was gone.
            // - The batch Actions menu now renders in the right pane too. R3 puts batch
            //   operations in the batch pane and R6 puts actions at the top of the thing they
            //   act on; with several batches ticked those are different panes, and the pane
            //   that LISTS the selection could not act on it.
            // Also: SyncedCount no longer falls back to SyncedItemCount - items and mailboxes
            // are different units and the fallback rendered an item count over a mailbox total.
            // 1.22.1: restore explicit BatchId mailbox loading, retrieve the complete set,
            // and report query failures instead of showing an empty batch. The 1.22.0 query
            // diagnosis above was disproven by same-credential live comparisons.
            // 1.22.2: distinguish never-started pending batches from genuinely empty batches.
            // 1.22.3: every row in the three lists that reorder carries an @key. Blazor diffs a
            // sibling list by position, so pinning a ticked mailbox above the OTHER MAILBOXES
            // divider handed its checkbox element to whichever row moved into that position, and
            // that row then drew as ticked when it was not. The Actions count and the ticked
            // pager were both right; only the boxes lied (owner, dev v1.22.1, 2026-09-30).
            // 1.22.4: the report dialog scrolls. The <pre> declared overflow/flex/min-height and
            // the report still ran off the bottom of the screen, because the <pre> is not a flex
            // child of .mig-modal - the Bootstrap .card-body between them is, and that is a
            // BLOCK, so those properties were inert (docs/AppLayoutAndScrolling-Plan.md, queue
            // item 21). The two-pane split also stops doing arithmetic on the chrome above it:
            // calc(100vh - 21rem) was a guess that was too small, so its pagers fell off the
            // bottom of the window (queue item 22).
            // 1.22.5: batch tick boxes stop dropping ticks (queue item 23, prod blocker). A
            // checkbox is toggled by the BROWSER before the server hears about it, so the
            // `if (IsBusy) return;` in ToggleBatchSelected discarded the tick while leaving it
            // drawn - the box read ticked, the right pane did not list the batch and the action
            // bar counted one fewer. The window opened in S3, when ticking down to one batch
            // started fetching that batch's mailboxes and so held IsBusy for a round trip.
            Version = "1.22.5",
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
            Category = ModuleCategories.Exchange,
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
            Category = ModuleCategories.Exchange,
            EnabledByDefault = true,
            IsSystemModule = false,
            // 1.5.0: searching message traces becomes its own Search grant, so opening the module
            // and analysing headers no longer carries the right to read other people's mail
            // (docs/MessageTracePermissionSplit-Plan.md, 2026-09-22). Declared only, for now:
            // nothing consults the new alias until the enforcement slice ships, and the owner
            // grants a group against it in between.
            // 1.5.1: both Access-tab descriptions said the split was live while nothing enforced
            // it, so the page told an administrator that the parent grant excluded trace search
            // when it did not (review finding mtps-1, 2026-09-22). They now state the unenforced
            // state; the enforcement slice replaces them with the final wording in the same
            // commit that makes the gates consult the alias.
            // 1.5.2: the Access tab headed both grants with the raw alias, so the distinction read
            // as "MessageTrace" versus "MessageTraceSearch" when what is being granted is header
            // analysis versus trace (owner ruling 2026-09-22). They now head as Header Analysis
            // and Trace Search, with the alias still beside each - it is the storage key and the
            // name in the denial log.
            Version = "1.5.2",
            DependsOn = "ExchangeOnline",
            MainPermission = new(
                "Access",
                "MessageTrace",
                "Open the module, analyse message headers, AND search message traces. NOT YET SPLIT: the Search permission below is declared but nothing enforces it, so this permission still grants trace search and the reading of any user's mail metadata. Do not grant it to anyone who should be limited to header analysis until the split is enforced.",
                FailClosed: true,
                DisplayName: "Header Analysis"),
            GranularPermissions = [
                new("Search", "MessageTraceSearch",
                    "Will grant message trace search: reading any user's senders, recipients, subjects, delivery status, message IDs and IP addresses across Exchange Online and the on-premises transport logs, the per-message delivery trail, and the detail exports built from it. NOT YET ENFORCED: granting this today gives nobody anything and withholding it blocks nobody. Grant it now to the groups that should keep trace search once enforcement lands.",
                    FailClosed: true,
                    DisplayName: "Trace Search")
            ],
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
            Category = ModuleCategories.Exchange,
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
            Category = ModuleCategories.Exchange,
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
            Category = ModuleCategories.Exchange,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.1.0: unblock now gates the TARGET through the protected-principal check. The module
            // previously re-checked only the operator, so a protected principal could be unblocked.
            // 1.4.0: CSV export of the blocked-sender list (docs/ModuleCsvExport-Plan.md).
            Version = "1.4.2",
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
            Category = ModuleCategories.DirectoryAndGroups,
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
            // 2.10.1: the bulk-remove confirmation is compact - buttons on the heading line, names
            // inline (owner, 2026-09-04).
            // 2.11.0: every control on the page now consults one busy predicate
            // (docs/ClickGatingAudit-Plan.md Revision 1, tier 1 page 6). Selecting a group is part
            // of it for the first time - the protection check owned no in-flight flag at all - and
            // Load Members no longer reads the selection back after its await, which used to tear
            // the circuit down if the operator closed the panel or picked another group mid-load.
            Version = "2.11.1",
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
            Category = ModuleCategories.DirectoryAndGroups,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.4.1",
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
            Category = ModuleCategories.DirectoryAndGroups,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.3.0: the module works at its real size (docs/Comms10kBulkResolveScale-Plan.md).
            // Landing across that plan's four slices; the version is set once, on S1.
            // - S1: address resolution asks the directory ONE question per 500 addresses instead
            //   of one per CSV row. At 5,279 addresses the old loop issued 5,279 sequential
            //   queries and ADWS invalidated the enumeration context partway through, which is
            //   the error the owner hit. A query error now aborts the whole resolution rather
            //   than reading as "these 500 were not found" - the write removes everyone absent
            //   from the resolved list, so that reading unsubscribes real people.
            //   S2: Preview and Download CSV stop throwing on the real group. They called
            //   Get-ADGroupMember, which expands every member into a full object and is bound
            //   by the ADWS MaxGroupOrMemberEntries cap (default 5000) - below the size this
            //   module exists to manage. Now the raw `member` attribute, unioned with
            //   primary-group members (lst-2: the linked attribute omits them and
            //   Get-ADGroupMember included them, so the swap alone would silently shorten the
            //   list). Detail resolves in batches; a DN that does not resolve falls back to its
            //   CN and still produces a row, because an unresolved member is still a member.
            //   S3: the principal-protection check is gone - no member check and no write-target
            //   check (owner ruling 2026-09-30, .agents/decisions.md, carried by the
            //   Constitution as a named scoped exception). It cost a runspace and an AD-module
            //   import per member, which at ten thousand members was the module's dominant cost
            //   and blocked its intended use. The replace now resolves the configured group name
            //   to its distinguished name and objectGUID once and binds every operation to that,
            //   so a rename between operations cannot retarget the write.
            //   S4: the write is clear-then-fill with a read-back, because the directory refuses
            //   a single-operation swap at this size. It gives up atomicity - the list is empty
            //   and then partial for about eight seconds - so the module reports five distinct
            //   outcomes instead of a bool, derived only from what the read-back observes, and
            //   says plainly when the list is incomplete or when it could not be confirmed.
            //   Not serialised (owner ruling 2026-09-30): two concurrent replaces can
            //   interleave and leave the list holding neither file; each run's read-back
            //   reports that rather than claiming success. Transient audit faults retry per
            //   operation, never per run.
            Version = "1.3.1",
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
            Category = ModuleCategories.DirectoryAndGroups,
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
            // 1.10.0: bulk add via paste list - every line resolved in one batched USER-only
            // home-domain query per chunk (a group line is reported, never added), a resolution
            // table, and "Add resolved" running each user through the same per-member handler as
            // the single Add (docs/GroupBulkActions-Plan.md S5).
            // 1.10.1: the bulk-remove confirmation is compact - buttons on the heading line, names
            // inline, one warning line for all group rows (a per-row warning scrolled the confirm
            // button off the screen; owner, 2026-09-04).
            // 1.11.0: click gating (tier 1 page 9 of 9). Two view-scoped busy predicates - the
            // browse view and the manage view have disjoint in-flight flags - gate every control
            // in the view that owns them; "Back to groups" stays clickable on purpose so a
            // post-write member reload cannot trap the operator, and the two "Manage members"
            // buttons are gated on the MANAGE predicate so a change still finishing on the group
            // just left cannot be walked back into. Two mid-flight reads fixed with entry
            // snapshots: the member load dereferenced the selected group in its catch after
            // "Back to groups" had nulled it (a throw inside a catch, which took the circuit
            // down), and the bulk-add paste was parsed after a yield from a box still accepting
            // keystrokes.
            Version = "1.11.1",
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
            Category = ModuleCategories.IdentityAndAccess,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.1.0: protection now resolves through Exchange. The AD-only lookup reported every
            // cloud-only user as "no AD object" and skipped the check, which for a Graph module is
            // the normal case - so protection was close to inert here.
            Version = "1.2.1",
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
            Id = "CloudPasswordReset",
            DisplayName = "Cloud Password Reset",
            Description = "Reset the password of an Entra ID cloud-only account that has no on-premises Active Directory object. The new password is emailed to the account owner, found from the employee ID on the account, and the reset is recorded in the audit log.",
            Route = "cloud-password-reset",
            IconCss = "bi bi-person-fill-nav-menu",
            Category = ModuleCategories.IdentityAndAccess,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.0.3",
            MainPermission = new(
                "Access",
                "CloudPasswordReset",
                "Open the module and reset the password of an Entra ID cloud-only account, including accounts holding administrative roles. The new password is emailed to the account's owner and is never shown to the operator.",
                FailClosed: true),
            GranularPermissions = [
                new("Reveal", "CloudPasswordResetReveal",
                    "Additionally see the new password on screen, for an account the module cannot find an owner mailbox for. This is the only way an operator can learn a password this module generates, so hold it to as few people as possible.",
                    FailClosed: true)
            ],
            ConfigFields = [
                new("GraphDelineaSecretId", "Graph App Delinea Secret ID", "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields"),
                new("ValidateTickets", "Validate ServiceNow tickets", "On: a ticket must validate through ServiceNow before a reset runs. Off: any non-blank ticket is accepted as audit metadata.", Required: false, DefaultValue: "false", FieldType: ConfigFieldType.Boolean)
            ]
        },
        new()
        {
            Id = "AccountLockoutRemediation",
            DisplayName = "Account Lockout Remediation",
            Description = "Identify account lockout source machines and log selected accounts off from implicated or scoped domain computers.",
            Route = "account-lockout-remediation",
            IconCss = "bi bi-person-fill-nav-menu",
            Category = ModuleCategories.IdentityAndAccess,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.2.1",
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
            Category = ModuleCategories.Exchange,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 2.6.0: click-gating conversion (tier 1, page 8 of 9). Every Room Finder and Room Type
            // control now names the IsBusy predicate; the Bulk Jobs panel is a second scope with no
            // busy state, and its Remove control acts on the rendered row instead of a live lookup.
            Version = "2.6.1",
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
            Category = ModuleCategories.IdentityAndAccess,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.2.0: every control on the page is gated on one in-flight predicate, and both write
            // handlers snapshot the form and their result so a mid-flight dismiss or keystroke can
            // no longer retarget a Conditional Access write, blank its ticket, or report a
            // successful write as failed (docs/ClickGatingAudit-Plan.md tier 1, page 2).
            Version = "1.2.1",
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
            Category = ModuleCategories.IdentityAndAccess,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.2.1",
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
            Id = "TrueLastLogon",
            DisplayName = "True Last Logon",
            Description = "When one person really last logged on, asked of every domain controller and of the cloud sign-in logs together, with how far the answer can be trusted.",
            Route = "true-last-logon",
            // The one nav-menu icon class that actually fits this module. Unlike the reused
            // gear above, bi-clock-fill-nav-menu already exists in NavMenu.razor.css, so the
            // package validator's icon check passes without adding CSS.
            IconCss = "bi bi-clock-fill-nav-menu",
            Category = ModuleCategories.IdentityAndAccess,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.0.0: the module becomes reachable. S1 and S2 built both halves and registered
            // nothing, so until now this existed only in C# (docs/TrueLastLogon-Plan.md S3).
            // NOTE: adding a module does NOT bump the base app version - only this line is set
            // (Constitution, Deployment And Versioning; .agents/decisions.md 2026-07-21).
            // 1.0.1: a UPN search works. The sweep used Get-ADUser -Identity, which resolves a
            // DN, GUID, SID or sAMAccountName and NOT a userPrincipalName, so every UPN failed
            // on every DC with "Cannot find an object with identity" - the module's first live
            // run. Now an RFC 4515 filter matching either attribute, escaped in C# so the
            // escaping is testable; no match and more than one match both fail closed.
            Version = "1.0.2",
            // One permission, no granular tier: the module reads and mutates nothing anywhere.
            // Classified NON-ALERTING under the Constitution's notification rule - it exposes
            // logon timestamps already visible in AD and Entra, and it is an account-hygiene
            // lookup rather than a security-response surface, so the audit record is sufficient
            // and no administrator alert is sent.
            MainPermission = new(
                "Access",
                "TrueLastLogon",
                "Open the module and look up when one account last logged on, across every domain controller and the Entra ID sign-in logs. Exposes logon timestamps, the domain controller that recorded them, and sign-in detail such as IP address, application and conditional-access result. Read-only - grants no ability to change anything.",
                FailClosed: true),
            GranularPermissions = [],
            ConfigFields = [
                new("GraphDelineaSecretId", "Graph App Delinea Secret ID",
                    "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields. The app registration needs BOTH AuditLog.Read.All and User.Read.All on Microsoft Graph. AuditLog.Read.All appears nowhere else in this app, so an existing module's registration is unlikely to carry it, and consenting it requires a Privileged Role Administrator or Global Administrator. User.ReadBasic.All is NOT sufficient. Leave this unset and the cloud half reports itself as not checked; it never reports the account as dormant.")
            ]
        },
        new()
        {
            Id = "RiskyUsers",
            DisplayName = "Risky Users",
            Description = "Review Microsoft Entra ID Protection risky users and their risk history.",
            Route = "risky-users",
            IconCss = "bi bi-person-fill-nav-menu",
            Category = ModuleCategories.IdentityAndAccess,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.4.1: the 403 on the single-user lookup names User.ReadBasic.All, which is what
            // that call actually needs - it selects `id` and nothing else, asking whether the
            // account exists. It named User.Read.All, sending an administrator to grant
            // tenant-wide full-profile read for an existence check. Owner granted and verified
            // the smaller scope against the live tenant, 2026-09-28.
            // 1.5.0: the page said what each FIELD was and never what either CARD was for, so
            // the first field's label read as the card's heading - and the browse card's first
            // field is "Risk level", which made it look like a risk-level control with an
            // unexplained Refresh button bolted on. Each card now states its purpose, and
            // Refresh is "Search", which is what it does (queue item 19, owner 2026-09-29).
            Version = "1.5.1",
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
                new("MaxTotalRows", "Maximum Total Rows", "Safety ceiling on one query, across all pages. The module follows Graph's paging until the matching set is complete; if more risky users match than this, it shows what it retrieved and says the list is partial. Defaults to 10000. Deliberately a NEW key: the old MaxRows meant page size, and a stored value would have silently capped the fetch at 500.", Required: false, DefaultValue: "10000")
            ]
        },
        new()
        {
            Id = "DhcpAuthorization",
            DisplayName = "DHCP Authorization",
            Description = "Authorize and deauthorize DHCP servers in Active Directory. Requires Enterprise Admin credentials via Secret Server.",
            Route = "dhcp-authorization",
            IconCss = "bi bi-gear-fill-nav-menu",
            Category = ModuleCategories.Infrastructure,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.4.0: every control on the page is gated on one in-flight predicate, and both write
            // handlers snapshot their inputs and their result so a mid-flight dismiss can no longer
            // report a successful AD write as failed (docs/ClickGatingAudit-Plan.md tier 1, page 1).
            Version = "1.4.1",
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
            Category = ModuleCategories.Infrastructure,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.1.0: mandatory ticket before any search, written on the search and
            // reveal audit events; ValidateTickets per-module validation switch.
            Version = "1.2.1",
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
            Id = "DefenderEndpointDevices",
            DisplayName = "Defender for Endpoint Devices",
            Description = "List and export Microsoft Defender for Endpoint devices, including devices discovered on the network that can be onboarded but are not.",
            Route = "defender-endpoint-devices",
            // Reused deliberately, same reason as IntuneDevices below and BitLockerRecovery above:
            // no device or shield icon class exists in wwwroot/app.css today and
            // tools/validate-module-package.ps1 rejects an icon class it cannot find. Adding one is
            // a separate change.
            IconCss = "bi bi-gear-fill-nav-menu",
            Category = ModuleCategories.Infrastructure,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.1.0: the listing divides the inventory into Last seen ranges and asks for each one,
            // so a tenant with more devices than the API's 10,000-row per-request cap can be listed
            // at all (docs/DefenderEndpointDevices-Plan.md Revision 3, R1(g)); the two hardcoded
            // filters are replaced by the machine record's real fields, and the default ceiling
            // rises from 20000 to 100000.
            Version = "1.1.1",
            // One permission, no granular tier: the module reads and exports and mutates nothing.
            MainPermission = new(
                "Access",
                "DefenderEndpointDevices",
                "Open the module and view or export the Defender for Endpoint device inventory, including network-discovered devices that are not onboarded, with their IP and MAC addresses, operating system and risk level. Read-only - grants no ability to change anything in Defender.",
                FailClosed: true),
            GranularPermissions = [],
            ConfigFields = [
                new("GraphDelineaSecretId", "Graph App Delinea Secret ID",
                    "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields. The app registration needs BOTH Machine.Read.All on WindowsDefenderATP and ThreatHunting.Read.All on Microsoft Graph. The second was optional while discovery sources were; the owner confirmed on 2026-09-21 that they are wanted, so it is not. Consenting the Graph one requires a Privileged Role Administrator or Global Administrator."),
                new("MaxDevices", "Maximum Devices",
                    "Safety ceiling on one run - how many devices this server will hold in memory for one operator. The module divides the inventory into Last seen ranges until every part is provably complete; if more devices match the server-side filters than this, it REFUSES the report rather than returning a partial one. Defaults to 100000. Raise it, or narrow the filters the API applies.",
                    Required: false, DefaultValue: "100000"),
                new("IncludeDiscoverySources", "Include Discovery Sources",
                    "Run the advanced hunting query that supplies the discovery sources, device type, vendor and model columns. Turn off when the app registration does not hold ThreatHunting.Read.All.",
                    Required: false, DefaultValue: "true", FieldType: ConfigFieldType.Boolean)
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
            Category = ModuleCategories.Infrastructure,
            EnabledByDefault = false,
            IsSystemModule = false,
            // 1.3.0: the search issues one Graph request per field (device name, UPN, serial) and
            // merges them - a single combined `or` filter returns 200 with an empty result on the
            // dev tenant (docs/IntuneDeviceManagement-Plan.md T2 Revision 2026-09-03).
            // 1.4.0: every control on the page now consults one busy predicate, widened to include
            // detailLoading (docs/ClickGatingAudit-Plan.md Revision 1, tier 1 page 5). A search can
            // no longer be started - by click or by Enter - while a device action is queued, which
            // used to discard that action's on-screen verdict.
            Version = "1.4.1",
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
            Id = "ServiceHealth",
            DisplayName = "Service Health",
            Description = "Microsoft 365 service status and open service incidents, read from the Microsoft Graph service announcements API.",
            Route = "service-health",
            // Reused deliberately, same reason as IntuneDevices above: no status/heartbeat icon
            // class exists in wwwroot/app.css today and the package validator rejects an icon
            // class it cannot find. Adding one is a separate change.
            IconCss = "bi bi-gear-fill-nav-menu",
            Category = ModuleCategories.Infrastructure,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.3.3",
            // One permission, no granular tier: the module reads and renders, and mutates
            // nothing (docs/ServiceHealth-Plan.md).
            MainPermission = new(
                "Access",
                "ServiceHealth",
                "Open the module and view current Microsoft 365 service status and the tenant open service incidents. Read-only - grants no ability to change anything.",
                FailClosed: true),
            GranularPermissions = [],
            ConfigFields = [
                new("GraphDelineaSecretId", "Graph App Delinea Secret ID",
                    "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields (the app registration needs ServiceHealth.Read.All)")
            ]
        },
        new()
        {
            Id = "LicensingUpdates",
            DisplayName = "Licensing Updates",
            Description = "Bulk update Exchange licensing SKU assignments (extensionAttribute11) via CSV upload.",
            Route = "licensing-updates",
            IconCss = "bi bi-list-nested-nav-menu",
            Category = ModuleCategories.IdentityAndAccess,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.1.2",
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
            Category = ModuleCategories.DirectoryAndGroups,
            EnabledByDefault = false,
            IsSystemModule = false,
            Version = "1.4.1",
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
            Category = ModuleCategories.Administration,
            EnabledByDefault = true,
            IsSystemModule = true,
            // 1.2.0: the protected-principals panel gains the Protected Group Targets list
            // (docs/ProtectedGroupWriteTarget-Plan.md T0) - pgwt-8.
            Version = "1.2.1",
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
            Category = ModuleCategories.Administration,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.2.2",
            MainPermission = new(
                "Access",
                "EventLog",
                "Read the audit trail of every action any operator has taken in any module, including targets, tickets and outcomes from modules the reader cannot otherwise open.",
                FailClosed: true),
            GranularPermissions = [
                new("Undo", "UndoAuditedActions",
                    "Reverse an audited action from the log, which writes the previous value back to the live target; the undo is itself audited.",
                    FailClosed: true)
            ],
            ConfigFields = [
                new("UsageTelemetryEnabled", "Record anonymous usage", "Records which modules are opened and which actions run, with no user, IP or target. Off stops new rows and hides the Home page disclosure; the Usage view keeps showing existing rows.", Required: false, DefaultValue: "true", FieldType: ConfigFieldType.Boolean)
            ]
        },
        new()
        {
            Id = "AdminBulkJobs",
            DisplayName = "Bulk Jobs",
            Description = "View, cancel and remove background bulk jobs across every module.",
            Route = "admin-bulk-jobs",
            IconCss = "bi bi-list-nested-nav-menu",
            Category = ModuleCategories.Administration,
            EnabledByDefault = true,
            IsSystemModule = false,
            Version = "1.0.1",
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
