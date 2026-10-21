# Review Status Index

One row per admitted finding. Details live in the finding record, never here.

Legend: `[ ]` open - `[~]` in progress - `[v]` verified/awaiting merge -
`[d]` merged/awaiting deletion - `[x]` complete - `[!]` contested - `[-]` declined.

| | ID | Severity / impact | Status | Branch | Reviewer |
|---|---|---|---|---|---|
| [x] | [gps-1](findings/gps-1.md) | LOW - prompt points the operator at a surface that no longer reports job completion | Complete | - | codex / gpt-5.5-dzs / xhigh |
| [x] | [gps-2](findings/gps-2.md) | LOW - four changed module pages render a stale version number | Complete | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [edl-1](findings/edl-1.md) | MEDIUM - a declined lockdown renders as a red failure row | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [edl-2](findings/edl-2.md) | MEDIUM - a failed Notes stamp is reported inside a green LockdownMove row | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [edl-3](findings/edl-3.md) | MEDIUM - a fresh lookup inherits the previous account's lockdown opt-out | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-1](findings/prog-1.md) | MEDIUM - the progress guard does not prove the slow derive is inside the window | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-2](findings/prog-2.md) | MEDIUM - DOM @onchange handlers are discovered by nothing | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [leak-1](findings/leak-1.md) | HIGH - the recurrence guard is bypassable three ways | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-3](findings/prog-3.md) | MEDIUM - a string literal can still counterfeit the progress proof | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-4](findings/prog-4.md) | MEDIUM - two lexical blind spots remain in the progress scanner | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-5](findings/prog-5.md) | MEDIUM - CloudPasswordReset's window does not pin its delivery tail | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-6](findings/prog-6.md) | LOW - two page tests anchor on prose and fail correct code | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-7](findings/prog-7.md) | LOW - a ClickGate exemption claims a read-back that both ExecuteOnPrem finallys do | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-8](findings/prog-8.md) | LOW - a third guard anchored on prose, dodged by luck of wording | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-9](findings/prog-9.md) | MEDIUM - the progress yield opened a null window on MessageTrace.s CSV export | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-10](findings/prog-10.md) | HIGH+MEDIUM - both Roslyn guards fooled by a local function or an overload | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-11](findings/prog-11.md) | MEDIUM - latent; three pages' @code blocks parse with errors and both guards read the recovery | Verified | - | the prog-10 implementer; codex confirmed |
| [v] | [rc-1](findings/rc-1.md) | HIGH - Remove completed could remove more mailboxes than the confirm bar counted | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [rc-2](findings/rc-2.md) | MEDIUM - a mailbox ticked in another batch silently narrowed Remove completed | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [rc-3](findings/rc-3.md) | MEDIUM - changing the mailbox filter mid-confirmation hid the rows Remove completed would remove | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [rc-4](findings/rc-4.md) | LOW - the tooltip described a narrowing the control was not doing | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [rc-5](findings/rc-5.md) | MEDIUM - a tick during confirmation was ignored by Remove completed instead of withdrawing it | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [rc-6](findings/rc-6.md) | LOW - pre-existing; a skipped row printed the placeholder instead of the status | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [mde-1](findings/mde-1.md) | HIGH - a missing AD module removes the whole device inventory to report a Site problem | Verified | - | codex / gpt-5.5 / xhigh |
| [v] | [mde-2](findings/mde-2.md) | MEDIUM - a failed observer lookup renders as data, with nothing on the page saying so | Verified | - | codex / gpt-5.5 / xhigh |
| [v] | [mde-3](findings/mde-3.md) | MEDIUM - the upload parse drops rows before the resolver, so they can never be reported | Verified | - | codex / gpt-5.5 / xhigh |
| [v] | [mde-4](findings/mde-4.md) | MEDIUM - the observer failure count is dropped on a mixed partial run | Verified | - | codex / gpt-5.5 / xhigh |
| [v] | [mde-5](findings/mde-5.md) | MEDIUM - an all-invalid upload still cannot reach its errors CSV | Verified | - | codex / gpt-5.5 / xhigh |
| [v] | [mde-6](findings/mde-6.md) | MEDIUM - a whole-run enrichment failure makes the seen-by filter empty the table | Verified | - | codex / gpt-5.5 / xhigh |
| [v] | [mde-7](findings/mde-7.md) | MEDIUM - mixed and failed Comms-10k upload states still cannot reach the errors CSV | Verified | - | codex / gpt-5.5 / xhigh |
| [v] | [mde-8](findings/mde-8.md) | MEDIUM - a blank device id leaves every enrichment cell blank, so the filter hides the device | Verified | - | codex / gpt-5.5 / xhigh |
| [v] | [mde-9](findings/mde-9.md) | MEDIUM - two rows of the same non-matching address are reported as one | Verified | - | codex / gpt-5.5 / xhigh |
| [v] | [cdr-c9](findings/cdr-c9.md) | HIGH - a failed queued mutation replays during retry or read-back | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-c1](findings/cdr-c1.md) | HIGH - configurable target and DN-addressed writes can retarget the broadcast replace | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-c10](findings/cdr-c10.md) | MEDIUM - repeated batch rows reject one person as ambiguous | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-c3](findings/cdr-c3.md) | HIGH - partial resolution writes a shortened list and refusals lose their audit/reason | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-c4](findings/cdr-c4.md) | HIGH - replacement can write a member set the operator did not approve | Independent review P2 resolved by coder proof; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-c11](findings/cdr-c11.md) | MEDIUM - interrupted replaces report requested or zero counts as written | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-c5](findings/cdr-c5.md) | HIGH - background replace submitters cannot see or manage durable outcomes | Independent review P1 resolved by coder proof; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-c2](findings/cdr-c2.md) | HIGH - queued replace omits current authorization and module enablement at write dispatch | Independent review P2 resolved by coder proof; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-c7](findings/cdr-c7.md) | HIGH - overlapping handlers release shared busy state and failed CSV uploads publish a usable prefix | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-c8](findings/cdr-c8.md) | MEDIUM - downloads export executable cells and post-delivery audit faults report failure | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-c6](findings/cdr-c6.md) | MEDIUM - editable ticket display diverges from immutable approved job/audit metadata | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-d8](findings/cdr-d8.md) | MEDIUM - numeric OS builds disappear and escaped site names are truncated | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-d10](findings/cdr-d10.md) | MEDIUM - malformed entries make a capped device page appear complete | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-d9](findings/cdr-d9.md) | MEDIUM - export failures escape without a handled result or failed audit | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-d4](findings/cdr-d4.md) | MEDIUM - inventory/export audits omit applied location filters, degradation and final counts | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-d2](findings/cdr-d2.md) | MEDIUM - concurrent singleton inventory calls report another run's hunting failure reason | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-d7](findings/cdr-d7.md) | HIGH - unbounded enrichment delays already-complete inventory and repeats systemic authorization failures | Independent CHANGES REQUIRED; its one P2 corrected and closed on coder-side proof | master | original 421588e verdict preserved; 3 guards / 6 regressions / 8 failures / 991 affected / 4,227 full passes + 3 skips |
| [v] | [cdr-d1](findings/cdr-d1.md) | MEDIUM - opening Defender starts an unrequested inventory read and renders no initial state | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-d3](findings/cdr-d3.md) | MEDIUM - overlapping Load/Download events bypass page-wide busy state | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [cdr-d5](findings/cdr-d5.md) | PERFORMANCE - repeated subnet parsing and filtered-list allocation | Verified on master; push pending | master | native / gpt-6-astra / max |
| [~] | [cdr-followup-1](findings/cdr-followup-1.md) | LOW - resolve-stage prose compares member count with address count | Merged; codex review pending | - | native / gpt-6-astra / max |
| [~] | [cdr-followup-2](findings/cdr-followup-2.md) | LOW - the same address-vs-member count mix-up in the Comms-10k validation banner and the service resolve message (reaches refusal reason, audit, notification) | Merged; codex review pending | - | implementer |
| [v] | [psl-1](findings/psl-1.md) | HIGH - failed optional Exchange command can replay in the next calendar-permission operation | Verified on master; push pending | master | native / gpt-6-astra / max |
| [v] | [psl-2](findings/psl-2.md) | MEDIUM - licensing failure can select an earlier row's error | Closed; reviewed clean | - | native / gpt-6-astra / max audit |
| [v] | [psl-3](findings/psl-3.md) | MEDIUM - DHCP mutation report can include stale discovery errors | Closed; reviewed clean | - | native / gpt-6-astra / max audit |
| [v] | [psl-4](findings/psl-4.md) | MEDIUM - exceptional pooled queries lose connection/precheck classification | Closed; reviewed clean | - | native / gpt-6-astra / max |
| [v] | [psl-5](findings/psl-5.md) | LOW - Self-Service Groups: after one DACL read failed, the next group's clean read was reported as denied (stale error records) | Closed; reviewed clean | - | implementer |
| [v] | [aud-a1](findings/aud-a1.md) | HIGH - module-admin grants match a same-named group in another domain | Verified | - | implementer-verified; review findings repaired |
| [v] | [aud-a3](findings/aud-a3.md) | HIGH - the protected-group check asks the host domain, so a foreign-domain target reads as unprotected | Verified | - | implementer-verified; review findings repaired |
| [v] | [aud-a2](findings/aud-a2.md) | HIGH - a secondary alias bypasses protected-user rules in three modules | Verified | - | implementer-verified; review findings repaired |
| [v] | [aud-a4](findings/aud-a4.md) | HIGH - Self-Service Groups ignores denies carried by the caller's groups | Verified | - | implementer-verified; review REQUIRED CHANGES - see aud-a4b |
| [v] | [aud-a1b](findings/aud-a1b.md) | MEDIUM - group-SID entry validates the shape, not the object; affects both access and admin controls | Closed; reviewed clean | - | codex-commercial / gpt-6-astra / max |
| [v] | [aud-a3b](findings/aud-a3b.md) | HIGH - three same-forest fail-open holes in the protected-group membership check | Closed; reviewed clean | - | codex-commercial / gpt-6-astra / max |
| [v] | [aud-a3c](findings/aud-a3c.md) | HIGH - universal groups of another domain were answered by the target domain's DC, which holds their links only if it is a global catalog | Closed; reviewed clean | - | implementer |
| [~] | [aud-a2b](findings/aud-a2b.md) | HIGH - three ways a protected mailbox is still reachable; the checked object is not provably the written one | Merged; codex raised a2c, fix in progress | - | codex-commercial / gpt-6-astra / max |
| [~] | [aud-a2c](findings/aud-a2c.md) | HIGH - the Exchange-target check binds the AD object by canonical SMTP address, so a non-recipient AD object carrying that address is what gets checked while Exchange writes the real mailbox | On master `7f1a0e6e`; codex review pending; dev check owed | - | codex / gpt-5.5-dzs / xhigh |
| [ ] | aud-a2d | MEDIUM - the write paths still send the operator's typed string after an identifier-bound check (three pages and their bulk paths) | Admitted (a2b record gap); queued behind aud-a7b merge | - | implementer |
| [v] | [aud-a5](findings/aud-a5.md) | HIGH - lockout remediation logs off by bare username and can sign out the wrong domain's account | Verified `cf3ed28` | - | implementer-verified; fix review in flight |
| [v] | [aud-a4b](findings/aud-a4b.md) | HIGH - the self-service DACL gate is not a faithful Windows access check; 7 findings | Closed - finding 7 fixed; reviewed clean | - | codex-commercial / gpt-6-astra / max |
| [v] | [aud-a8](findings/aud-a8.md) | HIGH - Conference Rooms jobs keep writing after the module is disabled | Verified `4ce4680`; guard widened `6d5ef9a` | - | codex-commercial / gpt-6-astra / max - no product defect, test gap closed |
| [v] | [aud-a7](findings/aud-a7.md) | HIGH - Out of Office checks one mailbox and can write another | Verified `da2c917`; repair `e8e8344` | - | codex-commercial / gpt-6-astra / max - CHANGES REQUIRED, stuck busy flag, closed |
| [v] | [aud-a6](findings/aud-a6.md) | HIGH - a borrowed EXO connection can target the previous organization | Verified `1dcc1a1`; repair `f71d60a` | - | codex-commercial / gpt-6-astra / max - CHANGES REQUIRED, fix was incomplete, closed |
| [v] | [aud-a6b](findings/aud-a6b.md) | HIGH - Disconnect-ExchangeOnline with no selector clears a process-wide static connection map, so destroying one retired runspace breaks every other borrower | Fixed - disconnect by ConnectionId, connects serialised; review + live acceptance pending | - | codex-commercial / gpt-6-astra / max |
| [~] | [aud-a7b](findings/aud-a7b.md) | MEDIUM - Out of Office captures only the mailbox; state, messages, schedule and ticket are still post-await live reads | Merged; codex raised a7d, fix in progress | - | codex-commercial / gpt-6-astra / max |
| [v] | [aud-a7c](findings/aud-a7c.md) | LOW - Out of Office CheckStatus read the mailbox for its audit and error result after the await | Closed; reviewed clean | - | implementer |
| [ ] | aud-a7d | LOW - the affected-user notification says "enabled" for a Scheduled auto-reply and names no schedule | Admitted (codex services-batch-2); fix in progress | - | codex / gpt-5.5-dzs / xhigh |
| [-] | [aud-a5b](findings/aud-a5b.md) | MEDIUM - blocked lockout targets vanish from the operator's report when any other target survives | DEFERRED - module parked (owner 2026-10-19) | - | implementer |
| [-] | [aud-a5c](findings/aud-a5c.md) | HIGH - a domain-qualified lockout target still resolves to the wrong domain's account; event 4740 carries TargetSid and it is discarded | DEFERRED - module parked (owner 2026-10-19) | - | codex-commercial / gpt-6-astra / max |
| [v] | [aud-a27](findings/aud-a27.md) | HIGH - the emergency-disable notice went to the global admin list instead of the configured security recipient, and the page claimed the security team had been notified whatever happened | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a26](findings/aud-a26.md) | MEDIUM - undoing an AD attribute edit notified nobody, because dispatch lives in the forward-edit page and undo does not go through it | Verified and fixed; review pending | - | implementer |
| [ ] | [aud-a26b](findings/aud-a26b.md) | MEDIUM - dispatch belongs to the page rather than to SaveAsync, so every caller must remember and a third one will not | Recorded follow-up; not approved | - | implementer |
| [v] | [aud-a23](findings/aud-a23.md) | HIGH - a partial cloud permission change notified neither the affected user nor the mailbox owner, because the gate was overall success and a partial is not one | Verified and fixed; review pending | - | implementer |
| [~] | [aud-a23b](findings/aud-a23b.md) | LOW - the on-prem path still parses applied rights out of Detail, which means something else on cloud results | Merged; codex review pending | - | implementer |
| [v] | [aud-a24](findings/aud-a24.md) | HIGH - a bulk mailbox or calendar run audited every row and sent one admin summary, and told the affected trustees and owners nothing | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a37](findings/aud-a37.md) | MEDIUM as a document, HIGH when used - the incident recovery guide described the retired per-instance storage, told the operator to stop one pool of two, and offered a delete-and-rebuild step that bricks a configured instance | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a35](findings/aud-a35.md) | HIGH - an empty catch around the app-pool stop let robocopy /MIR replace the binaries of a RUNNING application; three sites, one of which the audit missed | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a34](findings/aud-a34.md) | HIGH - re-running the installer without -ServiceAccount silently moved the pool off its domain account AND re-granted every folder ACL to the virtual account | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a36](findings/aud-a36.md) | HIGH - the generic installer mirrored new binaries over an instance and never snapshotted the config database those binaries migrate on first start | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a38](findings/aud-a38.md) | HIGH - a restart failure threw out of `finally` and replaced the promotion/rollback verdict; the restart was also never verified, so a successful promotion could leave prod down with a success message | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a38b](findings/aud-a38b.md) | MEDIUM - the promotion script now confirms its pool started but still assumes its pool stopped (bare Stop-WebAppPool + sleep) | Closed; reviewed clean | - | implementer |
| [~] | [aud-a38c](findings/aud-a38c.md) | MEDIUM - promote never checked which pool serves prod; a wrong but existing pool was stopped and dev mirrored over a running prod | Merged; codex raised a38f, fix in progress | - | implementer |
| [v] | [aud-a38d](findings/aud-a38d.md) | MEDIUM - under PowerShell 7 the IIS: drive is absent, so promote stopped and mirrored then reported prod DOWN as "pool does not exist" | Closed; reviewed clean | - | implementer |
| [ ] | aud-a38f | MEDIUM - the serving-pool check matches exact roots only; a nested IIS app under the mirrored tree served by another pool is overwritten live | Admitted (codex services-batch-2); fix in progress | - | codex / gpt-5.5-dzs / xhigh |
| [~] | [aud-a38e](findings/aud-a38e.md) | MEDIUM - the cutover did a bare stop plus sleep before snapshotting dev's live database | Merged; codex review pending | - | implementer |
| [~] | [aud-a38h](findings/aud-a38h.md) | MEDIUM - the cutover's restart reported started when IIS accepted the request; a pool that never came back went unnoticed on the failure path | Merged; codex review pending | - | implementer |
| [ ] | [aud-a38g](findings/aud-a38g.md) | MEDIUM - the cutover never checks the named pool serves the instance (a38c class); wrong-but-existing pool means a stale snapshot and the restore commands copy it over the live DB | Recorded; approved; needs master's Assert-AppPoolServesPath - queued | - | implementer |
| [v] | [aud-a39](findings/aud-a39.md) | HIGH - the cutover accepted a shared database inside a publish tree, which the next deploy of that instance deletes with robocopy /MIR, taking every instance's authorization store with it | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a40](findings/aud-a40.md) | MEDIUM - the cutover waited for a log line the app only writes when a REQUEST initialises it, so a healthy idle instance failed the final check | Verified and fixed; review pending | - | implementer |
| [v] | [aud-pipeline-provenance](findings/aud-pipeline-provenance.md) | HIGH for what it guards - the stale-build check skipped its comparison whenever either side was missing and printed "Dev commit matches source HEAD" anyway | Verified and fixed; review pending | - | implementer |
| [v] | [aud-whatif-directory](findings/aud-whatif-directory.md) | NONE - the audit claim that -WhatIf creates a directory before ShouldProcess does NOT reproduce (New-Item inherits WhatIf); disproved against unmodified source, ordering hardened anyway | False positive; ordering hardened; review pending | - | implementer |
| [v] | [aud-graphapps-stranding](findings/aud-graphapps-stranding.md) | HIGH blast radius - a failure on the second registration left the first in the tenant with a client secret that was never displayed and cannot be retrieved | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a25](findings/aud-a25.md) | MEDIUM - a security-group membership change never reached the group manager; REVIEWED by codex pre-landing, which found 2 High + 2 Medium defects (including a managedBy group address that would have emailed every member) - all remediated | Verified, fixed and reviewed | codex | implementer |
| [v] | [aud-header-bounds](findings/aud-header-bounds.md) | MEDIUM - ReceivedHeaders bypassed every cap in the model (5,000 pasted hops produced 5,000 parsed hops), and the caps that did exist truncated silently so a cut-short routing path read as complete | Verified and fixed | - | implementer |
| [v] | [aud-operational-hardening](findings/aud-operational-hardening.md) | MEDIUM - CI never installed sqlite3 so every config-backup test skipped silently, nothing ever ran the 5.1-critical scripts under 5.1, and a broken test file dropped ~80 tests while reporting green | Two of three fixed; ACL narrowing opt-in pending a host check | - | implementer |
| [v] | [cdt-1](findings/cdt-1.md) | MEDIUM - backup cleanup after a same-destination race deletes the other run's verified snapshot | Closed on guard proof | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [cdt-2](findings/cdt-2.md) | MEDIUM - installer mirrors staging even when ExchangeAdminWeb.dll is missing, if there is no database to back up | Closed on guard proof | - | codex / gpt-5.5-dzs / xhigh |
| [-] | cdt-3 | plan-mode paths read a database through the app tool - pre-existing and deliberate (scdi-3); declined, see `cdt-3.contested.md` | Declined | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [cdt-4](findings/cdt-4.md) | LOW - two Pester fixtures pick an arbitrary old DLL from $PSHOME | Closed on guard proof | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [cdt-5](findings/cdt-5.md) | MEDIUM - a fresh install or -Force deploy passes an unset config directory to the jobs check and dies on a binding error | Closed on guard proof | - | implementer |
| [v] | [cdt-6](findings/cdt-6.md) | LOW - a failure between the publish and the mirror (the pre-deploy backup included) left the staging publish and its appsettings.json behind | Closed on guard proof | - | implementer |
| [v] | [cdt-7](findings/cdt-7.md) | MEDIUM, test-only - the C7 yield-hold test context drained its queue once, so a late continuation hung the suite for hours | Merged; closed on guard proof | - | implementer |
| [v] | [cdt-8](findings/cdt-8.md) | HIGH - deploy -Force regenerated appsettings.json without ConfigStore:Path, silently repointing a shared-DB instance at an empty per-instance store | Closed; reviewed clean | - | implementer |
| [v] | [cdt-9](findings/cdt-9.md) | HIGH - deploy -Force took no verified backup; it raw-copied a live WAL database (4 KB main file, all rows in the -wal) | Closed; reviewed clean | - | implementer |
| [v] | [cdt-10](findings/cdt-10.md) | MEDIUM - config backups copied the jobs and usage databases, keeping job payloads and usage data past their stated retention | Closed; reviewed clean | - | implementer |
| [ ] | cdt-16 | HIGH, pre-existing - a failed robocopy mirror still restarts the pool on a partial publish, and a restart throw in the finally masks the mirror failure | Admitted (codex ops-batch-1); fix in progress | - | codex / gpt-5.5-dzs / xhigh |
| [ ] | cdt-17 | MEDIUM, pre-existing - an invalid ConfigStore:Path (relative, drive-relative, UNC) passes the backup gate and backs up the wrong file while the app refuses it at start | Admitted (codex ops-batch-1); fix in progress | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [aud-a28](findings/aud-a28.md) | MEDIUM - fifteen refusal paths across emergency disable and attribute edit returned before recording anything, so a blocked attempt left no durable audit | Verified and fixed; review pending | - | implementer |
| [ ] | [aud-a28b](findings/aud-a28b.md) | MEDIUM - SaveAsync has no behavioural coverage; a version that refused every call passed the full suite | Recorded follow-up; not approved | - | implementer |
| [v] | [aud-a30](findings/aud-a30.md) | MEDIUM as deployed, HIGH if enabled - a ticket number reached ServiceNow's query grammar, and the row that came back was never checked against the one requested | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a21-a22](findings/aud-a21-a22.md) | HIGH - an upstream ARC pass masked the receiving system's own SPF/DKIM/DMARC failure; the bulk-complaint finding never fired | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a20](findings/aud-a20.md) | HIGH - diagnostics evicted audit records from the event log, because one FIFO was trimmed by insertion order before the timestamp sort | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a10](findings/aud-a10.md) | MEDIUM - a typed UPN was used where Graph requires an object id, so every add-by-name failed and the error blamed permissions | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a9](findings/aud-a9.md) | HIGH - undo resolves a reusable UPN with no immutable id, so it can restore one person's values onto another's account | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a13-a16](findings/aud-a13-a16.md) | HIGH - the stale-target group: an MFA reset could target a different account than the one reviewed; a licence apply could drop its required notification after the write; an attribute save could be aimed at a user a concurrent search had swapped in; a reused country picker saved one record's list onto another | Verified and fixed, all four |  - | implementer |
| [v] | [aud-a32](findings/aud-a32.md) | MEDIUM - a cancelled export is emailed as ready and then refused by the page as never produced | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a17](findings/aud-a17.md) | HIGH - a Graph collection that failed partway through was returned as if complete, in two services | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a18](findings/aud-a18.md) | HIGH - a failed calendar read renders as "No calendar delegates" and audits as a successful report | Verified and fixed; review pending | - | implementer |
| [~] | [aud-a29b](findings/aud-a29b.md) | HIGH - revocation is not observable at all while a circuit stays open; the cached principal never refreshes | Merged; codex raised a29c/d/e, fix in progress; live dev check owed | - | codex-commercial / gpt-6-astra / max |
| [~] | [aud-a29c](findings/aud-a29c.md) | HIGH - the circuit-open revalidation runs in the background, so a stale-ticket operator can act before it completes | Merged; codex review pending; dev check owed | - | codex / gpt-5.5-dzs / xhigh |
| [~] | [aud-a29d](findings/aud-a29d.md) | HIGH - removing a grant from section_access drops it from the compared set, so an already-open page through that grant is never revoked | Merged; codex review pending; dev check owed | - | codex / gpt-5.5-dzs / xhigh |
| [~] | [aud-a29e](findings/aud-a29e.md) | MEDIUM - static AdminGroups with non-domain SIDs (BUILTINAdministrators) can authorize but are skipped by revalidation | Merged; codex review pending; dev check owed | - | codex / gpt-5.5-dzs / xhigh |
| [~] | [aud-a29f](findings/aud-a29f.md) | HIGH - the servicer gate ran IsInRole over every stored value, so a leftover name or a well-known SID could authorize servicing of protected principals and revalidation never saw it | Merged; codex review pending | - | implementer |
| [ ] | aud-a29g | MEDIUM, design - prerendered pages still render and run OnInitialized under a stale ticket (no handler can run); closing it costs a directory read per page request | Recorded; OWNER DECISION (cost) | - | implementer |
| [ ] | [aud-a33b](findings/aud-a33b.md) | HIGH, pre-existing - the SID data migration replaces grants from a pre-resolution snapshot and can restore a revoked grant | Recorded; not started | - | codex-commercial / gpt-6-astra / max |
| [ ] | [aud-a33c](findings/aud-a33c.md) | MEDIUM, pre-existing - the Graph key remap decides outside its write transaction and can revert a concurrent save | Recorded; not started | - | codex-commercial / gpt-6-astra / max |
| [v] | [aud-a29](findings/aud-a29.md) | HIGH - revoked Bulk Jobs access stays usable through an already-open page, including cross-module cancel and delete | Verified and fixed; review pending | - | implementer |
| [v] | [aud-a33](findings/aud-a33.md) | HIGH - two instances starting together can repeat a non-idempotent migration step and abort one startup | Verified and fixed; review pending | - | implementer |
| [~] | [aud-a6c](findings/aud-a6c.md) | MEDIUM, pre-existing - the EXO pool's Dispose/CleanupIdle can leave connections in a stopped pool, and a failed disconnect is silent | Merged; codex raised a6e, fix in progress | - | codex-commercial / gpt-6-astra / max |
| [v] | [aud-a6d](findings/aud-a6d.md) | LOW, latent - BorrowAsync destroyed a stale runspace via Task.Run with an already-cancelled token, so the delegate never ran and the connection was stranded | Closed; reviewed clean | - | implementer |
| [ ] | aud-a6e | MEDIUM - a borrow queued on a full pool passes the stop check, then connects after Dispose ran | Admitted (codex services-batch-2); fix in progress | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [aud-a31](findings/aud-a31.md) | HIGH - one operator expanding a batch deletes every operator's stored migration reports, instance-wide | Verified; fixed `7316dd9` + `6a26f21`; review pending | - | implementer |
| [v] | [flake-1](findings/flake-1.md) | MEDIUM - a cancelled caller took the token lock and the cached token and built its request anyway, in three clients | Verified `fc70130`; reviewed, record corrected `0ab6b75` | - | codex-commercial / gpt-6-astra / max |
Both from one Change review of `ad3c1e9..e5f9e5c` (the global-progress sweep), 2026-10-02.
Raw reviewer output: `gps-sweep.result.json`; prompt and schema alongside it.

`edl-1` and `edl-2` are both from one Change review of `1bbbb73..1313fb4` (queue item
18 slice S1), 2026-10-05. Raw reviewer output: `ed-s1.result.json`; prompt and schema
alongside it.

`edl-3` came from the S2 Change review of `2d0494c..b164d7b`, 2026-10-05. Raw output:
`ed-s2.result.json`. Its first fix was reopened on the GUARD rather than the fix and
repaired in `7e11c64`; the repair-delta round is `edl-3r.result.json`.

All three `edl-*` findings are `[v]` and not `[x]`: each is verified on main. They move to
`[x]` once the range carrying them has reached the remotes - check that live, it is not
recorded here.

`rc-1` to `rc-6` are the Migration **Remove completed** control, one work stream reviewed by
codex in four rounds over `029d9d0..7042b91` (raw envelopes `rcimpl.result.json`,
`rcimpl2.result.json`, `rcimpl3.result.json`, `rcimpl4.result.json`, prompts alongside).
Round 1 raised `rc-1` and `rc-2`, round 2 `rc-3` and `rc-4`, round 3 `rc-5` and `rc-6`, and
round 4 returned **clean**. Each was fixed in its own commit. `rc-6` is pre-existing - it is
at the base of the range in four places - and is recorded here because it was found and fixed
in this stream, not because this stream caused it. A FIFTH set of four findings preceded all
of these and is not in this table: they were raised against the PLAN before any code existed
(`rcplan.result.json`) and were resolved in `116d671`, which is where plan findings belong.

`mde-1` onward are the Defender for Endpoint **device location** stream, reviewed by codex over
`96cdb89..HEAD` in rounds (raw envelopes `out/session-review.txt` round 1, `out/round2.txt`
round 2, `out/mde-r3.txt` round 3, `out/mde-r4.txt` round 4). Rounds raised, in order:
`mde-1`-`mde-3`, then `mde-4`-`mde-5`, then `mde-6`-`mde-7`, then `mde-8`-`mde-9`. **No round
has come back clean, so the next one is always owed** - this repo's protocol is rounds until
clean, and the comparable Migration stream took four. Each finding is fixed in its own commit.

Four of them are **not** Defender findings. `mde-3`, `mde-5`, `mde-7` and `mde-9` are
Comms-10k: the reviewer was given the session's whole diff, which carried the rejected-address
CSV landed the same day, and has pulled it back in every round since. They are filed under this
prefix because that is the stream that found them, not because the module is related.

**EVERY round has found the previous round's fix incomplete at a NEIGHBOURING path, four times
running.** `mde-4` re-opened `mde-2`; `mde-5` re-opened `mde-3`; `mde-6` re-opened the Site
conflation `111bfa2` fixed; `mde-7` re-opened `mde-5`; `mde-8` re-opened `mde-6` per row
instead of per run; `mde-9` re-opened the duplicate decision `mde-3` recorded. Each original
was verified against the state it was written for and not against the one beside it. **Rounds 3
and 4 asked for that shape by name and it is how all four of their findings were found** - the
prompt is worth more than another look. Rounds 3 and 4 also cleared things they were asked to
attack, which is what makes the pattern a finding rather than a worry: round 4 confirmed
`mde-6`'s Site asymmetry argument holds and that `mde-7`'s condition covers all nine page
states.

**Round 5 came back CLEAN over `96cdb89..f5febd5` (raw envelope `out/mde-r5.txt`), which
closes the stream.** It was dispatched specifically to attack round 4's two fixes and it
cleared both: every inventory path reaches the page through `ApplyEnrichment`, the blank-id
branch and the unavailable-set branch both call `StampUnavailable`, and an observer-only
failure stamps the column before the row lookup, so a device with no hunting row still carries
the marker. For `mde-9` it confirmed the resolver reports the address it was given verbatim,
that three identical unresolved rows produce three rejections, that parse-time rejections and
duplicate rows are disjoint, and that the CSV writer does not de-duplicate. It also checked the
`Member()` helper question and found no other block-bodied caller that had been falsely green,
and no fix undoing another.

Five rounds, nine findings, one clean. The pattern that ran through rounds 1-4 did not recur
in round 4's own fixes - which is the first time in this stream that a round's work survived
the next round intact.

None of these is `[x]`: each is verified on `master` and none has reached the remotes. They
move to `[x]` when the range carrying them is pushed - check that live, it is not recorded here.

`mde-1` to `mde-5` were found and fixed on 2026-10-14 and were **not filed here at the time**;
their rows and records were written later the same day, after a drift sweep flagged that this
index's "zero open rows" was true only by omission.
