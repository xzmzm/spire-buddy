using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SpireBuddy.Runtime;

internal static class NonCombatBatchChecks
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    static JsonNode Shop(string kind)
    {
        var shop = JsonNode.Parse("""
            {"inventory_open":true,"can_close":true,"items":[
            {"index":0,"category":"card","card_id":"DRAW_CARD","card_description":"Draw 2 cards.","price":50,"is_stocked":true,"can_afford":true},
            {"index":1,"category":"potion","potion_id":"FIRE","price":60,"is_stocked":true,"can_afford":true},
            {"index":2,"category":"relic","relic_id":"UNWANTED","price":70,"is_stocked":true,"can_afford":true}]}
            """)!;
        if (kind == "fake_merchant")
        {
            shop["items"]![0] = JsonNode.Parse("""{"index":0,"category":"relic","relic_id":"FIRST","price":50,"is_stocked":true,"can_afford":true}""");
            shop["items"]![1] = JsonNode.Parse("""{"index":1,"category":"relic","relic_id":"SECOND","price":60,"is_stocked":true,"can_afford":true}""");
        }
        return new JsonObject
        {
            ["state_type"] = kind,
            [kind] = kind == "shop" ? shop : new JsonObject { ["shop"] = shop },
            ["player"] = new JsonObject { ["gold"] = 300, ["potions"] = new JsonArray() }
        };
    }

    internal static async Task Run()
    {
        foreach (var kind in new[] { "shop", "fake_merchant" })
        {
            var before = Shop(kind);
            var selected = ActionBatch.Select(before, GameState.Actions(before),
                JsonNode.Parse("""{"action_ids":["a0","a1","a3"]}""")!);
            var after = before.DeepClone();
            var inventory = kind == "shop" ? after[kind]! : after[kind]!["shop"]!;
            inventory["items"]![0]!["is_stocked"] = false;
            Check(ActionBatch.Resolve(before, selected[1], after) != null, "purchases survive earlier stock changes");
            inventory["items"]![1]!["can_afford"] = false;
            Check(ActionBatch.Resolve(before, selected[1], after) == null, "unaffordable queued purchase is withheld");
            inventory["items"]![1]!["can_afford"] = true;
            inventory["items"]![1]!["price"] = 90;
            Check(ActionBatch.Resolve(before, selected[1], after) == null, "changed price needs a fresh decision");
            inventory["can_close"] = false;
            Check(ActionBatch.Resolve(before, selected[2], after) == null, "unavailable close is withheld");
            var selection = JsonNode.Parse("""{"state_type":"card_select","card_select":{"cards":[{"index":0}]}}""")!;
            Check(!ActionBatch.CanContinue(before, selection, selected[0]) &&
                ActionBatch.Resolve(before, selected[2], selection) == null, "purchase opening a selection ends the batch");
            try
            {
                ActionBatch.Select(before, GameState.Actions(before), JsonNode.Parse("""{"action_ids":["a3","a0"]}""")!);
                throw new Exception("close before a purchase accepted");
            }
            catch (InvalidOperationException) { }
        }

        foreach (var api in new[] { "responses", "chat_completions" })
        foreach (var kind in new[] { "shop", "fake_merchant", "rewards", "card_select", "hand_select", "bundle_select" })
            await ExecuteBatch(api, kind);
        Console.WriteLine("PASS non-combat purchase, reward, selection and terminal batches in both transports");
    }

    static async Task ExecuteBatch(string api, string kind)
    {
        bool shop = kind is "shop" or "fake_merchant";
        string field = kind == "bundle_select" ? "bundles" : "cards";
        string terminal = kind switch
        {
            "shop" or "fake_merchant" => "close_shop",
            "rewards" => "proceed",
            "hand_select" => "combat_confirm_selection",
            "bundle_select" => "confirm_bundle_selection",
            _ => "confirm_selection"
        };
        var state = shop ? Shop(kind) : kind == "rewards"
            ? JsonNode.Parse("""{"state_type":"rewards","player":{"potions":[]},"rewards":{"can_proceed":true,"items":[{"index":0,"type":"gold","amount":50},{"index":1,"type":"potion","name":"Fire Potion"}]}}""")!
            : new JsonObject { ["state_type"] = kind, [kind] = new JsonObject
                { ["can_confirm"] = true, [field] = JsonNode.Parse("""[{"index":0,"id":"FIRST","selected":false},{"index":1,"id":"SECOND","selected":false}]""") } };
        int commands = 0;
        using var handler = new BatchHandler(api, shop ? new JsonArray("a0", "a1", "a3") : new JsonArray("a0", "a1", "a2"));
        var game = new ScheduledGameAdapter(action => action(), () => state.DeepClone(), command =>
        {
            var expected = commands < 2 ? kind switch
            {
                "shop" or "fake_merchant" => "shop_purchase",
                "rewards" => "claim_reward",
                "hand_select" => "combat_select_card",
                "bundle_select" => "select_bundle",
                _ => "select_card"
            } : commands == 2 ? terminal : "proceed";
            Check(command.Text("action") == expected, "batch command order " + kind);
            if (commands < 2)
            {
                if (shop)
                {
                    Check(command.Text("index") == commands.ToString(), "correct queued shop item");
                    var inventory = kind == "shop" ? state[kind]! : state[kind]!["shop"]!;
                    inventory["items"]![commands]!["is_stocked"] = false;
                    state["player"]!["gold"] = commands == 0 ? 250 : 190;
                    if (kind == "shop" && commands == 1)
                        state["player"]!["potions"]!.AsArray().Add(JsonNode.Parse("""{"slot":0,"name":"Fire Potion"}"""));
                }
                else if (kind == "rewards")
                {
                    Check(command.Text("index") == "0", "reward index re-resolves after claim");
                    var items = state[kind]!["items"]!.AsArray();
                    items.RemoveAt(0);
                    if (items.Count > 0) items[0]!["index"] = 0;
                }
                else
                {
                    var key = kind == "hand_select" ? "card_index" : "index";
                    Check(command.Text(key) == commands.ToString(), "correct selection toggle");
                    state[kind]![field]![commands]!["selected"] = true;
                }
            }
            else if (shop && commands == 2)
            {
                // Resume inside an already-open inventory: close must mark this
                // merchant visited so the local navigation never reopens it.
                var closed = JsonNode.Parse("""{"can_open":true,"can_proceed":true,"inventory_open":false}""")!;
                state[kind] = kind == "shop" ? closed : new JsonObject { ["shop"] = closed };
            }
            else state = JsonNode.Parse("""{"state_type":"game_over"}""")!;
            commands++;
            return new JsonObject { ["status"] = "ok" };
        }, (_, _, _, _, _, _) => new JsonObject());
        using var runtime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-batch-" + Guid.NewGuid().ToString("N")), "missing.dll", game, handler);
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["api_endpoint"] = "http://model/v1", ["model"] = "test", ["api_type"] = api });
        runtime.StartGameplay("Finish the chosen sequence.", "run");
        JsonNode status;
        var deadline = DateTime.UtcNow.AddSeconds(12);
        do { await Task.Delay(20); status = await runtime.Dispatch("GET", "/status", null); }
        while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
        Check(!status.Flag("thread_alive") && status.Text("status") == "idle", "batch completes " + api + kind + status.ToJsonString());
        Check(commands == (shop ? 4 : 3) && handler.Calls == 1, "whole sequence uses one model call " + api + kind);
    }

    sealed class BatchHandler(string api, JsonArray ids) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(++Calls == 1, "unexpected extra model call during deterministic batch");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var brief = body[api == "responses" ? "input" : "messages"].Items().Last(n => n.Text("role") == "user").Text("content");
            var snapshot = Regex.Match(brief, @"snapshot=([0-9A-Fa-f]{16})").Groups[1].Value;
            var args = new JsonObject { ["snapshot_id"] = snapshot, ["action_ids"] = ids.DeepClone(), ["message"] = "Taking the chosen items.", ["rationale"] = "The complete sequence is known.", ["plan"] = "Continue." }.ToJsonString();
            var response = api == "responses"
                ? new JsonObject { ["output"] = new JsonArray(new JsonObject { ["type"] = "function_call", ["call_id"] = "batch", ["name"] = "take_action", ["arguments"] = args }) }
                : new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "batch", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "take_action", ["arguments"] = args } }) } }) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
