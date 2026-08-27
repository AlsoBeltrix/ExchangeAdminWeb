# Machine-Specific Facts

Per-machine local facts (CLI paths, tool versions, host layout). Not portable — keep
portable process/rules in `AGENTS.md` and `.agents/repo-guidance.md`.

## ASHBIAMWEB1 (Windows Server 2022, primary dev/IIS host)

_First recorded 2026-07-21._

### Cross-harness review (`codereview` / `openreview`) reviewers

- **codex** (`codex-cli 0.147.0`, re-probed 2026-08-14; was recorded as `0.146.0` as of
  2026-08-05, `C:\Users\mcoelho\AppData\Roaming\npm\codex.ps1`) — Portkey
  gateway, API-key auth. Model slugs carry a provider-route prefix, e.g.
  `@azure-openai-eus2-global/gpt-5.5-dzs`; the **full prefixed slug must be passed to `--model`**
  (stripping the `@.../` prefix causes an `Either x-portkey-config or x-portkey-provider` failure).
  The `refresh_token_reused` OAuth errors it prints are harmless noise on the API-key path.
- **codex-commercial** (same `codex-cli` engine via wrapper
  `C:\Users\mcoelho\.local\bin\codex-commercial.ps1`) — OpenAI direct, ChatGPT-subscription auth.
  `CODEX_HOME=C:\Users\mcoelho\.codex-commercial`; the wrapper strips `OPENAI_*`/`PORTKEY_*` env
  vars so it uses subscription auth, and syncs the ptk MCP block. Default model `gpt-5.6-sol`,
  effort `max` (in its `config.toml`). Plain slug, **no** provider prefix.
- **codex-commercial registered as a Claude Code MCP server** (user scope):
  `claude mcp add codex-commercial -s user -- pwsh -NoProfile -File C:/Users/mcoelho/.local/bin/codex-commercial.ps1 mcp-server`.
  Use **forward slashes** in the path — the `! `/bash-input layer strips backslashes (observed
  2026-07-21: a backslash path stored mangled). MCP tools: `mcp__codex-commercial__codex` (start,
  returns a `threadId`) and `mcp__codex-commercial__codex-reply` (continue). Effort via
  `config: {"model_reasoning_effort": "max"}`; `sandbox: read-only`; `cwd` pin the repo path. The
  MCP `codex` tool has no `--output-schema` flag — embed the JSON verdict schema in the prompt and
  parse it from the reply. Preferred over the CLI for reviews (the CLI `.ps1` wrapper mangles the
  `exec` positional arg via `ValueFromRemainingArguments`).

### Deploy

- `deploy.ps1` / `deploy-pipeline.ps1 -Dev` must run in **Windows PowerShell 5.1** on this box
  (the `WebAdministration` IIS provider does not load under PowerShell 7). Dev app root:
  `D:\inetpub\ExchangeAdminWebDev`.
- `sqlite3.exe` is on PATH via winget (ops-script dependency for config backup). Re-verified
  2026-08-14 at
  `C:\Users\mcoelho\AppData\Local\Microsoft\WinGet\Packages\SQLite.SQLite_Microsoft.Winget.Source_8wekyb3d8bbwe\sqlite3.exe`.

### Test tooling

_Re-probed 2026-08-14._ Pester `6.0.1` and PSScriptAnalyzer `1.25.0` are installed; the
PowerShell suites run under `pwsh` (`C:\Program Files\PowerShell\7\pwsh.exe`), not 5.1.

### Claude Code transcript root (`tools/Get-TokenUsage.ps1`)

- transcript-root: `C:\Users\mcoelho\.claude\projects\D--source-ExchangeAdminWeb`

This project's transcripts only — the default scope the token baseline is measured on.
Point the tool at `C:\Users\mcoelho\.claude\projects` with `-TranscriptRoot` for every
local project. The script reads the first `transcript-root:` entry in this file; if a
second machine is ever recorded here, pass `-TranscriptRoot` explicitly on it.
- harness-cli: codex.ps1 (recorded 2026-07-27, refresh offer)
- harness-cli: codex.cmd (recorded 2026-07-27, refresh offer)
