# C4 implementation guidance - bind the approved member set

Read-only design advice from native agent `/root/design_confirmed_set`, dispatched as
`gpt-6-astra` at `max` under the approved Revision 6 workflow, 2026-10-15. These are agent
implementation choices within C4, not new owner rulings. No edits, tests or directory calls
were made in the design pass. Re-read symbols at implementation time; coordinate with C10.

## Identity and confirmation

A fingerprint is insufficient if the actual add still uses member DNs. C1 pins the group,
but a member DN reused after resolution could still identify a different person.

Carry one resolved-member value containing ObjectGuid and DistinguishedName through
Comms10kAddressResolver.Candidate/Outcome and Comms10kResolveResult. Read objectGUID in
QueryBatchCandidates; reject missing, invalid or empty GUIDs. Deduplicate by GUID (C10),
not by a pair of independently mutable GUID/DN lists.

Use one canonical fingerprint implementation: distinct nonempty GUIDs, lowercase N-format,
ordinal sort, newline join, UTF-8, SHA256.HashData. Persist a version prefix such as `v1:`.
Reject empty sets; count remains metadata, never the equality check.

After successful validation, keep the fingerprint with copies of its corresponding upload
addresses, filename and validated ticket. Submission captures that confirmation record before
its first await. C7 must preserve this binding; computing a hash from one result while later
serializing a replaced parsedEmails field is unsafe.

Keep runtime re-resolution. The processor makes a member set executable only after complete
resolution and matching fingerprint. Same-size different identities refuse; order, duplicate
and DN changes with identical GUIDs do not.

## Actual writes and observation

Preserve the current batches and single runspace, supplying GUID-only alternative DNs such
as `<GUID=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx>` as the member values of Set-ADGroup -Add.
Do not use `<GUID=...>;DN` (extended response syntax) or silently fall back to ordinary DNs.

Microsoft documents GUID-only alternative DNs as attribute values in modify requests, and
object-reference creation resolves that GUID to an existing object. Set-ADGroup documents
multiple LDAP attribute values in -Add. This supports the proposed boundary but is NOT an
observed end-to-end test of the installed cmdlet. Preserve that validation limit in the
implementation record; no live probe is authorized by this guidance.

- [Alternative DN forms](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/92a54869-38d7-4e71-a3be-5f67a0dcdd7e)
- [Object-reference behavior](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/538ec135-1465-4266-bb0a-777924f02846)
- [Set-ADGroup](https://learn.microsoft.com/en-us/powershell/module/activedirectory/set-adgroup?view=windowsserver2025-ps)
- [Add-ADGroupMember](https://learn.microsoft.com/en-us/powershell/module/activedirectory/add-adgroupmember?view=windowsserver2025-ps)

Add-ADGroupMember also documents GUID arrays, but replacing the bulk attribute path adds
unmeasured member-resolution costs; it is not the preferred bulk-preserving design.

Before clear, read primary-group member identities. Refuse if that set is unreadable or
contains a GUID outside the approved set, explaining that these members cannot be removed
by this operation. If it is a subset, proceed. This chooses C4's explicit refusal option and
implements the owner's exact-set ruling. Never change anyone's primaryGroupID.

Derive must not expand the approved expected set from a post-write observation. Unexpected
primary members appearing after preflight are a mismatch, not SucceededWithExceptions.
ReadBackMembers and Derive compare observed GUID sets; DNs are descriptive metadata. Batch
map every observed DN to GUID in the existing runspace if necessary. Missing/ambiguous mappings
mean CouldNotConfirm, not silently omitted members. Do not use QueryMemberDetails' permissive
error handling or reintroduce Get-ADGroupMember and its size cap.

The observation remains non-atomic. Concurrent directory changes or another replace cannot be
made transactional by this fix; existing no-locking/no-rollback decisions remain in force.

## Older payloads and tests

Add a nullable persisted fingerprint and semantically refuse absent, malformed or unsupported
versions, telling the operator to validate and resubmit. Do not invent an approved fingerprint
from the runtime set, migrate old jobs or make the property JSON-required: deserialization
failure in CountRows bypasses the processor mutation-audit path. Refuse with one failed audit
and a concrete result/notification reason; the runner continues after failed rows, so the
Replace row must not duplicate an earlier refusal audit. Startup already interrupts old
queued/running jobs rather than resuming them.

Essential guards: same-size different set; renamed DNs with identical GUIDs; absent/invalid
GUIDs; order/duplicate invariance; absent/unknown fingerprint; extra/included/unreadable
primary members; real add arguments use GUID-only identities; identity-aware read-back and
exactly one audit with a specific completion reason. Prove new guards by compiled mutations.

C2's authorization boundary is recorded separately in `cdr-c2-design.md`.
