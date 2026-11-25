namespace StandardAgents.IntegrationTests;

/// <summary>
/// A test that calls the real <c>claude</c> or <c>codex</c> CLI with your local subscription login.
/// Skipped unless <c>SA_LIVE_SUBSCRIPTION=1</c> and the CLI is on PATH; never runs in CI.
/// Run with: <c>SA_LIVE_SUBSCRIPTION=1 dotnet test --filter Category=LiveSubscription</c>.
/// </summary>
public sealed class LiveSubscriptionFactAttribute : FactAttribute
{
    public LiveSubscriptionFactAttribute(string cli)
    {
        if (Environment.GetEnvironmentVariable("SA_LIVE_SUBSCRIPTION") != "1")
            Skip = "set SA_LIVE_SUBSCRIPTION=1 to run against your local subscription CLI";
        else if (!OnPath(cli))
            Skip = $"'{cli}' is not on PATH";
    }

    static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(d => d.Length > 0 && File.Exists(Path.Combine(d, exe)));
}
