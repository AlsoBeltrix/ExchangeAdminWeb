# In-app help: hover help on controls, a Help panel on every page, help for every setting

Status: **Approved 2026-10-28, to start after 2026-10-31 (owner: "approved to start after 10/31"). The standing rule that a functionality change carries its help change is in the Constitution (Code Change Discipline) and Known Failure Class 5.**
after the current changes are in production ("that's next after the current changes are in prod").
Owner: Michael
Requested: 2026-10-28 (verbatim in section 1).
Last verified against code: `d117c35e` / 2026-10-28 for the inventory (section 2); `1464aa7f` /
2026-10-28 for the worked example (section 4), re-read after the employee-ID plan merged at
`683a8ce7` mid-draft. Every file and line cited was read at the commit its section names.
Versions (proposed): base app minor bump in H1 and patch bump in H2 (`2.36.0` at drafting; bump
from what master holds when a slice lands); a patch bump of each module whose own help lands (H2,
H3 and every module slice). Docs-only changes bump nothing. Section 5.3.
Authority: subordinate to `docs/ProjectConstitution.md` and `AGENTS.md`. The Constitution does not
require a written plan for UI text; this one exists because the owner asked for pre-planning, and
because it changes the module contract (two records every module declares) and the shared layout.
Builds on: `docs/CloudPasswordResetEmployeeId-Plan.md` (Implemented 2026-10-28), whose two settings
are the worked example.

## Exec summary

*The only part of this plan the owner reads or is bound by (`.agents/decisions.md` 2026-10-01).
Everything below it is agent-facing.*

**Your question, answered.** No. The new password-reset settings are explained in the app only by
the text under each box on the settings page, with no example; the reset page shows what will
happen to the account in front of you, but nothing explains the feature as a whole. And the app
has no comprehensive help. Of its 33 working pages, 8 say nothing beyond an audit notice or a
one-line purpose, and 5 more give a single line of instruction. One page, Intune Devices, has a
"what do these actions do" panel; two have a short list of steps. No page has a worked example.
The hover text that does exist cannot be seen by keyboard users or on a touch screen.

**What it does.** One help system for the whole app, in the app's existing style. A small "?"
beside a box or switch that needs explaining shows a short explanation and an example when you
hover over it, tap it or reach it with the keyboard, and screen readers read it out. A Help button
at the top of every page opens notes for that page: what the page is for, what each kind of access
to it lets a person do and, where a page needs them, step-by-step notes, worked examples and the
terms it uses (preflight, reset, ticket, protected principal). The settings pages get the same,
setting by setting.

**What you write and what you don't.** Most of the help builds itself from what every module
already declares - what the page is for, what each permission lets a person do, and each setting's
name, description, default and whether it is required - so every page and setting gets help the
day this lands, with nothing written; examples, step-by-step notes and explanations of individual
boxes are written by hand only where a page or setting needs them, a simple page needs none, and
nothing fails a module for leaving them out. Module developers get a short note in the module guide
about those optional extras, and nothing more.

**The worked example.** The password-reset page's help explains the check before a reset, where the
password goes, when it is shown on screen instead, and the refusal you defined, described the way
you asked ("can open the module, cannot see the password"). The setting for the cloud admin account
name endings shows an invented example, never your real endings, because the repo's rules keep
this environment's names out of the app.

**What it gets you.** Operators can find out what a box expects and what a button will do without
asking someone. Settings explain themselves to whoever switches them on. Help reaches keyboard,
touch and screen-reader users, which today's hover text does not.

**What it costs.** Three pieces of work to start: the shared parts (an app version bump), the
settings pages, and the password-reset page. After that, about one short piece of work per module
you choose. Beyond the password-reset page I recommend written notes for nine modules, a few hover
tips for thirteen, and nothing for six that already have enough. Taking everything is about
seventeen or eighteen short sessions, and you can stop after any of them. Each module's help bumps
that module's version by a patch.

**The biggest risk.** Help that drifts: an explanation that was true when written and is wrong
after a later change. Most of it cannot drift, because it is read from the module's own
declarations and quotes the page's own wording and setting names instead of copying them, and the
README will point at the help rather than keep a second copy. Hand-written explanations can still
go stale, so the rule is that whoever changes a page changes its help in the same piece of work.
The second risk is clutter: a "?" appears only where it adds something, a tip is two sentences at
most, and the Help panel stays closed until opened.

**What approving authorises.** The three starting pieces, the short module-guide note, and, module
by module, only the modules you pick from the numbered list in section 5. Nothing an operator can
do today changes: every existing message, hint and button stays as it is, except Admin Settings'
description, which is out of date and gets corrected. The work starts after the current changes are
in production.

---

## 1. The request and how this plan reads it

### 1.1 Verbatim

Owner, 2026-10-28:

> is this explained in the UI help? do we have comprehensive UI help? we need help popups on hover
> over a control/text box, a help dropdown arrow on pages so users can see usage examples where
> necessary and on the settings pages, etc. that's next after the current changes are in prod, so
> start the pre-planning with an agent.

"This" is the Cloud Password Reset setting of `docs/CloudPasswordResetEmployeeId-Plan.md` 4.3: the
owner name endings (`OwnerNameEndings`, shipped with the label "Cloud admin account name endings")
and its switch (`FillMissingEmployeeId`, "Fill in a missing employee ID"). Both landed during this
draft (`683a8ce7`).

Two owner notes reached the drafting agent through the supervisor the same day. The second
replaces the first; both are quoted so the change is visible.

Note 1 (superseded): *"it must include a change to docs/AdminModuleDeveloperGuide.md (and the
new-module playbook/skill that scaffolds a module, if one generates descriptor code) so developers
know they must add help text wherever it cannot be inferred at compile time ... with a tripwire test
that fails a module missing the must-write parts."*

Note 2 (governs): *"He said the developer-guide change is "probably" needed, not "must". Decide by
reading whether it is actually necessary: if the help system derives what it needs from the
descriptor and the page, say so and leave the guide alone; if there is hand-written help the system
cannot infer, say exactly what and add only the guidance for that. And NO tripwire that fails a
module for lacking comprehensive help: a simple operation does not need usage notes or examples, and
a module readiness test must not fail on their absence. Hand-written help is optional, declared when
a page or setting warrants it; the plan may suggest a soft check (a report of pages with no help,
not a failing test) and nothing stronger. Reflect this in the Exec summary."*

The supervisor's brief also asked for "a tripwire that every setting declared in the catalog has
help text". Note 2 withdraws coverage tripwires. Settings get help by construction instead
(section 3.6), and no test fails for help that is absent (section 6).

### 1.2 Readings

- "Help popups on hover over a control/text box": section 3.2.
- "A help dropdown arrow on pages": section 3.3.
- "Usage examples where necessary": hand-written, optional, per page or setting (3.6).
- "And on the settings pages": section 3.4.
- "Comprehensive": answered in section 2.7. The plan makes every page and setting reachable by help;
  it does not write notes for pages that do not need them (note 2).
- "Next after the current changes are in prod": nothing here starts before then.

### 1.3 Rulings the design must keep

- **The mandate** (`.agents/decisions.md` 2026-10-27): no intended functionality removed or
  significantly changed, judged by the operator's experience. Help only adds. Every existing
  banner, hint, placeholder, `title` tooltip and the Intune Devices panel stays as it is.
- **No clutter** (2026-09-25, `.agents/decisions.md:945-953`): help text may not be added by
  "ruining the UI", context must not be dumped into UI elements, and a control that appears once
  may carry the shared fact in one line and no more.
- **Rows** (`docs/RiskyUsersCompleteResults-Plan.md:509`, 2026-09-24): a control repeated on every
  row carries only what differs. **Consequences** (same plan, AC12 at `:768`): the consequence of a
  chosen action is visible without hovering at the confirmation step; "a tooltip alone does not
  satisfy this". **Today's hover text** (same plan, `:151-155`): a `title` attribute "does not
  appear on touch, does not appear for a keyboard user".
- **Audience and one source** (owner ruling 2026-09-02, recorded at `IntuneDevices.razor:140-145`
  and `:568-573`): pages are for L2 support desk staff, in plain words; the help panel is closed by
  default; its lines and the button tooltips come from one source.
- **Wording about access** (2026-10-27): describe permissions by what a person can do ("can open
  the module", "cannot see the password"), never "holds X but not Y".
- **Length** (`docs/VerbositySweep-Plan.md`, approved 2026-10-12): "Two short sentences is the
  ceiling for a banner, tooltip or empty state." Lead with the state, not the explanation.
- **No architecture in the UI** (`docs/AdminModuleDeveloperGuide.md`, UI Standards): "Avoid
  explanatory architecture text in the UI." Help says how to use a page, not how it is built.
- **Environment neutrality** (`.agents/repo-guidance.md` Invariant 7): no source names this
  deployment's domains, hosts, OUs, groups or addresses. Examples use reserved names.
- **Isolated module authors** (2026-10-22): the guide describes what the host exposes today, so the
  guide's help section lands in the same slice as the host surface it describes.
- **ASCII** (2026-07-21, `tools/Test-AsciiOnly.ps1`): tracked `.cs` files are pure ASCII. Help text
  lives in `.cs`, so it uses straight quotes and hyphens.
- **Working arrangement** (2026-10-26): one Opus 5.5 agent and one compile at a time; the single
  review is spent, so slices close on guard proof; self-review is forbidden.

## 2. What help exists today (read at `d117c35e`)

### 2.1 Descriptor text and where it shows

| Text | Declared at | Shown |
|---|---|---|
| Module `Description` | each descriptor in `Modules/ModuleCatalog.cs` | Home cards (`Home.razor:62`, `:81`); Module Configuration header (`ModuleConfig.razor:53`). **Not** on the module's own page. |
| Permission `Description` | `ModulePermission` | Module Configuration, Access tab only (`ModuleConfig.razor:199` via `PermissionDescriptionFor`, `:839-850`); guarded by `ModuleCatalogTests.Catalog_EveryPermissionCarriesAnOperatorFacingDescription` (`ModuleCatalogTests.cs:684`). Admins see it; operators never do. |
| Setting `Label`, `Description` | `ModuleConfigField` (`Modules/ModuleConfigField.cs:18-25`) | Module Configuration, Configuration tab (`ModuleConfig.razor:372-493`). |

There is no "hint" field. A setting carries `Key`, `Label`, `Description`, `Required`, `IsSecret`,
`DefaultValue` and `FieldType`; nothing else. 67 settings are declared across 26 modules (6 on/off
switches, 1 OU picker, 60 text boxes; counted at `1464aa7f`, after the two new ones); 16 declare a
default; none is secret.

How the Configuration tab renders a setting:

- The label is a bare `<label class="form-label">` (`:376-379`) with no `for`, so it is not tied
  to its control.
- The description is used twice: as the box's placeholder (`:385`, `:456`, `:484`) and as a
  `form-text` line under it (`:397-399`, `:458-460`, `:475-477`, `:487-489`). Neither is tied to the
  control for a screen reader.
- An on/off switch's own label is the word "Enabled" (`:471-472`), so a screen reader announces
  "Enabled, checkbox" without the setting's name.
- Several descriptions already carry their example inline as "e.g." (Exchange Online
  `ModuleCatalog.cs:146-147`, Exchange Migration `:487-488`, Conference Rooms `:1109-1121`, AD
  Attribute Editor `:1614`).
- One banner explains Secret ID fields for every module that has one (`ModuleConfig.razor:365-369`).

### 2.2 The settings pages

- **Module Configuration** (`/module-config/{ModuleId}`): as above, plus a hint under the Access
  tab ("Groups allowed to use this module, stored as SIDs...", `:165`) and one `title` explaining a
  servicing grant (`:317`). No page-level explanation of the tabs or of what saving no groups means.
- **Admin Settings**: hand-written hints for each protected-principal list, the Directory Read
  Credential and the logging level (`AdminSettings.razor:110, 120, 150, 178, 206, 237, 289, 301`),
  with placeholders as examples (`:144` "user@contoso.com", `:228` "adm-*"). The Modules tab shows
  each module's name only (`:80-101`). No descriptor text is rendered.
- **Exchange Online connection**: hand-written labels (`ExchangeOnlineConfig.razor:100, 108, 116`)
  that duplicate the descriptor's (`ModuleCatalog.cs:145-147`); the descriptor's descriptions are
  never shown. A certificate note (`:97`) and a one-line hint (`:118`).

### 2.3 The module pages

33 working pages: 31 module pages (one per catalog route) plus Module Configuration and the Message
Analysis reports sub-page. Home, Access denied and Error are excluded.

| What an operator is told | Pages |
|---|---|
| Nothing beyond an audit or read-only notice, a one-line purpose, a placeholder or empty-state text (8) | Account Lockout Remediation (nothing at all; parked), Cloud Password Reset (`CloudPasswordReset.razor:31-33`), Delegation Report (`:18`, `:20`), MFA Reset (`:32-33`), Out of Office (`:22`, `:24`), Recipient Lookup (`:20`, `:22`), Service Health (one filter tooltip, `:158`), Event Log (a telemetry note, `AdminEventLog.razor:126-129`) |
| One line of instruction (5) | AD Attribute Editor (`:233`), Defender for Endpoint Devices (`:64`), Named Locations (`:129`), M365 Group Management (`:110`), DHCP Authorization (ticket hint, `:119`) |
| Field hints or tooltips (17) | Mailbox Permissions, Calendar, AD Group Management, Self-Service Groups, Conference Rooms, Exchange Migration, Message Analysis, BitLocker Recovery, Blocked Senders, Comms-10k, Risky Users, True Last Logon, Bulk Jobs, Message Analysis reports, Admin Settings, Module Configuration, Exchange Online |
| A list of steps or an explanation panel (3) | Emergency Disable and Licensing Updates ("Instructions" cards, `EmergencyDisable.razor:203-209`, `LicensingUpdates.razor:212-215`); Intune Devices ("What do these actions do?", `IntuneDevices.razor:140-170`) |

Cloud Password Reset's other lines are state messages, not usage help: the force-change instruction
shown when the box is cleared (the owner's words, `.agents/decisions.md` 2026-09-23) and the reason
the reset button is unavailable. Since `683a8ce7` the page also says, under "Not set", why a
fill-in lookup found no owner (`FillLookupText`, `CloudPasswordReset.razor:901-920` at `1464aa7f`).

### 2.4 Hover text, hints and accessibility

- 79 `title` attributes on 15 pages; 41 of them on Exchange Migration. They show on mouse hover only.
- 37 `form-text` hints on 11 pages.
- `aria-describedby` appears once in the app (`SessionEndedNotice.razor`) and never on a page: no
  hint is tied to the control it describes.
- No page has a worked usage example; the only examples are placeholders and "e.g." clauses.
- The app ships Bootstrap 5.1's stylesheet and no Bootstrap JavaScript (`Components/App.razor`;
  `IntuneDevices.razor:568-573` records why its panel is toggled from C#). No icon font is shipped:
  the only `bi-*` classes defined are the nav icons (`NavMenu.razor.css`, `wwwroot/app.css`).

### 2.5 Outside the app

`README.md` has a section per module under Features (`:5-528`) and the CSV formats (`:1133-1160`);
five modules have a `docs/<Module>.md`. They are written for maintainers and administrators and
none is reachable from the app.

### 2.6 Drift found while reading

- Admin Settings' descriptor description, "Configure which AD groups have access to each application
  section." (`ModuleCatalog.cs:1621`), is out of date: section access moved to each module's page,
  as the page's own subtitle says (`AdminSettings.razor:44`). It shows on the Home card. Fixed in H2.
- `README.md` "Admin Settings Page" (`:74-76`) describes only module enablement. Fixed in H2.
- Module Configuration's Secret ID banner names a deployment-specific Secret Server template
  (`ModuleConfig.razor:368`). It is not a domain, host, OU, group or address, so Invariant 7 does
  not strictly reach it. Left as it is (J10); follow-up F1.
- The fill switch's shipped description (`ModuleCatalog.cs`, `FillMissingEmployeeId`, at
  `1464aa7f`) is six sentences, always visible under the checkbox - longer than the verbosity
  sweep's ceiling. It is the employee-ID plan's wording under the owner's approval, so this plan
  does not change it (J13).
- The headers of `docs/AdminModuleSpec.md` and `docs/AdminModuleDeveloperGuide.md` say "Last
  verified against code" in June 2026, before `2.36.0`. This plan does not re-verify either
  document as a whole, so its edits leave those lines alone and say so.

### 2.7 Verdict

Not comprehensive. Eight of 33 working pages tell an operator nothing about how to use them, five
more give one line, one page has an explanation panel, no page has a worked example, settings show
only their one-line description, and the existing hover text is mouse-only.

## 3. Design

### 3.1 Rules

1. **On demand.** Help is closed until asked for: a tip opens on hover, focus or tap; the panel
   opens on a click. Nothing new is always visible except the small markers.
2. **Only where it adds something.** A "?" appears only when its tip says something the visible
   text does not. Descriptions are still tied to their controls for screen readers everywhere.
3. **Short.** A tip is at most two short sentences plus one example line. Anything longer goes in
   the page's panel.
4. **Supplementary, never the only place.** A consequence an operator must see before acting stays
   on the confirmation step (Risky Users AC12). Help repeats or explains it; it never replaces it.
5. **Plain words for the page's audience**: L2 operators on module pages, administrators on the
   settings pages. Permissions are described by what a person can do.
6. **Reserved example names only**: `example.com`/`.net`/`.org`, `EXAMPLE\`, `DC=example`, the
   documentation IP ranges `192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24` and `2001:db8::/32`.
   The Microsoft placeholder names already in descriptions (`contoso`) are accepted.
7. **Quote by reference.** Help that names a setting renders the setting's label from the catalog;
   help that quotes a sentence the page shows uses the same constant the page uses; captions it
   quotes from markup are listed and pinned (section 6, T9).
8. **No behaviour change.** No help element is ever disabled, gated or able to trigger an
   operation.

### 3.2 Hover help on a control (`HelpTip`)

- **Look.** A small "?" button (Bootstrap button classes, at least a 24 by 24 pixel target, the
  text character "?" because no icon font ships), placed right after the label of the box or switch
  it explains.
- **Opens** on pointer hover and on keyboard focus (CSS `:hover` and `:focus-visible`), and on click
  or tap, which pins it open (Blazor state). **Closes** when the pointer leaves an unpinned tip, on
  Tab away, on Escape, and on a second click.
- **Content.** At most two short sentences, then an optional line `Example: <value> - <meaning>`.
  No links or controls inside.
- **Screen readers.** The tip is an element with `role="tooltip"` and an id. The "?" button's
  accessible name is "Help: <subject>" and it points at the tip with `aria-describedby`; the
  control it explains points at the tip too, so the help is read when the control itself has focus.
- **WCAG 2.1 1.4.13.** The pointer can move onto the tip without it closing; it stays until
  dismissed; Escape dismisses it (J4 for a tip the pointer rests on without focus).
- **No round-trip for hover.** The server sees only a click, an Escape or focus leaving the marker.
- **Never disabled**, and it takes no `Disabled` parameter, so the click-gate sweep's child-component
  enumeration never lists it.
- **Never on a control repeated per row**; at most one on that column's header.

### 3.3 The Help panel (`PageHelpMenu`)

- **Where.** A "Help" button with Bootstrap's CSS caret (`dropdown-toggle`), in the top bar left of
  the theme picker, inside the `<Authorized>` block that holds `UsageTracker` and `ThemePicker`
  (`MainLayout.razor:26-35`). One interactive island; no page is edited to get it (J1).
- **Which help.** Resolved from the route exactly as `ModuleVersion` and `UsageTracker` resolve it
  (`UsageTracker.RouteOf`, `UsageTracker.razor:71-81`), and re-resolved on `LocationChanged`, the
  `UsageTracker` pattern. Two host entries cover the routes that are not catalog routes:
  `module-config/{id}` (Module Configuration) and `message-analysis/reports` (the sub-route gap
  noted in `MessageTraceReports.razor`). The empty route (Home) shows the glossary of shared terms.
  Access denied and Error show no button.
- **Panel.** Opens under the button, over the page rather than pushing it down (J2): right-aligned,
  about 40rem wide or the viewport's width if smaller, scrolling within the viewport. Closed by
  default. Closes on its button, its close control, Escape (focus returns to the button through
  Blazor's built-in `FocusAsync`) and on navigation.
- **Sections, in this order, each left out when empty:**
  1. About this page - the module's `Description` (derived).
  2. How to use it - declared notes.
  3. Examples - declared.
  4. Controls on this page - the declared control tips, the same text as the hover tips.
  5. Terms - the shared terms the page declares, with their shared definitions.
  6. What access to this module allows - the main permission's description under "Using this
     module" and each granular permission's under its `DisplayName` or `Name` (derived).
- **Accessibility.** The button has `aria-expanded` and `aria-controls`; the panel is a region
  labelled by its heading, "Help: <DisplayName>"; it follows the button in DOM order, so Tab
  enters it; it is not modal and traps no focus.
- **What every module page gets from H1 with nothing written:** sections 1 and 6. Section 6 is new
  to operators: permission descriptions are shown today only to administrators on the Access tab.

### 3.4 Settings

**Module Configuration, Configuration tab** (`ModuleConfig.razor:372-493`), driven by the
descriptor:

- Every control gets an `id`; its label gets `for`; its description line gets an id and the
  control's `aria-describedby` names it (and the tip, when there is one); required controls get
  `aria-required`; the red asterisk is hidden from screen readers (it reads as "star"); an on/off
  switch's accessible name includes the setting's label. **Nothing visible changes**: label,
  asterisk, placeholder and description line stay as they are.
- A "?" follows the label only when the tip adds something the visible text does not: a declared
  default (shown only once verified, J11) and the setting's optional `Details` and `Example`
  (section 3.5). The visible description is not repeated inside the tip.

**Module Configuration's own Help panel** (host-declared): what each tab does; what access means
(a permission never configured is closed to everyone when it fails closed; one saved with no groups
is closed to everyone); module admins; that a saved setting goes to the shared configuration
database and applies to every instance that uses it (`.agents/decisions.md` 2026-09-04); the
unsaved-changes dot.

**Admin Settings** (host-declared): a tip per protected-principal list with a reserved-name example,
on the Directory Read Credential and on the logging level, plus its panel. The existing hints stay
(`AdminSettings.razor:110-301`). No tip per module switch on the Modules tab (J9).

**Exchange Online connection**: tips for its three fields built from the ExchangeOnline descriptor's
fields by key (`ModuleCatalog.cs:145-147`), so the descriptor's descriptions finally show without a
second copy, plus its panel.

### 3.5 Where the words live

| Text | Lives in | Written by | Shown in |
|---|---|---|---|
| What the page is for | descriptor `Description` (exists) | module author (exists) | Home card, Module Configuration header, panel section 1 |
| What each permission lets a person do | `ModulePermission.Description` (exists) | module author (exists) | Access tab, panel section 6 |
| A setting's name, meaning, required, default, type | `ModuleConfigField` (exists) | module author (exists) | Configuration tab; tied to the control; default in the tip |
| A setting's extra detail and example | **new** optional `ModuleConfigField.Help` (`SettingHelp`) | module author, when the format or effect is not obvious | the setting's tip |
| Page notes, examples, control tips, terms used | **new** optional `AdminModuleDescriptor.Help` (`ModuleHelp`), its content in the module's own `Modules/Help/<ModuleId>Help.cs` | module author, when the page warrants it | panel sections 2-5; control tips on the page |
| Shared terms | **new** `Modules/HelpConcepts.cs` | the host only | panel section 5; tips that reference a term |
| Help for host pages (Home glossary, Module Configuration, Message Analysis reports) | **new** `Modules/HostPageHelp.cs` | the host only | the panel on those pages |

Sketch of the shapes (the implementing agent refines names and members):

```csharp
public sealed record HelpExample(string Value, string Meaning);
public sealed record SettingHelp(string? Details = null, HelpExample? Example = null);
public sealed record HelpTipText(string Subject, string Text, HelpExample? Example = null);
public sealed record HelpTerm(string Term, string Definition);          // HelpConcepts members
public sealed record HelpSection(string Title, IReadOnlyList<string> Lines, bool Numbered = false);
public sealed record ModuleHelp
{
    public IReadOnlyList<HelpSection> Notes { get; init; } = [];        // includes "Examples"
    public IReadOnlyList<HelpTipText> Controls { get; init; } = [];
    public IReadOnlyList<HelpTerm> Terms { get; init; } = [];           // HelpConcepts objects, not keys
    public IReadOnlyList<string> QuotedCaptions { get; init; } = [];    // markup captions the notes quote
}
// ModuleConfigField gains a trailing `SettingHelp? Help = null`; AdminModuleDescriptor gains
// `public ModuleHelp? Help { get; init; }`. Both are optional with a null default.
```

- **Why on the descriptor.** The descriptor is the one thing an isolated module package already
  delivers (`integration/ModuleCatalog.snippet.cs`), so a module declares its help without editing
  a host file (2026-10-22). It also fits the draft `docs/ModuleDevelopmentPlatform-Plan.md`, whose
  section 6 forbids a new central registry: help travels with its module.
- **Safe to add.** `ModuleConfigField` is only ever constructed - by target-typed `new(...)` in the
  catalog and once by name in a test (`SettingsConnectionAndSecretSaveTests.cs:371`) - and never
  deconstructed (`grep` at `d117c35e`), so a trailing optional parameter breaks nothing. The package
  validator's descriptor regexes read `MainPermission`, `Id`, `Route` and `IconCss` only.
- **One token:** `{Setting:Key}` inside help text renders that module's setting label, so a renamed
  label follows. No other markup: help renders as text, never as `MarkupString`, so help text can
  never inject HTML.
- **Terms are objects** (`HelpConcepts.Ticket`), so a reference to a term that does not exist does
  not compile.

The shared terms (H1), each within the two-sentence ceiling:

| Term | Definition |
|---|---|
| Preflight | A read-only check before a change. It shows what will change and for whom; nothing is written until you confirm. |
| Reset | Replaces a password or sign-in method with a new one. The old one stops working at once and this portal cannot bring it back. |
| Ticket | The change or incident number the work is done under. It is recorded with the action in the audit log. |
| Protected principal | A user, group, OU or name pattern an administrator has listed as off-limits in Admin Settings. Pages that change directory objects refuse to change one and record the refusal; a page that treats them differently says so in its own help. |
| Audit log | The record of every action and refusal here: who, from which address, what, to which target, the result and the ticket. It is read on the Event Log page by those granted it. |
| Bulk job | Work the server runs in the background, row by row, so you can leave the page. Its results stay on the page that started it; administrators also see it on the Bulk Jobs page. |
| Fail closed | When a setting, an access list or a check cannot be read, the portal refuses instead of guessing. |
| Section access | The groups whose members may use a module or one of its permissions. A permission saved with no groups is closed to everyone. |
| Module admin | A group whose members may configure one module without being global administrators. |

"Protected principal" is worded for the three scoped exceptions (Self-Service Groups, Comms-10k,
Conference Rooms; Constitution, Protected Principals): it claims nothing they do not do. "Reset" is
referenced only by pages that mean a credential reset; AD Attribute Editor's "Reset" button reverts
an unsaved edit and does not reference it.

### 3.6 What builds itself, and what a developer writes

| Builds itself from the descriptor and the route | Written by hand, optional, only when warranted |
|---|---|
| Which help a page shows (its route) | A setting's extra detail and example (`SettingHelp`), when its format or effect is not obvious from the description |
| Panel section 1: what the page is for (`Description`) | Page notes and worked examples (`ModuleHelp.Notes`), when the workflow is not obvious |
| Panel section 6: what each permission lets a person do | A hover tip for a control on the page that is not a setting (`ModuleHelp.Controls` plus a `<HelpTip>` beside it) |
| Each setting's label and description, tied to its control | Which shared terms the page uses (`ModuleHelp.Terms`), from the host's list |
| Each setting's required flag (`aria-required` and the existing asterisk) and default (in its tip) | |
| The right control and accessible name per setting type (existing types; switches named by label) | |

**Is the guide change necessary?** Yes, but only for the right-hand column. The system cannot infer
an example, a workflow note, a control's explanation or which terms a page uses, and a module author
cannot discover the four optional declarations without being told. Everything in the left-hand
column needs no guidance and gets none. A module that declares nothing still has a Help panel with
sections 1 and 6, every setting tied to its control, and passes every test. Section 5.4 lists the
exact edits.

**Soft check, nothing stronger.** The guide's help section gives one search command that lists
descriptors with no `Help` and settings with no `SettingHelp`, for whoever wants to know. It is a
report, run on request. No test, gate or validator rule reads it.

### 3.7 The README, the module docs and the help

- **The help owns operator usage**: how to use a page, examples, what a control accepts, what to do
  when refused, and the shared terms. Operators cannot read the repo, so the help must stand alone.
- **The README and `docs/<Module>.md` own reference material**: requirements, grants and secrets,
  section access keys, audit actions, the full refusal catalogue for administrators.
- **Move, do not copy.** When a module's help slice finds operator-usage sentences in that module's
  README section, it moves them into the help and leaves one pointer line ("Operator usage: the
  page's Help panel; text in `Modules/Help/<ModuleId>Help.cs`"). Reference content stays.
- **Exception, recorded:** the owner's 2026-10-28 ruling on Recipient Lookup puts the field list
  "in the record and the README". This plan does not override it (5.5).

### 3.8 Constraints from the codebase

- **No JavaScript library.** Tips are CSS plus Blazor event handlers; the panel is Blazor state with
  Bootstrap's CSS classes; returning focus uses Blazor's built-in `FocusAsync`. Bootstrap's JS stays
  unshipped. One optional short inline script is J4. The HTML `popover` attribute was considered and
  not chosen: placing a popover beside its control needs CSS anchor positioning, which not every
  browser operators may use supports.
- **Themes.** Colours only through the existing `--ui-*` variables (`.dropdown-menu` is already
  themed, `wwwroot/app.css:1341-1344`; Bootstrap's `.popover` is not, so the tip has its own class),
  following `UiThemeCssTests`.
- **ASCII.** Help strings in `.cs` files are ASCII (straight quotes, hyphens, no arrows); the
  caret is CSS, not a character.
- **Click gating.** `ClickGateRegistry` keys entries by line number on its 12 converted pages
  (`ClickGateRegistry.cs:31-45`). A `<HelpTip>` placed after a `</label>` on the label's own line,
  and `aria-describedby` added to an existing control line, add no line, so no entry moves; a
  registered entry's snippet check (`tag.Text.Contains(Snippet)`) still matches a tag that gained an
  attribute. If a line must be added, re-anchor with `.agents/playbooks/clickgate-reanchor.md`.
  `ProgressRegistry` keys by handler and call text and has no line field at `d117c35e`.
- **Layout tests.** `UsageTrackerWiringTests` reads the `<Authorized>` block of `MainLayout.razor`;
  the new island sits inside the same block. `AppLayoutCssTests` guards the height chain; the panel
  overlays rather than pushes (J2), so the chain is untouched.

### 3.9 Judgement calls (each reversible in a word)

- **J1** The Help button lives in the top bar of every page, not inside each page's heading. The
  alternative needs an edit to every page and a re-anchor of the 12 converted pages, and makes the
  button something each module must remember.
- **J2** The panel opens over the page, not by pushing it down: no page layout or height chain is
  affected. Inline is the fallback if the overlay proves awkward on a page.
- **J3** Every module page gets the panel with its derived sections even when nothing is written;
  Home gets the glossary; Access denied and Error get nothing.
- **J4** Recommended: a short inline script beside the app's existing inline helpers
  (`Components/App.razor`) so Escape closes a tip the pointer rests on, as WCAG 2.1 1.4.13 asks.
  Without it, Escape works once the marker has focus and moving the pointer away closes the tip.
  Not a library either way.
- **J5** A marker appears only where it adds something (rule 2).
- **J6** Tips stay within two short sentences and an example (rule 3).
- **J7** No tips on per-row controls (rule from 2026-09-24).
- **J8** Existing `title` tooltips are left as they are, including Exchange Migration's 41; turning
  them into keyboard-reachable tips is follow-up F4, not this plan.
- **J9** No tip per module switch on Admin Settings' Modules tab: thirty markers in a grid is the
  clutter 2026-09-25 forbids, and each module's purpose is on its Home card and configuration page.
- **J10** The only existing text this plan changes is Admin Settings' stale description (2.6). The
  Secret ID banner's template name stays (F1).
- **J11** A setting's default is shown in its tip only after the slice confirms the consuming code
  uses that same default when nothing is saved. A mismatch is admitted as a finding, not shown.
- **J12** Help is compiled into the app, not editable at runtime: no new configuration write path,
  no markup, and a change ships with the code it describes.
- **J13** The fill switch's six-sentence description stays as shipped. The help system gives it a
  better home - its first and fifth sentences (what On and Off do) visible, the other four moved,
  word for word, into its tip - but the tip would then exceed the two-sentence ceiling, and the
  wording is the employee-ID plan's. Owner's call; not done unless he says so.

## 4. Worked example: Cloud Password Reset

Read at `1464aa7f`, after `docs/CloudPasswordResetEmployeeId-Plan.md` merged (`683a8ce7`). Setting
labels render from the catalog through `{Setting:Key}`, so a later rename follows. The deployment's
own endings, recorded in that plan's section 1 from its source script, never appear in help text;
the example is invented.

### 4.1 "Cloud admin account name endings" (`OwnerNameEndings`)

Visible, unchanged: the label, and the shipped description under the box - "Comma-separated endings
that your cloud admin account names add to their owner's logon name. The longest one an account's
name ends with is removed, ignoring case, before the owner is looked up. Leave blank if the names
add nothing."

The description already states the matching rule, so the tip adds only what it does not say
(rule 2): when the setting is used, that spaces do not matter (the parser trims each ending and
drops empty ones, `Services/CloudPasswordResetService.cs:682`), and an example. There is no default
line: the default is blank.

> Used only when Fill in a missing employee ID is on. Spaces around the commas are ignored.
> Example: -admin,_admin - jdoe-admin@example.com and jdoe_admin@example.com both point to the logon
> name jdoe.

Declared on the setting:

```csharp
Help: new SettingHelp(
    Details: "Used only when {Setting:FillMissingEmployeeId} is on. Spaces around the commas are ignored.",
    Example: new HelpExample("-admin,_admin",
        "jdoe-admin@example.com and jdoe_admin@example.com both point to the logon name jdoe."))
```

### 4.2 "Fill in a missing employee ID" (`FillMissingEmployeeId`)

Visible, unchanged: the label, the checkbox and the shipped six-sentence description (J13). Screen
readers now hear "Fill in a missing employee ID" instead of "Enabled".

Everything the switch does is already in its description, so the tip carries the derived default
and the one thing the description lacks, a worked example:

> Default: off.
> Example: On - jdoe-admin@example.com has no employee ID; with the ending -admin it points to the
> logon name jdoe, whose employee ID is saved on it before the password is emailed to jdoe.

Declared: `Help: new SettingHelp(Example: new HelpExample("On", "jdoe-admin@example.com has no
employee ID; with the ending -admin it points to the logon name jdoe, whose employee ID is saved on
it before the password is emailed to jdoe."))`. "Default: off" is derived from
`DefaultValue: "false"` and shown once J11's check passes (that plan's 4.3 reads an absent key as
off).

### 4.3 Hover tips on the page's controls

Four, each declared once in `Modules/Help/CloudPasswordResetHelp.cs` and listed again in the panel
(lines at `1464aa7f`):

| Next to | Tip |
|---|---|
| "Cloud account (UPN)" (`CloudPasswordReset.razor:46`) | The account's sign-in name, as Entra ID shows it. Example: jdoe-admin@example.com |
| "Ticket number" (`:144`) | Required, and recorded with the reset in the audit log. When Validate ServiceNow tickets is on, it must also be a valid ServiceNow ticket. Example: INC0010001 |
| "Force password change at next sign-in" (`:151-153`) | Leave it ticked: the owner then has to choose a new password at first sign-in, so the emailed one works once. Clear it only for an account whose sign-in cannot show a change prompt. |
| "Password goes to" (`:119`) | The mailbox of the one directory user whose employee ID matches the account's. You cannot change it. |

"Validate ServiceNow tickets" is `{Setting:ValidateTickets}`. The force-change tip leads with an
instruction, as the owner asked of that box's warning (`.agents/decisions.md:1048-1056`); the
warning itself (`:156-159`) is unchanged. There is no tip on the Employee ID row: its planned-save
line and its "Not set" reasons (`:101-115`) already say what is happening and what to do.

### 4.4 The page's Help panel, word for word

Quoted on-screen names are pinned against the page source (T9). Angle brackets mark text rendered
from the catalog or from a page constant.

> **Help: Cloud Password Reset**
>
> **About this page**
> <the module's Description, as the catalog holds it>
>
> **How a reset works**
> 1. Enter the account's sign-in name and choose "Check account". This preflight reads the account
>    and works out where the new password will go. Nothing is changed.
> 2. Read the panel that appears. "Password goes to" is the owner's mailbox. Check the account and
>    its directory roles: an account with an administrative role is reset like any other.
> 3. Enter the ticket number. Leave "Force password change at next sign-in" ticked unless the
>    account's sign-in cannot show a change prompt.
> 4. Choose "Reset password", then "Confirm reset". The portal makes a new password, sets it on the
>    account and emails it to the owner. It is not shown to you, except in the one case below.
>
> **Where the password goes**
> - To the owner's mailbox, never to you. The owner is the one directory user whose employee ID
>   matches the employee ID on the cloud account. You cannot choose or change the address.
> - If the owner cannot be found, "Password goes to" says why: the account has no employee ID, no
>   one or more than one person carries the number, the owner has no mailbox, or the directory
>   could not be searched.
> - If an administrator has switched on <Fill in a missing employee ID> and the account has no
>   employee ID, the check also shows the employee ID it will save and the directory account it was
>   taken from, found from the account's name. Check that this person owns the account before you
>   confirm. If no single owner is found, a line under "Not set" says why and what to do.
> - If you can see passwords on screen, a reset whose owner cannot be found shows the new password
>   once, on this page, and emails no one. It is not shown again.
>
> **When a reset is refused**
> - If you can open this page but cannot see passwords on screen, and the owner cannot be found,
>   the reset is refused with: "<The password cannot be delivered, and you do not hold the
>   permission to see it on screen.>" Nothing is changed. What to do depends on the reason shown:
>   no employee ID - the account needs one before it can be reset this way; more than one person -
>   the directory needs correcting; the directory could not be searched - try again shortly.
> - Also refused, with the reason on screen and in the audit log: an account synced from
>   on-premises Active Directory (reset it there), a guest account, a protected account, a ticket
>   that is not accepted, and a deployment with user email switched off.
> - "The account changed while you were confirming": the account changed between your check and
>   your confirm, so nothing was done. Check it again.
>
> **Examples** (all names are invented)
> 1. jdoe-admin@example.com carries employee ID 10001, and jdoe@example.com is the only directory
>    user with 10001. The password is emailed to jdoe@example.com.
> 2. jdoe-admin@example.com has no employee ID. <Fill in a missing employee ID> is on and <Cloud
>    admin account name endings> is -admin,_admin, so the name points to the logon name jdoe, whose
>    employee ID is 10001. The check shows that 10001 will be saved; on confirm it is saved on the
>    account, then the password is emailed to jdoe@example.com.
> 3. As example 2, but no directory user has the logon name jdoe. If you can see passwords on
>    screen, the password is shown once and emailed to no one. If you cannot, the reset is refused
>    and nothing is changed.
>
> **Controls on this page** - the four tips of 4.3.
>
> **Terms** - Preflight, Reset, Ticket, Protected principal, Audit log (3.5).
>
> **What access to this module allows**
> - Using this module: <the main permission's description>
> - Reveal: <the granular permission's description>

How the panel matches the code it describes, at `1464aa7f`:

- Preflight writes nothing (`LookupAsync`; that plan's 4.1 keeps it read-only).
- The refusal sentence is `WhyResetUnavailable`'s non-reveal branch (`:268-271`), shown at
  `:171-174`; the confirm-time variant (`:511-512`) stays verbatim and unquoted. Both stay as the
  owner ruled ("the existing cannot process error holds"). The help's own words follow the
  2026-10-27 wording rule; the quoted sentence is the page's and is not reworded.
- The reasons follow `DestinationRefusalText` (`:877`) and `FillLookupText` (`:901-920`), and their
  remarks on what to do per case.
- The other refusals are `SyncedAccount`, `GuestAccount` (`Services/CloudPasswordResetService.cs:
  356-357`, `:373-374`), `ProtectedPrincipal` (page `:707-708`), the ticket gate (`:437`) and
  `NotificationsDisabled` (`:519-520`).
- The planned save shows in the Employee ID row (`:101-107`) and in the confirm prompt
  (`:189-192`). The shown-once reveal is `:207-215`. "The account changed" is `:498`.
- Example 2 follows that plan's 4.2 table row 11 and its 4.5 Employee ID row; example 3 its row 3
  and its section 1 table.

### 4.5 What the slice changes (H3)

- **New** `Modules/Help/CloudPasswordResetHelp.cs`: the notes, examples, four control tips, terms,
  quoted captions, and the refusal sentence as a constant.
- `Components/Pages/CloudPasswordReset.razor`: `WhyResetUnavailable` (`:268-271`) returns that
  constant, text byte-identical; four `<HelpTip>`s on the label lines; `for`/`id` on the account and
  ticket labels (`:46`, `:144`, today not tied to their boxes); `aria-describedby` on the three
  controls. The page is on the not-converted list (`ClickGateRegistry.cs:68`) and its
  `ProgressRegistry` entry is keyed by handler and call names, so added lines move nothing.
  Re-check both if the page has been converted by then.
- `Modules/ModuleCatalog.cs`: `Help = CloudPasswordResetHelp.Page`; `Help:` on the two settings; a
  module patch bump from what master holds (`1.2.0` at `1464aa7f`) with a version-comment line.
- `README.md` Cloud Password Reset section: one pointer line. `docs/CloudPasswordReset.md`: a pointer
  for operator steps; its reference content stays.
- Tests T10 (section 6).

## 5. Rollout

### 5.1 Order

1. **H1 - the shared parts.** Records, `HelpConcepts`, `HostPageHelp` (Home glossary), `HelpTip`,
   `PageHelpMenu`, the top-bar line in `MainLayout.razor`, J4's script if taken, guards T1-T5, T8,
   T9, T11, T12, the guide and spec edits (5.4). Base minor bump. Visible result: a Help button on
   every module page with its derived sections; the glossary on Home.
2. **H2 - the settings pages.** Module Configuration (setting ties, markers, its panel), Admin
   Settings (tips, panel, the stale description), Exchange Online (tips from its descriptor, panel),
   J11's check of the 16 declared defaults, `README.md` "Admin Settings Page" and "Per-Module Config
   Pages". Tests T6, T7. Base patch bump; Admin Settings and Exchange Online module patch bumps.
3. **H3 - Cloud Password Reset.** Section 4. The settings it explains shipped at `683a8ce7`.
4. **Modules the owner picks from 5.2**, in this provisional order, re-ranked by the Event Log's
   Usage view (opens per module) on production when the work starts (A5).

### 5.2 Modules, numbered: take or leave

Notes and tips, one slice each:

1. **Emergency Disable** - what each step does, which can be undone from the snapshot, when to use
   it. The Instructions card stays.
2. **Mailbox Permissions** - accepted identity forms, the CSV format with an example (moved from
   `README.md` "CSV Format - Mailbox Permissions"), why every grant is refused while the Directory
   Read Credential cannot be had (operator-visible since `2.36.0`).
3. **Calendar** - access levels, the CSV example (`README.md` "CSV Format - Calendar Permissions"),
   the same refusal.
4. **AD Group Management** - paste formats, Resolve, nested groups and their one-way warning, bulk
   remove, protected group targets.
5. **Exchange Migration** - eligibility, batches, what each status allows (a batch with errors cannot
   complete, 2026-09-25), move-back.
6. **Conference Rooms** - Room Finder metadata, room lists by building, booking policies, room-type
   templates, the CSV preview, bulk jobs; setting examples where its 14 settings need them.
7. **Message Analysis** with its reports page - header analysis versus trace, time windows, detail
   exports and where they are kept.
8. **Event Log** - filters, the Usage view, the CSV download, what Undo does and does not do.
9. **Licensing Updates** - the CSV format with an example, the values, the preview.

A few tips only, grouped two or three to a slice:

10. MFA Reset. 11. Out of Office (the schedule's time zone). 12. Named Locations (IP range and
country examples in documentation ranges). 13. Self-Service Groups. 14. M365 Group Management.
15. Comms-10k. 16. DHCP Authorization. 17. BitLocker Recovery. 18. AD Attribute Editor (its "Reset"
button is not the shared term). 19. Defender for Endpoint Devices. 20. Risky Users. 21. True Last
Logon. 22. Recipient Lookup, after `docs/RecipientLookupAccuracy-Plan.md` lands (5.5).

None recommended - the derived help is enough:

23. Intune Devices (has its own panel; its `ActionHelp` could also feed the panel later). 24. Blocked
Senders. 25. Delegation Report. 26. Service Health. 27. Bulk Jobs. 28. Account Lockout Remediation
(parked, 2026-10-19: "do not spend on it").

Each module slice: declare `Help` (and any `SettingHelp`) in the module's own file, place its tips,
move operator usage out of its README section, bump its version, run the gates. Line keys only move
if a line is added on a converted page (3.8).

### 5.3 Versions

- Shared help components and the Module Configuration rendering are shared infrastructure: H1 bumps
  the base app minor version (a new shared capability, the `2.34.0` precedent of
  `docs/ConfigDbToolInApp-Plan.md`), H2 the patch.
- Help text is a module change: a patch bump of that module's `Version` in `ModuleCatalog`, with a
  version-comment line, in the slice that adds its help (H2 for Admin Settings and Exchange Online,
  H3 for Cloud Password Reset, every module slice).
- The derived panel H1 puts on every page changes no module's own code, so it bumps no module.
- Docs-only edits bump nothing. Bump from what master holds; each layer once per slice.

### 5.4 The module guide and the spec (in H1, with the surface they describe)

`docs/AdminModuleDeveloperGuide.md`:

- **Module Descriptor**: the two shape listings (`:205-221`, `:242-251`) gain the optional members,
  one sentence each. Without this the listings go stale, and the new-module command treats a
  difference between the guide and the code as something to report.
- **New section "In-app help (optional)"** after UI Standards, about twenty lines: what a module
  gets with nothing written (3.6, left column); the four optional declarations and when each is
  warranted (when an operator could otherwise get a format, a consequence or a refusal wrong; a
  one-step page needs none); the wording rules of 3.1; that absence fails nothing; that whoever
  changes a page's behaviour changes its help in the same commit; the soft-check command.
- **Required Files**: one optional line, `Modules/Help/<ModuleName>Help.cs`.
- Not changed: Review Checklist, Tests, Definition Of Done, Documentation Required. Help is
  optional, so it is not a checklist item.

`docs/AdminModuleSpec.md`: one bullet under UI Rendering - the Help panel (Description, permission
descriptions, optional `Help`) and setting help on Module Config pages. Its checklist is unchanged.

**Not changed, and why:**

- `.claude/commands/new-module-command.md` generates no descriptor code. Its Phase 0 reads the guide
  "in full. It is the binding contract", and Phase 1 drafts the complete descriptor from it, so the
  optional help reaches new modules through the guide. A mandatory step would contradict note 2.
- `tools/validate-module-package.ps1`: any rule fails packages - an Error always, a Warning under
  `-TreatWarningsAsErrors` - which is stronger than note 2 allows.
- `docs/templates/Plan-Template.md`: unchanged.

### 5.5 Other plans this meets

- **`docs/CloudPasswordResetEmployeeId-Plan.md`** (Implemented 2026-10-28): H3 explains what it
  shipped; its manual live checks (its section 13) are unaffected.
- **`docs/VerbositySweep-Plan.md`** (approved, not started): the same pages and the same kind of
  text. Help follows its shape rule. A page's help slice runs after that page's sweep slice, or the
  two are done as one pass on the owner's word; never side by side. Text the sweep cuts from
  always-visible places as explanation may land in that page's panel instead of disappearing.
  Note: its 4.1 says `ProgressRegistry` is line-keyed; at `d117c35e` it has no line field (A8).
- **`docs/ClickGatingAudit-Plan.md` tiers 2-4** (approved, not started): Module Configuration, Admin
  Settings and Exchange Online are tier 2 and not yet converted, so H2 before their conversion costs
  no re-anchoring; after it, H2 keeps line counts or re-anchors.
- **`docs/RecipientLookupAccuracy-Plan.md`** (approved 2026-10-28): the owner ruled the field list
  goes "in the record and the README". Recipient Lookup's tips (item 22) explain the fields from the
  page's own definitions and leave the README copy as ruled.
- **`docs/CancellationDepth-Plan.md`** (approved 2026-10-28): no conflict. Once its truthful leave
  prompt lands, a shared term quoting its three cases by reference is follow-up F5.
- **`docs/ModuleDevelopmentPlatform-Plan.md`** (draft): compatible; help lives on the descriptor.

## 6. Tests

Patterns already in the suite: a component's `BuildRenderTree` driven into a builder and its frames
read (`Comms10kTicketDisplayTests.cs:187-192`, `ConfigBooleanTests.cs:269-277`); handlers invoked on
a test `Renderer` (`DefenderSearchLifecycleTests.cs:243`); source-text guards over `.razor` files
(`ModuleCatalogTests.ModuleConfig_AccessTab_RendersEachPermissionsDescription`). No new package.

| # | Slice | Asserts |
|---|---|---|
| T1 | H1 | `HelpTip` renders a `<button type="button">` named "Help: <subject>" whose `aria-describedby` is the id of an element with `role="tooltip"` holding the text and the example line; two tips on one page get different ids; no `tabindex="-1"`; text frames only, no markup frame. |
| T2 | H1 | Click toggles the pinned state; Escape sets the dismissed state; focus leaving clears both. |
| T3 | H1 | For every catalog route the panel resolves that module; `module-config/X` and `message-analysis/reports` resolve their host entries; the empty route resolves the glossary; `access-denied` resolves nothing; a `LocationChanged` re-resolves and closes the panel. |
| T4 | H1 | For every module, the panel renders its `Description` and every permission description exactly as the catalog holds them (derived text cannot drift); `aria-expanded` follows the state; `aria-controls` names the panel; the panel is a labelled region; sections keep their order and an empty section renders nothing. |
| T5 | H1 | Escape and the close control close the panel and request focus back on the button. |
| T6 | H2 | For every module with settings: each control has an id, its label's `for` matches, its `aria-describedby` names its description line (and its tip when present), required controls carry `aria-required`, the asterisk is hidden from screen readers, a switch's accessible name contains the setting's label. |
| T7 | H2 | A setting renders a marker exactly when its tip has a verified default, `Details` or an `Example`; the tip never repeats the visible description. |
| T8 | H1 | **Neutrality.** Every string in every descriptor (descriptions, labels, defaults, `SettingHelp`, `ModuleHelp`), `HelpConcepts` and `HostPageHelp`: an address or UPN, a lowercase domain name, a `DC=` sequence, a down-level `NAME\account`, or an IP literal uses only rule 6's reserved names. Passes on today's strings (`grep` at `d117c35e` finds only `example.com` and `contoso` names in the catalog). It names no deployment value. |
| T9 | H1 | **Declared help is well formed** - it fails on broken help, never on absent help: no blank text; each `{Setting:Key}` names a setting of the same module; each `QuotedCaptions` entry appears in that module's page source; each declared control tip is placed on its page; on every page, `HelpTip` ids are unique and every `aria-describedby` names an id present on that page. |
| T10 | H3 | Cloud Password Reset's panel names its three examples and its sections; its refusal line is the page's constant (the help cannot paraphrase it); the endings tip renders the switch's catalog label inside its details, and its example; the switch's tip renders its example and "Default: off". |
| T11 | H1 | The help components' CSS sets colours only through `--ui-*` variables and has no rule conditional on a theme (`UiThemeCssTests` rules, extended to these files). |
| T12 | H1 | `PageHelpMenu` sits inside `MainLayout.razor`'s `<Authorized>` block (the `UsageTrackerWiringTests` shape). |

**Deliberately not tested:** that any module, page or setting *has* hand-written help. Note 2 rules
it out; section 3.6's report is the only coverage view.

**Mutation convention** (`AGENTS.md`, "Prove new tests bite"): each new guard is shown to fail
against a deliberate break of the code it guards, then the break is reverted byte-exact (`git diff`
clean) and the full suite re-run. Recorded per slice, as `docs/CloudPasswordResetEmployeeId-Plan.md`
section 9 records its rows:

| Mutant | Break | Must fail |
|---|---|---|
| M1 | drop `aria-describedby` from the tip's button | T1 |
| M2 | make the Escape handler a no-op | T2 |
| M3 | resolve every route to no module | T3 |
| M4 | render a fixed string in place of `Description` | T4 |
| M5 | drop `for` from the settings label | T6 |
| M6 | show the marker on every setting | T7 |
| M7 | put an address on a non-reserved domain into one help string | T8 |
| M8 | reference a setting key that does not exist | T9 |
| M9 | paraphrase Cloud Password Reset's refusal line in its help | T10 |
| M10 | hard-code a colour in the tip's CSS | T11 |
| M11 | move the island outside `<Authorized>` | T12 |

Existing tests that must stay green and may need care: `ModuleCatalogTests` (permission
descriptions and the Access tab guard), `SettingsFieldTests` and the settings harness tests,
`MigrationExclusionWordingTests` (pinned descriptions), `ConfigBooleanTests`,
`UsageTrackerWiringTests`, `AppLayoutCssTests`, `UiThemeCssTests`, and `ClickGateTests` on any
converted page a module slice touches.

## 7. Cost

| Slice | Content | Files, roughly | Sessions |
|---|---|---|---|
| H1 | records, terms, host help, two components and their CSS, one layout line, optional script, guards, guide and spec | ~16, of them ~9 new | 1-2 |
| H2 | the three settings pages, defaults check, two README sections, three version bumps | ~9 | 1 |
| H3 | Cloud Password Reset | ~6 | 1 |
| 1-9 | notes and tips, one module each | ~4-6 each | 1 each |
| 10-22 | tips only, two or three modules a slice | ~3-4 each | ~5 in all |
| 23-28 | nothing | 0 | 0 |

Everything recommended: about 17-18 sessions; the useful minimum is H1-H3. By `.agents/token-log.md`
for recent small slices (roughly 200K-550K agent tokens and 20-75 minutes each), H1 sits at the top
of that range and a tips-only slice near the bottom. These are estimates, not measurements.

## 8. Risks, biggest first

1. **Help that drifts from behaviour.** Derived text cannot drift (T4 renders the catalog's own
   strings). Setting names render from the catalog; quoted sentences are the page's constants
   (T10); quoted captions are pinned against the page source (T9); the README points instead of
   copying (3.7). **Residual:** hand-written explanations of behaviour ("spaces are ignored") can go
   stale without failing anything. The guide's rule (change the help with the behaviour, same
   commit) and the module's help living in one file named by its descriptor are the only defences.
2. **Clutter.** Markers only where they add something (J5, T7); two-sentence tips (J6); the panel
   closed by default; no per-row markers (J7); no existing text removed (J10); the owner takes or
   leaves each module (5.2).
3. **Environment leakage in examples.** T8 catches addresses, domains, DNs, down-level names and IP
   literals. **Residual:** a deployment name that is none of those (a template name, a site
   abbreviation, a group's bare name) passes T8; slices write examples from rule 6 only, and the
   endings example is invented (4.1).
4. **Screen-reader regressions.** Double announcements (the tip omits the visible description, T7),
   wrong ties (T6, T9), a trapped focus (the panel is not modal, T5). **Residual:** real screen
   readers differ; a manual pass with Narrator or NVDA on the slice's pages is stated as run or not
   run.
5. **Collision with the approved sweep and click-gate tiers** on the same pages: serial order per
   page (5.5); one agent at a time (2026-10-26).
6. **Overlay and clipping.** The panel covers part of the page while open (it closes in one key); a
   tip inside a scrolling pane near its edge scrolls with the pane. Checked by eye per slice; the
   inline fallback is J2.
7. **Stale panel after navigation.** Re-resolved on `LocationChanged` and closed (T3), the
   `UsageTracker` pattern.
8. **Cost creep.** The list is take-or-leave and the work can stop after any slice.

## 9. Assumptions and facts not established

- **A1** (now a fact) The employee-ID plan shipped `FillMissingEmployeeId` as "Fill in a missing
  employee ID" and `OwnerNameEndings` as "Cloud admin account name endings" (`1464aa7f`). Labels
  render by key, so a rename follows.
- **A2** (now a fact) The endings parser trims each entry and drops empty ones
  (`Services/CloudPasswordResetService.cs:682`), which is why the tip may say spaces are ignored.
- **A3** Operators use browsers that support `:focus-visible` (current Edge, Chrome, Firefox,
  Safari). Not verified for this deployment.
- **A4** An interactive island in the static layout survives enhanced navigation and sees
  `LocationChanged`, as `UsageTracker`'s comment and wiring test rely on (`UsageTracker.razor:11-14`).
- **A5** The Usage view's opens per module on production are a fair proxy for "the modules
  operators use most". No usage data was read for this draft.
- **A6** Module pages serve L2 operators and settings pages serve administrators (2026-09-02).
- **A7** Bootstrap stays at 5.1, stylesheet only.
- **A8** `docs/VerbositySweep-Plan.md` 4.1's statement that `ProgressRegistry` is line-keyed was not
  borne out at `d117c35e` (no line field); that plan's implementer should re-check.
- **A9** The app is English only; no localisation is planned.
- **A10** No accessibility standard is mandated for this app; WCAG 2.1 AA is the target for the new
  controls only.
- Not established: whether each of the 16 declared defaults matches what its consumer uses when
  nothing is saved (J11 checks it in H2); how the panel reads on the narrowest screens (checked by
  eye in H1).

## 10. Verification per slice

- `dotnet build ExchangeAdminWeb.slnx -c Release`; `dotnet test ExchangeAdminWeb.slnx` (never bare
  `dotnet test`); `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`;
  `git diff --check HEAD`; `tools/Test-AsciiOnly.ps1`.
- When a slice touches a converted page: `dotnet test ExchangeAdminWeb.slnx --filter
  "FullyQualifiedName~ClickGate"` green before the full suite.
- Each new guard's mutant run and restored byte-exact (section 6).
- By hand, stated as run or not run (manual acceptance is production use, `.agents/decisions.md`
  2026-10-12): keyboard-only pass over the slice's pages; Narrator or NVDA on one tip and the panel;
  the ten themes by eye; a touch device if one is to hand.
- One commit per slice with its `.agents/token-log.md` line; no per-slice review (2026-10-26); the
  supervisor does not review its agents' code.

## 11. Follow-ups logged, not authorised

- **F1** Neutral wording for the Secret ID banner's template name (`ModuleConfig.razor:368`).
- **F2** Conference Rooms' site-specific setting labels and keys (`ModuleCatalog.cs:1130`, `:1135`
  at `1464aa7f`) are environment-shaped. A label change is text only; a key change would orphan
  stored values.
- **F3** Count Help-panel opens in usage telemetry, to see which pages need better help.
- **F4** Turn explanatory `title` tooltips (79 on 15 pages) into keyboard- and touch-reachable tips,
  page by page.
- **F5** After `docs/CancellationDepth-Plan.md` lands, a shared term for leaving a page while work
  runs, quoting its prompt's three cases by reference.
- **F6** A scripted coverage report, if the search command is not enough.
- Side findings, outside this plan: Service Health renders `bi` icon classes
  (`ServiceHealth.razor:30`, `:161`, `:504-511`) for which no icon font or CSS is shipped, so those
  icons appear to render as nothing (read from source, not seen in a browser).
  `Components/Shared/TicketNumberInput.razor` is used by no page.

## 12. Records on approval, and status log

On approval, in the approving commit:

- `.agents/decisions.md`: the help rules - derived from the descriptor; hand-written help optional
  and declared where warranted; no coverage tripwire, a report at most; the help owns operator usage
  and the README points; the version rule of 5.3.
- `.agents/state.md`: a pointer to this plan under Next, in the owner's order.
- This header's Status line, and which numbered modules the owner took.

Status log:

- 2026-10-28 - Drafted at `d117c35e`. Owner notes 1 and 2 (section 1.1) applied: note 2 governs.
  The employee-ID plan merged mid-draft (`683a8ce7`); section 4 and the setting counts were re-read
  at `1464aa7f` and now quote the shipped labels and descriptions. Not approved; nothing is
  authorised.
