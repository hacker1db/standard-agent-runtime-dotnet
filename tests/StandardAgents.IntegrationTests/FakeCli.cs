using System.Globalization;
using System.Text.Json.Nodes;
using StandardAgents.Runtime;

namespace StandardAgents.IntegrationTests;

/// <summary>
/// A stand-in for the <c>claude</c> / <c>codex</c> CLIs: a bash script that records its argv,
/// stdin, environment, and working directory, then prints canned replies in order.
/// Lets the subscription providers run end to end with no login, no network, and no API key.
/// </summary>
public sealed class FakeCli : IDisposable
{
    const string Script = """
        #!/usr/bin/env bash
        set -u
        d="$(cd "$(dirname "$0")" && pwd)"
        n=$(( $(cat "$d/count" 2>/dev/null || echo 0) + 1 ))
        echo "$n" > "$d/count"
        printf '%s\n' "$@" > "$d/args.$n"
        cat > "$d/stdin.$n"
        env > "$d/env.$n"
        pwd > "$d/cwd.$n"
        ls -ld . | cut -c1-10 > "$d/mode.$n"
        [ -f "$d/sleep" ] && sleep "$(cat "$d/sleep")"
        [ -f "$d/exit" ] && { echo "fake failure" >&2; exit "$(cat "$d/exit")"; }
        reply="$d/reply.$n"; [ -f "$reply" ] || reply="$d/reply"
        out=""; prev=""
        for a in "$@"; do [ "$prev" = "-o" ] && out="$a"; prev="$a"; done
        if [ "$1" = "exec" ]; then
          [ -f "$d/no-output" ] || cat "$reply" > "$out"
        else
          cat "$reply"
        fi
        """;

    public string Dir { get; } = Directory.CreateTempSubdirectory("sa-fakecli-").FullName;
    public string Bin => Path.Combine(Dir, "fake-cli");

    public FakeCli()
    {
        File.WriteAllText(Bin, Script.ReplaceLineEndings("\n") + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Bin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Wraps <paramref name="text"/> the way <c>claude -p --output-format json</c> does.</summary>
    public static string ClaudeJson(string text, bool isError = false) =>
        new JsonObject { ["type"] = "result", ["is_error"] = isError, ["result"] = text }.ToJsonString();

    /// <summary>Sets the reply for every call, or for call number <paramref name="call"/> (1-based).</summary>
    public FakeCli Reply(string text, int? call = null)
    {
        File.WriteAllText(Path.Combine(Dir, call is null ? "reply" : $"reply.{call}"), text);
        return this;
    }

    public FakeCli ExitWith(int code) { File.WriteAllText(Path.Combine(Dir, "exit"), code.ToString(CultureInfo.InvariantCulture)); return this; }
    public FakeCli SleepFor(int seconds) { File.WriteAllText(Path.Combine(Dir, "sleep"), seconds.ToString(CultureInfo.InvariantCulture)); return this; }
    public FakeCli NoOutputFile() { File.WriteAllText(Path.Combine(Dir, "no-output"), ""); return this; }

    public int Calls => File.Exists(Path.Combine(Dir, "count")) ? int.Parse(File.ReadAllText(Path.Combine(Dir, "count")).Trim(), CultureInfo.InvariantCulture) : 0;
    public string[] Args(int call = 1) => File.ReadAllLines(Path.Combine(Dir, $"args.{call}"));
    public string Stdin(int call = 1) => File.ReadAllText(Path.Combine(Dir, $"stdin.{call}"));
    public string Mode(int call = 1) => File.ReadAllText(Path.Combine(Dir, $"mode.{call}")).Trim();
    public string Cwd(int call = 1) => File.ReadAllText(Path.Combine(Dir, $"cwd.{call}")).Trim();
    public Dictionary<string, string> Env(int call = 1) => File.ReadAllLines(Path.Combine(Dir, $"env.{call}"))
        .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);

    public CliProvider Provider(string kind, TimeSpan? timeout = null) => new(kind, Bin, timeout);

    public void Dispose() => Directory.Delete(Dir, recursive: true);
}
