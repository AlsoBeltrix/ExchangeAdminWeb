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
2026-08-27  GroupMemberNesting S1-S6 + gmn-4/5 fixes  fable-5 (est at opus-5 rates)  day-total reqs 179  mean-ctx 225K  est \(@{group=Total; requests=179; inputTokens=60161; cacheWriteTokens=926386; cacheReadTokens=39253112; outputTokens=366951; meanContext=224803; maxContext=539690; over200K=82; estimatedCostUsd=34.89}.estimatedCostUsd)  (day shared with TokenBudget S1-S4; codex reviews bill on their own harness)
