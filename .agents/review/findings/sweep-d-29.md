# sweep-d-29: Cloud Password Reset made four vault reads per reset and alerted on the employee-ID save before the password write (sweep item 29)

**Severity**: MEDIUM, off by default - `.agents/review/live-risk-sweep-2026-10-30.md` item 29: the Graph secret and a
token were fetched on every call (`CloudPasswordResetService.cs:282`, `:300`; called at `:315`, `:897`, `:1011`),
about 11-16 s; the owner and number searches run twice (`:580`, `:591`); the admin alert was sent between the
employee-ID save and the password write (`CloudPasswordReset.razor:551` before `:560`).
**Status**: Fixed on branch (code half); the search half is a reported conflict, not changed; awaiting review
**Branch**: `fix/sweep-d` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-d`, from master `6ea8963b`)
**Commit**: `ff48c6c5`, and `767feb60` (the two version pins that followed the bump) | **Reviewer**: implementer (candidate)

## Evidence

- At `6ea8963b` `GetGraphClientAsync` read the secret (`DelineaService.GetSecretFieldsAsync`, not covered by the kd-88
  credential window, which is `GetCredentialsBySecretIdAsync`'s) and built a new `GraphTokenClient`, which takes its own
  token, at each of the preflight read, the confirm's re-read, the save and the password write: four of each.

## Approach (as built)

- **One credential fetch and one token per operation.** The service keeps the client built from the module's Delinea
  record for `GraphClientWindow` = `DelineaService.DefaultCredentialWindow` (five minutes) measured FROM THE READ (not
  sliding, so a rotated or revoked secret is picked up within five minutes however busy the module is), keyed by
  Secret ID (a changed id is read afresh), under a lock; a failed read or an incomplete secret keeps nothing. The kept
  `GraphTokenClient` keeps its own token until near expiry. A preflight and its confirm (re-read, save, write) now make
  one Secret Server read and take one token. Seams: `SecretFieldsForTests`, `Clock`.
- **Alert after the write.** The save's administrator alert is held (`saveToAlert`) and sent from the handler's finally:
  after the password write and the reset's own alert, on every path the save reached (success, refusal, indeterminate,
  delivery failure, exception). What it says, who gets it and the feature's off-by-default are unchanged.
- README Cloud Password Reset: the alert's timing and the kept client. CloudPasswordReset `1.2.2`.

## Where the code goes beside the fix shape - and the one part not done

- **The searches are not changed: conflict reported.** Within one operation each search already runs once
  (`FillMissingEmployeeId`: `FindUserByLogonName` once at `:580`, `FindUserByEmployeeId` once at `:591`). They run
  twice per reset because the preflight (`CloudPasswordReset.razor` `LookupAsync`) and the confirm (`ExecuteResetAsync`,
  `ResetService.DeriveDestination(fresh)`) each derive the destination. The confirm's run is required: cpr-9 (HIGH,
  `.agents/review/findings/cpr-9.md`, "The plan requires the whole derivation at the moment of reset") made the confirm
  re-read the account and re-derive the destination so a password is never mailed to the owner of an employee ID the
  account no longer carries. Reusing the preflight's search answers at the confirm would reintroduce cpr-9. Left as is.
- The employee-ID plan (`docs/CloudPasswordResetEmployeeId-Plan.md` 4.7, 4.8) said the save's alert goes "immediately
  after" the save. It now goes after the password write; the page's doc comment says so. The plan is `Implemented`
  history and is not edited here.
- The window is absolute, not kd-88's sliding window, for the rotation reason above.

## Guard proof (`CloudPasswordResetOneCredentialTests`, `CloudPasswordResetFillPageSourceTests.T20`)

| Mutation | Failing tests |
|---|---|
| C1 keep no client | `A_preflight_and_its_confirm_read_the_vault_once_and_take_one_token`, `An_operation_more_than_five_minutes_after_the_read_reads_the_vault_again`, `The_window_is_measured_from_the_read_not_from_the_last_use` |
| C2 sliding window | `The_window_is_measured_from_the_read_not_from_the_last_use` |
| C3 never expires | `An_operation_more_than_five_minutes_after_the_read_...`, `The_window_is_measured_from_the_read_...` |
| C4 alert back between the save and the write | `The_save_alert_is_sent_after_the_password_write_and_after_the_resets_own_alert`, `The_save_alert_is_still_sent_when_the_password_write_is_refused_and_still_after_it`, `T20_the_save_is_audited_and_alerted_on_every_outcome_...` |
| C5 alert dropped | the two above, `T20`, and three existing `CloudPasswordResetFillPageRunTests` (planned fill, refused save, unanswered save) |

## Gates

Run once for the branch at its tip; see the sweep-d hand-back. Not run: a live Secret Server read or Graph write.

## Manual checks for the deploy list - before switching the fill-in on (from the sweep, unchanged)

- The app's granted Graph permissions and directory roles (the employee-ID save needs a user write grant, assumed
  `User.ReadWrite.All`).
- `Get-ADObject -SearchBase (Get-ADRootDSE).schemaNamingContext -LDAPFilter '(lDAPDisplayName=employeeID)' -Properties searchFlags`
  (bit 1 set means indexed; the `(employeeID=...)` search is domain-wide under the pickers' 30 s lock).
