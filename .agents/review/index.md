# Review Status Index

One row per admitted finding. Details live in the finding record, never here.

Legend: `[ ]` open - `[~]` in progress - `[v]` verified/awaiting merge -
`[d]` merged/awaiting deletion - `[x]` complete - `[!]` contested - `[-]` declined.

| | ID | Severity / impact | Status | Branch | Reviewer |
|---|---|---|---|---|---|
| [x] | [gps-1](findings/gps-1.md) | LOW - prompt points the operator at a surface that no longer reports job completion | Complete | - | codex / gpt-5.5-dzs / xhigh |
| [x] | [gps-2](findings/gps-2.md) | LOW - four changed module pages render a stale version number | Complete | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [edl-1](findings/edl-1.md) | MEDIUM - a declined lockdown renders as a red failure row | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [edl-2](findings/edl-2.md) | MEDIUM - a failed Notes stamp is reported inside a green LockdownMove row | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [edl-3](findings/edl-3.md) | MEDIUM - a fresh lookup inherits the previous account's lockdown opt-out | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-1](findings/prog-1.md) | MEDIUM - the progress guard does not prove the slow derive is inside the window | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-2](findings/prog-2.md) | MEDIUM - DOM @onchange handlers are discovered by nothing | Verified | - | codex / gpt-5.5-dzs / xhigh |
| [ ] | [leak-1](findings/leak-1.md) | HIGH - the recurrence guard is bypassable three ways | Open | - | codex / gpt-5.5-dzs / xhigh |
| [v] | [prog-3](findings/prog-3.md) | MEDIUM - a string literal can still counterfeit the progress proof | Verified | - | codex / gpt-5.5-dzs / xhigh |

Both from one Change review of `ad3c1e9..e5f9e5c` (the global-progress sweep), 2026-10-02.
Raw reviewer output: `gps-sweep.result.json`; prompt and schema alongside it.

`edl-1` and `edl-2` are both from one Change review of `1bbbb73..1313fb4` (queue item
18 slice S1), 2026-10-05. Raw reviewer output: `ed-s1.result.json`; prompt and schema
alongside it.

`edl-3` came from the S2 Change review of `2d0494c..b164d7b`, 2026-10-05. Raw output:
`ed-s2.result.json`. Its first fix was reopened on the GUARD rather than the fix and
repaired in `7e11c64`; the repair-delta round is `edl-3r.result.json`.

All three `edl-*` findings are `[v]` and not `[x]`: each is verified on main, but the
push is outstanding (`.agents/push-policy.md` is `ask`; the owner declined on
2026-10-05). They close when it lands.
