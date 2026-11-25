import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from "react";
import { useThread, type Message, type ThreadMessage, type WorkItem, type WorkMessage } from "@standardagents/react";

function isWorkblock(m: ThreadMessage): m is WorkMessage {
  return (m as WorkMessage).type === "workblock";
}

function prettyJson(text: string | null | undefined): string {
  if (text == null || text === "") return "";
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

function ToolCallCard({ call, result }: { call: WorkItem; result?: WorkItem }) {
  const state = result ? (result.status === "error" ? "error" : "success") : call.status === "error" ? "error" : "pending";
  return (
    <details className={`tool tool-${state}`} open>
      <summary>
        <span className="tool-icon" aria-hidden>⚙</span>
        <span className="tool-name">{call.name ?? "tool"}</span>
        <span className={`badge badge-${state}`}>{state === "pending" ? "running…" : state}</span>
      </summary>
      <div className="tool-section">
        <div className="tool-label">Arguments</div>
        <pre>{prettyJson(call.content) || "{}"}</pre>
      </div>
      {result && (
        <div className="tool-section">
          <div className="tool-label">Result</div>
          <pre>{prettyJson(result.content)}</pre>
        </div>
      )}
    </details>
  );
}

function Workblock({ block }: { block: WorkMessage }) {
  const results = new Map<string, WorkItem>();
  for (const item of block.workItems) {
    if (item.type === "tool_result" && item.tool_call_id) results.set(item.tool_call_id, item);
  }
  return (
    <div className="msg msg-assistant">
      <div className="msg-role">assistant · tools</div>
      {block.content && <div className="msg-body">{block.content}</div>}
      {block.workItems.map((item) => {
        if (item.type === "tool_call") {
          return <ToolCallCard key={item.id} call={item} result={results.get(item.tool_call_id ?? item.id)} />;
        }
        if (item.type === "content" && item.content) {
          return (
            <div key={item.id} className="msg-body">
              {item.content}
            </div>
          );
        }
        if (item.type === "tool_result" && !block.workItems.some((c) => c.type === "tool_call" && (c.tool_call_id ?? c.id) === item.tool_call_id)) {
          return <ToolCallCard key={item.id} call={{ ...item, type: "tool_call", content: null }} result={item} />;
        }
        return null;
      })}
    </div>
  );
}

function MessageBubble({ message }: { message: Message }) {
  const role = message.role;
  if (role === "system" && message.silent) {
    return <div className="msg-status">{message.content}</div>;
  }
  if (role === "tool") {
    return (
      <div className="msg msg-assistant">
        <ToolCallCard call={{ id: message.id, type: "tool_call", name: message.name ?? "tool", content: null }} result={{ id: message.id, type: "tool_result", content: message.content ?? null, status: message.tool_status === "error" ? "error" : "success" }} />
      </div>
    );
  }
  return (
    <div className={`msg msg-${role}`}>
      <div className="msg-role">{role}</div>
      <div className="msg-body">{message.content}</div>
    </div>
  );
}

function RunStatus() {
  const { runStatus, connectionStatus } = useThread();
  const state = runStatus?.state ?? "unknown";
  return (
    <div className="status">
      <span className={`pill pill-${state}`}>
        {state === "running" && <span className="spinner" aria-hidden />}
        {state}
        {runStatus?.reason && state !== "running" ? ` · ${runStatus.reason}` : ""}
      </span>
      <span className={`conn conn-${connectionStatus}`} title={`Stream: ${connectionStatus}`}>
        {connectionStatus}
      </span>
    </div>
  );
}

export function ChatPane({ title, onActivity }: { title: string; onActivity: () => void }) {
  const { workblocks, sendMessage, stopExecution, runStatus, loading, error } = useThread();
  const [draft, setDraft] = useState("");
  const [sendError, setSendError] = useState<string | null>(null);
  const endRef = useRef<HTMLDivElement>(null);
  const running = runStatus?.state === "running";

  useEffect(() => {
    endRef.current?.scrollIntoView({ block: "end" });
  }, [workblocks]);

  useEffect(() => {
    if (runStatus && runStatus.state !== "running") onActivity();
  }, [runStatus?.state, runStatus?.since]); // eslint-disable-line react-hooks/exhaustive-deps

  const submit = async (e?: FormEvent) => {
    e?.preventDefault();
    const content = draft.trim();
    if (!content) return;
    setSendError(null);
    setDraft("");
    try {
      await sendMessage({ role: "user", content });
    } catch (err) {
      setDraft(content);
      setSendError((err as Error).message);
    }
  };

  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      void submit();
    }
  };

  return (
    <div className="chat">
      <header className="chat-head">
        <h1>{title}</h1>
        <RunStatus />
        <button className="stop" onClick={() => void stopExecution()} disabled={!running}>
          ■ Stop
        </button>
      </header>
      <div className="messages">
        {loading && workblocks.length === 0 && <div className="placeholder">Loading…</div>}
        {error && <div className="error">{error.message}</div>}
        {!loading && workblocks.length === 0 && <div className="placeholder">Send a message to start.</div>}
        {workblocks.map((m) => (isWorkblock(m) ? <Workblock key={m.id} block={m} /> : <MessageBubble key={m.id} message={m} />))}
        <div ref={endRef} />
      </div>
      <form className="composer" onSubmit={submit}>
        {sendError && <div className="error">{sendError}</div>}
        <textarea
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={onKey}
          placeholder="Message… (Enter to send, Shift+Enter for newline)"
          rows={2}
          aria-label="Message"
        />
        <button className="primary" type="submit" disabled={!draft.trim()}>
          Send
        </button>
      </form>
    </div>
  );
}
