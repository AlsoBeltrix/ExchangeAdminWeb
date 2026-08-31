# Token Log

One line per landed slice, appended in the same commit that lands the slice - part of
the existing paperwork motion, not a separate ritual (docs/TokenBudget-Plan.md S3, AC9).

Figures come from `tools/Get-TokenUsage.ps1 -GroupBy Session` for the session that
produced the slice, on this machine's transcripts only. Costs are ESTIMATES at
first-party list rates (the tool prints its rate table); the model column is the model
that implemented the slice. Slices sharing one session share one line, labelled so.

The committed baseline these compare against is `.agents/token-baseline.json`
(August 2026; regenerate on or after 2026-09-01 so the full month is in it).

Format: `date  slice  model  reqs N  mean-ctx NK  est $X.XX`

2026-08-27  TokenBudget S1+S2  fable-5 (est at opus-5 rates)  reqs 62  mean-ctx 76K  est $6.79
2026-08-27  TokenBudget S3+S4  fable-5 (est at opus-5 rates)  reqs 64  mean-ctx 79K  est $7.17  (same session as S1+S2; session total at close, supersedes the line above)
2026-08-27  GroupMemberNesting S1-S6 + gmn-4/5 fixes  fable-5 (est at opus-5 rates)  day-total reqs 179  mean-ctx 225K  est $34.89  (day shared with TokenBudget S1-S4; codex reviews bill on their own harness)
2026-08-28  GroupListingCrossDomainFix  fable-5 (est at opus-5 rates)  reqs 38  mean-ctx 219K  est $8.76  (session figures at commit; session also spans the unauthorised pgwt start and its diagnosis/cleanup)
2026-08-28  lst-1..lst-3 fixes + review loop  fable-5 (est at opus-5 rates)  reqs 70  mean-ctx 312K  est $18.04  (same session; session total at lst-2 close, supersedes the line above; codex reviews bill on their own harness)
2026-08-28  ProtectedGroupWriteTarget S1-S4  fable-5 (est at opus-5 rates)  reqs 90  mean-ctx 359K  est $28.47  (same session; session total at S4 close, supersedes the line above)
2026-08-28  pgwt range-review fixes pgwt-4..9  fable-5 (est at opus-5 rates)  reqs 126  mean-ctx 431K  est $41.59  (same session; session total at loop close, supersedes the line above; codex reviews bill on their own harness)
2026-08-31  pgwt AC4 reversal (self-service target gate removed) + dev browser validation  fable-5 (est at opus-5 rates)  day-total reqs 116  mean-ctx 222K  est $31.17  (day shared with the crashed morning session; codex reviews none)
2026-08-31  GroupSearchForestScope  fable-5 (est at opus-5 rates)  day-total reqs 166  mean-ctx 239K  est $41.28  (same session; supersedes the line above; day shared with the crashed morning session)
2026-08-31  ProtectedTargetQueryGate (GroupManagement 2.6.0)  fable-5 (est at opus-5 rates)  day-total reqs 222  mean-ctx 267K  est $55.04  (same session; supersedes the line above; day shared with the crashed morning session)
2026-08-31  fsr-1 cross-domain routing fix + codereview round  fable-5 (est at opus-5 rates)  day-total reqs 262  mean-ctx 292K  est $67.25  (same session; supersedes the line above; codex generation pass bills on its own harness)
2026-08-31  BitLockerMandatoryTicket plan draft  fable-5 (est at opus-5 rates)  day-total reqs 327  mean-ctx 313K  est $85.03  (new session, same day; supersedes the line above as the day total)
