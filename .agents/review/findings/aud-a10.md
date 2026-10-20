# aud-a10: a typed identity was used where Graph requires an object id

**Severity**: MEDIUM - adding a member or owner by UPN failed every time, and the error sent
whoever read it to the wrong place.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 10 of the 2026-10-16 audit, verified against the code.

`Services/M365GroupManagementService.AddDirectoryObjectAsync` built the reference body as:

```csharp
["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{Uri.EscapeDataString(identity)}"
```

That slot takes an **object id**. `identity` is whatever the operator typed into the page - a
UPN or an email - so the reference named an object that does not exist by that key, and the
POST failed.

**The failure message is the second half of the finding.** It read "Verify the user exists and
the Graph app has Group.ReadWrite.All", so a malformed request presented as either a missing
user or a missing app permission. Anyone diagnosing it would go and check consent.

## Predicted observable failure

An administrator types a colleague's address into Add Member. It fails. They check the user
exists - it does. They check the Graph app's permissions - they are correct. Nothing they can
see explains it, because the request never named the user at all.

## Approach

`ResolveDirectoryObjectIdAsync` turns the typed identity into the id, and the reference is
built from that.

- An identity that **is already a GUID** is passed straight through with no lookup. A caller
  holding an id should not pay for a round trip, and should not have it mangled by being
  looked up as though it were a name.
- Anything else resolves through `/users/{identity}?$select=id`, which accepts a UPN - the one
  Graph route that takes the shape a person types.
- A failure **refuses and says which part failed**: not found, the HTTP status, or "no object
  id returned". The permissions line stays only on the actual POST failure, where it belongs.

## Files changed

- `Services/M365GroupManagementService.cs`
- `ExchangeAdminWeb.Tests/M365DirectoryReferenceTests.cs`

## Guard proof

Behaviour: the resolution runs against a real `GraphTokenClient` over a stub handler, so the
URL construction, status handling and JSON parsing are all exercised - five cases covering a
UPN resolving, a GUID passing through **without any request being made**, a 404, a 429, and a
response carrying no id.

**The first draft of this guard was worthless and was deleted rather than kept.** It asserted
on a reference string the test itself built, so it tested nothing about the service - exactly
the "passes for the wrong reason" shape this stream keeps finding. Recorded because the
instinct to keep a green test is the thing to resist.

One source scan: the add path resolves before building the reference, and the old
`directoryObjects/{identity}` spelling appears nowhere. That one is a scan because reaching
the add means passing the protected-principal gate, which resolves against on-prem AD.

| Mutation | Result |
| --- | --- |
| build the reference from `identity` again | FAIL - `TheAddPathResolvesBeforeBuildingTheReference` |

Source restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4368 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. `M365GroupManagement` 1.5.1 -> 1.6.0 - minor, because adding a
member now performs an extra directory lookup and can fail for a new reason.

## Known gaps

- **`AddMemberAsync` and `AddOwnerAsync` are not driven end to end.** The protection gate
  needs a fake resolver this file does not stand up, so the call site is pinned by a scan.
- The removal path takes an `objectId` parameter already and was not touched; whether every
  caller supplies a real id was not audited here.
- Nothing ran against real Graph.

## Closeout

Fixed directly on `master`. Independent review pending.
