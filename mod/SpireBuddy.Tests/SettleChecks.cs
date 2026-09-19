using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class SettleChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static async Task Run()
    {
        var loading = JsonNode.Parse("""{"state_type":"menu","options":["embark"],"loading":true}""")!;
        Check(!GameState.Ready(loading), "a loading frame is never ready");
        var idle = JsonNode.Parse("""{"state_type":"menu","options":["embark"]}""")!;
        Check(GameState.Ready(idle), "the same frame without loading is ready");

        // A transition that outlasts one idle window must still settle once the
        // game becomes interactive: embark and ancient-event intros load for far
        // longer than 30 s on slow machines.
        var delayed = new ScriptedAdapter(reads => reads < 10
            ? JsonNode.Parse("""{"state_type":"menu","options":[],"loading":true}""")!
            : idle.DeepClone());
        using (var runtime = Runtime(delayed))
        {
            var settled = await runtime.Stable(CancellationToken.None, idleWindow: TimeSpan.FromMilliseconds(700), hardCap: TimeSpan.FromSeconds(30));
            Check(settled.Text("state_type") == "menu" && settled["loading"] == null, "settles on the interactive frame after a long loading phase");
        }

        // A game stuck in loading forever still stops at the hard cap instead of
        // waiting indefinitely.
        var stuck = new ScriptedAdapter(_ => JsonNode.Parse("""{"state_type":"menu","options":[],"loading":true}""")!);
        using (var runtime = Runtime(stuck))
        {
            var stopped = false;
            try { await runtime.Stable(CancellationToken.None, idleWindow: TimeSpan.FromMilliseconds(500), hardCap: TimeSpan.FromSeconds(2)); }
            catch (InvalidOperationException) { stopped = true; }
            Check(stopped, "a permanently loading game stops at the hard cap");
        }

        // Read failures during heavy loading are progress too: the deadline
        // restarts, so a state that only becomes readable after several failed
        // polls still settles.
        var flaky = new ScriptedAdapter(reads => reads < 6
            ? throw new HttpRequestException("game busy")
            : idle.DeepClone());
        using (var runtime = Runtime(flaky))
        {
            var settled = await runtime.Stable(CancellationToken.None, idleWindow: TimeSpan.FromMilliseconds(700), hardCap: TimeSpan.FromSeconds(30));
            Check(settled.Text("state_type") == "menu", "failed reads during loading do not burn the settle budget");
        }

        // Transition frames without a loading marker (map travel, scene swaps)
        // keep evolving; an evolving snapshot must not burn the budget either.
        var evolving = new ScriptedAdapter(reads => reads < 12
            ? JsonNode.Parse($"{{\"state_type\":\"transition\",\"frame\":{reads / 3}}}")!
            : idle.DeepClone());
        using (var runtime = Runtime(evolving))
        {
            var settled = await runtime.Stable(CancellationToken.None, idleWindow: TimeSpan.FromMilliseconds(900), hardCap: TimeSpan.FromSeconds(30));
            Check(settled.Text("state_type") == "menu", "evolving transition frames do not burn the settle budget");
        }

        // A screen frozen non-interactive for the whole window still stops.
        var frozen = new ScriptedAdapter(_ => JsonNode.Parse("""{"state_type":"transition","message":"stuck"}""")!);
        using (var runtime = Runtime(frozen))
        {
            var stopped = false;
            try { await runtime.Stable(CancellationToken.None, idleWindow: TimeSpan.FromMilliseconds(900), hardCap: TimeSpan.FromSeconds(30)); }
            catch (InvalidOperationException) { stopped = true; }
            Check(stopped, "a frozen non-interactive screen still stops");
        }
        Console.WriteLine("PASS settle watchdog rides out loading transitions");
    }

    static BotRuntime Runtime(IGameAdapter game)
    {
        var directory = Path.Combine(Path.GetTempPath(), "spire-settle-" + Guid.NewGuid().ToString("N"));
        return new BotRuntime(directory, "missing.dll", game);
    }

    sealed class ScriptedAdapter(Func<int, JsonNode> read) : IGameAdapter
    {
        int reads;
        public Task<JsonNode> ReadState(CancellationToken ct) => Task.FromResult(read(reads++).DeepClone());
        public Task<JsonNode> Execute(JsonNode command, string expectedSnapshot, Func<bool> mayExecute, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonNode> Search(string query, string itemType, string rarity, int? offset, int? count, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonNode> ReadKnowledge(CancellationToken ct) => throw new NotSupportedException();
    }
}
