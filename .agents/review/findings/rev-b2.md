# rev-b2: callers that build an on-premises target without its SID fall back to label matching, so a renamed protected user is writable

**Severity**: HIGH - fail-open. `ProtectedPrincipalService` compares a protected user row by SID when the target carries
one and otherwise by the stored label (UPN or mail). Licensing Updates and the AD Attribute Editor build their target
snapshot without `ObjectSid`. Store a protected user, change the user's UPN and mail, and those two modules compare the
stale label, get NotProtected and write (`Set-ADUser`). The plan's rule is that the new build never matches names.
**Status**: Fixed on branch; awaiting review
**Branch**: fix/rev-abc
**Commit**: 76e9a19e
**Lines** (master `145f513f`): `Services/ProtectedPrincipalService.cs:1934-1936`; `Services/LicensingUpdatesService.cs:262-270`, `:382-387`; `Services/ADAttributeEditorService.cs:1317-1325`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review B, range `5c4c2970..82a67e2f`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-b.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Every on-premises target snapshot carries `objectSid` (both callers already read the object from AD); the check refuses
(incomplete, named reason) when a target lacks the identity the rule needs, and the label branch is removed. The test
that expects label matching for a missing SID encodes the defect and must change; add the renamed-user case for both callers.
Check every other `ProtectedPrincipalTarget` constructor for the same omission.

## As built (fix/rev-abc `76e9a19e`)

Verified real: `CheckDirectUserMatches` fell back to `MatchesIdentity(id.Label, target)` when the target carried no SID, and
Licensing Updates, the AD Attribute Editor, Group Management (four snapshots) and Self-Service Groups (two) built on-premises
snapshots without `objectSid`.
- The label branch is removed. A `user_sid` row matches a target carrying its SID. An on-premises target (a DN or a
  sAMAccountName) without one adds the problem `TargetSidNotRead` ("the target's objectSid was not read, so it cannot be
  compared with the protected user rows"), which refuses as an unconverted row does unless another rule matched - in
  `CheckAsync` and in `CheckWriteTarget`. A cloud-only target (neither) cannot be a row's object (Constitution, corrected
  2026-11-06: a cloud-only principal cannot be protected) - no match and no refusal.
- Every on-premises snapshot builder asks for `objectSid` and carries it (`ProtectedPrincipalService.ObjectSidOf`):
  `LicensingUpdatesService.ResolveUsers`, `ADAttributeEditorService` lookup (`GetPropertiesToLoad`), `GroupManagementService`
  (group listing gate, member resolution, group by DN, group by name), `SelfServiceGroupService` (member and user reads). The
  DN-less synthetic targets (unresolved/typed principals on Permission Validator, MFA Reset, Intune, Risky Users, Cloud
  Password Reset, Blocked Senders) carry no on-premises identity and are unaffected by the refusal; under identifier rows
  they never matched a `user_sid` row except by label.
- Help: README protection paragraph and the Protected principals pane note; module versions GroupManagement 2.16.4,
  SelfServiceGroups 1.14.3, LicensingUpdates 1.1.9, ADAttributeEditor 1.7.8.
- Tests that encoded label matching now carry SIDs (`ProtectedPrincipalServiceTests` via `SidFor(label)`,
  `EmergencyDisableServiceTests` via `UserSid`, `PermissionValidatorTests`, `ProtectedGroupWriteTargetTests`); the two
  cloud-only fallback tests now assert the Constitution's rule.

## Guard proof

| Test | Fails on the old code / mutation (label branch restored, objectSid dropped from the two callers) |
| --- | --- |
| `ProtectedPrincipalIdentifierTests.AUserSidRow_AgainstAnOnPremisesTargetWhoseSidWasNotRead_RefusesAsIncomplete_NeverByItsLabel` (replaces `..._ComparesByItsLabel`) | yes |
| `ProtectedPrincipalIdentifierTests.AUserSidRow_CannotMatchACloudOnlyTarget_ByItsLabel` | yes |
| `ProtectedPrincipalIdentifierTests.AUserSidRow_RenamedUser_IsProtectedByItsSid_ThoughNoLabelMatches` | passes on both (service compares by SID) |
| `ProtectedGroupWriteTargetTests.UsersRule_AgainstAGroupSnapshotWithoutItsSid_RefusesAsIncomplete_NotByTheLabel` | yes |
| `ProtectedPrincipalExchangeFallbackTests.CloudOnlyRecipient_IsNotMatchedByAUserRowsLabel`, `..._AddressedByAlias_IsNotMatchedByAUserRowsLabel` | yes |
| `LicensingUpdatesPowerShellLifecycleTests.ResolveUsers_TheSnapshotCarriesTheUsersObjectSid_SoARenamedProtectedUserIsComparedByIt` (renamed user, Licensing) | yes |
| `ADAttributeEditorSaveTests.ARenamedProtectedUser_IsRefused_ByTheSidTheLookupRead` (renamed user, Attribute Editor lookup then save) | yes |

7 failed under the mutation; restored, all pass.
