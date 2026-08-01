# Dev validation checklist -- `2.3.34`

Status: **Open.** Nothing here has been run. Written 2026-07-31 after the owner deployed
`2.3.34` to dev; the owner will work through it Monday.

Dev is `2.3.34`; prod is `2.3.30`. **Four work streams reached dev without any live
validation**, because each was deployed on top of the last while checks went unrun. This
file is the single list, ordered by what breaks worst if wrong -- not by which plan the
check came from. It copies no reasoning: each item cites its plan, which stays the
authority for why the check exists.

Record the outcome inline (`PASS` / `FAIL` / `SKIPPED` + a note). A check that is not run
is not a pass; leave it blank rather than guessing.

**Nothing below is a gate on prod promotion by itself** -- the owner decides that. But
items 1-4 are the ones whose failure would mean a protection control does not work.

---

## A. Protection controls (highest consequence)

Source: `docs/ProtectedPrincipalResolution-Plan.md`,
`docs/ProtectedPrincipalInputValidation-Plan.md`.

These four decide whether a protected principal can be modified by someone who should not
be able to. Everything else on the page is cosmetic next to them.

- [ ] **A1. Alias-addressed protected user is DENIED.** Mailbox Permissions, target
  `VRoche@O365.analog.com` (a secondary alias of the CEO row `vincent.roche@analog.com`).
  Expect: denied, citing the protected **user** rule. **This is the GAP 4 regression test.**
  Before `2.3.33` the alias missed AD entirely; in ConferenceRooms and GroupManagement it
  was allowed straight through. Must be re-run on prod after promotion.
  Result: ______

- [ ] **A2. The same alias is denied in ConferenceRooms and GroupManagement.** Same alias,
  as a room target and as a group member. Those two modules treat "not found" as "not
  protected" and allow, so they are where the bypass was *live* rather than masked.
  Result: ______

- [ ] **A3. Cloud-only principal reached by alias is still protected.** Needs a cloud-only
  mailbox that (a) has a secondary alias and (b) is in the protected user list -- add one
  temporarily if none exists. Target it by the alias. Expect: denied.
  This is review finding **ppv-1**: the `2.3.33` work closed the alias bypass for on-prem
  principals and reinstated it for cloud-only ones. Fixed in `a6927b2`, unit-tested, never
  exercised against a real tenant.
  Result: ______

- [ ] **A4. A normal synced mailbox by primary address is unaffected.** Any ordinary
  target: unchanged behavior, no added latency. Guards against the fallback firing when it
  should not.
  Result: ______

## B. The reported L1/L2 friction (the reason this started)

Source: `docs/ProtectedPrincipalResolution-Plan.md`.

- [ ] **B1. `Jabil.support@analog.com` as target resolves and proceeds.** The original
  ticket (INC1195142). Cloud-only; previously "identity resolution is unavailable".
  Result: ______

- [ ] **B2. `adspstaff@analog.com` (a mail-enabled group) resolves and proceeds.**
  `Get-ADUser` could never match a group; `Get-Recipient` can.
  Result: ______

- [ ] **B3. A deliberately malformed address gives an accurate not-found message.** Expect
  wording that names both directories and says to check the address -- **not** "contact
  your administrator", which is what made this look like an outage.
  Result: ______

- [ ] **B4. With EXO credentials deliberately wrong (dev only), an AD-miss target still
  DENIES**, with the unavailable message. The fail-closed half: an outage must not
  un-protect anyone.
  Result: ______

## C. Protected-principal admin page

Source: `docs/ProtectedPrincipalInputValidation-Plan.md`. Admin Settings -> Protected
Principals.

- [ ] **C1. An O365/Entra-only group is REFUSED as not found.** The question that started
  this work stream. Refusing is correct behavior -- cloud-only objects are non-protected by
  design -- and the message should say so rather than looking like a bug.
  Result: ______

- [ ] **C2. AD unreachable gives "try again later", NOT "not found".** The owner's D1
  ruling, and the reason `ValidateExists` exists at all. Stop the AD path or point at an
  unreachable DC, then try to add anything.
  **The most important check in section C** -- if these two messages are confusable the
  design has failed.
  Result: ______

- [ ] **C3. A typo'd user is refused AND the typed text stays in the box.** Clearing it on
  refusal would destroy what needs correcting.
  Result: ______

- [ ] **C4. A real user / group / OU is accepted**, and the saved value is the canonical
  form (UPN for users, DN for groups and OUs) rather than what was typed. Type
  `DOMAIN\username` for one of them to check the prefix is handled.
  Result: ______

- [ ] **C5. A pattern (`adm-*`) is accepted with no directory call.** Wildcards have no
  object to resolve; validating them would be wrong.
  Result: ______

- [ ] **C6. With AD unreachable, existing rows show NO badges.** Not "not in AD" on
  everything -- during an outage every entry fails to resolve, and badging them all reads
  as "your protection rules have been lost". Silence is the correct answer.
  Result: ______

- [ ] **C7. With AD up, a genuinely stale row shows its badge and can still be removed and
  saved.** Flagging must never block Save, or a decommissioned group becomes unremovable.
  Result: ______

- [ ] **C8. Save is refused while an add is still "Checking...".** Review finding
  **ppv-3**: saving mid-validation used to persist the list *without* the pending entry and
  still report success. Click Add then Save immediately.
  Result: ______

## D. Operator email resolution (on dev since `2.3.32`, never validated)

Source: `docs/OperatorEmailResolution-Plan.md`. Full list is items 1-8 there; these are the
load-bearing ones.

- [ ] **D1. Message Analysis: the Notify box pre-fills with your own address.** This is
  plan check 7, the load-bearing one -- it confirms `ClaimTypes.PrimarySid` is actually
  populated on this deployment. If the box is empty, read the warning log before concluding
  AD is down.
  Result: ______

- [ ] **D2. A second operator sees THEIR address, not the first operator's.** Guards
  against a resolution cached across circuits.
  Result: ______

- [ ] **D3. Type into the box immediately on load, before it pre-fills -- the typed value
  survives.** The late resolution must not overwrite it.
  Result: ______

- [ ] **D4. Historical search with a beyond-realtime range is accepted rather than
  refused.** **Exploratory, not a gate** (plan OQ-2): this path has never been exercised, so
  a failure is a discovery about untested code, not a defect in that change. Record what
  happens and stop; do not fix it under that plan.
  Result: ______

## E. MessageTrace export delivery (on dev since `2.3.31`, never validated)

Source: `docs/MessageTraceDownloadLink-Plan.md`. Full list is items 1-9 there.

- [ ] **E1. An 11+ message export arrives as a link with no attachment**, and the link
  lists the export on the reports page.
  Result: ______

- [ ] **E2. Download demands the ticket prompt and the CSV opens correctly.**
  Result: ______

- [ ] **E3. An account without MessageTrace access is denied the reports page.**
  Result: ______

- [ ] **E4. An unwritable export directory yields the FAILURE notice and a `Failed` row
  (not `Expired`), with the job still completing.** This is openreview F1 -- with the
  attachment gone, a swallowed save error would otherwise send a "ready" email for a file
  that does not exist.
  Result: ______

- [ ] **E5. With `Application:PublicBaseUrl` unset, the mail has prose and no hyperlink** --
  specifically no bare `/message-analysis/reports`, which is unresolvable from an email
  client.
  Note: that key is only written on a fresh install or with `-Force`, so an upgraded host
  may not have it. Absent is a supported state.
  Result: ______

## F. Prod-only defect, unfixable from dev

- [ ] **F1. Re-run the message-trace search that failed in prod** with
  `Object reference not set to an instance of an object.` Fixed in `2.3.32`; dev has the
  fix, prod does not. Confirms the fix works before promotion.
  Source: `docs/MessageTraceNullRow-Plan.md`.
  Result: ______

---

## Not covered here

- **Bulk Job Runner live validation** and **ConferenceRooms PP gate / GM-3 self-service
  groups** UI validation -- both long-deferred, tracked in `.agents/state.md` "Next up"
  items 1-2. Foldable into the same session but not part of the `2.3.34` change set.
- **Whether `sporting.tickets@analog.com` and `Jabil.support@analog.com` should be
  administratively reachable at all** (`docs/ProtectedPrincipalResolution-Plan.md` OQ-2) --
  an ops question, not a check.
