# Declarative Package Format & Runtime Contract

The Standard Agent Specification v0.2.0 (https://standardagentspec.org) defines agents in
TypeScript (`defineTool({ execute })`). Native runtimes cannot import TS modules, so this
runtime loads a **declarative JSON form of the same definitions**. Field names match the
spec exactly (`sideA`, `stopOnResponse`, `toolDescription`, ...). Only `execute` differs: function
tools are backed by a subprocess `command`.

## Package layout

```
my-package/
  package.json        # spec §Packaging: name, version, keywords:["standardagent"], standardagent.entryAgents
  agents.json         # { agents:{}, prompts:{}, models:{}, tools:{} }   (registries keyed by name)
  tools/...           # executables referenced by function tools
```

### agents.json

```jsonc
{
  "models":  { "local": { "name": "local", "provider": "openai", "model": "qwen2.5:3b",
                          "baseUrl": "http://localhost:11434/v1", "apiKeyEnv": "OPENAI_API_KEY",  // optional: no key → no Authorization header
                          "fallbacks": [] } },
  "prompts": { "main": { "name": "main", "toolDescription": "...", "model": "local",
                         "prompt": "text with {{VAR}}" | [ {"type":"text","content":"..."}, {"type":"include","prompt":"other"} ],
                         "tools": ["calc", {"name":"summarize","includeTextResponse":true,"initUserMessageProperty":"text"}],
                         "toolChoice": "auto", "env": {"VAR":"default"} } },
  "tools":   { "calc": { "description": "...", "args": { JSON Schema object },
                         "command": ["python3", "tools/calc.py"], "uses": [] } },
  "agents":  { "demo": { "name": "demo", "title": "Demo", "description": "...",
                         "sideA": { "prompt": "main", "stopOnResponse": true, "stopTool": null, "maxSteps": 10 },
                         "sideB": { "human": true } } }
}
```

Providers: `openai` (any OpenAI-compatible `/chat/completions`, e.g. Ollama) and `mock`
(deterministic, for tests: if the last message is a user message starting with `/tool NAME JSON`
it emits that tool call, otherwise echoes `echo: <last user text>`).

### Function tool protocol

Runtime spawns `command` with cwd = package dir, writes JSON to stdin:
`{"args":{...},"env":{...},"threadId":"..."}`. The tool prints JSON to stdout:
`{"status":"success","result":"..."}` or `{"status":"error","error":"..."}`. Non-zero exit,
timeout (30s) or invalid JSON → error tool result (spec Runtime §5.2: never crashes the loop).

Instead of `command`, a tool may set `"builtin"`: `kv_set` (args `key`,`value` → thread KV
`setValue`) or `kv_get` (args `key` → value or `null`). These exercise Runtime §14.

### Prompt tools

A prompt named in another prompt's `tools` is exposed as a tool with the prompt's
`toolDescription`, args `{ "<initUserMessageProperty or 'input'>": string }`. It runs a nested
step loop (stopOnResponse) and returns its final text as the tool result.

## Semantics implemented (spec references)

- Runtime §3 step cycle: drain queue → assemble context → call model → persist assistant →
  execute tools sequentially → persist results → evaluate stops.
- Runtime §4: system prompt interpolation (`{{KEY}}` from thread env, then prompt `env` defaults),
  side's own turns `assistant`, other side `user`.
- Prompts §2.4: recursive includes, circular include rejected at load.
- Runtime §5.2 tool errors persisted; §6 stop order `stopTool` → `stopOnResponse` → `maxSteps`
  (default 25, hard cap 100).
- Runtime §9 durable FIFO queue (SQLite), §11 one execution per thread, §10 terminate,
  §14 durable per-thread KV (`getValue`/`setValue`, null deletes).
- Models §3 fallback chain on provider error.

## HTTP (Thread Client API), prefix `/api`

Bearer auth (`AGENT_RUNTIME_TOKEN`; default `dev-token` only when bound to loopback; empty token
rejected; constant-time compare). Every error is `{error:{code,message}}`; internal failures 5xx.

| Route | Notes |
|---|---|
| `POST /api/auth/websocket-ticket` | `{pathname}` → `{ticket, expires_in:60}`, `Cache-Control: no-store`; pathname must be normalized and under `/api`; single-use, path-bound |
| `GET /api/agents` | AgentList; only creatable agents (prompt sideA + human sideB, in entryAgents); `?primary=true` filter |
| `POST /api/threads` | `{agent_id, tags?, env?}` → `{threadId, agent_id}`; `null`/wrong types → 400; unknown agent → 404 |
| `GET /api/threads` | ThreadList (`limit`,`offset`,`agent_id`) |
| `GET /api/threads/{id}` | ThreadRead; `status` = `{state, since(µs, changes only on state/reason change), reason}` |
| `PATCH /api/threads/{id}` | `{tags}` |
| `DELETE /api/threads/{id}` | terminate → abort in-flight run → wait for it to exit → delete rows → close streams (1000) → 200 |
| `GET /api/threads/{id}/messages` | MessagesPage (`limit`,`offset`,`order`,`includeSilent`,`depth`) |
| `POST /api/threads/{id}/messages` | `{role, content}` → `202 {status:"queued", messageId}`; 423 if terminated (atomic check with enqueue) |
| `DELETE /api/threads/{id}/messages/{mid}` | `{success:true}` / 404 |
| `GET /api/threads/{id}/fs` | `{files:[]}` (no filesystem yet) ; `GET .../fs/{path}` → 404 |
| `POST /api/threads/{id}/stop` | Client API §15: abort in-flight run only, settles `idle`/`aborted_by_user`; idle thread unchanged; → `{status:"stopped"}`. Thread stays open. |
| `GET /api/threads/{id}/stream?ticket=..&depth=&includeSilent=` | WebSocket, auth by ticket only. First frame `thread_status`; `thread_status` on every change; `message_data` on every message create/update (respecting depth/includeSilent); text `stream_ping` → `stream_pong`. Non-upgrade → 400. |

Message record (spec Messages §1): `{id, role, content, name, tool_calls (JSON-encoded STRING or null),
tool_call_id, tool_status, status ("completed"), attachments:null, reasoning_content:null,
reasoning_details:null, prompt, created_at(µs), parent_id:null, depth, silent, metadata:{}}`.
Runtime status messages (maxSteps, failures) are `role:"system"`, `silent:true`,
`metadata.status_kind` set, and are never sent to the model; client-sent system messages are.

Run reasons: `stop_on_response`, `stop_tool`, `max_side_steps`, `aborted_by_user`, `step_error`;
on startup a thread left `running` becomes `failed`/`execution_lost`, and threads with queued
messages are resumed. Context assembly inserts a synthetic error tool result for any assistant
tool call left unanswered (crash/abort). Env precedence: prompt.env < tool-ref env < thread env.
Prompt tools nest at most 3 deep. Tool subprocesses get a cleared environment (PATH, HOME, LANG,
TMPDIR only), stdin is written inside the 30s timeout, stdout capped at 1 MiB, and the process
group is killed on timeout/abort.

The web UI (`ui/`, built on `@standardagents/react`) is served by the runtime at `/` when
`serve --ui DIR` is given.

## Subscription providers (extension, not in the spec)

| provider | Runs | Auth used |
|---|---|---|
| `claude-cli` | `claude -p --output-format json --tools "" --strict-mcp-config --no-session-persistence --setting-sources "" --system-prompt <sys> [--model M]`, transcript on stdin, reads `.result` | your Claude Code login (Pro/Max) |
| `codex-cli` | `codex exec --skip-git-repo-check --ephemeral --sandbox read-only --color never -o <tmp> [-m M] -`, prompt on stdin, reads `<tmp>` | your Codex/ChatGPT login |

`model: "default"` omits the model flag. The child process gets `ANTHROPIC_API_KEY`,
`ANTHROPIC_AUTH_TOKEN`, `OPENAI_API_KEY`, `CLAUDECODE`, `CLAUDE_CODE_ENTRYPOINT` removed and
cwd = temp dir. The CLI's own tools are off (claude) / read-only sandbox (codex); the runtime
executes tools itself. Tools are described in the system prompt and the model must reply with
one JSON object: `{"tool_calls":[{"name":..,"arguments":{..}}]}` or `{"content":".."}`
(first balanced JSON object in the output is used; otherwise the whole text is the answer).
Only the vendors' official CLIs are invoked in their documented headless modes; no tokens are
read or replayed.
`SA_CLAUDE_BIN` / `SA_CODEX_BIN` override the executable. `serve` refuses a non-loopback bind for
a package with a CLI-backed model unless `--allow-remote` is given.
