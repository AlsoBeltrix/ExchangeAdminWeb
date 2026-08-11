# gmn-3: The admin picker would discard the selected object's stable identity

**Severity**: MEDIUM — a cross-domain or duplicate-named group chosen in the UI is not necessarily the group written.
**Status**: Verified
**Branch**: —
**Commit**: `c7897d1` (plan revision)

## Evidence

`docs/GroupMemberNesting-Plan.md` S5 specifies
`ADIdentityAutocomplete ObjectKind="Any" ReturnValueKind="SAM"`.

`Components/Shared/ADIdentityAutocomplete.razor:173` returns only `SamAccountName` for
`"SAM"`, discarding everything else about the chosen object — although `"DN"` is a
supported kind (`:174`) and the whole `ADSearchResult` is available via `OnResultSelected`
(`:96`, raised at `:164-165`).

Group search is deliberately forest-wide:
`Services/ADDirectorySearchService.cs:485-494` records that a local-domain-only
`Get-ADGroup` made WINROOT groups unreachable from the picker, and that a global-catalog
query returns both domains (measured: 18 ANALOG + 7 WINROOT for one term). `ADSearchResult`
carries `DistinguishedName` and `DnsDomain` for that reason.

So the picker can offer two same-named groups from different domains and hand the service
a bare sAMAccountName that distinguishes neither.

## Predicted observable failure

An admin picks `WINROOT\Enterprise Admins` from the dropdown. The service receives
`Enterprise Admins`, resolves it in the local domain, and either writes the WRONG group,
refuses as ambiguous, or reports not-found — with the UI having shown the correct choice.

## What

The plan routes an unambiguous UI selection through an ambiguous string. The component
already exposes both fixes; the plan simply chose the weakest return kind.

## Approach

S5's add-control bullet now binds `OnResultSelected` and holds the whole `ADSearchResult`
in page state, passing the DN to the service as the write target; `ReturnValueKind="DN"`
is named as the simpler alternative and declined only because it puts a DN in the visible
box. The revision also closes a hazard the finding did not raise: `ValueChanged` fires on
typed input while `OnResultSelected` does not, so a held DN must be cleared on every
keystroke or a retype writes to the previously picked object. Typed input routes through
the service's exact class-aware resolver with its exactly-one-match refusal. AC12b pins
both the cross-domain case and the retype case; the manual-check list names the WINROOT
group, since no test can reach a live global catalog.

## Files changed

- `docs/GroupMemberNesting-Plan.md` — picker carries DN via `OnResultSelected`; typed
  free-form input keeps an exact class-aware resolver with ambiguity refusal

## Guard proof

Not applicable: plan document, and the picker is Razor markup no test can render (no bUnit
harness). The implementation slice gets a source-level tripwire asserting the page does not
pass a bare SAM to the write, plus a manual check against a cross-domain group.

## Coder dispute (if any)

None. Verified against the component and the search service before admitting.

## Known gaps

Shares a root cause with gmn-1: the admin module identifies members by loose strings rather
than by a resolved directory object.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade: fallback — frontier equals standard on this transport, owner-ruled 2026-08-03)

openreview over `618235e9e18bb957860e36a03f1a4b4c5cd42b38..074bfdb7ddffd91e5e6e80904ed71e173ff4f03d`,
verdict `acceptable_with_changes`, `capability_ok: true`, 2026-08-11T18:24Z.
