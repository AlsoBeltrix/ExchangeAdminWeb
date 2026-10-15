# cdr-followup-1 - resolve-stage prose mixes address and member counts

Status: Open follow-up; outside the approved twenty findings and shared replay repair.
Severity: LOW / P3. Native `/root/review_c11`, `gpt-6-astra` at max, 2026-10-15.
Evidence head: `b0c535ee8d09f06405236200fda2e5dd177183d8`. The formatter is unchanged
from baseline `958cce71a973f45afa9cb3c892ce286e287aa930`.

## Evidence and consequence

Comms10kReplaceProcessor.ResolveAsync (lines 140-147, specifically 146 at the evidence
head) formats the distinct resolved member count as N of [input address count] matched
in AD. Two valid uploaded aliases for one person resolve to one GUID and zero rejections,
but produce 1 of 2 matched, falsely suggesting an unmatched address.

Comms10kAddressResolver (253-254) intentionally deduplicates by GUID. The existing
Comms10kAddressIdentityTests cross-batch test (line 13) supplies 501 addresses and asserts
500 members with zero rejections; the current formatter would report 500 of 501 matched.
BulkJobService.RecordRow (464) persists that stage message, and AdminBulkJobs (397) displays
row.Message. C11's requested, acknowledged and observed member counts remain correct.

This is a source-confirmed pre-existing wording defect. The dedicated alias-to-stage-message
integration test below has not been run. It does not block C11 or authorize expanding C5.

## Proposed follow-up

Scope separately after current authorized work. Label both quantities explicitly, such as
2 addresses resolved to 1 distinct member. Add a two-address/one-GUID processor case asserting
the persisted stage message and prove it fails with the old formatter. No code change is
approved by this record.
