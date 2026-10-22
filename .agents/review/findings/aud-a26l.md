# aud-a26l: The attribute editor's read-back compares strictly, so a value the directory stores in its own form fails a legitimate save

**Severity**: MEDIUM - a functional regression introduced by aud-a26j: the read-back compares the
value read against the value written with strict ordinal equality, nothing trimmed. Active Directory
stores several syntaxes in its own form - a DN-valued attribute (`manager`, `assistant`) comes back
in the directory's canonical case and spacing, a Boolean comes back as `True`/`False`, an integer
without leading zeros, some strings with the directory's own trimming. For those attributes every
legitimate save now ends as a failed `ADAttributeEditor_Update` naming the value read and the value
written, offers no Undo, and announces a failure to administrators, although the write landed. Loud,
never a false success, but it blocks real edits until fixed.
**Status**: Merged to master (`fee8a58b`); closed on guard proof; codex attr-batch-9 clean; dev acceptance owed; dev acceptance owed - blocks the deploy of the attribute editor until landed
**Branch**: `worktree-agent-a6af37cca287ebc2c`
**Commit**: `7b7acc41` (comments `97592e99`; plan `4684d84d`; merged `fee8a58b`)
**Reviewer**: implementer (surfaced by the aud-a28m/a26j agent, 2026-10-22, candidate 5 of its hand-back, recorded by it as a known gap)

## Evidence

At `6008a986`, `Services/ADAttributeEditorService.cs` `ConfirmWrite` / `UnconfirmedWrites`: ordinal
comparison of `NewValue` against the value read; "" equals absent; a one-element collection reads as
its element. The allowlist's `EditableAttribute.Type` names the attribute's kind (text, choice,
boolean, DN-valued if present); the comparison does not consult it.

## Approach (to be written by the implementing agent before code)

- Compare by the attribute's syntax, not by bytes: DN-valued attributes compare DN-wise
  (case-insensitive, whitespace-normalised around RDN separators, as `GroupManagementService` and
  the aud-a2i membership read compare DNs); Booleans parse both sides; integers parse both sides;
  plain strings compare ordinal after the directory's documented trimming of leading/trailing
  whitespace (label the trimming an assumption and keep it minimal). Where the allowlist's `Type` does
  not say, fall back to: equal ordinal, or equal after case-insensitive comparison when the attribute
  is known DN-valued by name - never a loose "contains".
- When the read differs only by form, the save is CONFIRMED and the record carries the value READ as
  the new value (what the directory holds), with no failure. A genuinely different or missing value
  still fails as aud-a26j does.
- Tests through `AttributeEditorHarness` with `SetStoresAs`: a manager DN stored in canonical case
  confirms; a Boolean written `true` stored `True` confirms with `True` recorded; a value stored with
  different content still fails; the undo path round-trips the normalised value. Mutation at the
  call site: compare ordinal again (the DN and Boolean cases fail).
- No live AD; the directory's normalisation rules are assumptions labelled in the record, with a
  dev acceptance item: edit a manager and a Boolean attribute on a test user and confirm the save is
  recorded as a success with the directory's form.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28m/a26j hand-back (`a8ae8dd9`),
the stale-DC LOW folded in from codex attr-batch-7 (`afa9dbf6`). One commit `fix(ad-attribute-editor): ...
(aud-a26l)`, first of three on this branch (from `afa9dbf6`). Behaviour: AD Attribute Editor (module version;
the supervisor owns the bump). Evidence re-read at `afa9dbf6`, `Services/ADAttributeEditorService.cs`:
PerformSave 1325-1473 sends the pre-write re-read (`Get-ADUser -Identity <DN>`, 1356-1361), `Set-ADUser`
(1419-1424) and the read-back (`ReadBackWrittenAttributes` 1520-1541, its Get-ADUser 1526-1530) with no
`-Server`; `UnconfirmedWrites` 1548-1567 compares every attribute `string.Equals(expected, actual, Ordinal)`
(1557); `ConfirmWrite`'s two messages (1500, 1508) name no server. `EditableAttribute.Type` (12) is the
editor's input kind - the configuration page offers String and Choice only (`ModuleConfig.razor` 579-582;
`docs/ADAttributeEditor-Plan.md` 246, "v1 supports String and Choice") - so it never names the directory's
syntax: the comparison takes the syntax from the value the directory returns and from the attribute's name.

**Rule.** A save is confirmed when every attribute it wrote reads back holding the value written, judged in
the attribute's own syntax; the record carries the value read, which is what the directory holds. A value
genuinely different, or missing, still fails exactly as aud-a26j has it. Never looser than the syntax: no
"contains", no prefix, and a string is never parsed because it looks like a Boolean or a number.

1. **Syntax, first match wins** (`UnconfirmedWrites` gains the allowlist; pure, internal static):
   - the value read is a `System.Boolean` - Boolean: the value written parses (`bool.TryParse`, true/false in
     any case) to it. *Assumption, not probed*: the module returns a Boolean-syntax attribute as
     `System.Boolean` (the editor already shows one as `True`/`False`, its ToString);
   - the value read is an integer type - Integer: the value written parses (invariant, `long`) to it.
     *Assumption, not probed*: the Integer and Large Integer syntaxes come back as Int32/Int64;
   - the attribute is DN-valued by name - `manager`, `assistant`, `secretary`, `seeAlso` (base-schema DN
     syntax), the name case-insensitive - DN-wise: split at unescaped commas (`ServerFromDn`'s split), each
     RDN trimmed and trimmed around its first `=`, the same RDNs in the same order compared OrdinalIgnoreCase
     (the repo's DN comparison: `GroupManagementService.IsSelfNest`, aud-a2i's membership read). A DN-syntax
     attribute outside the list compares as text: a loud false failure, never a false success;
   - `Type` String or Choice - a plain string: ordinal after trimming leading and trailing whitespace on both
     sides, nothing else (case and inner spacing stay significant). *Assumption, not probed*: the directory
     may store a string without its leading and trailing whitespace;
   - any other `Type` (a row the configuration page cannot write) - ordinal, untrimmed.
   Empty and absent stay equal (aud-a26j); one side absent is a difference. The record's and the result's new
   value stay the value read (its ToString, as the lookup and the re-read show it), so the undo's conflict
   check (`ADAttributeEditorUndoService` 253-276, ordinal against the lookup's ToString) meets that form.
2. **One server for the chain (folded in).** `server = GroupManagementService.ServerFromDn(
   target.DistinguishedName)`, once, before the session: the DN the pre-write re-read is asked for, so the
   object it returns - the one written and read back - is that DN's (the brief's "ServerFromDn of the re-read
   DN"). `-Server <server>` on the re-read, `Set-ADUser` and the read-back. A DN with no DC components: all
   three unrouted - the explicit fallback, aud-a2p's - with a warning logged, and the messages say "the
   default domain controller". *Assumption, not probed (DC affinity)*: three calls in one session sent to one
   domain name are answered by one domain controller (the module's locator); where they are not, a landed
   write reads back unconfirmed - loud, never a false success. No domain controller is discovered and pinned:
   that is a new call and failure path the brief does not ask for.
3. **Messages.** Not confirmed: "Set-ADUser on <upn> was not confirmed: read back by objectGUID <guid> from
   <server>, <each>. The value may not have replicated yet to the domain controller that answered for
   <server>." (the fallback: "from the default domain controller", "... yet to the default domain
   controller."). The read failed: "... reading <names> back by objectGUID <guid> from <server> failed:
   <error>". Each logs a warning naming the server.
4. Comments after the mutation round, in their own commit.

Tests. Harness (`AttributeEditorHarness.cs`, `LocalAd`): the Get-ADUser and Set-ADUser stand-ins take
`-Server`, recorded on each read and write (`ReadCall.Server`, `SetCall.Server`); new knobs, default off:
`ReadsAs` (Get-ADUser types each stored value as this returns it, as the module returns a Boolean) and
`ReplicaLag` (a Get-ADUser after a Set-ADUser in the same session is answered with the object as it was before
the write unless both carried the same `-Server`: a domain whose default routing can send the read to a domain
controller the write has not reached). ADAttributeEditorSaveTests, the allowlist extended per test (`manager`,
a Boolean attribute): (a) a manager written in lower case with spaces after its commas, stored in the
directory's form - confirmed, the record's and the result's new value the DN read; (b) a Boolean written
`true`, stored and read as `True` - confirmed, `True` recorded; (c) other content still fails - a manager
stored as another DN, a Boolean written `true` read `False`; the upper-cased title (aud-a26j's) unchanged; (d)
the undo round-trips the normalised manager - the forward record read back as the page reads it, CanUndo,
ExecuteUndoAsync restores the old DN, confirmed; (e) replica lag - confirmed, the same `-Server` on the re-read,
the write and the read-back; a DN naming no domain - all three unrouted, the warning, not confirmed, the
message naming the default domain controller; (f) pure, the rules above; (g) the harness's new knobs. The happy
path's exact call list is unchanged (the server is recorded beside it).

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched,
rebuilt, green): M1 the comparison ordinal again - (a), (b), (d) fail; M2 `-Server` dropped from the read-back
- (e) fails.

Files: `Services/ADAttributeEditorService.cs`, `ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`,
`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, this record. Dev acceptance owed: edit a manager and a
Boolean attribute on a test user; each save is recorded as a success with the directory's form.

## Fix

`Services/ADAttributeEditorService.cs`:

- PerformSave: `server = GroupManagementService.ServerFromDn(target.DistinguishedName)` before the session, a
  warning when it is null; `-Server <server>`, when there is one, on the pre-write re-read and on `Set-ADUser`;
  `ConfirmWrite(ps, target, reReadGuid, changes, allowlist, server, credential)`.
- ConfirmWrite: the read-back sent to the same server; the allowlist's Type for each name (the first row per
  name, case-insensitive) handed to UnconfirmedWrites; both messages name the server asked ("the default domain
  controller" for none), and the not-confirmed one adds "The value may not have replicated yet to the domain
  controller that answered for <server>." ("... to the default domain controller.").
- ReadBackWrittenAttributes: `-Server <server>` when there is one.
- UnconfirmedWrites (internal static, pure) judges each value by `ReadsAsWritten(SyntaxOf(type, name, value),
  expected, actual, value)` and takes `IReadOnlyDictionary<string, string> allowlistTypes`, not the rows: the
  aud-a28l tripwire `AllowlistConcurrentSaveTests.TheServicesOnlyAllowlistWriter_IsTheSaveThatChecksTheLoadedVersion`
  takes any non-private method handed allowlist rows for a writer, and this one reads only each row's Type.
- New beside it: `ReadBackSyntax` (internal enum), `SyntaxOf`, `ReadsAsWritten` and `SameDistinguishedName`
  (internal static, pure), `DnValuedAttributes`, `RelativeNames` and `IntegerOf` (private). `ReadDirectoryString`
  became `ReadDirectoryValue`: it returns the value read (a one-element collection's element), so its type can be
  seen; the record and the result still carry its ToString.

Harness (`ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`, `LocalAd`): the Get-ADUser and Set-ADUser stand-ins
take `-Server`, kept on each `ReadCall`/`SetCall` (null for none; the `Calls` strings are unchanged); `ReadsAs`
and `ReplicaLag`, default off, as planned - the write's server is the session's `$global:AttributeWriteServer`,
and a lagging read is answered from `User.BeforeLastWrite`, the attributes as they were before the last write.

Tests (`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, 17 cases):
`AManagerTheDirectoryHoldsInItsOwnForm_IsConfirmed_AndRecordedAsTheDnRead`,
`ABooleanWrittenTrue_ThatTheDirectoryHoldsAsTrue_IsConfirmed_AndRecordedAsTrue`,
`AValueTheDirectoryHoldsWithOtherContent_StillFails_WhateverItsSyntax` (a manager held as another DN; a Boolean
written true held False), `TheUndoOfAManagerHeldInTheDirectorysForm_RoundTrips`,
`AReadBackSentWhereTheWriteWent_IsConfirmed_ThoughAnotherReplicaLags`,
`ADnThatNamesNoDomain_GoesToTheDefaultDomainController_AndSaysSo`, the pure
`TwoDns_AreTheSame_OnlyRdnForRdn_IgnoringCaseAndTheSpacesAroundTheSeparators` (8 cases) and
`TheReadBack_ComparesInTheAttributesOwnSyntax_AndNeverLooser`, and the knob self-test
`TheHarness_TypesAReadValue_RecordsEachServer_AndLetsAReplicaLagBehindTheWrite`. Two existing tests follow the
change: aud-a26j's pure test hands UnconfirmedWrites the String types of its four attributes (its verdicts are
unchanged - a plain string's case still counts), and its applies-nothing test also asserts that the message names
`example.test` and says the value may not have replicated.

Caller-visible change, by design: a save whose read-back holds the value in the directory's own form - a DN in
its canonical case and spacing, a Boolean's True/False, an integer without leading zeros, a string without
leading or trailing whitespace - is a success recorded with the value read, and offers its Undo; one that holds
other content still fails as before. The pre-write re-read, the write and the read-back go to the object's own
domain (`-Server <its DNS name>`); a DN naming no domain logs a warning and goes to the default domain controller,
as every call did. Both failure messages name the server asked.

## Guard proof

No separate pre-fix reproduction: M1 is the comparison as at `afa9dbf6` (ordinal) at its call site, and M2 the
read-back as at `afa9dbf6` (no `-Server`); each fails the test that names it.

Session-local driver (`mutate.ps1`, session scratchpad `a26l-a26m-a28o-work`): the file's exact bytes saved, one
literal replacement that must match exactly once, a Release build with 0 errors required, filter
`FullyQualifiedName~ADAttributeEditor|FullyQualifiedName~AttributeEditorPageSaveAuditTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~UndoNotificationTests|FullyQualifiedName~UndoIdentityTests|FullyQualifiedName~EventLogUndoRefusalTests|FullyQualifiedName~Allowlist`
(234 tests), the exact bytes restored in a `finally`, the MD5 re-checked and the mtime advanced so MSBuild
rebuilds (log `a26l-mutations.log`). Both mutants built with 0 errors; each restore matched the original MD5. After
the round, a rebuild on the restored bytes and the filter: 234/234 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|
| M1 the read-back compared ordinal again (`string.Equals(expected, actual, Ordinal)` in place of `ReadsAsWritten`) | `ADAttributeEditorService.cs`, `A501418D27F3C0C5B7F8023E7DCFDAE5` | `2BF693119F7A5BAD0BD212D4477F6631` | `AManagerTheDirectoryHoldsInItsOwnForm_IsConfirmed_AndRecordedAsTheDnRead`, `ABooleanWrittenTrue_ThatTheDirectoryHoldsAsTrue_IsConfirmed_AndRecordedAsTrue`, `TheUndoOfAManagerHeldInTheDirectorysForm_RoundTrips`, `TheReadBack_ComparesInTheAttributesOwnSyntax_AndNeverLooser` |
| M2 `-Server` dropped from the read-back | `ADAttributeEditorService.cs`, `A501418D27F3C0C5B7F8023E7DCFDAE5` | `A35BFD8A070CE4A256CA65C619E6C1FE` | `AReadBackSentWhereTheWriteWent_IsConfirmed_ThoughAnotherReplicaLags` only - the write still carries the server, the read-back lands on the lagging replica |

Unchanged through the round: `AttributeEditorHarness.cs` `881714459FD1A645DBB7F84A5CE368F0`,
`ADAttributeEditorSaveTests.cs` `D27B629C0CC5F6364259FBE5BAC23149`. The code comments are written after the round,
in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental --no-restore`: 0 errors, 41 warnings, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --no-build --blame-hang --blame-hang-timeout 10m` (Debug, after a Debug
  build of this tree): exit 0, 5,444 passed / 3 skipped (the three known AD skips,
  `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`) / 0 failed, 5,447 total, 5m27s; nothing hung,
  no Sequence file. Baseline at `4684d84d` (the code as at `afa9dbf6`): 5,427 / 3 / 0, 5,430 total; the
  seventeen new cases are this record's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1`: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. No coder self-review verdict is
claimed.

## Known gaps

- *Assumptions, not probed* (no directory call is made here), each failing loud, never into a false success: the
  module returns a Boolean-syntax attribute as System.Boolean and an Integer or Large Integer one as Int32/Int64 -
  one returned as text compares as a plain string, so "true" against "TRUE" stays a failure; `manager`,
  `assistant`, `secretary` and `seeAlso` are DN syntax in every forest's schema, and a DN-syntax attribute outside
  that list compares as a plain string; a directory may drop a string's leading and trailing whitespace; three
  calls in one session sent to one domain name are answered by one domain controller (DC affinity).
- The allowlist's Type cannot name a syntax (String or Choice only), so the syntax comes from the value read and
  the attribute's name. A row stored with another Type is compared ordinal, untrimmed.
- The lookup and the preview stay unrouted; only the save's re-read, write and read-back carry `-Server`.
- Dev acceptance owed: edit a manager and a Boolean attribute on a test user; each save is recorded as a success
  with the directory's form.

## Comments after the round

Written after all three rounds, in their own commit (`97592e99`) with aud-a26m's and aud-a28o's. Checked by
`check-comment-diff.ps1` (session scratchpad) over `git diff -U0` before that commit: every added or removed line of
the three files is a comment or blank (115 lines), so each file's code lines are unchanged and in order. This record's
file, `Services/ADAttributeEditorService.cs`: `A501418D27F3C0C5B7F8023E7DCFDAE5` at this record's commit,
`8A8C96CCDDD10ECCF34A658693AC8582` after aud-a28o's code (the attempted values), `050D3F534AD1A4CBA9183F880805B977`
after the comments - why one server for the chain and the DC-affinity assumption, why the read-back is judged in the
attribute's syntax and what each rule assumes, why the allowlist arrives as Types, why the record takes the value
read, the DN comparison's limits. Two comments this fix had made false are corrected: the read-back's, which said it
carried no -Server because the write carried none, and UnconfirmedWrites', which said the comparison was ordinal. The
harness and the tests carry their comments from their own commits.

## Branch-tip gates - 2026-10-22 (`97592e99`: the three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none
  in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,453 passed / 3 skipped (the three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`)
  / 0 failed, 5,456 total, 5m12s; nothing hung, no Sequence file. Baseline at `4684d84d` (the code as at `afa9dbf6`):
  5,427 / 3 / 0, 5,430 total; the twenty-six new cases are this branch's (aud-a26l seventeen, aud-a26m four,
  aud-a28o five).
- `ClickGate*` 316/316 and `ProgressRegistryTests` 35/35, also run alone (Release).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

## Closeout

Fixed on branch `worktree-agent-a6af37cca287ebc2c`, not merged, not pushed. A module-scoped behaviour change to the AD
Attribute Editor (a read-back in the directory's own form confirms a save; the chain routed to the object's domain;
the messages name the server): its module version is due. No shared code changed (`GroupManagementService.ServerFromDn`
is called, not edited), so no base app bump. The version bumps are the supervisor's, at merge: `Modules/ModuleCatalog.cs`
and `ExchangeAdminWeb.csproj` were outside this brief.

## Folded in: the read-back can be answered by a different, stale DC (codex, attr-batch-7, 2026-10-22) - LOW

`Services/ADAttributeEditorService.cs` `ConfirmWrite` around line 1526: `Set-ADUser` is sent with no
`-Server`, and the immediate `Get-ADUser -Identity <objectGUID>` has none either, so in a multi-DC
domain a DC that has not replicated the write can answer and the correct save is reported Failed
with no Undo and a failure notice. Choose a writable DC once for the target object's domain
(`ServerFromDn` of the re-read DN, the aud-a2p/a2q routing; or the DC the pre-write re-read answered
from, read off `Get-ADUser`'s `-Server`-less result if the module exposes it) and pass that exact
`-Server` to the pre-write re-read, `Set-ADUser` and the read-back; label the DC-affinity assumption.
The not-confirmed message also says the value may not have replicated yet and names the server
asked. Test: a harness whose read-back server differs from the write server fails before the fix
and confirms after. Mutation at the call site: drop `-Server` from the read-back.
