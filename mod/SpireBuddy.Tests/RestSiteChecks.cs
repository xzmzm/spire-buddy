using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class RestSiteChecks
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    internal static async Task Run()
    {
        foreach (var api in new[] { "responses", "chat_completions" })
            await RestResult(api);

        var waiting = JsonNode.Parse("""{"state_type":"rest_site","rest_site":{"options":[],"can_proceed":false},"player":{"potions":[]}}""")!;
        Check(!GameState.Ready(waiting), "an empty rest animation is not ready");
        waiting["player"]!["potions"]!.AsArray().Add(JsonNode.Parse("""{"slot":0,"name":"Strength Potion"}"""));
        Check(!GameState.Ready(waiting), "a held potion does not make a rest animation ready");
        waiting["rest_site"]!["options"] = JsonNode.Parse("""[{"index":0,"is_enabled":false}]""");
        Check(!GameState.Ready(waiting), "disabled rest options are not ready");
        waiting["rest_site"]!["options"]![0]!["is_enabled"] = true;
        Check(GameState.Ready(waiting), "enabled rest options remain ready");
        var smithSelection = JsonNode.Parse("""{"state_type":"card_select","card_select":{"cards":[{"index":0,"name":"Strike"}]}}""")!;
        Check(GameState.Ready(smithSelection, JsonNode.Parse("""{"action":"choose_rest_option","index":1}""")),
            "smithing can settle on a card choice before proceed exists");
        Check(GameState.Ready(JsonNode.Parse("""{"state_type":"game_over"}""")!), "game over needs no legal actions");
        foreach (var kind in new[] { "rewards", "treasure", "shop", "fake_merchant", "crystal_sphere" })
        {
            var room = new JsonObject { ["can_proceed"] = false };
            var other = new JsonObject
            {
                ["state_type"] = kind,
                [kind] = kind == "fake_merchant" ? new JsonObject { ["shop"] = room } : room,
                ["player"] = waiting["player"]!.DeepClone()
            };
            Check(!GameState.Ready(other), kind + " also waits through discard-only frames");
            room["can_proceed"] = true;
            var next = GameState.ForcedAction(other, GameState.Actions(other));
            Check(GameState.Ready(other) && next?["command"].Text("action") ==
                (kind == "crystal_sphere" ? "crystal_sphere_proceed" : "proceed"),
                kind + " proceeds automatically once enabled despite the held potion");
        }
        Console.WriteLine("PASS rest animation settlement and post-action proceed in both transports");
    }

    static async Task RestResult(string api)
    {
        var state = JsonNode.Parse("""
            {"state_type":"rest_site","run":{"act":2,"floor":24,"ascension":1},
            "player":{"hp":12,"max_hp":76,"gold":64,"potions":[{"slot":0,"name":"Strength Potion"}]},
            "rest_site":{"options":[{"index":0,"name":"Rest"},{"index":1,"name":"Smith"}],"can_proceed":false}}
            """)!;
        var rested = state.DeepClone();
        rested["player"]!["hp"] = 34;
        rested["rest_site"]!["options"] = new JsonArray();
        rested["rest_site"]!["can_proceed"] = true;
        int commands = 0, animationReads = 0;
        using var handler = new SessionHandler(api);
        handler.Respond = (body, _) =>
        {
            Check(handler.Requests.Count <= 2, "rest and map choice should need only two model calls");
            if (handler.Requests.Count == 2)
            {
                var history = handler.History(body);
                var output = history.First(n => n.Text("type") == "function_call_output" || n.Text("role") == "tool");
                var result = JsonNode.Parse(output.Text(api == "responses" ? "output" : "content"))!;
                Check(result.Flag("settled") && result.Text("snapshot_id") == GameState.Fingerprint(rested),
                    "take_action must return the completed rest snapshot");
                Check(JsonNode.DeepEquals(result["legal_actions"], GameState.Actions(rested)),
                    "take_action must include proceed without list_legal_actions");
                Check(result["outcomes"]!.AsArray().Count == 1
                    && result["outcomes"]![0]!.Text("snapshot_id") == GameState.Fingerprint(rested),
                    "the rest outcome must also use the completed snapshot");
                Check(commands == 2 && state.Text("state_type") == "map"
                    && history.Last(n => n.Text("role") == "user").Text("content").Contains("snapshot=" + GameState.Fingerprint(state)),
                    "automatic proceed reaches the next map decision");
            }
            return Task.FromResult(handler.Action(body));
        };
        var game = new ScheduledGameAdapter(a => a(), () =>
        {
            // Keep identical healed, discard-only frames beyond both stability
            // checks and the existing 600 ms inspection retry delay.
            if (commands == 1 && ++animationReads > 8) state = rested.DeepClone();
            return state.DeepClone();
        }, command =>
        {
            Check(command.Text("action") == (commands switch
            { 0 => "choose_rest_option", 1 => "proceed", _ => "choose_map_node" }),
                "rest should execute once, then proceed without discarding the potion");
            if (commands++ == 0)
            {
                state = rested.DeepClone();
                state["rest_site"]!["can_proceed"] = false;
            }
            else state = commands == 2 ? SessionHandler.Map() : JsonNode.Parse("""{"state_type":"game_over"}""")!;
            return new JsonObject { ["status"] = "ok" };
        }, (_, _, _, _, _) => new JsonObject());
        using var runtime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-rest-" + Guid.NewGuid().ToString("N")), "missing.dll", game, handler);
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["api_endpoint"] = "http://model/v1", ["model"] = "test", ["api_type"] = api });
        runtime.StartGameplay("Rest to heal, then continue.", "run");
        JsonNode status;
        var deadline = DateTime.UtcNow.AddSeconds(12);
        do { await Task.Delay(20); status = await runtime.Dispatch("GET", "/status", null); }
        while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
        Check(!status.Flag("thread_alive") && status.Text("status") == "idle", "rest flow completes " + api + ": " + status.WriteString());
        Check(commands == 3 && handler.Requests.Count == 2 && animationReads >= 11,
            "wait for rest to finish without extra model calls or repeated mutations");
    }
}
