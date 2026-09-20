using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class AdapterChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task Run()
    {
        var queue = new Queue<Action>();
        var state = JsonNode.Parse("""{"state_type":"map","seed":9,"map":{"next_options":[{"index":0}]}}""")!;
        var command = JsonNode.Parse("""{"action":"choose_map_node","index":0}""")!;
        string fingerprint = GameState.Fingerprint(state);
        int mutations = 0, searches = 0;
        bool permitted = true;
        Action? duringRead = null;
        var adapter = new ScheduledGameAdapter(queue.Enqueue,
            () => { duringRead?.Invoke(); return state; },
            c => { Check(c["index"]!.GetValue<int>() == 0, "command ownership"); mutations++; return new JsonObject { ["status"] = "ok" }; },
            (q, type, rarity, character, offset, count) => { searches++; return new JsonObject { ["query"] = q, ["item_type"] = type, ["rarity"] = rarity, ["character"] = character, ["offset"] = offset, ["count"] = count, ["seed"] = 1 }; });
        Task<JsonNode> Execute(CancellationToken ct = default) => adapter.Execute(command, fingerprint, () => permitted, ct);
        void Pump() => queue.Dequeue()();

        var reading = adapter.ReadState(default);
        Check(!reading.IsCompleted, "read waits for main thread"); Pump();
        var observed = await reading;
        Check(observed["seed"] == null && state["seed"] != null, "adapter public-state firewall");
        observed["map"]!["next_options"]![0]!["index"] = 999;
        Check(state["map"]!["next_options"]![0]!["index"]!.GetValue<int>() == 0, "snapshot ownership");

        // A visible player-owned overlay is a pause, not an unstable game state.
        // ReadState intentionally remains pending until it closes so BotRuntime's
        // 30-second settle watchdog cannot turn ordinary map browsing into a stop.
        JsonNode pausedState = JsonNode.Parse("""{"state_type":"overlay","overlay":{"screen_type":"combat_map","wait_for_player":true}}""")!;
        var pauseAdapter = new ScheduledGameAdapter(a => a(), () => pausedState,
            _ => new JsonObject { ["status"] = "ok" }, (_, _, _, _, _, _) => new JsonObject());
        var pausedRead = pauseAdapter.ReadState(default);
        await Task.Delay(50);
        Check(!pausedRead.IsCompleted, "player overlay pauses state reads without completing");
        pausedState = state.DeepClone();
        var resumed = await pausedRead.WaitAsync(TimeSpan.FromSeconds(2));
        Check(resumed.Text("state_type") == "map", "state read resumes with a fresh snapshot after overlay closes");

        pausedState = JsonNode.Parse("""{"state_type":"overlay","overlay":{"wait_for_player":true}}""")!;
        using (var pauseCancelled = new CancellationTokenSource())
        {
            var cancelledRead = pauseAdapter.ReadState(pauseCancelled.Token);
            await Task.Delay(50);
            pauseCancelled.Cancel();
            await MustCancel(cancelledRead);
        }

        var searching = adapter.Search("strike", "card", "all", null, 0, 10, default);
        Check(searches == 0, "search waits for main thread"); Pump();
        Check((await searching).Text("query") == "strike" && (await searching).Text("offset") == "0" && (await searching).Text("rarity") == "all" && searches == 1 && mutations == 0, "read-only native search");

        var action = Execute();
        Check(mutations == 0, "mutation waits for main thread");
        command["index"] = 2; Pump();
        Check((await action).Text("status") == "ok" && mutations == 1, "exactly once dispatch with owned command");
        command["index"] = 0;

        action = Execute();
        state["map"]!["next_options"]![0]!["index"] = 1;
        Pump(); Check((await action).Text("status") == "error" && mutations == 1, "freshness checked at dispatch, not enqueue");
        state["map"]!["next_options"]![0]!["index"] = 0;
        command["index"] = 1;
        action = Execute(); Pump();
        Check((await action).Text("status") == "error" && mutations == 1, "reject non-enumerated command");
        command["index"] = 0;

        action = Execute(); permitted = false; Pump();
        Check((await action).Text("status") == "error" && mutations == 1, "stop/ pause before queued dispatch");
        permitted = true;
        using var cancelled = new CancellationTokenSource();
        action = Execute(cancelled.Token); cancelled.Cancel();
        await MustCancel(action); Pump();
        Check(mutations == 1, "cancelled queue item cannot execute on later frame");

        using var midRead = new CancellationTokenSource();
        duringRead = midRead.Cancel;
        action = Execute(midRead.Token); Pump(); await MustCancel(action);
        Check(mutations == 1, "cancellation during freshness read prevents mutation");
        duringRead = null;

        var uncertain = new ScheduledGameAdapter(queue.Enqueue, () => state,
            _ => { mutations++; throw new InvalidOperationException("action failed after dispatch"); }, (_, _, _, _, _, _) => new JsonObject());
        action = uncertain.Execute(command, fingerprint, () => true, default); Pump();
        try { await action; throw new Exception("uncertain error was swallowed"); }
        catch (InvalidOperationException) { }
        Check(mutations == 2 && queue.Count == 0, "uncertain action never retried");

        foreach (var (json, expected) in new[] {
            ("""{"state_type":"treasure","treasure":{"can_open":true}}""", "open_chest"),
            ("""{"state_type":"shop","shop":{"can_open":true}}""", "open_shop"),
            ("""{"state_type":"shop","shop":{"can_close":true}}""", "close_shop") })
        {
            var legal = GameState.Actions(JsonNode.Parse(json)!);
            Check(legal.Count == 1 && legal[0]!["command"].Text("action") == expected, "explicit room control: " + expected);
        }
        Check(GameState.Actions(JsonNode.Parse("""{"state_type":"monster","battle":{"is_play_phase":false},"player":{"potions":[{"slot":0}]}}""")!).Count == 0, "no actions during enemy/disabled phase");
        Check(GameState.Actions(JsonNode.Parse("""{"state_type":"transition","player":{"potions":[{"slot":0}]}}""")!).Count == 0, "transition is not actionable");
        Console.WriteLine("PASS native adapter queue, cancellation, overlay pause/resume, freshness, ownership, search, and explicit room controls");
    }
    static async Task MustCancel(Task task)
    {
        try { await task; throw new Exception("cancellation was swallowed"); }
        catch (OperationCanceledException) { }
    }
}
