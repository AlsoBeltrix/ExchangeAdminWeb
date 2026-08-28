# pgwt-3: DECLINED - "Malformed target rows fail open" (reviewer severity HIGH)

Declined at intake, 2026-08-28. The proposed behaviour (any malformed `group_target` row
fails EVERY target check closed) contradicts the store's own documented contract -
`ProtectedPrincipalRepository`: "Row-per-value means one bad entry can't drop the whole
config" - and the behaviour of all four existing protection lists, where a corrupt row
silently protects nothing rather than denying everything. A malformed row can only arise
from out-of-band DB editing (the validator is the sole writer and canonicalises to
`objectGUID|DN`); converting that into a denial of every group write in both modules turns
corruption into an availability weapon against group management. Store-LEVEL corruption
already fails closed (TryRead false -> CheckWriteTarget returns Failed - tested). The
GUID-string-comparison half is moot in practice: both sides of the comparison originate
from the same PSObject ObjectGUID.ToString() canonical form.

Owner may overrule; the reviewer's text is in `.agents/review/pgwt-s14.result.json`.

Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard (owner standing dispatch),
generation pass over `8700531..5336072`, verdict `findings` (7), capability_ok true.
