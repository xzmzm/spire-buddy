using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

internal interface IGameAdapter
{
    Task<JsonNode> ReadState(CancellationToken ct);
    Task<JsonNode> Execute(JsonNode command, string expectedSnapshot, Func<bool> mayExecute, CancellationToken ct);
    Task<JsonNode> Search(string query, string itemType, string rarity, int? offset, int? count, CancellationToken ct);
    Task<JsonNode> ReadKnowledge(CancellationToken ct);
    // The Combat Solver hand-off is optional; adapters without bridge access
    // simply report the solver as unavailable.
    Task<JsonNode> Solver(string op, CancellationToken ct) =>
        Task.FromResult<JsonNode>(new JsonObject { ["available"] = false });
}

// Game callbacks and the final freshness check run together on the main thread.
// Only detached, public JSON crosses back to the model worker.
internal sealed class ScheduledGameAdapter(
    Action<Action> schedule,
    Func<JsonNode> read,
    Func<JsonNode, JsonNode> execute,
    Func<string, string, string, int?, int?, JsonNode> search,
    Func<JsonNode>? knowledge = null,
    Func<string, JsonNode>? solver = null) : IGameAdapter
{
    public async Task<JsonNode> ReadState(CancellationToken ct)
    {
        // Some game screens are deliberately opened by the player on top of the
        // current decision (notably the map during combat). Returning those to
        // BotRuntime would make its 30-second "settle" watchdog interpret normal
        // browsing as a hung game and permanently stop play. Keep polling here
        // instead: this await is outside that watchdog, Stop still cancels via ct,
        // and the underlying decision is re-read fresh as soon as the overlay closes.
        while (true)
        {
            var state = await OnMain(_ => GameState.Public(read())!, ct).ConfigureAwait(false);
            if (!(state.Text("state_type") == "overlay" && state["overlay"].Flag("wait_for_player")))
                return state;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    public Task<JsonNode> ReadKnowledge(CancellationToken ct) =>
        OnMain(_ => GameState.Public(knowledge == null ? new JsonObject() : knowledge())!, ct);

    public Task<JsonNode> Search(string query, string itemType, string rarity, int? offset, int? count, CancellationToken ct) =>
        OnMain(_ => GameState.Public(search(query, itemType, rarity, offset, count))!, ct);

    public Task<JsonNode> Solver(string op, CancellationToken ct) =>
        OnMain(_ => GameState.Public(solver == null ? new JsonObject { ["available"] = false } : solver(op))!, ct);

    public Task<JsonNode> Execute(JsonNode command, string expectedSnapshot, Func<bool> mayExecute, CancellationToken ct)
    {
        var ownedCommand = command.DeepClone();
        return OnMain(dispatchToken =>
        {
            if (!mayExecute()) return Rejected("Stopped or paused before action dispatch.");
            var state = GameState.Public(read())!;
            if (GameState.Fingerprint(state) != expectedSnapshot) return Rejected("Game state changed before action dispatch.");
            if (!GameState.Actions(state).Any(a => JsonNode.DeepEquals(a?["command"], ownedCommand)))
                return Rejected("Command is no longer legal.");
            dispatchToken.ThrowIfCancellationRequested();
            if (!mayExecute()) return Rejected("Stopped or paused before action dispatch.");
            return execute(ownedCommand).DeepClone();
        }, ct);
    }

    static JsonNode Rejected(string message) => new JsonObject { ["status"] = "error", ["executed"] = false, ["error"] = message };

    async Task<JsonNode> OnMain(Func<CancellationToken, JsonNode> operation, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = deadline.Token.Register(() => completion.TrySetCanceled(deadline.Token));
        schedule(() =>
        {
            // A timed-out queued action must not execute when frames resume later.
            if (completion.Task.IsCompleted) return;
            try { deadline.Token.ThrowIfCancellationRequested(); completion.TrySetResult(operation(deadline.Token)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return await completion.Task.ConfigureAwait(false);
    }
}
