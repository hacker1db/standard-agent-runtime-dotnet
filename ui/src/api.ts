// Thin helpers for routes @standardagents/client does not wrap (thread list, delete).
export const ENDPOINT = "/api";
export const TOKEN_KEY = "agentbuilder_auth_token";

export function getToken(): string {
  try {
    return localStorage.getItem(TOKEN_KEY) ?? "";
  } catch {
    return "";
  }
}

export function setToken(token: string): void {
  try {
    localStorage.setItem(TOKEN_KEY, token);
  } catch {
    /* storage unavailable */
  }
}

export interface ThreadListEntry {
  id: string;
  agent_id: string;
  created_at: number;
  last_activity_at?: number;
  last_message_preview?: string | null;
  status?: string;
  status_reason?: string | null;
  terminated?: boolean;
  agent?: { name: string; title: string };
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(`${ENDPOINT}${path}`, {
    ...init,
    headers: { Authorization: `Bearer ${getToken()}`, ...(init.headers ?? {}) },
  });
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      if (body?.error?.message) message = body.error.message;
    } catch {
      /* non-JSON error */
    }
    throw new Error(message);
  }
  return res.json() as Promise<T>;
}

export async function listThreads(): Promise<ThreadListEntry[]> {
  const body = await request<{ threads: ThreadListEntry[] }>("/threads?limit=100");
  return body.threads ?? [];
}

export async function deleteThread(id: string): Promise<void> {
  await request(`/threads/${encodeURIComponent(id)}`, { method: "DELETE" });
}
