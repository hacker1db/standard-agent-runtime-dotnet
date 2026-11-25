using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using StandardAgents.Runtime;

namespace StandardAgents.Cli;

/// <summary>Thread Client API (FORMAT.md §HTTP), mounted under /api; optional static UI at /.</summary>
public static partial class Api
{
    public const string Prefix = "/api";
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    /// <param name="loopbackOnly">Reject requests whose Host or Origin is not a loopback name (DNS-rebinding and cross-site defense for local serving).</param>
    public static WebApplication Build(AgentRuntime rt, string token, Action<WebApplicationBuilder>? configure = null, string? uiDir = null, bool loopbackOnly = false)
    {
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("the API token must not be empty");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        configure?.Invoke(builder);
        var app = builder.Build();
        if (loopbackOnly) app.Use(async (ctx, next) =>
        {
            if (!IsLoopbackHost(ctx.Request.Host.Host) || (ctx.Request.Headers.Origin is { Count: > 0 } origin && !IsLoopbackOrigin(origin.ToString())))
            {
                await WriteError(ctx, 403, "forbidden", "only loopback Host and Origin are accepted");
                return;
            }
            await next();
        });
        Map(app, rt, token, uiDir);
        return app;
    }

    /// <summary>True for <c>localhost</c>, <c>127.0.0.0/8</c>, and <c>::1</c> (any port).</summary>
    public static bool IsLoopbackHost(string host)
    {
        host = host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));
    }

    /// <summary>True when an Origin header names an http(s) loopback host.</summary>
    public static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && IsLoopbackHost(u.Host);

    /// <summary>A random 256-bit bearer token (base64url).</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);

    static Task WriteError(HttpContext ctx, int status, string code, string message) => Error(status, code, message).ExecuteAsync(ctx);

    static bool IsApi(PathString p) => p.StartsWithSegments(Prefix);

    [GeneratedRegex("^/api/threads/[^/]+/stream$")]
    private static partial Regex StreamPath();

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Path of the request exactly as sent (for ticket path binding).</summary>
    static string RawPath(HttpContext ctx)
    {
        var raw = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith('/')) raw = (ctx.Request.PathBase + ctx.Request.Path).ToString();
        var q = raw.IndexOfAny(['?', '#']);
        return q >= 0 ? raw[..q] : raw;
    }

    /// <summary>Client API §4.2: normalized path beneath the prefix, no query/fragment, no encoded separators.</summary>
    public static bool IsNormalizedApiPath(string p)
    {
        if (!p.StartsWith(Prefix + "/", StringComparison.Ordinal) || p.Contains('?') || p.Contains('#') || p.Contains('\\')) return false;
        if (Regex.IsMatch(p, "%(2e|2f|5c)", RegexOptions.IgnoreCase)) return false;
        return p[1..].Split('/').All(s => s.Length > 0 && s != "." && s != "..");
    }

    static bool TokenMatches(string header, byte[] expected)
    {
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..]));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>Reads a JSON object body; returns an error result for a missing/malformed/non-object body.</summary>
    static async Task<(JsonObject? Body, IResult? Error)> ReadObject(HttpRequest req)
    {
        try
        {
            var node = await JsonNode.ParseAsync(req.Body);
            return node is JsonObject o ? (o, null) : (null, Error(400, "invalid_request", "body must be a JSON object"));
        }
        catch (JsonException e) { return (null, Error(400, "invalid_request", $"invalid JSON: {e.Message}")); }
    }

    static bool IsString(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    static IResult? BadPaging(int? limit, int? offset) =>
        limit < 0 || offset < 0 ? Error(400, "invalid_request", "limit and offset must be non-negative") : null;

    public static void Map(WebApplication app, AgentRuntime rt, string token, string? uiDir = null)
    {
        var pkg = rt.Package;
        var store = rt.Store;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var tickets = new ConcurrentDictionary<string, (string Path, DateTime Expires)>();
        var hub = new StreamHub(rt);

        // Every failure is {error:{code,message}}; internal failures are 500.
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception e) when (!ctx.Response.HasStarted && e is not OperationCanceledException)
            {
                app.Logger.LogError(e, "unhandled error for {Path}", ctx.Request.Path);
                ctx.Response.Clear();
                await WriteError(ctx, 500, "internal", "internal server error");
            }
        });
        app.UseStatusCodePages(async sc =>
        {
            var ctx = sc.HttpContext;
            if (!IsApi(ctx.Request.Path)) return;
            var s = ctx.Response.StatusCode;
            await WriteError(ctx, s, s switch { 400 => "invalid_request", 401 => "unauthorized", 404 => "not_found", 405 => "method_not_allowed", _ => "error" },
                s switch { 404 => "not found", 405 => "method not allowed", _ => "request failed" });
        });
        app.UseWebSockets();

        // Bearer auth on every /api route; the stream upgrade authenticates by ticket only (§4, §16).
        app.Use(async (ctx, next) =>
        {
            if (IsApi(ctx.Request.Path) && !(ctx.WebSockets.IsWebSocketRequest && StreamPath().IsMatch(ctx.Request.Path.Value ?? ""))
                && !TokenMatches(ctx.Request.Headers.Authorization.ToString(), expected))
            {
                await WriteError(ctx, 401, "unauthorized", "missing or invalid bearer token");
                return;
            }
            await next();
        });

        if (uiDir is not null)
        {
            var files = new PhysicalFileProvider(Path.GetFullPath(uiDir));
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }
        // Route matching must run after static files: otherwise the SPA fallback endpoint is
        // selected first and StaticFileMiddleware skips requests that already have an endpoint.
        app.UseRouting();

        var api = app.MapGroup(Prefix);

        object AgentEntry(AgentDef a) => new
        {
            id = a.Name, name = a.Name, title = a.Title, description = a.Description, icon = (string?)null,
            primary = a.Primary,
            side_a_backing = a.SideA.Human ? "human" : "prompt", side_b_backing = a.SideB.Human ? "human" : "prompt",
        };

        object ThreadAgent(AgentDef a) => new
        {
            id = a.Name, title = a.Title, description = a.Description,
            side_a_backing = a.SideA.Human ? "human" : "prompt", side_b_backing = a.SideB.Human ? "human" : "prompt",
        };

        object ThreadJson(ThreadRow t) => new
        {
            id = t.Id, agent_id = t.AgentId, user_id = (string?)null, tags = t.Tags, parent = (string?)null,
            terminated = t.Terminated, created_at = t.CreatedAt,
        };

        var creatable = pkg.Agents.Values.Where(pkg.IsCreatable).ToList();

        api.MapPost("/auth/websocket-ticket", async (HttpContext ctx) =>
        {
            var (body, err) = await ReadObject(ctx.Request);
            if (err is not null) return err;
            if (!IsString(body!["pathname"]) || !IsNormalizedApiPath(body["pathname"]!.GetValue<string>()))
                return Error(400, "invalid_request", "pathname must be a normalized path beneath /api without query or fragment");
            foreach (var (k, v) in tickets) if (v.Expires < DateTime.UtcNow) tickets.TryRemove(k, out _);
            var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            tickets[ticket] = (body["pathname"]!.GetValue<string>(), DateTime.UtcNow + TicketLifetime);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { ticket, expires_in = (int)TicketLifetime.TotalSeconds });
        });

        api.MapGet("/agents", (string? primary) =>
            Results.Ok(new { agents = creatable.Where(a => primary != "true" || a.Primary).Select(AgentEntry) }));

        api.MapPost("/threads", async (HttpContext ctx) =>
        {
            var (body, err) = await ReadObject(ctx.Request);
            if (err is not null) return err;
            if (!IsString(body!["agent_id"])) return Error(400, "invalid_request", "agent_id must be a string");
            var agentId = body["agent_id"]!.GetValue<string>();
            List<string> tags = [];
            if (body.ContainsKey("tags"))
            {
                if (body["tags"] is not JsonArray arr || !arr.All(IsString)) return Error(400, "invalid_request", "tags must be an array of strings");
                tags = arr.Select(x => x!.GetValue<string>()).ToList();
            }
            Dictionary<string, string> env = [];
            if (body.ContainsKey("env"))
            {
                if (body["env"] is not JsonObject eo || !eo.All(kv => IsString(kv.Value))) return Error(400, "invalid_request", "env must be an object of strings");
                env = eo.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
            }
            if (!creatable.Any(a => a.Name == agentId)) return Error(404, "not_found", $"unknown agent '{agentId}'");
            var t = rt.CreateThread(agentId, tags, env);
            return Results.Ok(new { threadId = t.Id, agent_id = t.AgentId });
        });

        api.MapGet("/threads", (int? limit, int? offset, string? agent_id) =>
        {
            if (BadPaging(limit, offset) is { } bad) return bad;
            int l = limit ?? 50, o = offset ?? 0;
            var (rows, total) = store.ListThreads(agent_id, l, o);
            return Results.Ok(new
            {
                threads = rows.Select(t =>
                {
                    var last = store.MessagePage(t.Id, 1, 0, true, false, 0).Rows.FirstOrDefault();
                    var agent = pkg.Agents.GetValueOrDefault(t.AgentId);
                    var st = rt.Status(t.Id);
                    return new
                    {
                        id = t.Id, agent_id = t.AgentId, user_id = (string?)null, tags = t.Tags, parent = (string?)null,
                        terminated = t.Terminated, created_at = t.CreatedAt, last_activity_at = t.LastActivityAt,
                        last_message_preview = last?.Content is { } c ? c[..Math.Min(c.Length, 120)] : null,
                        last_message_at = last is null ? (long?)null : last.CreatedAt / 1_000_000,
                        status = st.State, status_reason = st.State == "failed" ? st.Reason : null,
                        agent = new { name = t.AgentId, title = agent?.Title ?? t.AgentId, description = agent?.Description },
                    };
                }).ToList(),
                total,
                hasMore = o + rows.Count < total,
            });
        });

        api.MapGet("/threads/{id}", (string id) => store.GetThread(id) is { } t
            ? Results.Ok(new
            {
                thread = ThreadJson(t),
                agent = pkg.Agents.GetValueOrDefault(t.AgentId) is { } a ? ThreadAgent(a) : null,
                status = rt.Status(id),
            })
            : Error(404, "not_found", "thread not found"));

        api.MapPatch("/threads/{id}", async (string id, HttpContext ctx) =>
        {
            if (store.GetThread(id) is null) return Error(404, "not_found", "thread not found");
            var (body, err) = await ReadObject(ctx.Request);
            if (err is not null) return err;
            if (body!["tags"] is not JsonArray arr || !arr.All(IsString))
                return Error(400, "invalid_request", "tags must be an array of strings");
            store.SetTags(id, arr.Select(x => x!.GetValue<string>()).ToList());
            var t = store.GetThread(id);
            return t is null ? Error(404, "not_found", "thread not found")
                : Results.Ok(new { id = t.Id, agent_id = t.AgentId, user_id = (string?)null, tags = t.Tags, created_at = t.CreatedAt });
        });

        api.MapDelete("/threads/{id}", async (string id) => await rt.DeleteAsync(id)
            ? Results.Ok(new { success = true, threadId = id, deletedThreadIds = new[] { id }, deletedCount = 1 })
            : Error(404, "not_found", "thread not found"));

        api.MapGet("/threads/{id}/messages", (string id, int? limit, int? offset, string? order, string? includeSilent, int? depth) =>
        {
            if (store.GetThread(id) is null) return Error(404, "not_found", "thread not found");
            if (BadPaging(limit, offset) is { } bad) return bad;
            if (order is not (null or "asc" or "desc")) return Error(400, "invalid_request", "order must be asc or desc");
            if (depth < 0) return Error(400, "invalid_request", "depth must be non-negative");
            int l = limit ?? 100, o = offset ?? 0;
            var (rows, total) = store.MessagePage(id, l, o, order != "asc", includeSilent == "true", depth ?? 0);
            return Results.Ok(new { messages = rows.Select(MessageJson).ToList(), total, hasMore = o + rows.Count < total });
        });

        api.MapPost("/threads/{id}/messages", async (string id, HttpContext ctx) =>
        {
            if (store.GetThread(id) is null) return Error(404, "not_found", "thread not found");
            var (body, err) = await ReadObject(ctx.Request);
            if (err is not null) return err;
            var role = IsString(body!["role"]) ? body["role"]!.GetValue<string>() : null;
            if (role is not ("user" or "assistant" or "system")) return Error(400, "invalid_request", "role must be user, assistant or system");
            if (body.ContainsKey("content") && body["content"] is not null && !IsString(body["content"]))
                return Error(400, "invalid_request", "content must be a string");
            var content = body["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content)) return Error(400, "invalid_request", "content is required");
            string messageId;
            try { messageId = rt.Enqueue(id, role, content); }
            catch (KeyNotFoundException) { return Error(404, "not_found", "thread not found"); }
            catch (ThreadTerminatedException) { return Error(423, "thread_terminated", "thread is terminated"); }
            _ = rt.RunAsync(id);
            return Results.Json(new { status = "queued", messageId }, statusCode: 202);
        });

        api.MapDelete("/threads/{id}/messages/{mid}", (string id, string mid) =>
            store.GetThread(id) is null ? Error(404, "not_found", "thread not found")
            : store.DeleteMessage(id, mid) ? Results.Ok(new { success = true })
            : Error(404, "not_found", "message not found"));

        api.MapGet("/threads/{id}/fs", (string id) => store.GetThread(id) is null
            ? Error(404, "not_found", "thread not found") : Results.Ok(new { files = Array.Empty<object>() }));
        api.MapGet("/threads/{id}/fs/{**path}", (string id, string path) =>
            Error(404, "not_found", store.GetThread(id) is null ? "thread not found" : "file not found"));

        api.MapPost("/threads/{id}/stop", (string id) =>
        {
            if (store.GetThread(id) is null) return Error(404, "not_found", "thread not found");
            rt.Stop(id);
            return Results.Ok(new { status = "stopped" });
        });

        api.MapGet("/threads/{id}/stream", async (string id, HttpContext ctx, int? depth, string? includeSilent) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                if (store.GetThread(id) is null) { await WriteError(ctx, 404, "not_found", "thread not found"); return; }
                await WriteError(ctx, 400, "invalid_request", "this route requires a WebSocket upgrade");
                return;
            }
            var ticket = ctx.Request.Query["ticket"].ToString();
            if (ticket.Length == 0 || !tickets.TryRemove(ticket, out var tk) || tk.Expires < DateTime.UtcNow || tk.Path != RawPath(ctx))
            {
                await WriteError(ctx, 401, "unauthorized", "unknown, expired, used or mismatched stream ticket");
                return;
            }
            if (store.GetThread(id) is null) { await WriteError(ctx, 404, "not_found", "thread not found"); return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.ServeAsync(id, ws, depth ?? 0, includeSilent == "true", ctx.RequestAborted);
        });

        // Unknown /api routes get the error envelope; with --ui every other path falls back to index.html (SPA).
        var index = uiDir is null ? null : Path.Combine(Path.GetFullPath(uiDir), "index.html");
        app.MapFallback("{*path}", async ctx =>
        {
            if (IsApi(ctx.Request.Path) || index is null || !File.Exists(index)) { await WriteError(ctx, 404, "not_found", "unknown route"); return; }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(index);
        });
    }

    /// <summary>Message record (Messages §1) in its decoded form; tool_calls stays a JSON-encoded string.</summary>
    public static object MessageJson(MessageRow m) => new Dictionary<string, object?>
    {
        ["id"] = m.Id, ["role"] = m.Role, ["content"] = m.Content, ["name"] = m.Name, ["tool_calls"] = m.ToolCalls,
        ["tool_call_id"] = m.ToolCallId, ["tool_status"] = m.ToolStatus, ["status"] = m.Status, ["attachments"] = null,
        ["reasoning_content"] = null, ["reasoning_details"] = null, ["prompt"] = m.Prompt, ["created_at"] = m.CreatedAt,
        ["parent_id"] = null, ["depth"] = m.Depth, ["silent"] = m.Silent,
        ["metadata"] = m.StatusKind is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["status_kind"] = m.StatusKind },
    };

    /// <summary>Fan-out of runtime events to thread stream connections (Client API §16).</summary>
    sealed class StreamHub
    {
        sealed record Conn(string ThreadId, int Depth, bool IncludeSilent, Channel<string?> Out);

        readonly AgentRuntime _rt;
        readonly object _gate = new();
        readonly List<Conn> _conns = [];

        public StreamHub(AgentRuntime rt)
        {
            _rt = rt;
            rt.MessageAdded += m => Broadcast(m.ThreadId, c => m.Depth <= c.Depth && (c.IncludeSilent || !m.Silent),
                () => Frame(new { type = "message_data", message_id = m.Id, depth = m.Depth, data = MessageJson(m) }));
            rt.StatusChanged += (id, s) => Broadcast(id, _ => true, () => StatusFrame(s));
            rt.ThreadDeleted += id =>
            {
                lock (_gate) foreach (var c in _conns.Where(c => c.ThreadId == id)) c.Out.Writer.TryComplete(); // close 1000
            };
        }

        static string Frame(object o) => JsonSerializer.Serialize(o, Json);
        static string StatusFrame(ThreadStatus s) => Frame(new { type = "thread_status", status = s });

        void Broadcast(string threadId, Func<Conn, bool> filter, Func<string> frame)
        {
            lock (_gate)
            {
                string? f = null;
                foreach (var c in _conns)
                    if (c.ThreadId == threadId && filter(c)) c.Out.Writer.TryWrite(f ??= frame());
            }
        }

        public async Task ServeAsync(string threadId, WebSocket ws, int depth, bool includeSilent, CancellationToken aborted)
        {
            var conn = new Conn(threadId, depth, includeSilent, Channel.CreateUnbounded<string?>(new() { SingleReader = true }));
            lock (_gate)
            {
                _conns.Add(conn);
                conn.Out.Writer.TryWrite(StatusFrame(_rt.Status(threadId))); // §16.2.1 first frame, ordered before any change
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            var reader = Task.Run(async () =>
            {
                var buf = new byte[4096];
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var sb = new StringBuilder();
                        WebSocketReceiveResult r;
                        do
                        {
                            r = await ws.ReceiveAsync(buf, cts.Token);
                            if (r.MessageType == WebSocketMessageType.Close) return;
                            if (sb.Length < 1024) sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                        } while (!r.EndOfMessage);
                        if (r.MessageType == WebSocketMessageType.Text && sb.ToString() == "stream_ping") conn.Out.Writer.TryWrite("stream_pong");
                    }
                }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
                finally { conn.Out.Writer.TryComplete(); }
            }, CancellationToken.None);
            try
            {
                await foreach (var frame in conn.Out.Reader.ReadAllAsync(cts.Token))
                    await ws.SendAsync(Encoding.UTF8.GetBytes(frame!), WebSocketMessageType.Text, true, cts.Token);
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            finally
            {
                lock (_gate) _conns.Remove(conn);
                cts.Cancel();
                try { await reader; } catch { }
            }
        }
    }
}
