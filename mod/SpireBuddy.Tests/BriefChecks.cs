using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class BriefChecks
{
    internal static async Task Histories()
    {
        foreach (var api in new[] { "responses", "chat_completions" })
        foreach (var reset in new[] { false, true })
        {
            using var handler = new HistoryHandler(api, reset);
            var directory = Path.Combine(Path.GetTempPath(), "spire-brief-" + Guid.NewGuid().ToString("N"));
            var game = new ScheduledGameAdapter(a => a(), handler.State,
                _ => { handler.Step++; return new JsonObject { ["status"] = "ok" }; },
                (_, _, _, _, _) => new JsonObject());
            using var runtime = new BotRuntime(directory, "missing.dll", game, handler);
            await runtime.Dispatch("PUT", "/settings", new JsonObject
            {
                ["api_endpoint"] = "http://model/v1", ["model"] = "test", ["api_type"] = api,
                ["max_context_tokens"] = 250000
            });
            runtime.StartGameplay("win", "run");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            JsonNode status;
            do { await Task.Delay(50); status = await runtime.Dispatch("GET", "/status", null); }
            while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
            if (status.Text("status") != "idle" || handler.Step != 5)
                throw new Exception("Brief history lifecycle failed: " + status);
            Console.WriteLine($"PASS {api} brief history transitions (forced reset={reset})");
        }
    }

    internal static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var state = JsonNode.Parse("""
            {"state_type":"map","run":{"act":1,"floor":9},"player":{
              "hp":59,"max_hp":75,"gold":154,"max_potion_slots":2,
              "deck":[{"name":"Zap","description":"Channel 1 Lightning.","keywords":[{"name":"Channel","description":"Make an orb."}]}],
              "relics":[{"id":"CORE","name":"Cracked Core","description":"Start with Lightning.","counter":0}],
              "potions":[{"slot":0,"name":"Flex Potion","description":"Gain 5 Strength."}]
            }}
            """)!;
        var original = state.ToJsonString();
        var actions = new JsonArray(new JsonObject { ["id"] = "a0", ["summary"] = "map Treasure[0]" });
        var memory = new BriefMemory();
        string Render(BriefMemory context, string goal = "win") => GameState.Brief("0123456789ABCDEF", goal, "seek block", state, actions, context);
        var first = Render(memory);
        var second = Render(memory);
        Check(first.Contains("[Deck 1]") && first.Contains("Start with Lightning.") && first.Contains("Gain 5 Strength.") && first.Contains("Channel: Make an orb."), "first brief must be self-contained");
        Check(!second.Contains("[Deck") && !second.Contains("[Relics") && !second.Contains("[Keywords]") && !second.Contains("instructions="), "unchanged sections omitted");
        Check(second.Contains("[Potions 1/2] free slots: 1\n") && second.Contains("[Potion 0] Flex Potion\n") && !second.Contains("Gain 5 Strength."), "known potions retain capacity, free slots, and names only");
        Check(second.Contains("snapshot=0123456789ABCDEF") && second.Contains("HP 59/75") && second.Contains("plan=seek block") && second.Contains("a0 map Treasure[0]"), "current decision essentials retained");
        Check(state.ToJsonString() == original, "brief memory cannot mutate public state");
        Check(Render(new BriefMemory()) == first, "new or cleared history must restore all context");
        Check(Render(memory, "survive").Contains("instructions=survive"), "changed instructions reported");

        state["player"]!["relics"]![0]!["counter"] = 2;
        var counter = Render(memory);
        Check(counter.Contains("Cracked Core (2)") && !counter.Contains("Start with Lightning."), "counter changes reported without repeated description");
        state["player"]!["relics"]![0]!["description"] = "Start with Frost.";
        Check(Render(memory).Contains("Start with Frost."), "changed relic rules resent");
        state["player"]!["relics"]!.AsArray().Clear();
        Check(Render(memory).Contains("[Relics 0] none"), "empty relic roster explicitly replaces old roster");

        state["player"]!["potions"]!.AsArray().Clear();
        var empty = Render(memory);
        Check(empty.Contains("Flex Potion no longer held") && empty.Contains("[Potions] none (2 slots)"), "potion removal explicit without claiming it was used");
        Check(!Render(memory).Contains("no longer held"), "removal only reported once");
        state["player"]!["potions"]!.AsArray().Add(JsonNode.Parse("""{"slot":1,"name":"Flex Potion","description":"Gain 5 Strength."}"""));
        var reacquired = Render(memory);
        Check(reacquired.Contains("[Potion 1] Flex Potion\n") && !reacquired.Contains("Gain 5 Strength."), "reacquired potion uses remembered definition and current slot");
        state["player"]!["max_potion_slots"] = 3;
        state["player"]!["potions"]!.AsArray().Add(JsonNode.Parse("""{"slot":2,"name":"Swift Potion","description":"Draw 3 cards."}"""));
        var gap = Render(memory);
        Check(gap.Contains("[Potions 2/3] free slots: 0\n"), "leading free slot named so held slot indexes cannot read as a full belt");
        state["player"]!["potions"]!.AsArray().Add(JsonNode.Parse("""{"slot":0,"name":"Regen Potion","description":"Heal 15."}"""));
        Check(Render(memory).Contains("[Potions 3/3] all slots full\n"), "full belt states capacity without free slots");
        state["player"]!["potions"]![0]!["description"] = "Gain 7 Strength.";
        Check(Render(memory).Contains("Gain 7 Strength."), "changed potion definition resent");
        state["player"]!["potions"]![0]!["name"] = "Other Potion";
        Check(Render(memory).Contains("Flex Potion no longer held"), "replacement at same slot reports removal");

        state["player"]!["deck"]![0]!["is_upgraded"] = true;
        Check(Render(memory).Contains("Zap+ (Channel 1 Lightning.)"), "deck upgrades reported");
        state["player"]!["deck"]![0]!["keywords"]![0]!["description"] = "Make a different orb.";
        Check(Render(memory).Contains("Channel: Make a different orb."), "changed keyword definition resent");
        state["player"]!["deck"]!.AsArray().Clear();
        Check(Render(memory).Contains("[Deck 0]"), "empty deck explicitly replaces old deck");
        Console.WriteLine($"PASS history-scoped brief compression ({first.Length} -> {second.Length} characters unchanged)");
    }
}

internal sealed class HistoryHandler(string api, bool reset) : HttpMessageHandler
{
    internal int Step;
    readonly Dictionary<int, string> sessions = new();
    internal JsonNode State()
    {
        if (Step >= 5) return new JsonObject { ["state_type"] = "game_over" };
        var state = JsonNode.Parse("""
            {"state_type":"map","map":{"next_options":[{"index":0},{"index":1}]},
            "run":{"floor":1},"player":{"deck":[{"name":"Zap","keywords":[{"name":"Channel","description":"Make an orb."}]}],
            "relics":[{"name":"Core","description":"Start with Lightning."}],"potions":[]}}
            """)!;
        state["run"]!["floor"] = Step;
        if (Step is 1 or 2 or 4)
        {
            state["state_type"] = "monster";
            state["battle"] = new JsonObject { ["round"] = Step, ["turn"] = "player" };
        }
        return state;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
        var history = body[api == "responses" ? "input" : "messages"]!.AsArray();
        sessions[Step] = body.Text("prompt_cache_key");
        if (Step == 2 && (sessions[1] == sessions[2]) == reset)
            throw new Exception("Combat must retain its session until its own context rolls.");
        if (Step == 3 && (sessions[0] == sessions[3]) == reset)
            throw new Exception("Game session must resume independently of combat, respecting its own token limit.");
        if (Step == 4 && (sessions[4] == sessions[1] || sessions[4] == sessions[2]))
            throw new Exception("A new fight must launch a fresh combat session.");
        var brief = history.Last(n => n.Text("role") == "user")!.Text("content");
        // map -> new combat -> same combat -> retained run -> another new combat
        var full = reset || Step is 0 or 1 or 4;
        if (brief.Contains("[Deck") != full || brief.Contains("Start with Lightning.") != full || brief.Contains("[Keywords]") != full)
            throw new Exception($"Missing or redundant history context at step {Step}: {brief}");
        var args = new JsonObject
        {
            ["snapshot_id"] = GameState.Fingerprint(State()), ["action_ids"] = new JsonArray("a0"),
            ["message"] = "Continue.", ["rationale"] = "test", ["plan"] = "win"
        }.ToJsonString();
        var response = api == "responses"
            ? new JsonObject { ["output"] = new JsonArray(new JsonObject { ["type"] = "function_call", ["call_id"] = "action", ["name"] = "take_action", ["arguments"] = args }) }
            : new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "action", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "take_action", ["arguments"] = args } }) } }) };
        if (reset) response["usage"] = new JsonObject { ["input_tokens"] = 300000, ["output_tokens"] = 100 };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
    }
}
