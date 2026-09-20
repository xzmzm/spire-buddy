using System.Net;
using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class MerchantChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static async Task Run()
    {
        var waiting = JsonNode.Parse("""{"state_type":"fake_merchant","fake_merchant":{"shop":{"items":[],"can_proceed":false}},"player":{"potions":[{"slot":0}]}}""")!;
        Check(!GameState.Ready(waiting), "an uninitialized fake merchant is not ready, even with a held potion");
        Check(GameState.ForcedAction(waiting, GameState.Actions(waiting)) == null, "no shop action is invented while the event is loading");
        var defeated = waiting.DeepClone();
        defeated["fake_merchant"]!["started_fight"] = true;
        defeated["fake_merchant"]!["shop"]!["can_proceed"] = true;
        Check(GameState.ForcedAction(defeated, GameState.Actions(defeated))?["command"].Text("action") == "proceed", "a defeated merchant proceeds without trying to open a shop");

        foreach (var kind in new[] { "shop", "fake_merchant" })
        foreach (bool jev in new[] { false, true })
            await EnterShop(kind, jev);
        Console.WriteLine("PASS normal/fake merchant loading, automatic entry for both providers, purchase, close and proceed without reopening");
    }

    static async Task EnterShop(string kind, bool jev)
    {
        using var scenario = new Scenario(kind, jev);
        var directory = Path.Combine(Path.GetTempPath(), "spire-merchant-" + Guid.NewGuid().ToString("N"));
        using var runtime = new BotRuntime(directory, "missing.dll", scenario.Game, scenario);
        await runtime.Dispatch("PUT", "/settings", new JsonObject
        {
            ["use_jev_strategy"] = jev, ["api_endpoint"] = "https://buddy.test/v1", ["jev_endpoint"] = "https://jev.test/evaluate"
        });
        runtime.StartGameplay("Continue the run.", "run");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonNode status;
        do { await Task.Delay(20); status = await runtime.Dispatch("GET", "/status", null); }
        while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
        Check(!status.Flag("thread_alive") && status.Text("status") == "idle", "merchant run stopped: " + status["agents"].Text("last_report"));
        Check(scenario.LoadingReads >= 4 && scenario.Requests == 1, "loading and navigation need no provider calls");
        Check(scenario.Commands.SequenceEqual(new[] { "choose_map_node", "open_shop", "shop_purchase", "close_shop", "proceed" }), "merchant action order: " + string.Join(",", scenario.Commands));
    }

    sealed class Scenario(string kind, bool jev) : HttpMessageHandler
    {
        JsonNode state = JsonNode.Parse("""{"state_type":"map","map":{"next_options":[{"index":0,"type":"Unknown"}]}}""")!;
        bool entering;
        internal int LoadingReads, Requests;
        internal readonly List<string> Commands = [];
        JsonNode Room(bool open = false, bool loading = false)
        {
            var shop = new JsonObject
            {
                ["inventory_open"] = open, ["can_open"] = !open && !loading,
                ["can_close"] = open, ["can_proceed"] = !open && !loading,
                ["items"] = open ? JsonNode.Parse("""[{"index":0,"category":"relic","relic_id":"TEST","relic_name":"Test relic","price":50,"is_stocked":true,"can_afford":true}]""") : new JsonArray()
            };
            return new JsonObject
            {
                ["state_type"] = kind, ["loading"] = loading,
                [kind] = kind == "shop" ? shop : new JsonObject { ["event_id"] = "FAKE_MERCHANT", ["shop"] = shop },
                ["player"] = JsonNode.Parse("""{"hp":50,"max_hp":70,"gold":100,"max_potion_slots":2,"potions":[{"slot":0,"name":"Held potion"}]}""")
            };
        }

        internal IGameAdapter Game => new ScheduledGameAdapter(a => a(), () =>
        {
            if (entering && ++LoadingReads >= 4) { state = Room(); entering = false; }
            return state.DeepClone();
        }, command =>
        {
            var action = command.Text("action");
            Commands.Add(action);
            switch (action)
            {
                case "choose_map_node": state = Room(loading: true); entering = true; break;
                case "open_shop":
                    Check(Requests == 0, "shop opens before asking either provider, despite an available proceed button");
                    state = Room(open: true); break;
                case "shop_purchase":
                    Check(Requests == 1, "purchases remain a provider decision");
                    JevStrategy.Shop(state)!["items"]![0]!["is_stocked"] = false;
                    state["player"]!["gold"] = 50; break;
                case "close_shop": state = Room(); state["player"]!["gold"] = 50; break;
                case "proceed": state = new JsonObject { ["state_type"] = "game_over" }; break;
                default: throw new Exception("Unexpected merchant command " + action);
            }
            return new JsonObject { ["status"] = "ok" };
        }, (_, _, _, _, _, _) => new JsonObject());

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Requests++;
            Check((body["questions"] != null) == jev, "purchase uses the configured provider");
            Check(Commands.SequenceEqual(new[] { "choose_map_node", "open_shop" }), "provider only sees the opened inventory");
            var purchase = GameState.Actions(state).Items().Single(a => a["command"].Text("action") == "shop_purchase");
            var response = jev
                ? new JsonObject { ["answers"] = new JsonObject { ["action0"] = new JsonObject { ["type"] = "choice", ["choice"] = purchase.Text("id") } } }
                : new SessionHandler("responses").Call("take_action", new JsonObject
                {
                    ["snapshot_id"] = GameState.Fingerprint(state), ["action_ids"] = new JsonArray(purchase.Text("id")),
                    ["message"] = "Buy the relic.", ["rationale"] = "", ["plan"] = ""
                });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.WriteString()) };
        }
    }
}
