# Agent Decisions

Durable repo decisions. Not a chat log. Each entry should make sense without
conversation history and should name superseded guidance when relevant.

## Decisions

### 2026-09-22 - No tooling enumerates the directory, and Graph is reached through GraphConnect

Status: Active. Scope: every script and module in this repo that touches Graph or AD, not just
the survey that prompted it.

**Do not enumerate the directory.** Owner, verbatim: *"you cannot enumerate all users. that is a
data exfill security flag. I told you in-scope users have been updated."* A `Get-MgUser -All` (or
any equivalent full listing) to find a known set of accounts is an exfiltration pattern whatever
the intent, and it is flagged as one. **A population is an INPUT, supplied by the operator, not
something tooling discovers.** Read each named account with its own `-UserId` call.

This also matches the app. `CloudPasswordReset` resolves one named target per operation and never
lists the tenant, so a survey that enumerates is not even measuring what the module does.

**Graph is connected through `GraphConnect` from the M365Connections module, never a direct
`Connect-MgGraph`.** Owner, verbatim: *"I don't log in to graph like this. I use the connection
module's GraphConnect."* It authenticates the existing app registration through its Delinea
credential helper and is **app-only**, so a script that asks for delegated `-Scopes` is both wrong
and prompting the operator for a sign-in this tenant does not use. AD comes from the same module's
`ADImport`. `Connect-AllM365Services` is not used for single-service work - it also runs module
updates and opens other connections. The module path is machine-specific and lives in
`.agents/machines.md` as `m365-connections-module:`, never in a script.

**Where both rules came from is worth keeping:** the deleted survey tooling at `80419d7` did it the
wrong way on both counts, and its 2026-09-22 replacement inherited both faults by being modelled on
it. Recovering deleted tooling as a pattern carries its defects forward.

**That replacement is itself now deleted, and the reason extends this rule.** After the
enumeration was removed from its Graph side, it still swept every account against every domain in
the forest - the same bulk pattern, corrected in one place and left in the other. Owner: *"'each'
'accross the forest' implies data exfil."* **So the rule is not "do not call `-All`"; it is that a
directory operation is scoped to one named subject, at the moment something is being done to that
subject.** A loop over a population is a census however each individual query is written, and
measuring a property across a population is therefore not something this repo's tooling does. The
property gets enforced at the point of use instead, fail-closed, one subject at a time.

### 2026-09-22 - CloudPasswordReset is OFF HOLD, and employeeId matching is back on the table

Status: Active. Scope: `CloudPasswordReset`. **Supersedes the 2026-09-14 hold** in full, and
reopens what the 2026-09-11 ruling closed on the evidence available then.

Owner, verbatim: *"yes, that plan is off hold."* Said in answer to a message naming exactly what
the hold was blocking - querying Graph or AD to measure the employeeId match rate, and revising
`docs/CloudPasswordReset-Plan.md` - so **both are authorized**, not merely unprohibited.

**What changed underneath the old ruling.** Queue item 10 asks the module to match on employeeId
and derive the destination mailbox from it. On 2026-09-11 that was measured impossible:
employeeId was populated on **0 of 172** cloud accounts, and stamping them was refused, which is
what produced *"if we cannot get a 100% working match, then matching is off the table"* and the
operator-types-the-destination design. The owner has since stamped them. Verbatim, 2026-09-22:
*"yes, already updated the accounts in-scope."* **The blocker was the data, and the owner changed
the data** - this is not a reinterpretation of the old survey.

**What is now authorized:** a read-only coverage survey against the stamped accounts, and the plan
revision that follows from it.

**What is NOT yet authorized, and must not be inferred from the lift:** implementation. The plan's
current design is built around the operator typing the destination, which only exists because
matching had failed. Matching returning changes that design, so the plan is revised and re-approved
before any C# is written.

**Two things the revision must carry rather than quietly drop.**
1. **The 100% bar is the owner's own, and "already updated" is a reported action, not a measured
   match rate.** The survey measures it; a partial result is a fresh decision for the owner, not
   something to route around with a fallback.
2. **D4 comes back.** The separate `CloudPasswordResetReveal` permission was justified by the
   operator being unable to obtain the password, then undermined when the operator began typing the
   destination. A derived destination restores that justification, and the plan already requires
   D4 be re-put before S5. It was never withdrawn on its merits.

**The security property this restores, stated because it is the reason the work is worth doing:**
under the typed-destination design an operator can address another user's password to themselves,
and only the audit record and the admin alert catch it afterwards. A derived destination prevents
it instead of detecting it.

### 2026-09-22 - Queue 4: an operator without trace search does not see the Trace Search tab

Status: Active. Settled in conversation on 2026-09-22. Answers open question 4 of
`docs/MessageTracePermissionSplit-Plan.md` and **overrules that plan's recommendation**, which
argued for a disabled tab naming the missing permission.

**HIDE the tab.** Verbatim: *"hide"*. This matches what the app already does for authorization
rather than for busy state: `Components/Pages/Home.razor:53-65` wraps each module card in an
`AuthorizeView` on the module's policy alias, so a person who lacks a permission does not see the
capability at all. The plan's contrary argument rested on `MessageTrace.razor:397-399`, a comment
about disabled *buttons* stating their reason - that rule is about a control refusing a click
silently, which is a different situation from a capability the operator was never granted.

**A consequence the plan did not have to handle while the tab was merely disabled, and which the
enforcement slice now must:** `MessageTrace.razor:878` sets `activeTab = "trace"` programmatically
from the header-analysis handoff, and runs the trace outright when that path is taken with
`runNow`. Hiding the tab button does not close that route. The handoff control must be hidden for
an operator without the granular permission, and `RunTrace` must refuse on the server regardless -
hiding is presentation and never the gate.

### 2026-09-21 - Queue 6: the owner OVERRULED the do-not-containerize recommendation

Status: Active. Supersedes the verdict in `docs/Containerization-Feasibility.md`, which stands as
the record of the analysis but no longer as the recommendation.

Verbatim: *"we need to containerize the app and it needs to work. fix."* The recommendation against
it was put to the owner first, with its blocker named, and they reaffirmed. **That is their call and
it is now the requirement.** Do not re-litigate it; the feasibility document's job from here is to
supply the constraints, not the conclusion.

**What the analysis established that still binds, regardless of the verdict:**

1. **The Delinea bootstrap credential is the blocker, and it is a code change.** The app's own API
   username and key for Delinea live in **Windows Credential Manager**, a per-user store
   (`Services/CredentialManagerService.cs` uses the WinRT `PasswordVault`;
   `Services/DelineaService.LoadCredentials` reads it at construction and fails closed when empty).
   A container has no user profile to read it from, and baking it into an image would ship a
   credential, which the Constitution's credential-isolation rule forbids. **The fix is a provider
   seam**: read the bootstrap credential from an environment variable or a mounted secret file,
   keeping Credential Manager as the fallback so the existing IIS deployment is untouched. This is
   also why the csproj carries an OS-versioned TFM and why both deploy scripts set
   `loadUserProfile`.
2. **Windows containers, not Linux** - `System.DirectoryServices` and the hosted Exchange Online
   PowerShell module force it, independently of the credential change.
3. **Windows Authentication needs a gMSA and a credential spec**, and containerised means Kestrel
   rather than IIS, which means **Negotiate and therefore SPNs registered in the forest**.
   `deploy.ps1:381` currently removes the Negotiate provider specifically to force NTLM and avoid
   SPN registration; Kestrel cannot reproduce that, so the SPN work the present deployment was
   designed to avoid becomes mandatory. **This needs the owner's AD team and is not ours to do.**
4. **The shared SQLite config database guard would falsely PASS.** `ConfigStorePath.cs:38-43`
   refuses a UNC path because SQLite locking is unreliable over SMB - but SMB global mapping
   presents the share as a drive letter, so the guard accepts it while the hazard is unchanged.
5. **`config/exchangeadmin-jobs.db`, `-usage.db` and `logs/` land in container scratch space**,
   which is discarded on stop.

**Still unanswered and it changes the design:** where "elsewhere" is. A container in the same forest
with a gMSA can do the whole job; one with no line of sight to on-prem AD and Exchange cannot do the
AD, on-prem Exchange or DHCP work at all - that is a property of the network, not a design choice.
The plan proceeds on **same forest, gMSA-joined** as the default because it is the only shape in
which the app's features work, and says so explicitly rather than assuming it silently.

### 2026-09-21 - Owner answers on queue items 4, 5 and 8

Status: Active. Settled in conversation on 2026-09-21, in the owner's own words where quoted.

**Queue 5, other tenants/domains - ONE other tenant, credentials in Delinea.** Verbatim: *"there's
one other tenant. creds for that tenant will live in delinea."* So the zero-work reading is
falsified: this is the separate-tenant case, not extra accepted domains on the existing tenant.
`docs/MessageTraceMultiTenant-Plan.md` is the live plan.
**A consequence the owner should be told if it turns out wrong:** the existing tenant does NOT
authenticate through Delinea - it uses a certificate from the host's `LocalMachine\My` store plus
AppId/Organization from module config, and `MessageTrace`'s existing `DelineaSecretId` is the
*on-prem* credential only. Tenant 2 is therefore being designed around a **client secret held in
Delinea**, which is the natural fit for Delinea and which Exchange Online accepts. That reading was
stated to the owner as an assumption rather than asked as a question, because it is the obvious one;
if they meant a certificate, the design changes and the plan must be revised.

**Queue 8, Defender for Endpoint - display name is "Defender for Endpoint Devices"** (open question
6). Not cosmetic: it sets the nav label, the route `defender-endpoint-devices` and the
section-access alias `DefenderEndpointDevices`. Shipped in S2.

**Queue 8 - discovery sources ARE wanted** (open question 1). Verbatim: *"that was the literal
request from my boss's boss."* The cost was put to the owner plainly first - `ThreatHunting.Read.All`
is tenant-wide, reads every advanced-hunting table including email, identity and cloud app, and
needs a Privileged Role Administrator or Global Administrator to consent rather than the
Application Administrator the device permission needs - and the owner reaffirmed. **So slice S4 is
no longer droppable**, the `IncludeDiscoverySources` config field ships in S2 rather than being
deferred as dead config, and the app registration needs BOTH permissions.

**Queue 4, message trace vs header analysis - RE-GRANT DELIBERATELY, do not copy the existing
group across** (open question 1 of that plan). The owner chose the safer option with its cost
stated: **nobody can trace until the owner adds them to the new `MessageTraceSearch` group, and
that is an outage from the moment the enforcement slice deploys.** The plan's three-commit shape
with a mandatory deploy boundary is therefore load-bearing, not ceremony - slice 1 declares the
alias inert, the owner configures the group, and only then does the enforcement slice land.
Recovery from a mistimed deploy needs a **global** admin, not a module admin, because the
section-access save handler re-checks `AdminSettings` specifically.

### 2026-09-18 - Weekend backlog run: plans self-approve after codex consensus, and pushes are standing

Status: Active for the 2026-09-18 weekend backlog run only. Scopes down to the standing
rules below when the run ends. Supersedes, for that window, the AGENTS.md Prime Invariant
"Code changes require an approved plan" as an OWNER gate, and the `.agents/push-policy.md`
ask-before-push rule.

The owner set the goal "work through as much of the `queue.txt` backlog as you can over the
weekend... only stop if you cannot proceed without me", and answered three gates:

1. **Plan authority.** Verbatim: "plan, review with codex (default), revise, review loop
   until consensus, then implement." The owner's approval step is replaced by a codex
   review loop, not removed: a plan is implementable only once codex and the coder reach
   consensus on it, and each revision round is re-reviewed. Codex is the default harness;
   the standard pair in `.agents/review/harnesses.local.json` applies. This is a PLAN
   (approach-soundness) review, closest in shape to `.agents/playbooks/openreview.md`,
   not the landed-diff defect hunt in `codereview.md`. A plan that cannot reach consensus
   is a stop-and-ask, which is the one case the owner said to interrupt them for.
2. **Priority.** Verbatim: "I don't give a fuck" - ordering across queue items is the
   working agent's call, not an owner gate. Do not ask again this run.
3. **Push.** Both remotes, as each slice lands. Standing for this run only; the repo policy
   file is unchanged and reverts to ask afterwards.

Also settled by the owner's instruction "use agents for coding to keep your context from
blowing up": implementation subagents ARE authorized for this run. This overrules, for its
duration, the `.agents/repo-guidance.md` Token Budget rule "never orchestrate an
implementation subagent from a supervising session" and the one-slice-one-session rule. It
also resolves, for this run only, the standing "Unsettled guidance conflicts" blocker in
`.agents/state.md` between the 2026-09-01 ruling and the 2026-08-27 decision - the owner has
now twice chosen the coding-subagent shape. The underlying conflict is NOT settled for
normal operation; it still needs owner reconciliation.

Unchanged and still binding: the Constitution, verification before completion, guard proof
per slice, one finding per commit, and the rule that self-review is forbidden.

### 2026-09-18 - The app-wide click-gating sweep is approved at full depth for all nine tier-1 pages

Status: Active. Amends the 2026-09-17 rule below on two points; see "What the
reconnaissance changed". Plan and evidence: `docs/ClickGatingAudit-Plan.md`, Revision 1.

The owner was shown three re-scope options after reconnaissance roughly tripled the
estimate, and chose the largest: **all nine tier-1 pages, everything closed**, at 20-29
sessions plus 2-3 dev-deploy acceptance passes. Tiers 2, 3 and 4 remain unapproved.

Approved scope, in the revised page order (easiest to hardest, by measured structure, not
by write count): DhcpAuthorization, NamedLocations, MailboxPermissions, CalendarPermissions,
IntuneDevices, GroupManagement, M365GroupManagement, ConferenceRooms, SelfServiceGroups.
Plus slice 0 (scanner, shared reader, registry, eleven assertions), slice 1 (the two
prerequisite fixes), the `ADIdentityAutocomplete` / `RecipientAutocomplete` shared-component
contract change, and the snapshot-at-entry obligations on six pages.

**What the reconnaissance changed**, all verified against source on 2026-09-18:

1. **"One page-level predicate" is wrong on two pages.** `SelfServiceGroups` has two
   mutually exclusive views with disjoint flag sets - a single predicate either traps the
   operator in the manage view or leaves the browse view ungated, and it was the only page
   whose adversarial verdict came back "unsound". `ConferenceRooms` has a Bulk Jobs panel
   driven by a background `OnJobChanged` callback that the form predicate has no authority
   over. The registry therefore carries a **list** of scoped predicates. This qualifies
   point 1 of the 2026-09-17 entry; the default is still one per page.
2. **"Clear it in a `finally`" does not close the risk it was written for.** There is no
   `ErrorBoundary` anywhere in the app (zero matches across `Components/`, `Services/`,
   `Program.cs`), so an exception escaping a handler tears the circuit down rather than
   leaving a live page with a stuck flag. A `finally` lowers the flag and the circuit still
   dies. The remedy on a throw path is a **catch that converts the throw into a visible
   failure result**; the `finally` remains correct for non-throw paths. The genuine
   page-deadeners are a call with no timeout or cancellation token, and a normal return that
   skips a lowering nested inside an `if`. This qualifies point 4 of the 2026-09-17 entry.
3. **A handler guard is the wrong mechanism for any DOM-synced control** - a checkbox,
   radio, `@bind` select or `InputFile`. The handler refuses, the backing field is
   unchanged, the render diff emits no correction, and the browser keeps the operator's
   action while the server never took it. Three refusal mechanisms are required: the
   disabled attribute (the only safe one here), a child-component `Disabled=` parameter, and
   the handler guard, which is safe only where no rendered attribute mirrors server state.
4. **Gating narrows but cannot close a post-await read of a live form field.** On six of
   eleven pages this is the sharpest defect. Only a snapshot at handler entry closes it.

**Unsettled, recorded as an assumption rather than a fact.** Whether Blazor Server can
dispatch a second event callback while the first handler is suspended at an `await`
decides whether markup-only gating is ever sufficient. Reconnaissance split on it and
neither side could cite file evidence, because it is a framework question. The work
proceeds assuming it CAN, and stays robust either way by using the disabled attribute plus
an in-handler entry guard. Confirm empirically on a dev deploy before relying on either
answer.

### 2026-09-17 - A control is clickable only when its click will definitively execute

> **Amended 2026-09-18** (entry above): points 1 and 4 below are qualified by
> reconnaissance findings. The rule itself stands unchanged.

Status: Active general rule. Implemented on `Components/Pages/Migration.razor` only
(`docs/MigrationButtonGating-Plan.md`, module 1.9.0). The app-wide sweep is an
unscoped follow-up, not part of that work.

Owner ruling, verbatim:

> so it should not be possible to click any buttons until the system is ready for that
> button click to be definitively executed.

The rule. A control must be disabled whenever an asynchronous operation is running whose
result could invalidate that click, or be invalidated by it. It is not enough for a
handler to guard against re-entering itself. A Blazor circuit stays interactive across
every `await`: the page keeps rendering and keeps accepting clicks while a handler is
suspended, so any control left live during a long operation can be clicked, accepted, and
then silently discarded or applied to state that has since been replaced underneath it.
Silently dropping an accepted click is the failure being outlawed, not just the crash.

What follows from it, and is the shape to copy:

1. **One page-level predicate, not per-control guards.** A single `IsBusy` reads every
   in-flight flag on the page; every control consults it. A per-control guard only knows
   about its own operation, which is exactly the blind spot.
2. **Staged state is not in-flight state.** "Waiting for the operator to type a ticket"
   must stay out of the predicate. Folding it in disables the Confirm button at the only
   moment it is ever rendered, and no destructive action can be executed again. This was
   caught by review of the plan, before code; all three originally proposed tests would
   have passed the broken version.
3. **Set the flag before the first `await`, not after an authorization round-trip.** Until
   the flag is set the predicate reads false and the gate is open, so the click can be
   repeated for the whole duration of that first await.
4. **Clear it in a `finally`.** A stuck flag under a page-wide gate deadens every control
   on the page for the life of the circuit, so a leak is far more damaging here than under
   a per-control guard.
5. **`<a>` ignores the `disabled` attribute.** Tab strips and link-styled controls need the
   refusal in the handler; the `disabled` class is styling only.
6. **Name the exemptions and say why.** Controls that touch no in-flight state stay live -
   dismissing a banner, closing a panel, serving a constant, and cancelling a staged action
   the operator must always be able to back out of. Everything else is gated, and a test
   asserts the ungated set equals exactly that named list, so a new ungated button fails.

### 2026-09-14 - Runtime owner investigation: employee CLD scope and L2-only workflow

Status: Active requirements and investigation authority. Module implementation remains
on hold pending an approved revised plan. This later instruction reopens runtime owner
analysis; it does not revive the deleted survey code or approve implementation.

The owner requires the changed password to be emailed only to the account owner, with
the association determined at runtime. Only L2 uses this app. The ServiceNow request
does not reach the app, and the employee neither visits the app nor completes an
authentication/approval workflow through it. Advance user enrollment is ruled out.
There is no existing cloud-admin-to-AD-user mapping. A maintained owner map and
employeeId backfilling remain rejected approaches.

When the mixed privileged-account population was identified, the owner selected
**"Individually owned employee CLD accounts"**, rather than every account in the old
target list. This is a scope definition, not permission to classify every unmatched
identity as external or to exclude employee accounts merely because a resolver fails.

The owner directed completion of the actual-directory investigation and explicitly
instructed use of `D:\source\scripts\Modules\M365Connections.psm1` through PTK to
establish Graph access. The connection receipt belongs in `.agents/machines.md`.
Current findings and proposed rules belong in
`.agents/research/cloud-password-owner-runtime.md`; they are not an approved plan or
an independent attestation of every candidate's ownership. No directory writes,
password resets, permission grants, deployment, or implementation were authorized in
this investigation. D4 was not decided.

### 2026-09-14 - CloudPasswordReset is on hold

Status: Active. Scope: `CloudPasswordReset`. Does not supersede the entry below -- that design
record stands and is what a resumption would start from.

Owner ruling, verbatim: *"stop this module's development and put it on hold."* It came
immediately after D4 was put with a recommendation and answered *"no. neither."* **No reason for
the hold was given, and none is recorded here, because inventing one would put a fabricated
rationale into the durable record that a later reader would take as the owner's.**

**Nothing is half-built.** The stream produced a plan and, briefly, PowerShell survey tooling
that is deleted (`a56f41f`). There is no descriptor in `Modules/ModuleCatalog.cs`, no service,
page, permission, config field or version bump. The hold leaves no partial state to unwind and
no dead code in the app.

**What is prohibited until a new owner go:** any implementation slice, any further revision of
`docs/CloudPasswordReset-Plan.md` beyond recording this hold, and any Graph or AD query in
service of the module. The earlier single-run survey approval was consumed on 2026-09-11 and
does not carry.

**D4 lapsed, it did not settle.** The question -- whether `CloudPasswordResetReveal` still
fences anything once the operator types the destination and can therefore address any password
to themselves -- received neither (a) nor (b). It must be put again before S5 if the module
resumes. The recommendation recorded in the plan is a recommendation, not an approval, and the
plan says so at the decision.

**One item outlives the hold and is still open with the owner:** disposition of the survey
CSVs. The original record said three files; the current filename-only inventory differs and is
owned by `.agents/machines.md` (Cloud survey export inventory, 2026-09-14). Survey exports
name every in-scope cloud-only account, its UPN, display name and object id, plus the owner
sAMAccountNames, emails and DNs the derivation matched. They were deliberately not shredded
earlier because, unlike the tracked tooling, they are unrecoverable and only implicit
confirmation had been given. With the module stopped they have no remaining purpose.

### 2026-09-11 - Owner derivation is abandoned; the operator names the destination

Status: Active. Scope: `CloudPasswordReset`. **Supersedes** the "Searched domains are an
operator setting" entry below (its subject no longer exists), the derived-destination rule in
`docs/CloudPasswordReset-Plan.md`, plan decision D3, and the S0 survey gate.

The plan derived the on-premises owner of a cloud-only account at each reset and mailed the
password there, so the operator never learned it. The S0 survey measured how often that works:
**80 of 172 in-scope accounts, 46.5%**. The misses are not wrong matches -- all 84 returned zero
directory rows -- and they split along naming eras (`Last, First` resolves 86%, `First Last`
18%, single-token 0%). Two mechanical fixes were identified (underscore-to-dot UPN separator,
flipped display-name order) with a ceiling near 84%.

Owner ruling, verbatim: *"the way this works now is L1 gets the call, L2 gets the escalation,
and L2 reaches out directly to L3 to do the change. that's slow and bad process. this tool
needs to make it fast and correct. everything is logged, users are notified, and security in
the app controls access. we are overcomplicating this. if we cannot get a 100% working match,
then matching is off the table."*

`employeeId` was dropped as a source in the same ruling. It is the strongest available
identifier and is populated on the on-premises side, but on **0 of 172** cloud accounts, and
the remedy -- stamping it onto several hundred CLD accounts -- was already refused.

**What replaces it.** The operator types the destination address on the reset form. The
password is generated, PATCHed, and mailed there; the operator does not see it on the ordinary
path. The destination address is a required field in the audit event and in the administrator
alert email, so a wrong or self-directed address is visible after the fact rather than
invisible. Owner, same day: *"destination email needs to be in the logs and in the admin alert
email."*

**The security property that was traded away, stated plainly so no later reader mistakes it
for an oversight.** The old design's claim was that resetting even a Global Administrator gave
the operator nothing, because the password went somewhere they did not control. That claim is
gone: an operator who can type the destination can type their own address. What now bounds the
risk is section access (who holds the permission at all), the audit record, and the
administrator notification -- detection and accountability, not prevention. The owner made this
trade knowingly and on the record; the process it replaces (L2 phoning L3) has neither
property.

Consequence left open in the plan as D4: the separate `CloudPasswordResetReveal` permission was
justified by the operator being unable to obtain the password otherwise. It no longer is.

### 2026-09-11 - Searched domains are an operator setting, not a hard-coded scope

Status: Active. Scope: `CloudPasswordReset` owner lookup; the reasoning is app-wide. Supersedes
nothing; refines the environment-neutrality entry below by settling what replaces the rejected
assumption.

Sequence, recorded because two proposals were rejected and the reasons differ. (1) The owner
lookup binds only the app host's own domain, so a `sAMAccountName` collision elsewhere is
invisible. (2) I proposed closing that on ADI's topology; rejected as a hard-coded environment
assumption. (3) I proposed searching the entire forest; **also rejected**, and correctly: the
estate has several domains and trusts, most with no relationship to Entra, so a forest-wide
sweep queries irrelevant directories and manufactures collisions that are not real ones.

Owner ruling, verbatim: *"it does not need to search all domains. we have several domains,
trusts, etc. search the domain checked. only check domains that sync to Azure or that you want
to check. stop wrapping the admins in bubble-wrap. you have not sold me on the notion that the
admin is assumed to be too stupid or short-sighted to read an in-app note about how to choose
the right domains and make the fucking decision."*

The design: module config gains **Search Domains**, a checkbox list enumerated at runtime from
the forest and its trusts -- no domain name hard-coded, defaulted, or typed by hand, which
satisfies environment neutrality without inventing scope. The lookup queries exactly the checked
domains. Two or more matches across that set is `Ambiguous` and refuses. An in-app note beside
the setting says how to choose: the domains whose accounts sync to Entra, or that you otherwise
want searched. Nothing checked, or the setting unreadable, refuses -- the app's standing
fail-closed rule for unavailable authorization data, not a guard on the operator's judgement.

The general principle, and the reason this entry exists rather than a plan edit alone: **an
operator setting with a clear explanation is the correct answer where I was reaching for an
automatic one.** This module's whole purpose is to give L2 the admin console's capabilities
inside an audited, credential-free interface. Withholding a control because an administrator
might choose badly is the opposite of that. Withhold credentials, enforce audit, fail closed on
missing data -- never withhold the decision.

### 2026-09-11 - No ADI-specific assumption anywhere, including in reasoning

Status: Active; operative rule is in `.agents/repo-guidance.md` (Architectural Invariants, item 7).
Settled rationale and supersession history are archived verbatim in
`.agents/history/decision-archive.md` (2026-09-14 sweep).

### 2026-09-11 - Cloud Password Reset: owner go, and S0 is the gate

Status: Active. Scope: `docs/CloudPasswordReset-Plan.md`. Owner said *"go"* on the plan on
2026-09-11. The plan's Status header moves from Draft to In progress.

The go is a go on the plan **as the plan is written**, and the plan makes S0 a hard gate on
everything after it. So the go authorizes S0 and nothing beyond it: no C# implementation, no
module registration, no version bump. S0 is a read-only survey that measures what fraction of
the several hundred cloud-only accounts can have an on-premises owner derived reliably enough
to mail a password to. **No slice after S0 starts until the owner has seen that number and
said go again.** A low rate does not shrink the design, it replaces it, which is precisely why
the number comes before the code.

What landed under this go:

- `tools/CloudAccountOwnerDerivation.psm1` - the three pure derivation functions (candidate
  keys, name corroboration, fail-closed outcome collapse). No AD, Graph, network or
  filesystem access. It is the model for `Services/CloudAccountOwnerResolver.cs` in S1; where
  the two ever differ, the C# is authoritative and the module is corrected, because a
  divergence makes the survey's numbers a lie.
- `tools/Get-CloudAccountOwnerCoverage.ps1` - the survey. `-PlanOnly`-shaped (Architectural
  Invariant 4), reads Graph and AD, writes one CSV, never calls PATCH.
- `tests/ps/CloudAccountOwnerDerivation.Tests.ps1` - 40 tests, no directory required.

Two judgement calls made inside the plan's scope rather than referred up, both recorded in the
plan's S0 section:

1. **`UnresolvedOwnerDisabled` is measured as its own outcome class.** The plan names `Enabled`
   as needed for the leaver rule but never states the rule's shape. Measuring the class
   separately lets the owner rule on it with a number in hand instead of having the shape
   guessed for them.
2. **A `ForestMatchCount` column that the module itself cannot produce**, because of the
   finding below.

**Finding raised, and NOT closed: the module's user lookup binds the local domain only.**
`ADDirectorySearchService.ValidateExists` issues its USER query with no `-Server`. The `-Server`
DN routing at `ADDirectorySearchService.cs:355` is scoped to `objectKind == "Group"`, and
`ResolveGlobalCatalog` is called only from the `Search` path at `:660`. A `sAMAccountName`
collision in another domain of the same forest is therefore invisible and can never be reported
as `Ambiguous`, leaving name corroboration as the only thing between a collision and a password
mailed to the wrong person.

I attempted to close this on ADI's forest topology (owner-supplied: NT4-era forest-root/user-
domain pattern, `winroot.analog.com` holding schema and some admin accounts and no mailboxes,
`ad.analog.com` holding all users and mailboxes, app host `ASHBIAMWEB1` joined to
`ad.analog.com`). **The owner rejected the closure** -- see the environment-neutrality entry
above, dated the same day. The topology is recorded there as background only; it is explicitly
NOT load-bearing and no rule may rest on it.

A forest-wide replacement was then proposed and also rejected. The settled design is the
operator-chosen **Search Domains** set - see the entry of that name above, which is where this
finding actually closes. Full statement in `docs/CloudPasswordReset-Plan.md`, S0 findings.
Consequence for S0 itself: the survey takes the domain set as a parameter, because a survey that
reproduces the local-domain behaviour measures the hit rate of code that will not ship.

### 2026-09-10 - Audit events go to Splunk, so their fields are an interface

Status: Active. Scope: stated for `CloudPasswordReset`, but the fact is app-wide. Owner,
verbatim: *"these logs are going to splunk, so they need to be explicit and clear."*

Audit events are forwarded to a SIEM. That makes field names and value types a published
interface: somebody builds a dashboard or an alert on them, and a rename or a re-typed value
breaks it silently. Six rules, written into `docs/CloudPasswordReset-Plan.md` (Audit fields,
and Splunk) and binding on that module:

1. One fact per field - no packed `key=value; key=value` strings. The precedent NOT to copy is
   `wipeFlags` in `Components/Pages/IntuneDevices.razor:1414`, which crams five settings into
   one sentence and forces a field extraction on every query.
2. Booleans are JSON booleans, never `"Yes"` / `"true"` / `"(set)"`.
3. Enumerated fields draw from a closed list stated in the plan; never free text, never an
   operator- or upstream-supplied string.
4. Every field appears on every event of its action, with an explicit null where it does not
   apply. Absent and null read differently to a search.
5. Names are frozen once shipped; renaming one is a breaking change to someone's dashboard.
6. No secret, and nothing derived from one - for a password that means no length, no entropy,
   no word count, no hash.

`AuditService` already emits one JSON object per event (`AuditService.cs:392-411`) and
`MergeExtra` (`:38-45`) passes nulls through, so no transport change is needed.

Existing modules were not written against these rules and some pack strings. Bringing them
into line is a separate stream with its own plan; this entry does not authorize a sweep.

### 2026-09-10 - Cloud Password Reset: change-at-next-sign-in is an operator option, and the destination address is shown

Status: Active. Two owner corrections to the plan's exec summary, same day.

**`forceChangePasswordNextSignIn` is an operator checkbox, defaulting to unchecked.** Two
corrections in sequence, both from the owner the same day. The draft hard-coded it `true`:
*"NO. that disallows signin in too many instances."* I then hard-coded it `false`, which was
also wrong: *"no. 2 should be an option just like it is in the MS portal. the point to this
whole app is to provide L2 with access to a subset of admin tools in an audited and secure
interface without giving them actual elevated credentials. change on login is an OPTION."*

The general rule that decides this class of question: **this app exists to give L2 the admin
console's capabilities inside an audited, credential-free interface.** Where the portal offers
an admin a choice, removing that choice makes the app a worse tool than the thing it replaces.
Withhold credentials and enforce audit - do not withhold controls.

Default unchecked, because the owner's first objection is a real failure mode: many accounts
in this population sign in through paths that cannot service a change-password interrupt, and
a reset that silently returns an unusable account is the worse of the two errors. The operator
ticks the box when they know the account can take it.

Three consequences are requirements: the choice is recorded in the success audit's `extra`;
the owner email's closing line follows the choice and never promises a prompt that will not
appear; and because unchecked is the default, the generated password is usually the account's
durable password - which is what makes the generator's 18-32 characters and 60-bit floor
load-bearing rather than a nicety.

**The resolved destination address is displayed, read-only.** Owner challenge: *"why name
only? is that a precaution of some kind? the tech knows who opened the fucking ticket. you
need to justify this."* It could not be justified and is reversed. The address is not a
secret, the tech generally knows the requester, and the same mailbox is visible in the app's
own AD search. Hiding it removed the only human check on the module's real residual risk - a
derivation resolving to the wrong person - which a tech reading the address catches at a
glance. The control that matters is untouched and is a different one: the address is
**derived, read-only, never editable, never chosen from a list**. Seeing the destination is
not selecting it.

Detail in `docs/CloudPasswordReset-Plan.md` (Graph surface consequence 3; owner-resolution
outcomes; AC4, AC17). No code is authorized.

### 2026-09-10 - Cloud Password Reset: the app generates the password, using pwgen's algorithm as the model

Status: Active. Owner ruling, verbatim: *"d1 app chooses. there's a password generator in
D:\source\pwgen that will serve as the model. use the algorithm it's using, not the code."*

**The app generates the password.** No operator-supplied password is accepted at any layer of
the module - there is no field for one in the page, the service signature, or the request
model. This closes plan decision D1.

**pwgen is the model, not a dependency.** `D:\source\pwgen` is a Rust CLI. The module
reimplements its *method* in C#: a diceware-style passphrase of 2-6 words drawn from a 7,771
word list, balanced capitalisation with no adjacent duplicate styles, per-gap separators from
`!@#$%&*?+=`, digit and symbol padding distributed across every slot rather than appended,
18-32 characters, and an entropy floor of 60 bits computed against the *effective* pool
(geometric mean of same-length candidates). 100 failed attempts refuse rather than emit a
weaker password. Its Rust source is not ported and its binary is not invoked - the app must
not shell out to anything to make a password.

**Two adaptations are mandatory in the C# version.** All randomness comes from
`RandomNumberGenerator`; `System.Random` is barred, enforced by a source-text test. The word
list is an embedded resource with recorded provenance and licence, not a host-editable file,
so a deployment cannot silently shrink the pool.

Detail in `docs/CloudPasswordReset-Plan.md` ("The generated password", S2, AC13-AC16). No code
is authorized; the plan is still Draft and S0 still gates it.

### 2026-09-10 - Cloud Password Reset: a failed send fails closed, and downstream delivery is out of scope

Status: Active. Two owner rulings in one sentence, given in response to codex finding cpr-2
over `c493b2a..7c47c3c`, verbatim: *"if the password gets sent but the email fails.. how will
the app know the email fails if it fails downstream? it won't. that's outside the scope. if
the send itself fails, then fail closed."*

**Fail closed on a failed send.** The draft plan displayed the password to the operator when
the Graph PATCH succeeded but the SMTP send then failed - on any tier, bypassing the
`CloudPasswordResetReveal` gate. That is removed. The password is discarded, the page reports
"changed but not delivered", and the event audits as `CloudPasswordReset_DeliveryFailed`. The
account is briefly in a state nobody knows the password for; that is recoverable by running
the reset again, and it needs no escape hatch on the page. Displaying it instead would hand
every operator a way to see a password by provoking a send failure, which inverts the whole
model for a condition that fixes itself on retry.

**Downstream delivery is out of scope.** The app knows only whether the SMTP handoff
succeeded. A message accepted and then bounced, junked, or refused by a full mailbox is
invisible to it, and this module does not attempt to track that. "Delivered" means "accepted
by the mail server", wherever it appears in `docs/CloudPasswordReset-Plan.md`.

Related: codex finding cpr-1 (an operator who is the derived owner of the target) was
**declined** the same day - `.agents/review/cpr-1.contested.md`. No escalation exists when
the operator already holds the account.

Detail in `docs/CloudPasswordReset-Plan.md` (Delivery; Review log). No code is authorized;
the plan is still Draft and S0 still gates it.

### 2026-09-09 - Service Health: no gradients

Owner ruling on the deployed 1.3.0 board, in full: "no gradients."

The header band's `linear-gradient(135deg, var(--ui-brand), var(--ui-info))` is removed; the
band takes the flat `var(--ui-brand)` fill. This is a narrow exception to the 2026-09-08
"mirror the original" ruling, which still governs the rest of the page - the original's own
stylesheet had a gradient there and the port no longer does. Where the two meet, the newer
ruling wins, and it names gradients and nothing else.

Scope: no gradient anywhere in `Components/Pages/ServiceHealth.razor.css` (`grep -c
gradient` returns 0). The `#4fc3f7` header-icon literal is unaffected and is still the only
literal the style guard allows.

Module `ServiceHealth` `1.3.0` -> `1.3.1`; base app unchanged at `2.20.2`. Detail in
`docs/ServiceHealth-Plan.md` round 5.

### 2026-09-08 - Service Health mirrors the original dashboard's appearance; no redesign, no charts

Status: Active. Supersedes the round 2 and round 3 presentation decisions in
`docs/ServiceHealth-Plan.md` where they conflict.

Owner ruling, after five rejected rounds of design work: "just make it look like the
fucking original since you are incapable of improving it." An earlier remark praising a
donut chart was explicitly retracted ("I mistakenly told you the circle chart thing was
good"), so the page carries **no charts**.

What this settles:

- The Service Health page is a fidelity port of the appearance of the standalone Flask
  dashboard at `D:\source\servicehealthmonitor`, not an improvement on it. Do not redesign
  it. Changes to its look need a fresh owner ruling.
- The port reproduces the original's geometry but not its palette: `wwwroot/app.css`
  requires every colour to come from a `--ui-*` token, so the original's literal hexes are
  mapped onto tokens and the nine non-default themes keep working. One literal, `#4fc3f7`,
  is allowed for the header icon on the brand-filled header band and is pinned by a test.
- Two earlier decisions are reversed. The separate incident section returns (the original
  has one, filtered by the clicked service card, and the newer instruction wins over the
  round 3 "no flat list" ruling). The wildcard text filter is replaced by the original's
  service dropdown; "filter *" is read as "default to everything", which "All Services"
  satisfies.
- The sort control the owner asked for is retained, styled as a third dropdown in the
  original's filter row - the feature stays, the look does not change.
- "Active Issues" counts issues with no `endDateTime`, the original's definition, not
  "not resolved". This closes the carried 17-vs-15 count discrepancy in favour of 15.

### 2026-09-04 - Dev and prod share ONE config database; promotion never copies it

Status: Active. Supersedes the 2026-06-18 dev-wins promotion rule ("prod's config
should MIRROR dev's exactly - a wholesale replace", `tools/promote-dev-to-prod.ps1`
config block) and `.agents/repo-guidance.md` Architectural Invariant 2 ("Config
promotion is dev-wins"). Governs `docs/SharedConfigDb-Plan.md` (drafted the same day).

Owner rulings 2026-09-04, in order: "prod deployment cannot keep overwriting the prod
db and all settings with what's in dev." Then, rejecting a prod-keeps-its-own-copy
answer: "can't prod and dev use the same DB with each deployment making a backup
first? I don't want to maintain two dbs and two sets of settings, and not replacing
the db on promotion means that we might not be getting new database tables or
settings. I need a good option, not more compromises."

What this settles:

- Both instances on the server open the SAME `exchangeadmin.db`, at a path outside
  either app root, named in each instance's appsettings. There is one set of module
  enablement, section access, module settings and protected principals. A change made
  on dev is live on prod at once, including enabling or disabling a module; the owner
  chose that over two settings sets.
- Promotion never copies, replaces or merges the config database. New tables and
  settings reach prod through the shared file: whichever build starts first migrates
  it (in practice dev), and the other must accept it.
- A build therefore accepts a database NEWER than itself and migrates an older one.
  Corollary binding on every future migration: schema steps are ADDITIVE ONLY - add
  tables, add nullable-or-defaulted columns, add indexes; never rename, drop, or
  change the meaning of an existing column. A destructive change needs its own plan
  and a coordinated deploy of both instances.
- Each instance keeps its own cache invalidation honest across processes: a change
  committed by the other instance is picked up on the next read (SQLite's
  `data_version` counter), not on restart.
- Every deploy of either instance takes the verified backup from the shared path
  first, as today. The per-instance databases (bulk jobs, usage telemetry) stay per
  instance: a shared job queue would be run by both processes.
- One-time switch: dev's current database becomes the shared one (it is the superset -
  prod's has only ever received copies of it); prod's is kept as a backup.

### 2026-09-04 - Anonymous usage telemetry: event rows with a throwaway session id

Status: Active. Governs `docs/UsageTelemetry-Plan.md` (drafted the same day).

Owner request 2026-09-04: "is it possible to add some telemetry to this so I can see
how people are using it? the event log shows actual changes made, but things like
theme, modules opened and not used, etc. would be useful. it can be anonymous and
lightweight." Offered the fork daily counters vs anonymous event rows; owner ruling:
"event rows".

What this settles:

- Usage telemetry is a separate store from the audit log and never duplicates it: it
  records that a module was opened and that an action of a given name ran in it, never
  the target, the outcome detail, the operator name or the IP. The audit log remains
  the record of who did what.
- Rows are anonymous by construction: no user identity field exists in the table. Each
  row carries a random session id minted when the browser circuit opens and discarded
  with it, so "opened and left without acting" can be answered per visit. The owner
  accepted that long sequences could in theory hint at who was working when; identity
  itself is never stored.
- Lightweight means fire-and-forget: a telemetry write can never change, delay, or
  mask the operation that triggered it, and a failed telemetry write is logged, not
  surfaced.
- Storage is a SEPARATE operational SQLite database beside the jobs database
  (`config/exchangeadmin-usage.db`), never the config database: prod promotion replaces <!-- lint: allow (owner ruled leave-it, 2026-09-08: runtime usage DB is intentionally created outside source control) -->
  prod's config DB wholesale with dev's, so telemetry in it would be overwritten with
  dev's on every promotion (codex openreview finding ute-1, 2026-09-04; the first draft
  of this entry said "the existing SQLite config database" and was wrong). Age-based
  sweep; the plan fixes the retention period. The file is deploy-excluded with the rest
  of `config/`, not promoted, and not backed up - it is disposable.

### 2026-09-02 - Intune Devices notification and Entra-removal defaults are not Module Config settings

Status: Active. Supersedes the config-default half of D2 in
`docs/IntuneDeviceManagement-Plan.md` (2026-08-14), recorded there as
"Revision 2026-09-02". D2's act-time checkbox half stands unchanged.

Owner ruling 2026-09-02: "the email options should not live in global module
settings. they should be in the tool so the user doing the wipe can make the
determination."

What changes:

- The four Boolean config fields are removed from the `IntuneDevices` descriptor:
  `NotifyUserOnDelete`, `NotifyUserOnRetire`, `NotifyUserOnWipe` and
  `RemoveEntraObjectByDefault`. The Entra-removal default falls under the same ruling
  because it is the same shape - a deployment-wide default for an act-time checkbox.
  `GraphDelineaSecretId` and `SearchResultLimit` remain.
- The operator running the action decides, at that moment, whether the affected user
  is emailed and whether the Entra ID device object is also removed. The checkboxes
  stay exactly where they were, on the confirm bar.
- Their starting states are fixed in code, not read from config
  (`IntuneDeviceService.NotifyUserStartsTicked`, `.EntraRemovalStartsTicked`):
  notification off for Delete, on for Retire, on for Wipe - D2's defaults, now
  hardcoded - and Entra removal off. EntraDelete offers no notification at all (null,
  not false), so the absence stays deliberate.
- Unchanged: the app-wide user-notification switch still outranks the checkbox, and a
  suppressed send is still stated on screen and in the audit event rather than
  silently doing nothing.

### 2026-09-01 - Boolean settings must be non-ambiguous controls, never free text

Status: Active. A standing design rule for every admin surface, and the queue's P1.

Owner ruling 2026-09-01, after finding Prevent Self-Grant rendered as a text box
whose help text says "(true/false)": "that is fucking stupid. it should be
impossible to 'misspell' a vital security settings. no compromise on that.
checkboxes or other non-ambiguous controls are the only acceptable controls."

What changes:

- A boolean module/admin setting may never render as a free-text input. Checkbox,
  toggle, or an equally non-ambiguous control only. This binds every existing and
  future boolean config field.
- Work item (queue P1, plan `docs/BooleanConfigControls-Plan.md`): add a Boolean
  config field type rendered as a checkbox on Module Config. Known instances:
  `PreventSelfGrant` (`ModuleCatalog.cs:150`, the one live offender - a repo-wide
  search found no other true/false text field) and the BitLocker plan's upcoming
  `ValidateTickets`, whose S2 now declares the Boolean type.
- Server-side parse guards stay: the stored value is still a string a bad deploy or
  out-of-band edit can corrupt, so fail-closed reads (btv-1) remain the backstop.
  The control fixes the honest-operator path; the guard covers the rest.

### 2026-08-31 - ServiceNow ticket validation: required later, seam built now, per-module switch

Status: Active. Satisfies the Constitution's External Integrations clause ("Ticket
fields are plain audit metadata unless ServiceNow validation or writeback is
explicitly requested") - validation is now explicitly requested, app-wide. Writeback
is not.

Owner ruling 2026-08-31, during BitLocker ticket-field planning: "service now
integration IS going to be required, so any place where a ticket field exists is
going to need to tie into that validation once it's live. so, to stop painting into
corners, why don't you build that, have an on/off switch in admin settings per
module, and have the off setting allow any text and the on setting actually
validate? I don't have API access to SNow yet, so the point now is to make adding it
later less of a fucking disaster."

Corrected the same day against the code: a ServiceNow client ALREADY EXISTS and is
dormant, not absent. `Services/ServiceNowService.cs` implements `ValidateTicketAsync`
(Table API, INC/REQ table routing, active-state check), registered as a singleton
(`Program.cs:192`), switched by the app-wide deployment setting `ServiceNow:Enabled`
(dormant -> every ticket passes, by design; `docs/AdminModuleDeveloperGuide.md:1067-1069`
documents it as a planned feature awaiting API access). Eight pages already call it
inline at action time (MailboxPermissions, CalendarPermissions, ConferenceRooms,
Comms10k, GroupManagement, MfaReset, DhcpAuthorization, plus a never-referenced
shared component `Components/Shared/TicketNumberInput.razor`). What is missing is
exactly the owner's per-module switch, plus coverage of the ticket fields that do
not validate at all.

What changes:

- A shared per-module policy layer (`ITicketValidator`) is built now over the
  existing `ServiceNowService`, as shared infrastructure (base app bump when it
  lands). First consumer: the BitLocker mandatory ticket gate
  (`docs/BitLockerMandatoryTicket-Plan.md`).
- Each module with a ticket field gets its own on/off switch in Module Config
  (per-module, not global). Off (the default): any non-blank ticket is accepted as
  plain audit metadata - today's behavior. On: the ticket must actually validate
  through `ServiceNowService.ValidateTicketAsync`.
- While `ServiceNow:Enabled` is false (no API access yet), a module switched On
  refuses ticket-gated operations with a message saying validation is on but the
  ServiceNow integration is dormant - fail closed and visible, never a decorative
  switch. The dormant service's own everything-passes behavior is NOT surfaced
  through a module that claims to validate.
- When ServiceNow API access arrives, enabling it is deployment config plus one
  recorded pre-condition: `ServiceNowService` reads `ServiceNow:Password` from
  appsettings (`ServiceNowService.cs:21`), which the Constitution's credential rule
  (PAM-held service-integration passwords, ServiceNow named explicitly) does not
  permit for live use - moving that credential to the PAM store is part of the
  go-live work, recorded here so it is not lost. Rewiring the eight existing
  call sites through the per-module validator is the same future sweep.
- The seam stays backend-agnostic in its interface (same reasoning as the PAM
  seam), while delegating to `ServiceNowService` as its only backend today.

### 2026-08-31 - RiskyUsers reads: audited, never alert-emailed (D2 ruled)

Status: Active. Extends the 2026-06-30 per-module read classification to `RiskyUsers`,
the first module purpose-built for security response. The Constitution's Notifications
clause makes this a deployment classification; this entry is that classification.

Owner ruling 2026-08-31: "views? no." and "it should be logged, but not alert emailed."

What changes: every RiskyUsers read audits via `AuditService.LogModuleAction` (mandatory,
as everywhere); the read path never wires `EmailService`. AC17 in
`docs/RiskyUsersModule-Plan.md` asserts exactly that shape. Mutating actions still
notify per the mutating-action rule. The RiskyUsers pre-ship gate is cleared - the module
now waits only on an implementation go (and the owner-side Entra app registration).

### 2026-08-31 - Reviewer verification rounds: CRITICAL-only, and only on explicit owner approval

Status: Active; operative rule is in `.agents/repo-guidance.md` (Earned Practices).
Settled rationale and supersession history are archived verbatim in
`.agents/history/decision-archive.md` (2026-09-14 sweep).

### 2026-08-31 - Self-service is never gated by Protected Group Targets (reverses pgwt AC4)

Status: Active; operative rule is in `docs/ProjectConstitution.md` (Protected Principals).
Settled rationale and supersession history are archived verbatim in
`.agents/history/decision-archive.md` (2026-09-14 sweep).

### 2026-08-27 - Implementation model: Fable 5 end to end (amends Token Budget D1)

Status: Active. Amends D1 of `docs/TokenBudget-Plan.md` (Implemented 2026-08-27): the L4c
assignment table's implementer column is superseded; the rest of the assignment stands.

Owner direction 2026-08-27: "go with fable", choosing among a Sonnet 5 session, an Opus 5
session, or Fable 5 end to end for implementing the queued plans. The stated goal was "I
just want to minimize duplicating work", with the added fact that Fable bills ~2x Opus
rates (~5x Sonnet).

What changes: implementation is done by **Fable 5, end to end, one fresh session per
slice**. What stands from D1: codex/GPT-5.5 review at full strength, Gemini as the
owner-dispatched third harness for contested findings, no economising on reasoning
effort, and the session/cache/request discipline in `.agents/repo-guidance.md` (Token
Budget).

Reason: at one-slice-one-session discipline a slice estimates roughly $3 (Sonnet) to $14
(Fable) whichever model implements - the rate difference is noise. The cost that matters
is rework: a review-fix cycle costs another session plus owner attention and roughly eats
the rate saving, so the strongest implementer minimizes duplicated work, which was the
goal. The orchestrator-plus-implementation-subagent shape is rejected outright: it pays
twice by construction (supervising context plus subagent tokens).

The Sonnet 5 trial is NOT scheduled. If cost pressure returns, the recorded fallback is
D1's original assignment (Sonnet 5 at `high`/`xhigh` implements) with its revert trigger,
started on a lower-stakes plan rather than authorization code. `.agents/token-log.md`
keeps per-slice cost measured either way, so this choice stays observable.

### 2026-07-31 - Protected-principal admin input is validated under the app-pool identity, not the Delinea directory-read secret

Status: Active. Plan `docs/ProtectedPrincipalInputValidation-Plan.md` (Approved, owner
2026-07-31), D2.

Owner direction 2026-07-31. The Protected Principals admin page will validate typed entries
against Active Directory before accepting them. Two credentials could perform that lookup: the
app pool's ambient Windows identity (already used by `Services/ADDirectorySearchService.cs`,
no secret) or the protected-principal directory-read secret from Delinea (used by enforcement,
`ProtectedPrincipalService.GetDirectoryReadSecretId`). Validation uses the **app-pool identity**.

The drafted recommendation was the Delinea secret, reasoning that only the enforcement
credential can prove a rule will work at enforcement time. The owner rejected that on least
privilege: "the delinea stored secret has vastly more permissions than the app pool, and we need
to use it only when necessary." The divergence the recommendation guarded against does not exist
here - anonymous LDAP lookups are permitted in this environment and any authenticated user can
read any user or group, so both credentials see the same objects for a read-only existence
check. This sits inside the Constitution's Credential Isolation carve-out for "an operation that
is explicitly read-only and approved for ambient Windows identity".

**Scope limit - do not inherit this silently.** The ruling is explicitly conditioned on this
deployment ("at present, in this environment"). If a deployment ever restricts directory read
access so the app pool and the directory-read secret see different objects, this must be
revisited: an entry could then validate clean under the weaker credential and still fail at
enforcement.

Related but distinct: the 2026-07-28 Option A entry below also reuses the app-pool-credentialed
AD read, but its justification was that the picker "only assists typing" and the real write
re-validates under the module credential. That reasoning does **not** transfer here - what the
admin page saves *is* the rule, with no second check. This decision stands on the environment
fact above, not on that precedent.

### 2026-07-28 - Retire the `Security:ExcludedUsers` appsettings fallback (exclusions are module-config only)

Status: Active. Plan `docs/RetireExcludedUsersAppsettingsFallback-Plan.md` (Approved,
owner 2026-07-28; Implemented). App version `2.3.29` -> `2.3.30`.

Owner direction 2026-07-28. Both readers of the legacy exclusion list -
`PermissionValidator.GetConfiguredExclusions` and
`ProtectedPrincipalService.GetLegacyExclusions` - previously fell back to the
`Security:ExcludedUsers` string array in `appsettings.json` when the
`MailboxPermissions/ExcludedUsers` module config was empty. That fallback was invisible
to the Protected Principals admin UI, so a principal could be blocked with no cause the
admin could see or clear from the UI (hit in practice: a test account blocked by the
self-service groups gate). The fallback is removed; exclusions now come only from the
DB-managed protected-principal store and the MailboxPermissions module config.

Reconciled read-only against the DB `protected_principal` store on both installs before
removal: `vincent.roche` (exact DB duplicate) and `VRStaff` (covered by
`group|ANALOG\VR Staff`) keep protection; `mcoelho-2` (test account) and `CLD_LIC_MS_BOD`
correctly lose it - the owner removed `CLD_LIC_MS_BOD` from the UI as the wrong group and
its lingering presence in appsettings was itself the bug this change fixes. No DB
migration needed. Host cleanup (removing the `Security:ExcludedUsers` block from each
host's runtime `appsettings.json`) is runtime data outside source control, done per box
after deploy; `PreventSelfGrant` and `AllowedGroups` stay.

### 2026-07-28 - Self-Service Groups member picker reuses the app-pool-credentialed AD autocomplete (Option A)

Status: Active. Plan `docs/SelfServiceGroupsMemberListingAndPicker-Plan.md` (Approved
2026-07-28), which adds member listing + a member-name typeahead to the shipped GM-3 module.

Owner direction 2026-07-28. The member add box reuses the existing shared control
`Components/Shared/ADIdentityAutocomplete` as-is. Its live suggestion search runs under the
**app-pool ambient identity** (`Services/ADDirectorySearchService.cs:8`), NOT the
SelfServiceGroups module's Delinea credential. This is a deliberate, accepted exception to the
module's otherwise-strict per-module credential isolation, bounded because the picker only
assists typing an identity string: the actual write still routes through
`SelfServiceGroupService.ChangeMemberAsync`, which re-resolves the member USER-ONLY under the
module credential, runs the protected-principal check, re-checks eligibility, and
read-back-reconciles. A suggestion sourced from the weaker read path can never cause a bad
write.

Rejected: Option B (build a second, module-credentialed typeahead) — declined as not worth the
extra code/test surface for a read-only suggestion list. The write-path isolation is unchanged
either way.

### 2026-07-24 - GM-3 task 2 scaled back: eligibility = manager-can-update-membership + on-demand single-group search

Status: Active (supersedes the admin-allowlist eligibility of the 2026-07-22 on-prem-only
entry and codex F5, and the never-recorded 2026-07-23 broad ACE-scan direction; plan
`docs/SelfServiceGroupManagement-Plan.md` §6.3 and task 2 revised).

Owner direction 2026-07-24. The eligibility half of GM-3 is scaled back to two cheap
targeted AD lookups. Both prior approaches are DROPPED:

- the admin-controlled immutable-ID allowlist (the original codex-F5 resolution), and
- the 2026-07-23 "show every group the caller can update via any GenericAll / GenericWrite /
  WriteProperty-on-member ACE" rule, which required a domain-wide ACL scan.

Reason: the ACL scan is not searchable by trustee (`nTSecurityDescriptor` cannot be queried
by SID), so "groups I can edit" would need reading every group's ACL. The group universe was
sized at **41,368 groups domain-wide** (`Get-ADGroup -Filter *`, 2026-07-24) — a full scan is
~6-12 min. No OU scope narrows it safely: the AD has grown since NT4.0 and any OU allowlist is
brittle and silently drops groups. The owner judged that cost/risk unjustified for the value,
and the admin allowlist an "arbitrary security speedbump" (a manager can already edit their
group in ADUC).

New rule:
- **Passive list = manager-can-update-membership only.** Show groups where the caller is the
  declared `managedBy` manager AND "Manager can update membership" is on (the WriteProperty-on-
  `member` ACE that checkbox grants). SelfMembership (self-only) does NOT qualify. **Enforced at
  LIST TIME, not at write (owner, 2026-07-24):** task 1's `managedBy`/`msExchCoManagedByLink` LDAP
  filter is necessary but not sufficient — a group can name the caller as manager with the checkbox
  UNCHECKED. So for each candidate the filter returns, read its ACL and confirm the caller's
  WriteProperty-on-`member` (or GenericWrite/GenericAll) ACE before including it; EXCLUDE any that
  fail. This means task 1's current filter-only list must gain the per-group ACL check — task 1 is
  the candidate set, not the final list. Cost is bounded (ACL read over the small per-user set, not
  the 41k domain). A loading spinner is REQUIRED while the list builds (the per-group ACL read adds
  latency) so users do not assume the page is dead and resubmit.
- **On-demand single-group search.** A user who knows they can manage a group (per the discovery
  finding, `tools/Discover-GroupMembershipDelegation.ps1`: edit rights are almost all direct
  per-user ACEs, not helpdesk-group delegation) types the group name. Resolve once, injection-safe
  (codex F11); read the group; confirm the caller can manage its membership (manager-with-
  WriteMember OR a direct membership-write ACE on the caller's SID); return it if manageable, else
  an error directing them to contact the IT Support Desk. Single per-name lookup, no scan.

F5 is met without an allowlist: the rule keys on an actual membership-write right (authorization),
not mere ownership, and the AD write credential's ACL/JEA rights remain the least-privilege
backstop. Still fail-closed — a group not confirmed manageable is refused, and a hard AD read
failure is an error, never an empty/allowed result (Known Failure Class #3).

Supersedes: the admin-allowlist eligibility in the 2026-07-22 on-prem-only entry and codex F5.

### 2026-07-22 - GM-3 scope narrowed AGAIN: on-prem AD only, M365/delegated-Entra dropped entirely

Status: Active (supersedes the delegated-Entra elements of both 2026-07-22 entries below;
plan `docs/SelfServiceGroupManagement-Plan.md` revised to on-prem-only)

Owner direction 2026-07-22, during slice-1 implementation. The M365 half of GM-3 is
DROPPED. GM-3 is now on-prem Active Directory self-service group management ONLY.

Reason: the delegated-Entra design forced a decision on actor<->Entra-account binding
(codex F1). With hybrid identity, synced users bind Windows-SID -> Entra
`onPremisesSecurityIdentifier`, but the owner's real need surfaced as "log into Windows
as my on-prem account, manage groups my Azure-only -CLD privileged account owns." That
is cross-identity management: the acting identity (Windows, drives authorization + audit)
and the owning identity (Entra -CLD) are different privilege levels. Allowing it would
re-open (in a self-driven form) the cross-identity path the earlier 2026-07-22 decision
dropped, and would require dual-identity audit + authorization keyed off the signed-in
Entra account + a fresh security review. Owner's ruling: not worth it -- users can already
manage O365 group membership they own directly in the Microsoft portal, which enforces
proper auth. The value the portal does NOT give self-service users is on-prem AD group
management; that is what GM-3 now delivers.

Consequence:
- DROPPED: the second (Entra/OIDC) auth scheme, Microsoft.Identity.Web/MSAL, the token
  cache, actor<->Entra binding, `/me/ownedObjects` cloud-ownership query, the dedicated
  delegated Entra app registration, and the pre-ship security-review gate that existed
  for the delegated tokens. Codex findings that existed solely for the delegated flow
  (F1, F2, F3, F4, F8, F12, F13) are moot.
- RETAINED: on-prem AD ownership reverse-lookup (`managedBy` + `msExchCoManagedByLink`),
  fail-closed eligibility allowlist (codex F5), user-only member add/remove with
  pre-write re-checks + protected-principal gate (codex F7, F9), injection-safe
  identifier resolution (codex F11), audit + affected-user notification on on-prem
  security-group changes (codex F10, now with no background-worker tension since there
  is no cross-service token/outbox concern).
- The "unified list spanning both backends" and the partial-failure banner (AC8) collapse
  to a single on-prem source; no merge, no per-backend banner needed.

Reverted in-progress code from the delegated flow (slice 1 steps 1-2): the
Microsoft.Identity.Web package reference and the `DelegatedEntraSettings*` provider are
removed; the `ISecretFieldsReader` seam on `DelineaService` is retained only if the
on-prem path uses it, else removed.

Supersedes: the delegated-Entra design direction (2026-07-22 entry below) and the
"self-service spans BOTH on-prem AD and M365" element of the scope-narrowed entry below.

### 2026-07-22 - GM-3 scope narrowed: self-service only, admin "manage for others" dropped

Status: Active (refines the design-direction entry below the same date; plan
`docs/SelfServiceGroupManagement-Plan.md` being revised to this scope)

Owner direction 2026-07-22. GM-3 is a SELF-SERVICE feature only:

- A signed-in user manages the groups THEY own -- both on-prem AD and M365 -- via the
  unified "load the groups I can manage" surface. This works because a user querying
  their OWN cloud ownership is a delegated call Microsoft supports.
- Admins get NO new capability. For both AD and M365 groups an admin continues to use
  the EXISTING search-by-name group-management screens (AD Group Management,
  M365 Group Management) unchanged.
- The admin "manage groups for a specified user X" path is DROPPED. Reason: Microsoft
  Graph offers no efficient application-permission (app-only) way to answer "which
  groups does user X own." Verified 2026-07-22 against current Microsoft Learn docs
  three ways: (1) `/users/{id}/ownedObjects` (+ `/microsoft.graph.group` cast) lists
  "Not supported" for Application permissions; (2) `/groups` has no server-side
  `$filter` on the `owners` navigation property (only count-based `/$count` filters);
  (3) the only app-only fallback is a full tenant group scan (tens of thousands of
  groups) which is non-viable at ~30k-user scale. An admin cannot stand in for a user
  app-side; only the signed-in user's own delegated token returns their owned groups.

Consequence for the plan: the entire admin-path / actor-vs-subject / delegated-admin-role
complexity (former task 8, codex F6) is REMOVED, not deferred. The `ManageOthers`
granular permission is dropped from the module descriptor. Delegated Entra sign-in is
retained ONLY for the user's own cloud-ownership query. This collapses much of the
round-1/round-2 codex finding surface that existed solely to support the admin path.

Supersedes: the "admin can do the same on behalf of a specified user" element of the
2026-07-22 design-direction entry below, and its AC7 / admin-path tasks in the plan.
The delegated-auth foundation, on-prem reverse-lookup, fail-closed eligibility, and
member-add/remove-only first cut are UNCHANGED.

### 2026-07-22 - GM-3 self-service group management: design direction (delegated Entra auth for cloud ownership)

Status: Active (design agreed; plan `docs/SelfServiceGroupManagement-Plan.md` still to be written
and approved before any code)

Owner direction 2026-07-22, after a codex-commercial design consult. GM-3 (self-service group
management, queued 2026-06-17 commit `75e33be`) is designed as follows. This entry records the
agreed shape; the implementation plan is not yet written and no code is approved.

**Goal:** a user (ultimately all-staff, gated by the app's normal per-module access group) is
presented an easy-to-navigate, UNIFIED list of the groups they can change, spanning BOTH on-prem
AD and M365 — with columns for group type, location (on-prem/M365), and other owners. Admins can
do the same for a specified user. The experience must not require the user to know where a group
lives.

**Load model:** an explicit "load all the groups I own" button (with a "this can take some time"
note), NOT a preload on page open. Loads everything the user can update from both sources and
presents one merged list. (Owner rejected search-then-validate: users must not have to know a
group's exact name; the list must be presented up front.)

**Cloud ownership lookup requires delegated Entra authentication (the decided fork):**
- The app is `Negotiate` (Windows/Kerberos) only today, and its Graph access is APP-ONLY (client
  credentials). Microsoft does NOT support `/users/{id}/ownedObjects` under application
  permissions, so "list the groups this user owns" cannot be answered app-only without walking
  every group in the tenant.
- Tenant scale (~30k users, tens of thousands of groups) rules out load-all-and-filter / scanning.
- Two viable paths were weighed: (A) add delegated Entra (OIDC) sign-in so the app queries Graph
  AS THE USER and Microsoft returns exactly their owned groups; (B) a backend-maintained ownership
  index refreshed periodically. **Owner chose A.** B was rejected as "brittle and messy" and it
  cuts against the app's no-background-worker posture (2026-06-17).
- Delegated sign-in is expected to be SILENT for most users (they already have a live O365 browser
  session); it is standard OIDC authorization-code flow — the browser returns a one-time code, the
  backend redeems it for a per-user token. The user's token is never shuttled from browser to
  backend by the app.
- This adds a SECOND auth scheme (Entra/OIDC) alongside the existing Windows/Negotiate one, for
  this feature. That is the real cost and the reason a plan + security review are required.

**Security requirements (hard, to be written into the plan):**
- A delegated token acts only as that user, only within registered scopes — strictly LESS powerful
  than the existing app-only credential. Register the NARROWEST Graph scopes that do the job.
- Per-user tokens are kept only in the protected server-side session store, NEVER logged, NEVER
  written unencrypted. Prefer NOT requesting offline_access (no refresh token to steal) unless a
  concrete need appears; rely on short access-token lifetime.
- Blazor Server circuit isolation: each circuit reads ONLY its own token; no shared/static token
  field. Self-service owner is ALWAYS derived from the authenticated principal; any submitted
  owner id is ignored (admin "manage for user X" is a SEPARATE trusted server entry point gated by
  a granular admin permission, never a caller-supplied "isAdmin" flag).
- Explicit security-review gate before this ships.

**On-prem ownership (no scan needed):** AD answers "groups this user owns" via reverse lookup on
both the single-valued `managedBy` and the Exchange multi-owner (co-managers / `msExchCoManagedByLink`)
list — per-user server-side queries, not a tenant scan.

**Codex consult findings folded into the design (2026-07-22):**
- Ownership alone is NOT authorization. `managedBy`/owners is directory metadata; a user owning a
  privileged group must not be able to self-escalate. Add a fail-closed "manageable group"
  eligibility rule (e.g. allowlisted scope/OU) ON TOP of ownership. Every write re-checks:
  re-read group, re-check eligibility, re-check ownership by immutable id, protected-principal
  check on the affected member, re-check policy immediately before writing.
- Structure: prefer ONE unified descriptor (main access = all-staff + admin groups; granular
  `ManageOthers` permission = admin group for the owner-picker mode), over two descriptors, since
  the main policy already accepts multiple AD groups. Two descriptors only if navigation truly
  needs two front doors — then static wrapper pages over one shared component; NEVER let one
  descriptor borrow another module's credential (Constitution).
- Unify the EXPERIENCE, not the backends: query both concurrently behind adapters, merge
  normalized results, preserve per-source capabilities (CanManageMembers/Owners, IsDynamic).
  Partial failure (one backend down) shows healthy results + a prominent "incomplete - M365
  unavailable" banner, NEVER "no groups found", NEVER a silent drop, disable stale selections.
  Fail-closed per backend.
- M365 search: use Graph `$search` (tokenized) with `ConsistencyLevel: eventual` + `$count=true`
  + pagination + post-ranking over displayName/description; `contains()` is unsupported on
  displayName. (The current M365 search is prefix-only single-field — the reported "can't find it"
  bug.) Graph client needs explicit-header support first.
- Pre-existing bugs codex flagged to fix as part of this: on-prem 200-result cap does not guarantee
  the exact match was fetched; on-prem ranking searches email but ignores it when ranking; M365
  add-member accepts UPN/email but Graph needs a directory object id (must resolve first).
- First cut (ship value, no corner-painting): unified owned-groups surface; self + admin modes;
  member add/remove ONLY. NO owner mutation, NO group create/update/delete/ownership transfer
  (owner changes alter the authorization predicate itself); dynamic M365 groups read-only; keep
  existing admin group pages as-is for legacy CRUD.
- Repo rule to respect: the no-user-notification exception is scoped to M365 group changes only;
  on-prem security-group membership changes may require affected-user notification.

### 2026-07-22 - Module packaging/import deferred as low-value/high-cost

Status: Active

Decision (owner, 2026-07-22): module packaging/import is **deferred** — the owner judged it a
**low-value, high-cost add**. No plan is to be written and no code is to be built. This is not a
teardown of the recorded end-state direction (UI-driven `.zip` upload, see the 2026-06-29 and
2026-06-18 entries below); those entries remain the durable record of *where it would go if
revived*. This entry sets the current standing intent: **do not work on it**, do not raise it as
the next backlog item, and do not treat the ~54 hand-wired `Program.cs` registrations or the
compiled `ModuleCatalog` as debt to pay down for packaging's sake.

Why: the only concrete friction it solves is that a module-scoped fix cannot reach prod without a
full app rebuild+redeploy. The owner is the sole module author and the app deploys as one unit via
script; the rebuild-to-ship cost is acceptable and does not justify the contract-refactor +
per-module-assembly + package + UI-loader effort (and, at the runtime-load end, an arbitrary-code
ACE surface in a privileged Exchange/AD tool).

Revisit only if a real second deployment or a third-party module author appears. Until then this
supersedes the "first leg = self-registration seam, agent's discretion on interim steps" framing
of the 2026-06-29 entry: there are no interim steps to take now.

### 2026-07-21 - Adding a new module does not bump the base app version

Status: Active; operative rule is in `docs/ProjectConstitution.md` (Deployment And Versioning).
Settled rationale and supersession history are archived verbatim in
`.agents/history/decision-archive.md` (2026-09-14 sweep).

### 2026-07-21 - Code and logging source is pure ASCII (CI-enforced)

Status: Active

Code and logging source in this repo is pure ASCII: no em-dashes, smart quotes, arrows, or
other non-ASCII characters in `.cs`, `.ps1`, or `.psm1` files -- comments, log messages, and
audit/operator-facing string literals included. Use `--` or `-` for dashes, `->`/`=>` for
arrows, straight quotes, plain `...` for ellipsis. This generalizes the existing
PowerShell-only rule (`.agents/repo-guidance.md` Architectural Invariants #6 / Known Failure
Class #6, which required ASCII for 5.1-read scripts) to all code and logging.

Scope (owner, 2026-07-21): code and logging ONLY. Explicitly out of scope and NOT enforced:
docs (`.md`), UI-rendered content (`.razor` markup), and `Services/EmailService.cs` (whose
only non-ASCII is deliberate emoji/copyright inside HTML email bodies -- non-ASCII inside the
rendered HTML is legitimate UI). Toolkit-owned files (`AGENTS.md`, `.agents/playbooks/*`) are
never agent-edited and are not code/logging files anyway.

Reason:
Non-ASCII in code/logging carries no benefit and real, already-realized cost. A BOM-less
em-dash in `tools/JobStateWarning.psm1` broke a Windows PowerShell 5.1 deploy this session
(5.1 reads it as ANSI and mangles it into parse errors). Audit strings flow into logs and the
SQLite audit DB where encoding can drift or render wrong; em-dash-vs-hyphen is invisible in
review and survives or dies silently through copy-paste, terminals, and tooling. UI text is
excluded because it renders through UTF-8 HTML/markup where encoding is safe and the glyphs
are intentional.

Enforcement (owner, 2026-07-21; implemented 2026-07-21): a CI gate runs
`tools/Test-AsciiOnly.ps1`, which scans tracked `.cs`/`.ps1`/`.psm1` (excluding
`Services/EmailService.cs`) and fails the build on any non-ASCII byte. Wired into
`.github/workflows/ci.yml` (`powershell` job, "ASCII lint" step). The sweep that made the
tree ASCII-clean landed first (commit `c2e2f6f`), then this gate, so CI is green on
introduction.

### 2026-07-21 - ConferenceRooms protected-principal check is one guarded-execution enforcement point

Status: Active

The ConferenceRooms module enforces the protected-principal gate through a single
`ConferenceRoomProtectionGate.GuardThenRunAsync(identity, onDenied, onAllowed)` helper
(`Services/ConferenceRoomProtectionGate.cs`), not per-path inline copies. Every room-mutating
write — single-room Finder, single-room Type, and each bulk row — reaches its write only inside
the gate's `onAllowed` delegate, so the check runs exactly once per write and no path can write
without passing it. The write's trace scope opens inside `onAllowed`, so the protection decision is
fully made before any side effect (fail-closed; Known Failure Class #1). Denial auditing stays with
each caller so per-path action labels are preserved (Finder `ConferenceRooms_SetMetadata`, Type
`ConferenceRooms_SetType`, bulk `_Bulk`-suffixed with captured job actor/ip/ticket).

This supersedes the three prior near-duplicate inline checks (page Finder had none — the gap;
page Type and the bulk processor each had their own copy). Closes the last known protected-principal
gap (`docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`, Implemented; finding pp-finder-1).
The guard is deliberately ConferenceRooms-scoped, not added to the shared `ProtectedPrincipalService`
— keeping it module-local made this a module-version bump (`ConferenceRooms` 2.2.0 → 2.3.0) with no
app-version bump, per the two-rule versioning policy.

Reason:
Duplicated authorization checks drift — the single-room Finder path was the copy that silently
lacked the gate. A guarded-execution helper makes the write unreachable except through the gate, so
the invariant is structural rather than maintained by discipline across call sites. Subordinate to
the 2026-06-29 protected-principal decision and `docs/ProjectConstitution.md` §Protected Principals.

### 2026-07-02 - Bulk operations run as durable, user-initiated, ticketed, audited server-side jobs

Status: Active

The Bulk Job Runner (`docs/BulkJobRunner-Plan.md`, Implemented) is built. ConferenceRooms bulk
apply (Room Finder / Room Type CSV) no longer runs an inline loop inside the Blazor circuit; it is
submitted as a durable server-side job that survives the submitting browser closing. Several
durable decisions this locks in:

1. **A durable job runner narrows — does not overturn — the 2026-06-17 no-background-worker
   posture.** `BulkJobService` is a self-pumping singleton, not an `IHostedService`/timer. The
   2026-06-17 decision removed the app's only hosted service because it mutated AD *unattended*
   under a synthetic actor with no ticket. This runner is different in kind: every job is
   user-initiated, carries a real submitter + IP + ServiceNow ticket, is fully audited per row,
   and is always cancellable. It does nothing on a schedule — on startup it runs exactly one
   one-shot reconciliation (via an explicit `InitializeAsync()` call in Program.cs, because a DI
   singleton is not constructed until resolved), then only acts when an operator submits.

2. **Job state lives in a SEPARATE operational SQLite database, `config/exchangeadmin-jobs.db`, never the config DB.** <!-- lint: allow (owner ruled leave-it, 2026-07-27: runtime jobs DB is intentionally created outside source control) --> Rationale: job state is environment-local (a dev job must never appear
   in prod), high-churn, and prunable; the config DB is promoted dev→prod and backed up before
   every deploy. Mixing them would reintroduce the "many concerns in one store" coupling the
   2026-06-12 SQLite migration exists to kill. The jobs DB inherits the `config/` deploy
   exclusion + ACL but is **excluded from the config backup/promote path** — it is never promoted
   and never restored. Consistent with the 2026-06-12 "operational state → SQLite" decision.

3. **No resume across restart.** On startup every non-terminal job (Running OR Queued) is flipped
   to `Interrupted` — full stop. An interrupted job is a truthful record an operator can inspect
   and re-submit, never something that silently resumes or sits stuck "Running". Queue promotion
   happens only within a live process. This, plus always-cancellable jobs and a display-only
   "Stalled" classification for a stale heartbeat, is the load-bearing anti-brittleness rule: no
   job state a human cannot clear, and nothing claims to be running when it isn't. Known honest
   limit: an in-flight PowerShell cmdlet cannot be aborted mid-call (no cancellation-token path),
   so cancel stops a job *before its next row*; a genuinely wedged call clears on the next recycle
   via orphan reconciliation.

4. **Off-circuit authorization = option (a): capture the authorization DECISION at submit,
   re-check per row.** The app has no SAM→groups lookup — authorization is entirely
   `ClaimsPrincipal`-based (role claims + `IsInRole` on the live Windows principal), which a job
   worker thread does not have. At submit (on the circuit) the job records which of the section's
   allowed groups the submitter actually satisfied (via claims OR `IsInRole`), and the runner
   re-checks that captured decision against the section's *current* allowed set per row, fail
   closed. This authorizes the submission and re-checks the snapshot; it does **not** detect
   mid-job group-membership revocation — parity with today's one-check-per-loop model, not a
   regression. The group-match logic is extracted into a shared pure `GroupMembershipChecker` used
   by both the live `GroupAuthorizationHandler` and the job re-check so they cannot diverge.
   (Option (b), live per-row SAM→groups re-resolution, was not built.)

5. **Protected-principal gate enforced in the job, per row, on BOTH Room Finder AND Room Type
   paths — no carve-out.** This closes **GAP 3** (see below): the Room Finder bulk path previously
   had no protected-principal check at all. Fail closed on Unavailable/Ambiguous/CheckFailed/
   exception, audited as a denial, reported in the row result. Applies the 2026-06-29 "protected
   principals are off-limits to every mutating module — no carve-outs" decision to this surface.

6. **Deploy scripts warn (not block) on active jobs before recycle.** `tools/JobStateWarning.psm1`
   is called by every script that stops the app pool (`deploy.ps1`, `tools/promote-dev-to-prod.ps1`;
   `deploy-pipeline.ps1` is covered transitively as it delegates to both). It lists Running/Queued
   jobs and proceeds — a wedged job must never block the recycle that clears it.

Generalization (owner, 2026-07-02): the runner is a thin general `BulkJobService` with
ConferenceRooms as the first caller behind an `IBulkJobProcessor` seam, so other bulk modules
(Migration, Licensing) can reuse it later. The job service, store, queue and lifecycle are
module-agnostic.

### 2026-06-30 - Notifications enforcement sweep: three rule-1 gaps fixed; rule-2 read-alerting classified non-applicable and deferred

Status: Active

The 2026-06-29 "Notifications are mandatory" decision was docs-only; older modules predated it
and were never retrofitted. A read-only audit (2026-06-30) of all 20 non-system modules found
rule 1 (admin notification on every mutating action) mostly already honoured, with three silent
gaps, and rule 2 (alert on security-sensitive reads) effectively unimplemented. Owner direction
(2026-06-30) resolved scope as follows.

**Rule 1 — three gaps fixed (admins notified):**
- `MfaReset` (`1.0.3`→`1.0.4`, commit `bd68d10`): admin notification on every real
  `MfaReset_Execute` attempt (reset, protected block, fail-closed outcome, exception); skips the
  trivial ticket-invalid / auth-denied pre-gates and the read-only ListMethods path. Page change,
  fail-safe.
- `ConferenceRooms` (`2.0.11`→`2.0.12`, commit `6e83ef9`): admin notification on all four write
  paths (single Finder, single Type per apply; bulk Finder, bulk Type one summary per CSV apply
  with counts — LicensingUpdates bulk precedent, not per row). Page change, fail-safe.
- `AccountLockoutRemediation` (`1.0.0`→`1.0.1`, commit `14c6219`): one summary admin notification
  per **executed** logoff (both public paths), gated on `result.Executed` so dry-runs stay silent.
  Placed at the public-method boundary, not the per-row `AuditLogoff` sites (those sit past the
  credential gate — untestable and would email per session). Service change + 3 non-vacuous tests.
  `EmailService`'s two `SendAdminNotificationAsync` overloads were made `virtual` (no behaviour/
  signature change) to give tests a seam to observe firing; the repo had none.

**Rule 3 (notify affected user):** none of the three gap modules are user-permission grants, so
admins-only. **Open, gated on testing:** `AccountLockoutRemediation` user-notification (telling a
logged-off user) is deferred until the module is actually exercised — nobody uses it yet and it is
not validated. Revisit after real testing; record a follow-up decision then. Do not build it now.
(Update 2026-07-22: the `AccountLockoutRemediation` module is disabled/deferred as unusable in
this environment; this user-notification question is parked with the module and decided only if it
is picked back up.)

**Rule 2 (alert on security-sensitive reads): classified non-applicable for this app, alerting
deferred.** Candidate reads (DelegationReport, MessageTrace, EventLog viewer, RecipientLookup,
AccountLockout discovery) all already audit, and the app exposes only data already visible in AD /
the address book. Owner: these are not genuinely sensitive reads, so audit logging is sufficient
and per-read admin alerting is **not** wanted (it would bury the change-notifications that matter
under message-trace / event-log-open volume). **Never** notify users for these. The lift is small
but the value is negative, so read-alerting is deferred indefinitely, not scheduled. The
Constitution §Notifications rule-2 wording was narrowed (this commit) so its old examples
("audit lookups", "protected-object inspection") no longer contradict this classification.

Plan: `docs/NotificationsEnforcementSweep-Plan.md` (Status: Implemented). App version unchanged
throughout (no functional `EmailService` change); each gap module took a **patch** bump because
this is conformance to already-mandatory behaviour, not new capability (owner, 2026-06-30).

Builds on / enforces the 2026-06-29 "Notifications are mandatory" decision (below), which remains
the canonical statement of the three rules in `docs/ProjectConstitution.md` §Notifications.

### 2026-06-30 - Migration eligibility check: protected status is a separate axis, suppresses single-user create

Status: Active

Decision (owner direction 2026-06-30):
The Migration module's **Check Eligibility** step must flag protected principals, treating
protected status as an axis **orthogonal** to the Exchange/AD eligibility verdict — it does
not change Eligible/Ineligible:

- Protected **and** eligible in Ex/AD → still shown **Eligible**, flagged as a protected
  principal that must be escalated outside this tool.
- Protected **and** ineligible in Ex/AD → still shown **Ineligible**, with the protected/
  escalate flag plus the real ineligibility reason(s).

Create-button behavior differs by entry type (explicit owner direction):

- **Single-user entry:** a protected principal is treated, for the **Create Migration Batch**
  button, exactly like an ineligible user — the Create card/button does not appear.
- **Group / bulk (CSV) entry:** no change to the create flow — surfaced "as already decided."
  The 2026-06-30 GAP 2 batch gate already filters protected targets out at creation and
  reports them; the eligibility table simply shows the protected flag at check time.

Fail-closed: when protection cannot be verified (Unavailable / Ambiguous / CheckFailed), the
target is flagged protected (escalate), consistent with the GAP 2 gate's posture.

Mechanism: reuses the existing in-service protection check (`CheckProtectedAsync`) via a new
`ApplyProtectionFlagAsync` seam called from `CheckMigrationEligibilityAsync`; no new protection
logic. The check is a read — no new denial audit row or admin alert is raised at check time
(the GAP 2 gate already does that at create time); the existing eligibility-check audit detail
and admin notification record protected status.

Builds on the 2026-06-30 GAP 2 decision (below): that decision governs *batch creation*; this
one moves protected *visibility* earlier, to the eligibility check, and adds single-user create
suppression.

Implemented: module `Migration` `1.2.0` → `1.3.0` (app version unchanged); commits `acf877d`
(model+service+tests), `2fb842c` (UI+audit), + docs/version slice. Plan:
`docs/MigrationEligibilityProtectedFlag-Plan.md` (Status: Implemented). 4 new unit tests,
proven non-vacuous; 593/593 green.

### 2026-06-30 - Migration batches: filter protected principals out, never silently, never block the whole batch

Status: Active

Decision (owner direction 2026-06-30):
When a migration batch (`Migration` module, `CreateMigrationBatchAsync`, both ToCloud and
ToOnPrem) contains a protected principal among its targets, the protected target(s) are
**filtered out** and the batch is created for the remaining (non-protected) targets. Two
hard constraints from the owner:

1. **It must never fail silently** — every exclusion is reported back to the operator
   clearly and directly (a distinct, always-visible warning block in the UI naming each
   excluded principal and the reason), audited as its own denial row, and included in the
   admin notification body.
2. **One protected target must never block the whole batch** — the rest are still migrated.

Degenerate case: if **every** target is protected (including the single-target path),
nothing is created and the operator is told plainly why, with the escalate-outside-this-tool
message.

This closes **GAP 2** from the 2026-06-29 protected-principal sweep (`.agents/state.md`).
It applies the 2026-06-29 "protected principals are off-limits to every mutating module"
decision to the migration-batch surface, and chooses the *filter-and-report* enforcement
shape (not refuse-whole-batch, not silent-drop) per explicit owner direction.

Protection check scope: reuses the existing on-prem-AD check (`ProtectedPrincipalService`
`ResolveWithStatusAsync` + `CheckAsync`), fail-closed on Unavailable/Ambiguous/exception.
Same accepted, documented cloud-only limitation as GroupManagement / M365GroupManagement:
a cloud-only target AD cannot resolve returns `NotFound` and is treated as not protected.
This is most relevant on the ToOnPrem (move-back) path, where targets are cloud mailboxes.

Implemented: module `Migration` `1.1.3` → `1.2.0` (app version unchanged); commits
`0b855ac` (service+tests), `5d72978` (UI+audit+notification), + this docs/version slice.
Plan: `docs/MigrationProtectedPrincipalGate-Plan.md` (Status: Implemented).

### 2026-06-29 - Module distribution end state: UI-driven .zip upload that installs/updates a module

Status: Deferred (superseded as current intent by the 2026-07-22 deferral above; retained as the
record of the end-state direction if the work is ever revived)

Decision (owner direction 2026-06-29):
The end state for module distribution is that the **main app can load a module from the UI as
a `.zip` upload** — an administrator uploads a packaged module through the web UI and the app
installs or updates it, with **no full app rebuild-and-redeploy** for that module. Whether the
package carries a **precompiled** module assembly or **source compiled at runtime** is left
**open** — the owner is explicitly not deciding that yet. Interim steps toward this are at the
agent's discretion; this entry records the destination, not the route.

This is **long-term thinking. Nothing is to be built now and no plan is approved.** It is
recorded so the requirement is durable repo memory rather than living only in chat.

Why this is now recorded (triggering context):
A one-line BlockedSenders UI fix (module 1.0.1 → 1.0.2) could not reach prod on its own — prod
still runs BlockedSenders 1.0.0 — because today a "module" is not an installable unit: it is C#
+ Razor compiled into the single `ExchangeAdminWeb.dll`, its services hand-wired in `Program.cs`
(~54 registrations), its policies generated from the compiled `ModuleCatalog` at startup. There
is no seam where a module plugs in, so any module change requires rebuilding and shipping the
whole app. That friction is the motivation for this direction.

Refines / updates: the **2026-06-18 "Module packaging direction"** decision (same file), which
set `.zip` package + `tools/validate-module-package.ps1` validator as near-term scope and
**deferred runtime upload / assembly loading** as "the hardest and riskiest version … solves a
problem the owner does not currently have." That deferral still holds for *now* (no
implementation), but the owner has now confirmed UI-driven upload **is** the intended end state,
not merely a someday-nice-to-have. The 2026-06-18 entry's near-term scope (rebuild-to-install,
documented package + validator) is the sensible first leg; this entry sets the further
destination it builds toward.

Assessed terrain (agent analysis 2026-06-29 — guidance for the future plan, not owner decisions):
- The hard prerequisite is a **module contract / self-registration seam**: a module declares its
  own services, policies, routes, catalog descriptor, and components, and the app *discovers*
  modules instead of hand-wiring them in `Program.cs`. This refactor is valuable and low-risk
  regardless of precompiled-vs-runtime, and is step 1.
- **Precompiled-vs-runtime only forks at the install step.** The contract refactor, splitting
  each module into its own assembly, and the package format are shared groundwork either way.
  Runtime compilation means accepting and compiling arbitrary code in a privileged Exchange/AD
  tool (an ACE surface) — agent lean is precompiled, but the owner has deferred the call.
- **"From the UI" almost certainly still means a quick self-restart to apply**, not true
  zero-restart live loading: Blazor Server fixes its module/route/DI set once at startup.
  Zero-restart live swap fights the framework hardest and is the riskiest variant; treat it as
  "probably never," not the target.
- Plausible interim staging (each step independently useful, single deploy until step 4):
  (1) module contract / self-registration; (2) each module builds as its own DLL loaded from a
  folder at startup → install/update = drop a DLL + restart, no full rebuild; (3) `.zip` package
  + validator (the 2026-06-18 near-term scope); (4) UI upload + self-restart to apply.

Canonical location / next step when actioned:
This entry is the durable requirement. The implementation scope still belongs in a
`docs/ModulePackaging-Plan.md` that must be written and approved before any code. <!-- lint: allow (owner ruled leave-it, 2026-07-27: this is the intended future plan path and is not written yet) --> The
precompiled-vs-runtime decision is to be made when that plan reaches the install/loader stage,
and recorded then. See also `.agents/state.md` "Queued work → Module packaging/import" and the
OPEN versioning-rule blocker (new modules should not bump the base app version), which is the
same end state viewed from the versioning angle.

### 2026-06-29 - M365 group member/owner changes: admin notification only, no affected-user notification

Status: Active

Decision: Adding or removing a member or owner of an M365 group sends an **admin
notification only**. No notification is sent to the affected user (the member/owner being
added or removed).

This **refines** the 2026-06-29 "Notifications are mandatory" decision, which requires
that a permission/access change also notify the affected user. Group member/owner changes
are excluded from that affected-user requirement: per owner, M365 group membership is
typically not tied to permissions, and even when it is, user-facing emails would only
drive tickets. Admin notification and full audit still apply to every change.

Scope: `M365GroupManagement` module member/owner add/remove only. The broader
affected-user notification rule stands unchanged for genuine permission grants/changes in
other modules.

Also recorded here: **GAP 1 from the 2026-06-29 protected-principal sweep is closed for the
principal-write surface.** `M365GroupManagementService` member/owner add/remove now routes
the target identity through an in-service protected-principal gate (`CheckAsync`, fail
closed on Unavailable/Ambiguous/CheckFailed) before any Graph write, mirroring
`GroupManagementService`. Group create/update/delete remain ungated by design (owner:
member/owner only, no protected-*group* gating). Known limitation: the gate resolves
against on-prem AD, so a cloud-only account AD cannot resolve returns NotFound and is
treated as not protected — accepted risk, consistent with on-prem Group Management.

Implemented: module `M365GroupManagement` 1.0.3 → 1.1.0 (app version unchanged); commits
`211c6eb` (service+tests), `03c443a` (UI). Plan:
`docs/M365MemberOwnerManagement-Plan.md` (Status: Implemented).

### 2026-06-29 - Protected principals are off-limits to every mutating module — no carve-outs

Status: Active

Decision:
No module in this tool may perform a mutating operation whose **target** is a protected
principal. This is absolute and covers every change type — account state, permissions, group
membership, directory attributes, password/session state, anything — across every module that
writes, including Emergency Disable, AD Attribute Editor, Group Management, and the planned
M365 member/owner management. If the object being changed is a protected principal (directly,
or transitively via a protected group), the operation must refuse, fail closed, and audit the
denial. There is no group-management exception and no "routine change" exception.

This explicitly resolves the previously-open question (recorded in `.agents/state.md`) of
whether routine group add/remove should be exempt from protected-principal gating. The answer
is **no exemption**: the guard binds to the *target* of the write, uniformly.

Audience rationale (why this is non-negotiable):
This tool's users are L2 helpdesk personnel who are deliberately NOT trusted with direct admin
access. The tool exists to hand them a limited, heavily-logged, notified subset of admin
actions to save L3/L4 from grunt work. The protected-principal guard is what stops an L2
operator from acting on a high-value identity (e.g. adding the CEO to a group that changes his
O365 licensing) in response to an unvetted ticket. Operations against protected principals must
escalate to a real admin outside this tool, not be processable within it.

Scope / mechanism note:
This is a confirmation and scope-clarification of existing intent, not a new rule. The guard is
the existing protected-principal check; this decision forbids treating any mutating module as
out of its scope. The narrow, documented compensation-cleanup bypass allowed by
`docs/ProjectConstitution.md` §Protected Principals (line: "Never bypass ... unless the bypass
is narrowly scoped, documented, and required for compensation cleanup") is unchanged — that is
the only permitted exception and it is not a helpdesk-facing operation.

Canonical location:
`docs/ProjectConstitution.md` §Protected Principals remains the authority. This entry settles
the open scoping question and removes the ambiguity that let group membership be read as a
possible exception.

Reason:
Owner direction 2026-06-29. Settles the open blocker that was gating M365 member/owner
management design.

Supersedes:
The open question in `.agents/state.md` ("should protected-principal checks gate routine group
add/remove?"). Resolved: yes, they gate it, with no carve-out.

### 2026-06-29 - Notifications are mandatory for changes, security reads, and permission changes

Status: Active

Decision:
Notification is no longer discretionary. Three rules now bind every module:

1. **Every mutating action** (any create, write, delete, or change to a user, mailbox,
   group, identity state, access state, password, token/session state, or directory
   attribute) must send an administrator notification.
2. **Every security-sensitive read** (any module whose purpose is to surface
   security-relevant data — e.g. lockout/sign-in/audit lookups, protected-object
   inspection) must send an administrator alert.
3. **Any change to a user's permissions or access** must additionally notify the
   affected user, not only administrators.

Notification is in addition to the mandatory audit event, never a substitute. Notification
failure must not change or mask the backend operation result (same fail-safe rule as audit).

All notification goes through the existing shared `Services/EmailService.cs`
(`SendAdminNotificationAsync` incl. the generic `details`-dictionary overload for arbitrary
module data; `SendUserNotificationAsync` for the affected user). Modules must NOT build a
bespoke mailer, SMTP client, or message template; if a new shape is needed, add an overload
(with a test) to `EmailService` rather than notifying from the module.

Canonical location:
The rule lives in exactly one place — `docs/ProjectConstitution.md` §Auditing And Tracing →
Notifications. `docs/AdminModuleDeveloperGuide.md` §Notifications and `docs/AdminModuleSpec.md`
(audit/trace requirements + new-module checklist) point to it and name the methods; they do
not restate the policy.

Reason:
Owner direction 2026-06-29. Privileged directory/Exchange/Graph changes and security lookups
must be visible to administrators in real time (not only in the audit log), and users must
learn when their own access changes.

Supersedes:
The prior discretionary guidance in `docs/AdminModuleDeveloperGuide.md` §Notifications
("use `EmailService` only when the workflow warrants", "avoid alert fatigue — routine reads
usually should not email anyone", "security-response modules *may* notify"). That text is
removed, not retained, so the deprecated rule does not linger to confuse a future reader.

### 2026-06-26 - SQLite native-lib advisory CVE-2025-6965: upgrade availability re-opened

Status: Active. Former package-availability basis falsified 2026-09-14 as of `a16c316`.
The original 2026-06-26 receipt is preserved verbatim in
`.agents/history/decision-archive.md`; current evidence and next action are owned by
`.agents/state.md` (Blockers / SQLite). Newer packages now exist, but compatibility
with this app has not been verified and no dependency update was performed in the drift sweep.

The owner's document-and-track ruling stands. Do not suppress `NU1903`. A supported
update needs its own plan and verification, including the config-store tests; no new risk
acceptance or code authority is inferred from this records correction.

### 2026-06-18 - SQLite config store: three design decisions resolved; module packaging direction set

Status: Active

These resolve the three Phase-A-blocking open questions in `docs/SqliteConfigStore-Plan.md`
§9 and set the scope direction for the (not-yet-written) module packaging plan. The
SQLite plan stays **Draft** — these decisions unblock it but the owner has not yet given a
go/no-go to execute the migration. When the plan is next revised, fold these in and record
them in its review log.

1. **DB location (SqliteConfigStore-Plan §3b): Option A — the SQLite file lives in the
   existing `config/` directory, one DB per environment (dev and prod each have their own).**
   A single DB shared between dev and prod was considered and rejected: it would make dev
   config changes instantly live in prod (removing the test-then-promote safety net), cannot
   hold per-environment values (security groups, connection targets, `PathBase`), and a
   network-shared single-file SQLite DB reintroduces exactly the file-locking/corruption
   failure class the migration exists to kill. The "seamless config sync" goal is met instead
   by the existing dev→prod promote plus the planned prod→dev `-Refresh` flag, not by a shared
   file. This keeps the `config/` deploy protections (robocopy `/XD config`, ACL, snapshot)
   doing real work; §7's conditional retirement list resolves to the Option-A column.

2. **Data-access library (§3a): `Microsoft.Data.Sqlite` + thin hand-written repositories.
   No Entity Framework.** The config data is key/value pairs and short lists with no
   relational structure, so EF's ORM advantages buy nothing while adding a heavy dependency,
   generated migration artifacts needing their own review, and behavior-hiding "magic" that
   works against the Constitution's inspectable-behavior bias. Revisit only if module
   packaging later makes the data model genuinely relational.

3. **Cache-invalidation model (§5B.2): add a cheap DB change-token (the recommended
   `schema_meta` counter), not the accept-the-staleness option.** Without it, an out-of-band
   write (the prod→dev `-Refresh` tool, a manual DB edit) leaves the running app serving stale
   cached config for up to 30 s — or, for section access, until an app restart — because the
   writing path is no longer the same instance that holds the cache. The change-token lets
   readers detect a change and refresh immediately, and also makes the corrupt-store probes
   cheap.

4. **Module packaging direction** (NOTE: the whole packaging effort is now **deferred** as
   low-value/high-cost — see the 2026-07-22 entry at the top of this file; the scope below is
   retained only as the record of intended shape if revived): **modules are distributed as `.zip`
   packages with a validation tool, but installation still requires a back-end rebuild. Runtime
   upload / assembly loading is explicitly deferred.** Runtime `.zip`-upload-no-rebuild is the hardest
   and riskiest version of the feature (Blazor pages/routes are compiled ahead of time, and it
   means loading arbitrary code into a privileged Exchange/AD admin tool), and it solves a
   problem the owner does not currently have — the owner is the only module author today, and
   it was always a "nice to have for other deployments." "Modules are compiled extensions
   installed by an administrator" is a defensible enterprise posture (cf. SharePoint `.wsp`,
   Dynamics plugins, much of the Jenkins/Jira/Grafana ecosystem). Scope when the plan is
   written: documented `.zip` package structure + `tools/validate-module-package.ps1` as the
   gate; defer runtime upload until a real second deployment needs it. This sets the scope for
   the future `docs/ModulePackaging-Plan.md`; that plan is still required before any <!-- lint: allow (owner ruled leave-it, 2026-07-27: this is the intended future plan path and is not written yet) -->
   implementation.

Reason:
Owner decisions 2026-06-18 after a plain-English walkthrough of the four open questions.

### 2026-06-18 - Conference Rooms: cloud-only room lists, and partial-write is reported

Status: Active

Decision (two related owner decisions, 2026-06-18):

1. **Room lists are created in the cloud with no organizational unit.** The Conference Rooms
   module creates room lists via Exchange Online `New-DistributionGroup` and must NOT pass
   `-OrganizationalUnit`. Exchange Online does not understand on-prem AD OUs, so passing one
   (the legacy `RoomListOU` value, an on-prem OU path) made every room-list creation fail with
   "organizational unit not found." The room list is consequently a cloud-only object — it is
   NOT created on-prem and synced up like the company's other distribution lists. The owner
   accepts this divergence given on-prem Exchange is slated for decommission (next year). The
   `RoomListOU` config field was removed entirely.

2. **Partial Room Finder applies are reported and audited as partial, not as plain failures.**
   A Room Finder apply performs several non-transactional writes across EXO and on-prem AD
   (`Set-Place`, then `Set-ADUser` for City/State/Country, then timezone, then room-list
   membership). If an early step commits and a later one fails, the room is left
   half-configured. The result must surface this explicitly (`RoomOperationResult.Partial`, a
   "PARTIAL" UI badge, and the partial detail in the audit record) rather than reporting a bare
   failure that implies nothing changed. Re-running a row is safe (every step is idempotent).
   The inherent residual — a genuine write failure after the pre-mutation preflight passes —
   cannot be eliminated (two systems, no distributed transaction); it is made visible instead.

Reason:
Both came out of a live owner test (2026-06-18) of Room Finder apply plus a follow-up review.
This also corrects an earlier `docs/CommitReview-2026-06-17.md` note that described the
partial-write residual as "accepted" when no such decision existed.

Scope guard:
This does NOT change how other DLs are managed, and does not endorse cloud-first creation for
anything beyond Conference Rooms room lists.

### 2026-06-17 - TestAccountPool module removed

Status: Active

Decision:
The TestAccountPool module is removed from the application as of app version `2.3.10`.
Deleted: `Services/TestAccountPoolService.cs`, `Services/TestAccountPoolCleanupWorker.cs`,
`Components/Pages/TestAccountPool.razor`, `ExchangeAdminWeb.Tests/TestAccountPoolServiceTests.cs`,
the catalog descriptor in `Modules/ModuleCatalog.cs`, both `Program.cs` registrations, the
orphaned `EmailService.SendTestAccountPasswordAsync` helper, and the config seeds/docs in
`tools/Install-ExchangeAdminWeb.ps1`, `appsettings.json.sample`, and `README.md`.
`ModuleCatalogTests` counts updated (modules 21→20, configurable aliases 28→27).

Reason:
Owner direction (2026-06-15): the module was never activated in any environment and is not
wanted. It was also the application's only `AddHostedService` — a background timer
(`TestAccountPoolCleanupWorker`) that mutated AD unattended under a synthetic
`"System"/"BackgroundWorker"` actor with no ticket — which was the architectural oddity that
prompted removal. Removing it also retires the background-thread variant of the
connection-lifetime hazard tracked in `docs/SqliteConfigStore-Plan.md`.

Notes:
Historical audit-log entries (`TestAccountPool_*`) are intentionally NOT scrubbed. Historical
docs (`docs/Incident-*`, `docs/ProdReadiness*`) keep their references as history. This entry is
the authority for the removal; `docs/FutureModules-Plan.md` and `docs/SqliteConfigStore-Plan.md`
point here.

### 2026-06-17 - Credentials live in the deployment's PAM solution, not hardcoded to Delinea

Status: Active; operative rule is in `docs/ProjectConstitution.md` (Credential Isolation).
Settled rationale and supersession history are archived verbatim in
`.agents/history/decision-archive.md` (2026-09-14 sweep).

### 2026-06-10 - Adopt the standard `.agents/` governance layout

Status: Active; operative rule is in `AGENTS.md` (Invariants and Source of Truth).
Settled rationale and supersession history are archived verbatim in
`.agents/history/decision-archive.md` (2026-09-14 sweep).

### 2026-06-12 - Runtime config and operational data move to SQLite

Status: Active (direction approved by Michael; implementation gated on approval of
`docs/SqliteConfigStore-Plan.md`, drafted 2026-06-15, Status: Draft)

Decision:
The scattered JSON fragments under `config/` (and runtime-editable operational state
generally) will move to a SQLite database stored outside the deploy target. SQL
Express was considered and rejected: no ADI policy mandates a managed SQL instance for
this app, the app is single-writer/single-box by design, and SQLite removes ops
surface (no service, file-copy backups, `VACUUM INTO` snapshots for the planned
prod<->dev config copy tool) where Express adds it.

Consequences:
- New modules and new app settings self-register idempotently at startup
  (INSERT-if-missing with defaults). This RELAXES the 2026-06-12 owner direction
  "the app must never write enablement state at startup" for NON-DESTRUCTIVE seeding
  only; destructive startup writes remain forbidden.
  **IN EFFECT as of SQLite Phase C (app 2.3.20, 2026-06-18):** `ModuleEnablementService.
  SeedMissingModules()` runs at startup (Program.cs, after the migrator) and does
  `INSERT ... ON CONFLICT DO NOTHING` for catalog modules with no row, at their
  `EnabledByDefault`. It reads-first and only opens a write transaction when something is
  actually missing (a no-op seed bumps no change token). It NEVER modifies an existing row
  (the 2026-06-12 incident regression — ExchangeOnline flipped to false — is guarded by a
  test that fails if seeding becomes destructive), and it no-ops on a corrupt/unreadable
  store. The original "no *destructive* startup write" direction stands unmodified.
- Much of the 2026-06-12 incident hardening (config/ backup snapshots, post-deploy
  drift check, robocopy config exclusions, corrupt-JSON guards) becomes
  obsolete-by-design; the plan must enumerate what is retired vs kept.

Reason:
Repeated config-file incidents (see `docs/Incident-2026-06-12-DevConfigLoss.md`) all
stem from many loose files shared by the app, deploys, and humans. Transactional
single-file storage retires the corrupt-file and partial-state failure classes
structurally.

### 2026-06-10 - `docs/ProjectConstitution.md` remains the highest engineering authority

Status: Active; operative rule is in `.agents/repo-guidance.md` (Authority Order).
Settled rationale and supersession history are archived verbatim in
`.agents/history/decision-archive.md` (2026-09-14 sweep).
