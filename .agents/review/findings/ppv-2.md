# ppv-2: DOMAIN-prefix stripping corrupts distinguished names containing an escaped comma

**Severity**: MEDIUM — a legal AD distinguished name is mangled before the LDAP filter is
built, so a valid group or OU is refused as nonexistent and an already-saved one is badged
stale, inviting removal of a working protection rule.
**Status**: Verified
**Branch**: default-branch mode (one commit per finding, per repo policy)
**Commit**: (filled in after commit)

## Evidence

`ADDirectorySearchService.BuildExactMatchFilter` (`Services/ADDirectorySearchService.cs:405-413`)
normalizes **every** identity — user, group and OU alike — before building the filter.
`NormalizeIdentity` (`:423-433`) strips everything up to and including the first
backslash unless that backslash is trailing.

A DN may legally contain an escaped comma. Executed against the exact algorithm:

```
IN : OU=Sales\, East,DC=contoso,DC=com
OUT: , East,DC=contoso,DC=com
IN : CN=VIP\, Tier0,OU=Groups,DC=contoso,DC=com
OUT: , Tier0,OU=Groups,DC=contoso,DC=com
```

Groups and OUs are stored **as DNs**: the picker returns `ReturnValueKind="DN"`
(`Components/Pages/AdminSettings.razor:150`, `:172`) and `CanonicalValue` stores the DN
(`Services/ProtectedPrincipalEntryValidator.cs:128-135`). So the corrupted value is
exactly the form these fields carry.

## Predicted observable failure

Two, both operator-visible:

1. **Add is refused.** Picking a group whose DN contains an escaped comma builds a filter
   against the mangled remainder, AD returns nothing, and the entry is refused with
   "not found in Active Directory. Check the name" — for an object the operator just
   selected from the directory's own dropdown.
2. **A working rule is badged stale.** The slice-4 sweep runs the same lookup over saved
   entries, gets `NotFound`, and shows "not in AD" on a rule that is protecting people.
   The badge's stated meaning invites the admin to remove it.

Test that would catch it: `BuildExactMatchFilter("OU=Sales\\, East,DC=contoso,DC=com",
"OU")` must contain the full DN, not the suffix after the escape.

## What

`DOMAIN\` stripping was written for the simple `CONTOSO\jdoe` form the page's own hint
text invites, and applied unconditionally to every object kind. A backslash inside a DN is
an escape character, not a domain separator, so the two uses of `\` were conflated.

## Approach

Make the stripping shape-aware rather than kind-aware alone. A `DOMAIN\name` prefix is a
NetBIOS name followed by a single backslash with no `=` before it; a DN always contains
`=` before any backslash. Strip only when the input matches the domain-qualified shape,
and pass anything DN-shaped through to `EscapeLdapFilter` untouched.

Kind-only gating would be insufficient: users may also be entered as DNs, and the group
filter matches on `distinguishedName` among other attributes.

## Files changed

- `Services/ADDirectorySearchService.cs` — `NormalizeIdentity`.
- `ExchangeAdminWeb.Tests/ADDirectoryValidateExistsTests.cs` — DN-with-escaped-comma
  cases for user, group and OU.

## Guard proof

`ADDirectoryValidateExistsTests` — disabling the DN shape check (restoring unconditional
`DOMAIN\` stripping) fails **5** tests: the three
`NormalizeIdentity_DnWithEscapedComma_*` cases and both
`BuildExactMatchFilter_DnWithEscapedComma_KeepsTheWholeDn` cases. Restored: 38/38 in the
file.

## Coder dispute (if any)

None. Reproduced directly against the algorithm.

## Known gaps

Whether any DN in this tenant actually contains an escaped comma is unverified. The fix is
warranted regardless: the failure is silent, and the sweep's badge actively encourages
deleting the affected rule.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Harness: codex-cli 0.146.0 (`codex exec --json`, `-s read-only`).
Reviewed SHA: `521bb6e62741c7433a827079d1c53eef0b3b4fec`
Base SHA: `10d159363eeed955d825bf304a143594686b034b`
`capability_ok`: true. Verdict: **findings** (4). 2026-07-31 UTC.

Reviewer's better_approach: "Make DOMAIN\\ stripping object-kind and shape aware. Do not
strip backslashes from DNs; only strip a domain prefix for simple user/group names that
match a DOMAIN\\name form, then pass DNs through to EscapeLdapFilter unchanged." Adopted,
with the shape test carrying the decision rather than object kind.
