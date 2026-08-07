# BitLocker Recovery module

Look up BitLocker recovery keys from the ExchangeAdminWeb admin UI. Searches
use the historical SQLite archive by default and can explicitly include live
Active Directory when needed.

Status: Integrated into the host 2026-08-07. Manual validation in progress on dev; the archive
path, live AD fallback and reveal have all been exercised by the owner, and one defect found that
way is fixed (`blr-3`, a second search rendering a false "no keys found" while the live query was
still in flight).

## Purpose and operators

Service desk staff handling a recovery call: someone is locked out at the
BitLocker pre-boot screen and needs their recovery key read to them.

Two things make this more than an AD lookup:

- Deleting a computer object from AD deletes its `msFVE-RecoveryInformation`
  children. For retired hardware the archive is the **only** surviving copy.
- The recovery screen values survive a machine rename or reimage. Operators can
  search by full key ID GUID, short key ID prefix, or 48-digit recovery key.

## What it reads

- One SQLite database, written by the scheduled `Export-BitLockerKey.ps1` task
  in the `scripts` repository (`BitLocker/`).
- Optional live AD `msFVE-RecoveryInformation` objects, using a module-specific
  Delinea credential.

This module never writes to AD or to the archive.

See `BitLocker/docs/2026-08-05-bitlocker-recovery-design.md` in that repository
for the schema, the export, and the storage rules.

## Required permissions

| Policy | Purpose |
| --- | --- |
| `BitLockerRecovery` | Access the module. Fail-closed. |

Fail-closed because a recovery key decrypts an entire disk. Configure the
section access groups on the module config page; with none configured, access
is denied.

## Required module config

| Field | Required | Purpose |
| --- | --- | --- |
| `ArchiveDatabasePath` | Yes | Full path to the archive database. |
| `DelineaSecretId` | No | Secret Server record for the AD account allowed to read `msFVE-RecoveryPassword`. Required only for live AD fallback. |
| `ActiveDirectorySearchBase` | No | DN limiting live searches to one AD subtree. |
| `ActiveDirectoryServer` | No | Domain controller used for live searches. |
| `SearchResultLimit` | No | Rows per search. Defaults to 50, capped at 500. |

`ArchiveDatabasePath` must be a **local path on the web server**, not a UNC
path. SQLite's WAL mode needs shared memory that SMB does not provide.

If the scheduled export runs on another host, replicate a read-only archive copy
to local disk on the ExchangeAdminWeb server after each export run.

## Required credentials

Archive-only search needs no Delinea Secret Server record, no Graph app
registration, and no Exchange connection.

Live AD fallback needs a module-specific Delinea Secret Server record containing
a username/password/domain credential for an account allowed to read
`msFVE-RecoveryPassword`, a confidential AD attribute. The module passes that
credential explicitly to AD cmdlets; it must not fall back to the app pool
identity.

The scheduled export has its own service account with the same AD read right.
That account is separate from the module's Delinea credential.

## Does it mutate data?

No. Read-only. There is therefore no ticket requirement, no protected-principal
check, and no confirmation dialog: those exist to guard writes, and this module
performs none.

## Protected principals

Not applicable. The module touches no user, mailbox, group, or directory
object.

## Audit actions

| Action | Category | When |
| --- | --- | --- |
| `BitLockerSearchByName` | lookup | A search by computer name |
| `BitLockerSearchByKeyId` | lookup | A search by recovery-screen identifier |
| `RevealRecoveryKey` | `BitLockerRecovery` | An operator reveals a key |

The **reveal** is the security event, not the search. A search returns masked
rows; revealing is the moment a recovery key reaches a human, and it records
the operator, the target machine, the key ID, and whether the machine is still
in the directory.

Recovery keys themselves never enter an audit record, a log line, or an error
message.

This includes the **search** target, not only the reveal. An operator may paste a
48-digit recovery key into the recovery-screen box to find a machine, so the audit
record shows `(recovery key, redacted)` in place of the pasted value. Key IDs are
recorded verbatim: they are identifiers, not secrets, and they are what makes the
record useful.

An audit write failure is logged and swallowed: the key has already been shown
by then, so failing the operation would misreport what happened.

### A degraded search audits as a success

When live Active Directory is requested and fails, the archive results are still
returned and the page shows a warning, so the search is recorded as **successful**.
The audit record therefore does not distinguish a complete search from one where
a source was unreachable.

This is deliberate rather than an oversight. `AuditService.LogLookupAction` takes
no `extra` dictionary, so carrying the degradation into the record would mean
changing a shared host audit signature -- a change with a much wider blast radius
than this module. The warning is visible to the operator at the moment it matters,
and the failure detail goes to the application log.

Consequence worth knowing when reading an audit trail: a `BitLockerSearchByName`
or `BitLockerSearchByKeyId` success does not prove live Active Directory was
searched, only that the search ran and returned what it could.

## Operation tracing

None. Operation traces exist for multi-step backend transcripts; this module
performs read-only lookups and reveal auditing. The audit record is the durable
trail, and raw PowerShell streams or recovery passwords must never be traced.

## Fail-closed behaviour

An unreachable required source is an **error**, never an empty result:

| Condition | Result |
| --- | --- |
| `ArchiveDatabasePath` unset | Error, no search |
| Archive file missing | Error, no search |
| `DelineaSecretId` unset or credentials unavailable | Archive-only searches still run; requested live AD fallback shows archive rows plus a warning |
| Live AD query fails | Archive-only searches still run; requested live AD fallback shows archive rows plus a warning |
| AD returns recovery objects but no readable passwords | Archive-only searches still run; requested live AD fallback shows archive rows plus a warning |
| Module config corrupt | Error, no search |
| Query fails | Error, sanitised message |
| Archive read, nothing matched | Success, "no keys found"; UI notes live AD was not searched |
| Live AD fallback and archive read, nothing matched | Success, "no keys found" |
| Live AD fallback fails and archive has no match | Success with warning; UI says no archive key was found and live AD did not complete |

The distinction matters on a live call. "This machine has no key in the archive"
and "I could not reach the selected source" look identical if both render as an
empty table. Configured paths and backend details are kept out of
operator-facing messages; they go to the log.

## Manual dev validation

1. Enable the module in Admin Settings and grant your group section access.
2. Set `ArchiveDatabasePath` to a database built by
   `Import-BitLockerArchive.ps1`.
3. Search a current computer name fragment; confirm an archive-backed masked row.
4. Search a historical/deleted computer; confirm an **Archive only** masked row.
5. Set `DelineaSecretId` to the module's AD reader secret.
6. Confirm the ExchangeAdminWeb server has the ActiveDirectory PowerShell module.
7. Repeat a current computer search with **Search live Active Directory too**
   checked; confirm a **Live AD** masked row and expect a noticeable delay.
8. Reveal one key; confirm the key appears and an audit record is written with
   the machine name but **not** the key.
9. Search a key ID with braces, the short key ID prefix, and a 48-digit recovery
   key; confirm each finds the machine.
10. Point `ArchiveDatabasePath` at a nonexistent file; confirm an error, not an
    empty table.
11. Remove or break the Delinea secret config; confirm archive-only search still
    runs and requested live AD fallback shows archive rows plus a warning rather
    than returning a clean miss.
12. Sign in as a user outside the section access groups; confirm
   `/bitlocker-recovery` denies directly, not only by hiding the nav link.

## Rollback

Disable the module in Admin Settings. It holds no state and writes nothing, so
there is nothing to undo. Operators fall back to `BLSearch.ps1`.

## Not in this MVP

- No export or key rotation. Read-only by design.
- No Intune or Entra-held keys.
- No self-service recovery for end users.
