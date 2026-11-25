#!/usr/bin/env bash
# Demo: build the web UI + the .NET runtime, serve both at http://127.0.0.1:8787, then (optionally)
# drive the Ops Helper agent over the Thread Client API.
#   ./demo.sh                 scripted demo, keeps serving until Ctrl-C
#   DEMO_SCRIPT=0 ./demo.sh   skip the scripted messages
# ops_helper needs `ollama pull qwen2.5:3b`; ops_helper_claude / ops_helper_codex use your CLI logins.
set -euo pipefail
cd "$(dirname "$0")"
ROOT=$PWD
TOKEN=${AGENT_RUNTIME_TOKEN:-$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')}; ADDR=127.0.0.1:8787; API=http://$ADDR/api
[ -d ui/node_modules ] || (cd ui && npm ci --silent)
(cd ui && npm run -s build >/dev/null)
DB=$(mktemp -d)/demo.db
dotnet build -v q StandardAgents.sln >/dev/null
AGENT_RUNTIME_TOKEN=$TOKEN dotnet src/StandardAgents.Cli/bin/Debug/net10.0/StandardAgents.Cli.dll serve \
  --package "$ROOT/examples/demo-agent" --port 8787 --db "$DB" --ui "$ROOT/ui/dist" &
PID=$!
trap 'kill $PID 2>/dev/null' EXIT
until curl -sf -H "Authorization: Bearer $TOKEN" $API/agents >/dev/null; do sleep 0.3; done
echo; echo "  ▶ Web UI:  http://$ADDR/#token=$TOKEN"; echo
h=(-sS -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json')
if [ "${DEMO_SCRIPT:-1}" != 0 ]; then
  echo "== agents"; curl "${h[@]}" $API/agents | jq -c '.agents[] | {id,title}'
  TID=$(curl "${h[@]}" -X POST $API/threads -d '{"agent_id":"ops_helper","tags":["demo"],"env":{"TEAM":"platform"}}' | jq -r .threadId)
  echo "== thread $TID (open it in the UI)"
  send() { echo; echo "you> $1"; curl "${h[@]}" -X POST $API/threads/$TID/messages -d "$(jq -n --arg c "$1" '{role:"user",content:$c}')" >/dev/null
    sleep 0.5
    until [ "$(curl "${h[@]}" $API/threads/$TID | jq -r .status.state)" != running ]; do sleep 0.5; done
    curl "${h[@]}" "$API/threads/$TID/messages?limit=20" | jq -r '.messages | (map(.role)|rindex("user")) as $u | .[$u+1:][] | if .role=="tool" then "  ← \(.name): \(.content)" elif .tool_calls then "  → \(.tool_calls|fromjson|map(.function.name+.function.arguments)|join(", "))" else "agent> \(.content)" end' ; }
  send "What is (1234 + 4321) * 2? Use the calculator."
  send "Remember under key oncall that Sam is on call this week."
  send "Who is on call? Check memory key oncall."
fi
echo; echo "Serving .NET runtime + UI at http://$ADDR/ — Ctrl-C to stop."
wait $PID
