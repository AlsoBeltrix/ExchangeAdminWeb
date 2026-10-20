# aud-a23b: the on-premises path still reads applied rights out of Detail

**Severity**: LOW. Correct today. It is a convention with one remaining consumer and a sharp
edge that `aud-a23` had to route around.
**Status**: Recorded follow-up; not approved
**Branch**: --
**Commit**: --

## Evidence

`Components/Pages/MailboxPermissions.razor`, the `ExecuteOnPrem` path:

```csharp
var succeeded = opResult.Detail?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
```

`Detail` means different things on different paths. On a partial result it is the applied
rights. On a **successful cloud grant** it is the OWA link text. This parse is safe only
because `AddMailboxPermissionsOnPremAsync` never returns a cloud result, where `onPrem: true`
suppresses the link.

`aud-a23` added `PermissionResult.AppliedRights`, which says the same thing unambiguously and
is populated on the on-premises paths too. This site is now the only consumer of the old
convention.

## Predicted observable failure

Not reachable as written. It becomes reachable if the on-premises and cloud paths are ever
merged, if `ForGrant` gains a link on the on-premises branch, or if someone copies this line
to a cloud call site - which `aud-a23` nearly did, and which would put a URL in the
permission-type line of a user notification.

## Approach (proposed, not approved)

Replace the split with `opResult.AppliedRights ?? []` and delete the `succeeded` local. The
two per-right audit calls above it use `succeeded.Contains(...)`, which reads the same from
the list. Three lines, and the page must stay at its `ExpectedLineCount` of 808 or be
re-anchored per `.agents/playbooks/clickgate-reanchor.md`.

## Known gaps

- Other readers of `Detail` across the app were not surveyed. The overloading is the real
  problem and this is one instance of it.
