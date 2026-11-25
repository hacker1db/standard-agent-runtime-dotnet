using System.Diagnostics;

namespace StandardAgents.IntegrationTests;

/// <summary>`serve` keeps subscription-backed packages on loopback unless --allow-remote is passed.</summary>
[Trait("Category", "Integration")]
public sealed class ServeGuardTests
{
    static string DemoPackage()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "examples", "demo-agent"))) return Path.Combine(d.FullName, "examples", "demo-agent");
        throw new DirectoryNotFoundException("examples/demo-agent");
    }

    static async Task<(int Code, string Stderr)> Cli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "StandardAgents.Cli.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["AGENT_RUNTIME_TOKEN"] = "test-token";
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await stderr);
    }

    [Fact]
    public async Task NonLoopbackServeWithSubscriptionModelIsRefused()
    {
        var db = Path.Combine(Path.GetTempPath(), $"sa-guard-{Guid.NewGuid():N}.db");
        try
        {
            var (code, stderr) = await Cli("serve", "--package", DemoPackage(), "--host", "0.0.0.0", "--port", "0", "--db", db);
            Assert.Equal(1, code);
            Assert.Contains("--allow-remote", stderr);
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(db) + "*")) File.Delete(f); }
    }
}
