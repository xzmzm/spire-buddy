using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SpireBuddy.Runtime;

internal static class CardChoiceChecks
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static List<JsonNode> Plan(JsonNode state, params string[] ids) => ActionBatch.Select(state, GameState.Actions(state),
        new JsonObject { ["action_ids"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) });
    static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Invalid card sequence was accepted.");
    }

    internal static async Task Run()
    {
        var fixture = new Battle();
        var state = fixture.State;
        foreach (var ids in new[]
        {
            new[] { "c3 c3" }, new[] { "c3 c1 c1" }, new[] { "c3 c1", "c1" },
            new[] { "c1", "a0" }, new[] { "c1", "c3 c1" }, new[] { "a4", "c1" },
            new[] { "a4 c1" }, new[] { "c3 c99" }, new[] { "c0" }, new[] { "c3 a0" },
            new[] { "c1@missing" }, new[] { " " }
        }) Reject(() => Plan(state, ids));
        Reject(() => ActionBatch.Select(state, GameState.Actions(state), JsonNode.Parse("""{"action_ids":[null]}""")!));
        Reject(() => Plan(state, "c3 " + string.Join(' ', Enumerable.Repeat("c1", 32))));
        var snapshot = state.ToJsonString();
        var action = Plan(state, "c3 c2", "a4")[0];
        Check(state.ToJsonString() == snapshot, "planning cannot mutate state");
        Check(action["command"].Text("card_index") == "2" && action["choices"]![0]!.Text("instance_id") == "11", "one-based aliases bind exact original cards");
        fixture.Execute(action["command"]!);
        var choices = new ActionBatch.HandChoices(JsonNode.Parse(snapshot)!, action);
        Check(choices.Next(fixture.State)?["command"].Text("card_index") == "1", "identical copies retain instance identity");
        fixture.State["hand_select"]!["max_select"] = 0;
        Check(choices.Next(fixture.State) == null, "selection limit checked before clicking");
        fixture.State["hand_select"]!["max_select"] = 1;
        fixture.State["hand_select"]!["min_select"] = 2;
        Check(choices.Next(fixture.State) == null, "incomplete planned selection withheld");
        fixture.State["hand_select"]!["min_select"] = 1;
        fixture.State["hand_select"]!["cards"]![1]!["can_select"] = false;
        Check(choices.Next(fixture.State) == null, "only currently legal selection commands execute");
        fixture.State["hand_select"]!["cards"]![1]!["can_select"] = true;
        fixture.State["player"]!["draw_pile_count"] = 7;
        Check(choices.Next(fixture.State) == null, "new draw stops before choice");

        fixture = new Battle();
        foreach (var card in fixture.State["player"]!["hand"].Items()) card.AsObject().Remove("instance_id");
        var original = fixture.State.DeepClone();
        action = Plan(original, "c3 c1")[0];
        fixture.Execute(action["command"]!);
        Check(new ActionBatch.HandChoices(original, action).Next(fixture.State) == null, "ambiguous legacy copies are never guessed");
        fixture = new Battle();
        original = fixture.State.DeepClone();
        action = Plan(original, "c3 c1")[0];
        fixture.Execute(action["command"]!);
        choices = new ActionBatch.HandChoices(original, action);
        var toggle = choices.Next(fixture.State)!;
        fixture.Execute(toggle["command"]!); choices.Accepted(toggle);
        fixture.State["hand_select"]!["can_confirm"] = false;
        Check(choices.Next(fixture.State) == null, "unavailable confirmation is never invented");
        fixture.State["hand_select"]!["can_confirm"] = true;
        fixture.State["hand_select"]!["selected_cards"]![0]!["instance_id"] = 11;
        Check(choices.Next(fixture.State) == null, "unexpected selection cannot be confirmed");

        state = new Battle().State;
        state["player"]!["hand"]![0]!["target_type"] = "AnyEnemy";
        state["battle"]!["enemies"] = JsonNode.Parse("""[{"entity_id":"left","hp":20},{"entity_id":"right","hp":20}]""");
        Reject(() => Plan(state, "c1"));
        Check(Plan(state, "c1@right")[0]["command"].Text("target") == "right", "explicit target resolves");
        state["battle"]!["enemies"]![0]!["hp"] = 0.0;
        Check(Plan(state, "c1")[0]["command"].Text("target") == "right", "single legal target may be omitted");
        state["player"]!["hand"]![2]!["description"] = "Draw 2 cards. Discard a card.";
        Reject(() => Plan(state, "c3 c1", "a4"));
        Check(Plan(state, "c3 c1").Count == 1, "uncertain play may end a batch");

        state = new Battle().State;
        var actions = GameState.Actions(state);
        var brief = GameState.Brief(GameState.Fingerprint(state), "win", "block", state, actions);
        Check(brief.Contains("c3: Survivor") && brief.Contains("play: c1 c2 c3 c4") && !brief.Contains("a2 play Survivor"), "brief avoids repeated card action descriptions");
        var legacyLength = brief.Replace("c1:", "0:").Replace("c2:", "1:").Replace("c3:", "2:").Replace("c4:", "3:")
            .Replace("  play: c1 c2 c3 c4\n", string.Join("\n", actions.Items().Where(a => a["command"].Text("action") == "play_card").Select(a => "  " + a.Text("id") + " " + a.Text("summary"))) + "\n").Length;
        Check(brief.Length < legacyLength, "card aliases reduce brief size");
        fixture = new Battle();
        fixture.Execute(Plan(fixture.State, "c3")[0]["command"]!);
        brief = GameState.Brief("id", "", "", fixture.State, GameState.Actions(fixture.State));
        Check(brief.Contains("select: c1 c2 c3") && !brief.Contains("[Hand 3]"), "selection brief has only one card numbering space");
        Check(Plan(fixture.State, "c2")[0]["command"].Text("action") == "combat_select_card", "card aliases also select on an existing selection screen");
        Console.WriteLine($"PASS card-choice validation and compact briefs ({legacyLength} -> {GameState.Brief(GameState.Fingerprint(state), "win", "block", state, actions).Length} characters)");

        foreach (var api in new[] { "responses", "chat_completions" })
        foreach (var mode in new[] { "basic", "legacy", "shifted", "multi", "auto_confirm", "auto_select", "auto_end", "two_plays", "new_draw", "missing", "stop" })
            await Execute(api, mode);
    }

    static async Task Execute(string api, string mode)
    {
        var battle = new Battle(mode);
        string[] ids = mode switch
        {
            "legacy" => ["a2 c1", "a1", "a4"],
            "shifted" => ["c1", "c3 c2", "a4"],
            "multi" => ["c3 c2 c1", "c4", "a4"],
            "auto_end" => ["c3 c1"],
            "auto_select" => ["c2 c1", "a2"],
            "two_plays" => ["c3 c1", "c4 c2", "a4"],
            _ => ["c3 c1", "c2", "a4"]
        };
        using var handler = new ChoiceHandler(api, ids);
        using var runtime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-choices-" + Guid.NewGuid().ToString("N")), "missing.dll", battle.Adapter, handler);
        battle.AfterSelect = mode == "stop" ? () => runtime.Dispatch("POST", "/stop", null).GetAwaiter().GetResult() : null;
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["api_endpoint"] = "http://model/v1", ["model"] = "test", ["api_type"] = api });
        runtime.StartGameplay("Finish the turn.", "fight");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonNode status;
        do { await Task.Delay(20); status = await runtime.Dispatch("GET", "/status", null); }
        while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
        Check(!status.Flag("thread_alive"), "choice sequence must finish " + api + mode);
        var expected = mode switch
        {
            "shifted" => "play:10 play:12 choose:11 confirm end",
            "multi" => "play:12 choose:11 choose:10 confirm play:13 end",
            "auto_confirm" => "play:12 choose:10 play:11 end",
            "auto_select" => "play:12 end",
            "auto_end" => "play:12 choose:10 confirm end",
            "two_plays" => "play:12 choose:10 confirm play:13 choose:11 confirm end",
            "new_draw" => "play:12 choose:10 confirm",
            "missing" => "play:12",
            "stop" => "play:12 choose:10",
            _ => "play:12 choose:10 confirm play:11 end"
        };
        Check(string.Join(' ', battle.Log) == expected, "exact card/action order " + api + mode + ": " + string.Join(' ', battle.Log) + " status=" + status.Text("last_report"));
        bool interrupted = mode is "new_draw" or "missing";
        Check(handler.Calls == (interrupted ? 2 : 1), "model calls " + api + mode);
        Check(status.Text("status") == (interrupted ? "error" : "idle"), "sequence status " + api + mode);
        Console.WriteLine($"PASS {api} inline hand choices {mode}");
    }

    sealed class Battle
    {
        readonly string mode;
        internal JsonNode State = JsonNode.Parse("""
            {"state_type":"monster","battle":{"round":1,"turn":"player"},"player":{"draw_pile_count":8,"potions":[],"hand":[
              {"index":0,"instance_id":10,"id":"DEFEND","name":"Defend","cost":1,"target_type":"Self","description":"Gain 5 Block."},
              {"index":1,"instance_id":11,"id":"DEFEND","name":"Defend","cost":1,"target_type":"Self","description":"Gain 5 Block."},
              {"index":2,"instance_id":12,"id":"SURVIVOR","name":"Survivor","cost":1,"target_type":"Self","description":"Gain 8 Block. Discard a card."},
              {"index":3,"instance_id":13,"id":"STRIKE","name":"Strike","cost":1,"target_type":"Self","description":"Deal 6 damage."}
            ]}}
            """)!;
        internal readonly List<string> Log = [];
        internal Action? AfterSelect;
        internal IGameAdapter Adapter => new ScheduledGameAdapter(a => a(), () => State.DeepClone(), Execute, (_, _, _, _, _) => new JsonObject());
        JsonArray Hand => State["player"]!["hand"]!.AsArray();
        internal Battle(string mode = "basic")
        {
            this.mode = mode;
            if (mode == "two_plays") Hand[3]!["id"] = "SURVIVOR";
            if (mode == "auto_end") foreach (var card in Hand.Items()) card["can_play"] = card.Num("instance_id") == 12;
            if (mode == "auto_select") { Hand.RemoveAt(3); Hand.RemoveAt(1); Reindex(Hand); }
        }
        static void Reindex(JsonArray cards) { for (int i = 0; i < cards.Count; i++) cards[i]!["index"] = i; }
        static JsonNode SelectionCard(JsonNode card)
        {
            var result = card.DeepClone().AsObject();
            result.Remove("target_type"); result.Remove("can_play");
            return result;
        }
        internal JsonNode Execute(JsonNode command)
        {
            switch (command.Text("action"))
            {
                case "play_card":
                    var card = Hand[command.Num("card_index")!.Value]!;
                    Log.Add("play:" + card["instance_id"]);
                    bool selection = card.Text("id") == "SURVIVOR";
                    Hand.RemoveAt(command.Num("card_index")!.Value); Reindex(Hand);
                    if (selection)
                    {
                        State["state_type"] = "hand_select";
                        State["hand_select"] = new JsonObject
                        {
                            ["mode"] = "simple_select", ["min_select"] = mode == "multi" ? 2 : 1, ["max_select"] = mode == "multi" ? 2 : 1,
                            ["cards"] = new JsonArray(Hand.Items().Select(SelectionCard).ToArray()), ["selected_cards"] = new JsonArray(), ["can_confirm"] = false
                        };
                        if (mode == "missing") State["hand_select"]!["cards"]!.AsArray().RemoveAt(0);
                        if (mode == "auto_select")
                        {
                            State["hand_select"]!["selected_cards"]!.AsArray().Add(SelectionCard(Hand[0]!));
                            Confirm();
                        }
                    }
                    break;
                case "combat_select_card":
                    var data = State["hand_select"]!;
                    var cards = data["cards"]!.AsArray();
                    var selected = cards[command.Num("card_index")!.Value]!.DeepClone();
                    Log.Add("choose:" + selected["instance_id"]);
                    cards.RemoveAt(command.Num("card_index")!.Value); Reindex(cards);
                    data["selected_cards"]!.AsArray().Add(selected);
                    data["can_confirm"] = data["selected_cards"]!.AsArray().Count >= data.Num("min_select");
                    if (mode == "auto_confirm") Confirm();
                    AfterSelect?.Invoke();
                    break;
                case "combat_confirm_selection": Log.Add("confirm"); Confirm(); break;
                case "end_turn": Log.Add("end"); State = new JsonObject { ["state_type"] = "game_over" }; break;
                default: throw new Exception("Unexpected mutation " + command);
            }
            return new JsonObject { ["status"] = "ok" };
        }
        void Confirm()
        {
            foreach (var chosen in State["hand_select"]!["selected_cards"].Items())
                Hand.Remove(Hand.Items().Single(c => c.Num("instance_id") == chosen.Num("instance_id")));
            Reindex(Hand);
            State.AsObject().Remove("hand_select");
            State["state_type"] = "monster";
            if (mode == "new_draw") State["player"]!["draw_pile_count"] = 7;
        }
    }

    sealed class ChoiceHandler(string api, string[] ids) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // A second request proves a stopped chain handed its fresh state back
            // to the model. End the fixture without authorizing any more actions.
            if (++Calls > 1) throw new HttpRequestException("Fixture: fresh decision reached.");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var brief = body[api == "responses" ? "input" : "messages"].Items().Last(n => n.Text("role") == "user").Text("content");
            var args = new JsonObject
            {
                ["snapshot_id"] = Regex.Match(brief, "snapshot=([0-9A-Fa-f]{16})").Groups[1].Value,
                ["action_ids"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["message"] = "Blocking and discarding.", ["rationale"] = "Known hand choices.", ["plan"] = "End turn."
            }.ToJsonString();
            var response = api == "responses"
                ? new JsonObject { ["output"] = new JsonArray(new JsonObject { ["type"] = "function_call", ["call_id"] = "choices", ["name"] = "take_action", ["arguments"] = args }) }
                : new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "choices", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "take_action", ["arguments"] = args } }) } }) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
