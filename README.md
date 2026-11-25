# standard-agent-runtime-dotnet

> **Status: experimental.** The core step loop, tools, and Thread Client API work; see [Known gaps](#known-gaps).

A .NET 11 runtime for the Standard Agent Specification v0.2.0 that loads the declarative JSON packages described in [`FORMAT.md`](FORMAT.md).

| Project | Purpose |
|---|---|
| `src/StandardAgents.Runtime` | Package loader (includes, cycle detection, validation), `{{VAR}}` interpolation, `openai`, `mock`, `claude-cli` and `codex-cli` providers, SQLite store (threads, messages, queue, KV), step loop, tools |
| `src/StandardAgents.Cli` | `serve`, `chat`, and `run` commands, plus the `/api` HTTP surface (`Api.cs`) |
| `ui/` | Web chat UI built on `@standardagents/react`, served at `/` with `serve --ui ui/dist` |
| `examples/demo-agent/` | Demo package: `ops_helper` (Ollama), `ops_helper_claude` / `ops_helper_codex` (subscription CLIs), `echo_bot` (mock) |
| `tests/StandardAgents.Tests` | xunit tests that use only the mock provider (the calculator test needs `python3`) |

## Build and test

```bash
dotnet build StandardAgents.sln
dotnet test StandardAgents.sln
```

## Demo with the web UI

```bash
./demo.sh            # builds ui/ and the runtime, serves both at http://127.0.0.1:8787
```

## Usage

`dotnet run` changes the working directory to the project, so pass an absolute `--package` path, or run the built dll:

```bash
CLI="dotnet src/StandardAgents.Cli/bin/Debug/net11.0/StandardAgents.Cli.dll"

# One-shot run (uses a throwaway temp DB unless --db is given)
$CLI run --package examples/demo-agent --agent echo_bot --message hello
$CLI run --package examples/demo-agent --agent echo_bot --message '/tool calculator {"expression":"1+2"}'
$CLI run --package examples/demo-agent --agent ops_helper --message "what is 17*23?"   # needs Ollama + qwen2.5:3b

# Interactive REPL (an empty line exits). Prints tool calls, tool results, and replies.
$CLI chat --package examples/demo-agent --agent ops_helper --db chat.db

# HTTP server
AGENT_RUNTIME_TOKEN=dev-token $CLI serve --package examples/demo-agent --port 8788 --db agents.db
```

### HTTP example

```bash
H="Authorization: Bearer dev-token"
TID=$(curl -s -H "$H" -H 'content-type: application/json' -d '{"agent_id":"echo_bot"}' localhost:8788/api/threads | jq -r .threadId)
curl -s -H "$H" -H 'content-type: application/json' -d '{"role":"user","content":"hi"}' localhost:8788/api/threads/$TID/messages   # 202
curl -s -H "$H" "localhost:8788/api/threads/$TID/messages?order=asc"
curl -s -H "$H" -X POST localhost:8788/api/threads/$TID/stop    # terminate; later sends return 423
```

The server implements these routes: `GET /api/agents`, `POST|GET /api/threads`, `GET|PATCH|DELETE /api/threads/{id}`, `GET|POST /api/threads/{id}/messages`, and `POST /api/threads/{id}/stop`. Errors use the body `{"error":{"code","message"}}`.

## Semantics

- **Step loop:** drain the queue, build the context (the interpolated system prompt, then the history), call the model with its fallback chain, persist the assistant message, run the tools in order, and persist their results. Stop checks run in this order: `stopTool`, then `stopOnResponse` (default `true`), then `maxSteps` (default 25, capped at 100).
- **One execution per thread:** a per-thread `SemaphoreSlim`. Messages that arrive during a run are drained at the next step, or by a follow-up pass.
- **Tools:**
  - A subprocess tool gets JSON on stdin and has a 30 s timeout.
  - A non-zero exit, invalid JSON, or a timeout becomes an `error` tool message.
  - The `kv_set` and `kv_get` builtins read and write the per-thread KV. Setting `value: null` deletes the key.
- **Prompt tools:** a nested loop at `depth + 1` that is persisted but excluded from the depth-0 context. Its argument is named by `initUserMessageProperty`, or `input` when that is not set.

## Known gaps

The following are not implemented: WebSocket streams and tickets, handlers, hooks, effects, subagents, attachments and the filesystem, `runCode`, decisions, users, `DELETE /messages/{id}`, and `includeTextResponse` (prompt tools always return their final text). `stop` terminates the thread, as FORMAT.md says. In the spec, `stop` only aborts the run.

## Subscription providers (`claude-cli`, `codex-cli`)

Extension, not in the spec (see "Subscription providers" in [`FORMAT.md`](FORMAT.md)). `CliProvider.cs` runs the
official, logged-in CLIs headlessly, so no API key is needed:

- `claude-cli`: `claude -p --output-format json --tools "" --strict-mcp-config --no-session-persistence --setting-sources "" --system-prompt <sys> [--model M]`, transcript on stdin, reads `.result` (fails on `is_error`).
- `codex-cli`: `codex exec --skip-git-repo-check --ephemeral --sandbox read-only --color never -o <tmp> [-m M] -`, prompt on stdin, reads and deletes `<tmp>`.

`model: "default"` omits the model flag. The child runs in the temp dir with `ANTHROPIC_API_KEY`,
`ANTHROPIC_AUTH_TOKEN`, `OPENAI_API_KEY`, `CLAUDECODE` and `CLAUDE_CODE_ENTRYPOINT` removed, a 600 s
timeout, and arguments passed via `ArgumentList` (no shell). Tools are listed in the system prompt; the
model replies with `{"tool_calls":[...]}` or `{"content":"..."}` (first balanced JSON object wins,
otherwise the whole text is the answer), and the runtime executes the tools itself.

```bash
dotnet src/StandardAgents.Cli/bin/Debug/net11.0/StandardAgents.Cli.dll run --package examples/demo-agent \
  --agent ops_helper_claude --message "What is 17*23? Use the calculator."   # or ops_helper_codex
```

## License

[MIT](LICENSE)
