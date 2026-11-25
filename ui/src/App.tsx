import { useCallback, useEffect, useMemo, useState } from "react";
import { AgentBuilderClient, AgentBuilderProvider, ThreadProvider, type AgentListEntry } from "@standardagents/react";
import { ENDPOINT, deleteThread, getToken, listThreads, setToken, type ThreadListEntry } from "./api";
import { ChatPane } from "./Chat";

const DEFAULT_TOKEN = "dev-token";

export function App() {
  const [token, setTokenState] = useState(() => {
    const t = getToken();
    if (!t) setToken(DEFAULT_TOKEN);
    return t || DEFAULT_TOKEN;
  });
  const [tokenDraft, setTokenDraft] = useState(token);
  const client = useMemo(() => new AgentBuilderClient(ENDPOINT), []);
  const [agents, setAgents] = useState<AgentListEntry[]>([]);
  const [agentId, setAgentId] = useState("");
  const [threads, setThreads] = useState<ThreadListEntry[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refreshThreads = useCallback(async () => {
    try {
      setThreads(await listThreads());
    } catch (e) {
      setError(`Threads: ${(e as Error).message}`);
    }
  }, []);

  useEffect(() => {
    setError(null);
    client
      .listAgents()
      .then((list) => {
        setAgents(list);
        setAgentId((cur) => (cur && list.some((a) => a.id === cur) ? cur : list[0]?.id ?? ""));
      })
      .catch((e: Error) => setError(`Agents: ${e.message}`));
    void refreshThreads();
  }, [client, token, refreshThreads]);

  const saveToken = () => {
    const t = tokenDraft.trim();
    setToken(t);
    setTokenState(t);
  };

  const newThread = async () => {
    if (!agentId) return;
    try {
      const { threadId } = await client.createThread({ agent_id: agentId });
      await refreshThreads();
      setActiveId(threadId);
    } catch (e) {
      setError(`Create thread: ${(e as Error).message}`);
    }
  };

  const removeThread = async (id: string) => {
    if (!confirm("Delete this thread?")) return;
    try {
      await deleteThread(id);
      if (activeId === id) setActiveId(null);
      await refreshThreads();
    } catch (e) {
      setError(`Delete: ${(e as Error).message}`);
    }
  };

  const active = threads.find((t) => t.id === activeId);

  return (
    <AgentBuilderProvider config={{ endpoint: ENDPOINT }}>
      <div className="layout">
        <aside className="sidebar">
          <div className="brand">Standard Agents</div>
          <label className="field">
            <span>Token</span>
            <div className="row">
              <input
                type="password"
                value={tokenDraft}
                onChange={(e) => setTokenDraft(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && saveToken()}
                aria-label="Bearer token"
              />
              <button onClick={saveToken} disabled={tokenDraft.trim() === token}>
                Save
              </button>
            </div>
          </label>
          <label className="field">
            <span>Agent</span>
            <select value={agentId} onChange={(e) => setAgentId(e.target.value)} aria-label="Agent">
              {agents.length === 0 && <option value="">No agents</option>}
              {agents.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.title}
                </option>
              ))}
            </select>
          </label>
          <button className="primary" onClick={newThread} disabled={!agentId}>
            + New thread
          </button>
          {error && <div className="error" role="alert">{error}</div>}
          <div className="threads-head">
            <span>Threads</span>
            <button className="ghost" onClick={refreshThreads} title="Refresh">
              ↻
            </button>
          </div>
          <ul className="threads">
            {threads.map((t) => (
              <li key={t.id} className={t.id === activeId ? "active" : ""}>
                <button className="thread-item" onClick={() => setActiveId(t.id)}>
                  <span className="thread-title">
                    {t.agent?.title ?? t.agent_id}
                    <span className={`dot ${t.status ?? ""}`} title={t.status} />
                  </span>
                  <span className="thread-preview">{t.last_message_preview || "No messages yet"}</span>
                </button>
                <button className="ghost danger" onClick={() => removeThread(t.id)} title="Delete thread" aria-label="Delete thread">
                  ✕
                </button>
              </li>
            ))}
            {threads.length === 0 && <li className="empty">No threads</li>}
          </ul>
        </aside>
        <main className="main">
          {activeId ? (
            <ThreadProvider key={`${activeId}:${token}`} threadId={activeId} useWorkblocks>
              <ChatPane title={active?.agent?.title ?? active?.agent_id ?? "Thread"} onActivity={refreshThreads} />
            </ThreadProvider>
          ) : (
            <div className="placeholder">Select a thread or create a new one.</div>
          )}
        </main>
      </div>
    </AgentBuilderProvider>
  );
}
