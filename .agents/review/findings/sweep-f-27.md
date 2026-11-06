# sweep-f-27: an attribute-editor save counted as done only if its read-back matched, by unprobed type and DC assumptions (sweep item 27)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 27: `ADAttributeEditorService.PerformSave` assumed
three calls sent to one domain name reach one DC ("Assumption, not probed (DC affinity)"), that Boolean comes back as
System.Boolean (`if (read is bool)`) and Integer as Int32/Int64, and that only manager/assistant/secretary/seeAlso are DN syntax.
A miss filed a landed save as failed ("... was not confirmed: read back by objectGUID ..."), with no Undo and a failure mail.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-f` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-f`, from master `624ff16e`)
**Commit**: `4a196a3e` (`fix(sweep-f, item 27): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- **The pin.** `PinDomainController`: before the re-read, `Get-ADRootDSE -Credential <save's> [-Server <domain from the DN>]`;
  its `dnsHostName` - the DC that answered - is the `-Server` of the re-read, the write and the read-back, and its
  `schemaNamingContext` the schema read's base. Discovered at runtime, logged, never configured or named. A DN naming no domain
  asks the RootDSE unrouted and pins to the DC that answers. A RootDSE that cannot be read (throw, error, no host) leaves the
  chain on the domain name as before, with a warning ("DC affinity assumed").
- **The syntax from the schema.** `ReadSchemaSyntaxes`: one `Get-ADObject -SearchBase <schema> -LDAPFilter
  (|(lDAPDisplayName=..)..)` (values RFC 4515-escaped) on the pinned DC, `attributeSyntax` mapped 2.5.5.8 Boolean, 2.5.5.9 /
  2.5.5.16 Integer, 2.5.5.1 DN, anything else "none of those". `SyntaxOf` takes the schema's word first; without it the old
  rules decide (they fail loud, never confirm falsely). Used by the read-back and by the pre-write change computation.
- **Type-tolerant, never looser.** Boolean by value from System.Boolean or its text (`TRUE`, `True`); Integer by value from any
  integral type or its invariant text; DN by `SameDistinguishedName` for any DN-syntax attribute; a string-syntax attribute stays
  text ("007" is not 7). A real difference still fails closed: no old/new pair (no Undo), failure notice, as before. Read back
  from the write's own DC, the message says so instead of blaming replication.
- Harness: `Get-ADRootDSE`, `Get-ADObject` stand-ins (`RootDseHost`, `RootDseFailure`, `Schema`, `SchemaFailure`) and
  `DomainNameReachesAnyDc`, the lag the pin removes. README AD Attribute Editor; ADAttributeEditor `1.7.7`.

## Where the code goes beside the fix shape

- The preview's re-read is not pinned and reads no schema (it writes nothing; the save recomputes the changes on its own pinned
  re-read).
- One more read per save (RootDSE) and one schema query; both on the DC already in use.

## Assumptions not verified live

- That `Get-ADRootDSE -Server <domain>` with the save's credential answers with `dnsHostName` and `schemaNamingContext`, and
  that the account may read the schema's `attributeSyntax` (a refusal falls back, logged).

## Guard proof (`ADAttributeEditorPinAndSyntaxTests`, `ADAttributeEditorSaveTests`)

| Mutation | Failing tests |
|---|---|
| M27a no pin | `TheChain_IsPinnedToTheDcThatAnsweredTheRootDse_...`, `AReadBackSentWhereTheWriteWent_...`, `ADnThatNamesNoDomain_IsPinnedToTheDefault...`, `AWriteThatRaisesNothingAndAppliesNothing_...` |
| M27b no schema read | `ABooleanOrIntegerReadBackAsText_IsConfirmedByValue(Flag)`, `ADnSyntaxAttributeOffTheNamedList_IsComparedByDn`, `TheSchemaRead_AsksThePinnedDcsSchemaFor_...`, `ASchemaThatCannotBeRead_...` |
| M27c Boolean only as System.Boolean | `ABooleanOrIntegerReadBackAsText_IsConfirmedByValue(Flag)` |
| M27d Integer only as an integral type | `ABooleanOrIntegerReadBackAsText_IsConfirmedByValue` (2 integer rows) |
| M27e DN syntax not taken from the schema | `ADnSyntaxAttributeOffTheNamedList_IsComparedByDn` |
| M27f a string-syntax attribute parsed as an integer | `AStringAttribute_IsNeverParsed_EvenWithTheSchemaRead`, `TheSchemasSyntax_DecidesBeforeTheValuesType` (2), `TheChain_...` |
| M27g any text read as true | `AValueThatParsesToAnotherFlagOrNumber_OrNotAtAll_StillFailsClosed` (2 Boolean rows), `ABooleanOrInteger...(Flag)` |

## Gates

Run once for the branch at its tip; see the sweep-f hand-back. Not run: a live save.

## Deploy-time checks

- The sweep's: on a test account, save one extensionAttribute and one msExchRecipientDisplayType value - expect an Undo button
  and no "was not confirmed". The application log shows "pinned to domain controller <host>" for each save; a "DC affinity
  assumed" or "schema syntax ... could not be read" warning means the fallback ran (record it).
