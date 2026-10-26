# cons-5: a second save while one is running is dropped with no word to the operator

**Severity**: LOW - no wrong write; an operator action disappears. `Components/Pages/ModuleConfig.razor` about 880,
`Components/Pages/AdminSettings.razor` about 392 and `Components/Pages/ExchangeOnlineConfig.razor` about 370: the
aud-a67 (4)/(13) guard returns without a banner, a status line or a refusal record, so a second save - including one for
a different dirty setting on the same page instance - is silently ignored while its setting stays dirty.
**Status**: Open - admitted 2026-10-26 from codex settings-batch-8 on aud-a64/aud-a67; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 620e9d5a head f08a664a (merged fe67668d, with d0fc4c70); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat f08a664a)

## Evidence

Codex settings-batch-8 finding 2 at `fe67668d`.

## Approach

- Keep the one-save guard and show a non-success status ("A save is already running; wait for it to finish.") without
  clearing the dirty state; the three pages are line-keyed, so edit in place or re-anchor. Test per page; mutation: the
  status dropped.

## Guard proof

Pending.