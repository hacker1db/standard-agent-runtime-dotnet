using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StandardAgents.Runtime;

public sealed record ThreadRow(string Id, string AgentId, List<string> Tags, Dictionary<string, string> Env,
    long? Terminated, long CreatedAt, long LastActivityAt);

/// <summary>Message record. <see cref="Status"/> is "pending" only for queued (not yet delivered) messages.</summary>
public sealed record MessageRow(string Id, string ThreadId, string Role, string? Content, string? Name,
    string? ToolCalls, string? ToolCallId, string? ToolStatus, string Side, int Depth, bool Silent, long CreatedAt,
    string? Prompt = null, string? StatusKind = null, string Status = "completed");

/// <summary>Execution State §4 run status: state, since (µs, changes only with state/reason), reason.</summary>
public sealed record ThreadStatus(string State, long Since, string? Reason)
{
    public static readonly ThreadStatus Never = new("idle", 0, null);
}

public enum EnqueueResult { Ok, NotFound, Terminated }

/// <summary>SQLite durability for threads, messages, the per-thread FIFO queue (§9) and KV (§14).</summary>
public sealed class Store
{
    readonly string _cs;
    readonly object _clock = new();
    long _lastMicros;

    public Store(string path)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true, DefaultTimeout = 30 }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS threads(id TEXT PRIMARY KEY, agent_id TEXT NOT NULL, tags TEXT NOT NULL, env TEXT NOT NULL,
              terminated INTEGER, created_at INTEGER NOT NULL, last_activity_at INTEGER NOT NULL,
              status_state TEXT NOT NULL DEFAULT 'idle', status_since INTEGER NOT NULL DEFAULT 0, status_reason TEXT);
            CREATE TABLE IF NOT EXISTS messages(seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT UNIQUE NOT NULL, thread_id TEXT NOT NULL,
              role TEXT NOT NULL, content TEXT, name TEXT, tool_calls TEXT, tool_call_id TEXT, tool_status TEXT,
              side TEXT NOT NULL, depth INTEGER NOT NULL, silent INTEGER NOT NULL, created_at INTEGER NOT NULL,
              prompt TEXT, status_kind TEXT);
            CREATE INDEX IF NOT EXISTS messages_thread ON messages(thread_id, seq);
            CREATE TABLE IF NOT EXISTS queue(seq INTEGER PRIMARY KEY AUTOINCREMENT, thread_id TEXT NOT NULL, message_id TEXT NOT NULL,
              role TEXT NOT NULL, content TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS kv(thread_id TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY(thread_id, key));
            """);
        // Upgrade databases created by earlier versions (columns are appended, so SELECT * indexes stay stable).
        foreach (var (table, col) in new[] { ("threads", "status_state TEXT NOT NULL DEFAULT 'idle'"), ("threads", "status_since INTEGER NOT NULL DEFAULT 0"),
                     ("threads", "status_reason TEXT"), ("messages", "prompt TEXT"), ("messages", "status_kind TEXT") })
            try { Exec(c, $"ALTER TABLE {table} ADD COLUMN {col}"); }
            catch (SqliteException e) when (e.Message.Contains("duplicate column")) { }
    }

    SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    static int Exec(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    static SqliteCommand Cmd(SqliteConnection c, string sql, (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        using var c = Open();
        return Query(c, sql, map, ps);
    }

    int Run(string sql, params (string, object?)[] ps) { using var c = Open(); return Exec(c, sql, ps); }

    /// <summary>Strictly increasing microsecond timestamps so ordering by created_at is stable.</summary>
    public long NowMicros()
    {
        var now = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        lock (_clock) return _lastMicros = Math.Max(now, _lastMicros + 1);
    }

    static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // threads
    public ThreadRow CreateThread(string agentId, List<string> tags, Dictionary<string, string> env)
    {
        var now = NowSeconds();
        var t = new ThreadRow(Guid.NewGuid().ToString(), agentId, tags, env, null, now, now);
        Run("INSERT INTO threads(id,agent_id,tags,env,terminated,created_at,last_activity_at) VALUES($id,$a,$t,$e,NULL,$c,$c)",
            ("$id", t.Id), ("$a", agentId), ("$t", JsonSerializer.Serialize(tags)), ("$e", JsonSerializer.Serialize(env)), ("$c", now));
        return t;
    }

    static ThreadRow MapThread(SqliteDataReader r) => new(r.GetString(0), r.GetString(1),
        JsonSerializer.Deserialize<List<string>>(r.GetString(2))!, JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3))!,
        r.IsDBNull(4) ? null : r.GetInt64(4), r.GetInt64(5), r.GetInt64(6));

    public ThreadRow? GetThread(string id) => Query("SELECT * FROM threads WHERE id=$id", MapThread, ("$id", id)).FirstOrDefault();

    public (List<ThreadRow> Rows, int Total) ListThreads(string? agentId, int limit, int offset)
    {
        const string where = "WHERE ($a IS NULL OR agent_id=$a)";
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: true); // one read snapshot for count + page
        var total = Query(c, $"SELECT COUNT(*) FROM threads {where}", r => r.GetInt32(0), ("$a", agentId))[0];
        var rows = Query(c, $"SELECT * FROM threads {where} ORDER BY last_activity_at DESC, created_at DESC, id LIMIT $l OFFSET $o",
            MapThread, ("$a", agentId), ("$l", limit), ("$o", offset));
        tx.Commit();
        return (rows, total);
    }

    public void SetTags(string id, List<string> tags) => Run("UPDATE threads SET tags=$t WHERE id=$id", ("$t", JsonSerializer.Serialize(tags)), ("$id", id));

    /// <summary>Marks the thread terminated (µs, Client API §3.3). Idempotent.</summary>
    public void Terminate(string id) => Run("UPDATE threads SET terminated=COALESCE(terminated,$n) WHERE id=$id", ("$n", NowMicros()), ("$id", id));

    static void Touch(SqliteConnection c, string id) => Exec(c, "UPDATE threads SET last_activity_at=$n WHERE id=$id", ("$n", NowSeconds()), ("$id", id));

    public bool DeleteThread(string id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        foreach (var table in new[] { "messages", "queue", "kv" }) Exec(c, $"DELETE FROM {table} WHERE thread_id=$id", ("$id", id));
        var n = Exec(c, "DELETE FROM threads WHERE id=$id", ("$id", id));
        tx.Commit();
        return n > 0;
    }

    // run status (Execution State §4)
    public ThreadStatus GetStatus(string id) =>
        Query("SELECT status_state,status_since,status_reason FROM threads WHERE id=$id", r => new ThreadStatus(r.GetString(0), r.GetInt64(1), S(r, 2)),
            ("$id", id)).FirstOrDefault() ?? ThreadStatus.Never;

    /// <summary>Persists a status transition; returns the new status, or null when state and reason are unchanged.</summary>
    public ThreadStatus? SetStatus(string id, string state, string? reason)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        var cur = Query(c, "SELECT status_state,status_reason FROM threads WHERE id=$id", r => (r.GetString(0), S(r, 1)), ("$id", id));
        if (cur.Count == 0 || (cur[0].Item1 == state && cur[0].Item2 == reason)) return null;
        var since = NowMicros();
        Exec(c, "UPDATE threads SET status_state=$s,status_since=$n,status_reason=$r WHERE id=$id", ("$s", state), ("$n", since), ("$r", reason), ("$id", id));
        tx.Commit();
        return new ThreadStatus(state, since, reason);
    }

    public List<string> RunningThreads() => Query("SELECT id FROM threads WHERE status_state='running'", r => r.GetString(0));
    public List<string> ThreadsWithQueue() => Query("SELECT DISTINCT q.thread_id FROM queue q JOIN threads t ON t.id=q.thread_id WHERE t.terminated IS NULL", r => r.GetString(0));

    // messages
    public MessageRow AddMessage(string threadId, string role, string? content, string side, int depth = 0, string? name = null,
        string? toolCalls = null, string? toolCallId = null, string? toolStatus = null, bool silent = false, string? id = null,
        string? prompt = null, string? statusKind = null)
    {
        var m = new MessageRow(id ?? Guid.NewGuid().ToString(), threadId, role, content, name, toolCalls, toolCallId, toolStatus,
            side, depth, silent, NowMicros(), prompt, statusKind);
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        Exec(c, "INSERT INTO messages(id,thread_id,role,content,name,tool_calls,tool_call_id,tool_status,side,depth,silent,created_at,prompt,status_kind) " +
                "VALUES($id,$t,$r,$c,$n,$tc,$tci,$ts,$s,$d,$si,$ca,$p,$sk)",
            ("$id", m.Id), ("$t", threadId), ("$r", role), ("$c", content), ("$n", name), ("$tc", toolCalls), ("$tci", toolCallId),
            ("$ts", toolStatus), ("$s", side), ("$d", depth), ("$si", silent ? 1 : 0), ("$ca", m.CreatedAt), ("$p", prompt), ("$sk", statusKind));
        Touch(c, threadId);
        tx.Commit();
        return m;
    }

    static MessageRow MapMessage(SqliteDataReader r) => new(r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4), S(r, 5), S(r, 6),
        S(r, 7), S(r, 8), r.GetString(9), r.GetInt32(10), r.GetInt32(11) != 0, r.GetInt64(12), S(r, 13), S(r, 14));

    public List<MessageRow> Messages(string threadId, int maxDepth = 0) =>
        Query("SELECT * FROM messages WHERE thread_id=$t AND depth<=$d ORDER BY seq", MapMessage, ("$t", threadId), ("$d", maxDepth));

    public (List<MessageRow> Rows, int Total) MessagePage(string threadId, int limit, int offset, bool desc, bool includeSilent, int depth)
    {
        const string where = "WHERE thread_id=$t AND depth<=$d AND ($si=1 OR silent=0)";
        (string, object?)[] ps = [("$t", threadId), ("$d", depth), ("$si", includeSilent ? 1 : 0), ("$l", limit), ("$o", offset)];
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: true);
        var total = Query(c, $"SELECT COUNT(*) FROM messages {where}", r => r.GetInt32(0), ps)[0];
        var rows = Query(c, $"SELECT * FROM messages {where} ORDER BY seq {(desc ? "DESC" : "ASC")} LIMIT $l OFFSET $o", MapMessage, ps);
        tx.Commit();
        if (desc) rows.Reverse();
        return (rows, total);
    }

    /// <summary>Messages §6: deletes a message; tool results cascade with their assistant message.</summary>
    public bool DeleteMessage(string threadId, string messageId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        var m = Query(c, "SELECT * FROM messages WHERE thread_id=$t AND id=$id", MapMessage, ("$t", threadId), ("$id", messageId)).FirstOrDefault();
        if (m is null) return false;
        if (m.Role == "assistant" && m.ToolCalls is not null)
            foreach (var call in OpenAiProvider.ParseToolCalls(m.ToolCalls))
                Exec(c, "DELETE FROM messages WHERE thread_id=$t AND role='tool' AND tool_call_id=$c", ("$t", threadId), ("$c", call.Id));
        Exec(c, "DELETE FROM messages WHERE thread_id=$t AND id=$id", ("$t", threadId), ("$id", messageId));
        tx.Commit();
        return true;
    }

    // queue (Runtime §9)
    /// <summary>Atomically checks the thread is open and queues the message (no TOCTOU with terminate/delete).</summary>
    public (EnqueueResult Result, MessageRow? Pending) Enqueue(string threadId, string role, string content)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        var t = Query(c, "SELECT terminated FROM threads WHERE id=$id", r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$id", threadId));
        if (t.Count == 0) return (EnqueueResult.NotFound, null);
        if (t[0] is not null) return (EnqueueResult.Terminated, null);
        var id = Guid.NewGuid().ToString();
        Exec(c, "INSERT INTO queue(thread_id,message_id,role,content) VALUES($t,$m,$r,$c)", ("$t", threadId), ("$m", id), ("$r", role), ("$c", content));
        tx.Commit();
        return (EnqueueResult.Ok, new MessageRow(id, threadId, role, content, null, null, null, null, "b", 0, false, NowMicros(), Status: "pending"));
    }

    /// <summary>Moves every queued message into the thread, FIFO, in one IMMEDIATE transaction.</summary>
    public List<MessageRow> DrainQueue(string threadId, string side)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        var items = Query(c, "SELECT seq,message_id,role,content FROM queue WHERE thread_id=$t ORDER BY seq",
            r => (Seq: r.GetInt64(0), Id: r.GetString(1), Role: r.GetString(2), Content: r.GetString(3)), ("$t", threadId));
        var rows = new List<MessageRow>();
        foreach (var q in items)
        {
            var m = new MessageRow(q.Id, threadId, q.Role, q.Content, null, null, null, null, side, 0, false, NowMicros());
            Exec(c, "INSERT INTO messages(id,thread_id,role,content,side,depth,silent,created_at) VALUES($id,$t,$r,$c,$s,0,0,$ca)",
                ("$id", m.Id), ("$t", threadId), ("$r", m.Role), ("$c", m.Content), ("$s", side), ("$ca", m.CreatedAt));
            Exec(c, "DELETE FROM queue WHERE seq=$s", ("$s", q.Seq));
            rows.Add(m);
        }
        if (items.Count > 0) Touch(c, threadId);
        tx.Commit();
        return rows;
    }

    public bool HasQueued(string threadId) => Query("SELECT 1 FROM queue WHERE thread_id=$t LIMIT 1", _ => 1, ("$t", threadId)).Count > 0;

    // kv (Runtime §14)
    public string? GetValue(string threadId, string key) =>
        Query("SELECT value FROM kv WHERE thread_id=$t AND key=$k", r => r.GetString(0), ("$t", threadId), ("$k", key)).FirstOrDefault();

    public void SetValue(string threadId, string key, string? value)
    {
        if (value is null) Run("DELETE FROM kv WHERE thread_id=$t AND key=$k", ("$t", threadId), ("$k", key));
        else Run("INSERT INTO kv VALUES($t,$k,$v) ON CONFLICT(thread_id,key) DO UPDATE SET value=excluded.value",
            ("$t", threadId), ("$k", key), ("$v", value));
    }
}
