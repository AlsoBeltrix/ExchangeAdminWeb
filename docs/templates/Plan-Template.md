# <Feature> Plan

Status: Draft | Approved | In progress | Implemented | Superseded
Owner: Michael
Last verified against code: <commit / date>

<!-- Sections marked [YOU] are written or approved by Michael, in plain language.
     Sections marked [MODEL] are drafted by the model and only skimmed by Michael.
     This is a change ticket for source code. Treat it like one. -->

## 1. Goal  [YOU — 3 to 6 sentences]
What the tool needs to do and why. The "this is what the tool needs to do"
statement you already write today goes here, verbatim. No technical vocabulary
required.

## 2. Non-goals  [YOU — bullets]
What this work stream will NOT do. Anything a model proposes later that is not
traceable to §1 lands here or gets cut. This section is the scope fence.

## 3. Acceptance criteria  [YOU approve each; model may propose]
Observable behaviors: "done means...". Each must be checkable by a human or a
test. If you can't picture verifying it, it isn't a criterion yet.
- AC1:
- AC2:

## 4. Failure behavior  [YOU own — this is the risk section of a change ticket]
For each external dependency or risky step: what happens when it fails?

| Step / dependency | If it fails | The user sees | System state afterward |
|---|---|---|---|

## 5. Rollback / blast radius  [YOU own]
How is this change reversed? What is affected if it goes wrong in production?

## 6. Design sketch  [MODEL — Michael skims]
Components touched, new services/pages/scripts, conformance to
docs/AdminModuleSpec.md and CLAUDE.md invariants. Every claim about existing
code must come from reading the current file, not memory.
On request, restate this section as: "runtime moving parts, and what each one
can break" — that is the form Michael reviews it in.

## 7. Task breakdown  [MODEL — Michael skims]
Ordered tasks. Each task cites the acceptance criteria it serves (AC1, AC2...).
A task that cites nothing is out of scope by definition.

## 8. Test plan  [MODEL writes; YOU check the mapping only]
Which tests prove which criterion. xUnit for Services, Pester for .ps1.
Every AC in §3 must appear here at least once. That 1:1 check is the entire
review burden for this section.

## 9. Traceability check  [MODEL fills when iteration ends; YOU read]
List every element of §6–§7 NOT traceable to an acceptance criterion.
Empty list = clean. Non-empty = Michael decides per item: cut it, or
promote it to a new AC in §3.

## 10. Review log  [MODEL appends each round]
Round, reviewer, findings (evidence standard: file:line quotes, named
mechanisms), resolution. Unresolved findings accepted as risk are recorded
here explicitly with Michael's sign-off.
