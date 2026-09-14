# Cloud password reset: runtime owner investigation

Status: Investigation and proposed design; not an approved implementation plan.
Date: 2026-09-14. Repository base: `85824d8`.

## Required behavior

L2 alone uses the app. The ServiceNow request is outside the app. L2 selects a cloud
account; the app determines its owner's normal mailbox at runtime, changes the
password, and sends it there. There is no employee-facing workflow, advance enrollment,
maintained owner map, or operator-supplied delivery address. The owner clarified that
the population is individually owned employee CLD accounts, not every privileged cloud
identity. These requirements reopen the owner-resolution investigation; implementation
of the held module remains unapproved. See `.agents/decisions.md` 2026-09-14.

## Evidence collected

The September 11 CSV was used as a list of object IDs, not as current directory truth.
Its SHA-256 is `BEA6EB1FE750EC006D4BE370E8B5DF16664BE4BB8228A7D8EB96AAE1F005D30B`.
Current Graph profiles and transitive group memberships were read for all 172 listed
objects, with no failed or truncated responses. Current AD queries used the original
selected domains, `ad.analog.com` and `winroot.analog.com`. Those are the tested scope,
not domain constants suitable for application code.

Candidate discovery queried current account names, UPNs, email addresses, aliases and
surnames; it returned 983 directory objects across both domains without hitting the
5,000-per-domain cap. These objects are search candidates, not 983 proposed recipients.
A broader prefix/name check of the remaining named identities returned seven additional
objects; none supplied a supported owner pair. A targeted contact search produced no
corresponding contact. Selected deleted-object queries returned no matching tombstones;
that does not establish that an account never existed.

Authentication phone methods were read for 92 unresolved or weaker-match cases: 64 had
a phone registration. A directory lookup for those numbers found one exact full-number
match supporting an already discovered employee candidate whose AD login gained a
numeric suffix. This is useful corroboration, not a universal replacement for name and
account evidence. Authentication-device relationship requests for two cases returned
HTTP 400; device-based linkage was not established and is not part of the proposal.

The diagnostic's broad punctuation folding was used for discovery. It is not an
instruction to remove all distinctions between accounts when authorizing delivery.

## What the data establishes

A deterministic, in-memory rule experiment over the current records produced:

| Result | Accounts |
| --- | ---: |
| One enabled, mail-enabled employee-record candidate | 89 |
| A supported employee pair whose AD account is disabled | 1 |
| No supported employee pair in the searched data | 82 |

Here, an employee-record candidate has a nonblank existing AD employee identifier,
an enabled AD account, a nonblank mailbox address and a supported identity match.
That predicate is not an independent HR employment classification.

The selected AD records carry `employeeType=EMP` for **72** accounts and `CWK` for
**17**. The receipt labels these separately and marks only the 72 EMP-tagged pairs
eligible for the employee-only proposal. A directory-backed contingent-worker account
must not be silently counted as an employee. A shipped module must express its approved
workforce scope through deployment configuration rather than infer company semantics
from a hard-coded worker-class string.

All 89 selected cloud accounts are enabled. The proposed rule experiment preserved
**all 80** earlier resolved account-to-AD-user pairs and selected **nine additional**
pairs. Its 89 selections comprise 84 current-primary-alias/name matches, two
current-primary-alias/name-variant matches, and three legacy-alias/exact-name matches.
There were no unresolved ties among the pairs it selected.

**These are candidate-resolution results, not an independently verified 100% ownership
or employee-population coverage claim.** The old 172-object list mixed populations.
Of the 82 without an AD pair, 41 have vSOC membership, 10 have MSFT-DART membership, and
31 are outside those two groups. Current company fields explicitly identify Palo Alto
on 28 unmatched vSOC accounts. Name searches also found corresponding guest identities
with Secureworks, Palo Alto and Arrow mail domains in four cases. The guest matches
support external-affiliation investigation; a matching name alone does not prove the
people are identical. Service-account-looking names remain classification evidence,
not an automatic exclusion rule. Group membership alone is also insufficient: 17 vSOC
members in the old list have AD employee-record candidates.

Neither 'unmatched means external' nor 'has a role means employee' is a valid way to
calculate the intended population. Named accounts whose employee status is unresolved
must remain explicit exceptions until classified independently. They must not be
silently excluded to manufacture a 100% result.

## Concrete failures of the former matching method

1. **Historical aliases were treated like current login names.** One employee's current
   sAMAccountName also survives as a historical SMTP alias on another employee's account.
   The current login and the cloud person's name select one person; an OR query that
   turns either alias hit into a veto reports a false collision.
2. **Email domains were discarded.** An external address and a corporate address share
   a local part. Their directory objects describe different people; the external record
   is also disabled and has no employee identifier. Equal local parts are insufficient
   to equate their ownership evidence.
3. **Cloud legal-name fields were never fetched.** One account's cloud display name uses
   a nickname, while its cloud givenName contains the legal name held in AD. Another
   differs only in spacing inside the given name. The current source fields resolve
   these discrepancies without a per-person nickname table.
4. **Preferred names, suffixes and historical login conventions need separate treatment.**
   The observed cases include a shortened first name, a generational surname suffix,
   a legacy admin suffix, and an AD login that acquired a numeric suffix. These are
   distinguishable from two different people when the rest of the record is considered.

The local per-account diagnostic receipt is listed in `.agents/machines.md`. It includes
actual selections for human inspection without putting employee email addresses in git.

## Proposed runtime algorithm

1. **Read the selected cloud object afresh by object ID.** Use UPN, givenName, surname,
   displayName and current account state. Obtain candidates from the configured directory
   domains using current account names and UPNs, structured names and aliases. A failure,
   incomplete page or result cap is not a successful empty result.
2. **Keep the source of each match.** A current sAMAccountName or current UPN is different
   evidence from a historical SMTP alias. Preserve complete email addresses and their
   namespaces. Historical aliases can discover a candidate; they do not automatically
   outweigh contradictory current identity data.
3. **Corroborate the person using the available legal and preferred names.** Strip only
   the recognized CLD decoration. Handle name order, punctuation and spacing explicitly.
   Compare cloud structured fields and display-name parts against the corresponding AD
   fields. Use name variants only alongside the current account evidence: the experiment
   required a current primary key for a shortened first name or generational-suffix case.
   An arbitrary edit-distance score must not authorize delivery.
4. **Treat legacy login conventions as a separate fallback.** The experiment allowed
   the observed terminal admin marker or AD numeric suffix only with exact first- and
   last-name agreement and one resulting person. Existing authentication data can add
   corroboration where available. Broader similarity alone is not a fallback recipient.
   These legacy rules remain proposals to approve explicitly, not established guarantees.
5. **Select one eligible employee record and take its mailbox from the directory.** Do
   not choose the first hit, discard a known disabled owner in favor of a weaker live
   match, or collapse different people because their aliases overlap. Multiple equally
   supported people, absent ownership evidence, disabled owners and unreadable data
   refuse the reset before the password is changed.
6. **Bind the operation to that result.** Show L2 the resolved identity and destination
   read-only. Revalidate the binding immediately before the reset. Generate the password
   server-side, apply it to the selected cloud object, and pass it directly to the mail
   service addressed only to the resolved mailbox. Keep the existing audit and
   administrator notification requirements, including the destination address. Do not
   place the password in those records or notifications.

This is a rules-based runtime association with directory-sourced delivery. It requires
neither an owner database nor employee participation. The diagnostic receipt is evidence
for evaluating the rules; the application must never read that receipt as an owner map.

## Remaining validation and next action

- Independently classify the remaining named identities against the clarified employee
  scope before making an employee-coverage percentage claim. Current records establish
  some external affiliations, not every remaining account's status.
- Review the 89 proposed pairs and approve the exact legacy/name-variant rules. Matching
  directory attributes provides operational evidence; it is not cryptographic proof of
  a person's employment or ownership.
- Establish the intended L2 role's ability to edit the cloud identity fields and AD
  mailbox/name fields used by the resolver. Owner-only delivery depends on those sources
  being outside the resetting operator's effective write authority. This was not tested.
- On approval of the approach, revise the held implementation plan before shipping code.
  The former typed-destination plan does not implement these requirements. D4 remains an
  old unresolved design question; no reveal-path decision was inferred in this pass.

Verification in this pass: current read-only AD/Graph queries, complete-response/cap
checks, deterministic experiment on the collected records, and comparison preserving
all 80 prior selected pairs. No C# or PowerShell product code, permissions,
passwords, directory attributes or deployment state were changed. No application build,
test suite, reset, mail delivery or independent owner acceptance test was run. Records
require `git diff --check` before commit.
