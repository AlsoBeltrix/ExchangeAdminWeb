# aud-a26l: The attribute editor's read-back compares strictly, so a value the directory stores in its own form fails a legitimate save

**Severity**: MEDIUM - a functional regression introduced by aud-a26j: the read-back compares the
value read against the value written with strict ordinal equality, nothing trimmed. Active Directory
stores several syntaxes in its own form - a DN-valued attribute (`manager`, `assistant`) comes back
in the directory's canonical case and spacing, a Boolean comes back as `True`/`False`, an integer
without leading zeros, some strings with the directory's own trimming. For those attributes every
legitimate save now ends as a failed `ADAttributeEditor_Update` naming the value read and the value
written, offers no Undo, and announces a failure to administrators, although the write landed. Loud,
never a false success, but it blocks real edits until fixed.
**Status**: Plan written; in progress - blocks the deploy of the attribute editor until landed
**Branch**: `worktree-agent-a6af37cca287ebc2c`
**Commit**: -
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

## Guard proof

Pending.

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
