using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SpireBuddy.Runtime;

internal static class EventKnowledgeChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static string Temp() => Path.Combine(Path.GetTempPath(), "spire-event-check-" + Guid.NewGuid().ToString("N"));
    static string FixtureAssembly => typeof(EventKnowledgeChecks).Assembly.Location;

    static JsonNode State(int stage = 0, string id = "KNOWLEDGE_FLOWER")
    {
        var state = JsonNode.Parse("""
        {"state_type":"event","run":{"act":2,"floor":22},
         "player":{"hp":40,"max_hp":70,"gold":100,"potions":[],
           "deck":[{"name":"Strike","cost":1,"description":"Deal 6 damage."}],"relics":[]},
         "event":{"event_name":"巨型花朵","options":[{"index":0,"title":"取蜜"},{"index":1,"title":"深入"}]},
         "map":{"bosses":[{"name":"Visible boss"}],"next_options":[{"index":0,"col":0,"row":3,"type":"Monster"}],
           "nodes":[{"col":0,"row":3,"type":"Monster","children":[[0,4]]},{"col":0,"row":4,"type":"RestSite","children":[]}]}}
        """)!;
        state["player"]!["hp"] = 40 - new[] { 0, 5, 11 }[stage];
        state["event"]!["event_id"] = id;
        var options = state["event"]!["options"]!;
        options[0]!["description"] = $"Gain {new[] { 35, 75, 135 }[stage]} Gold.";
        options[1]!["description"] = $"Lose {stage + 5} HP.";
        options[0]!["text_key"] = $"{id}.EXTRACT_{stage + 1}";
        options[1]!["text_key"] = $"{id}.REACH_DEEPER_{stage + 1}";
        return state;
    }

    internal static async Task Run()
    {
        var directory = Temp();
        var lookup = new EventKnowledge(FixtureAssembly, directory);
        var rules = await lookup.Lookup("KNOWLEDGE_FLOWER", CancellationToken.None);
        Check(rules.Text("status") == "ok", "fixture event lookup: " + rules);
        Check(rules["matches"].Items().Single().Text("decompiled_source").Contains("hp >= 19"), "appearance condition is available");
        var related = rules["related_models"].Items().Select(n => n.Text("name")).ToArray();
        Check(related.Contains("KnowledgeCore") && related.Contains("KnowledgeDraw") && !related.Contains("KnowledgeDecoy"),
            "reward effects and transitive power rules are included, but prose names do not become dependencies");
        var same = await lookup.Lookup("Knowledge Flower", CancellationToken.None);
        Check(JsonNode.DeepEquals(same, rules), "registry IDs and English names resolve to the same rules");
        same["matches"]![0]!["decompiled_source"] = "altered";
        Check(JsonNode.DeepEquals(rules, await lookup.Lookup("KNOWLEDGE_FLOWER", CancellationToken.None)), "callers cannot mutate the cache");
        Check(JsonNode.DeepEquals(rules, await new EventKnowledge(FixtureAssembly, directory).Lookup("KnowledgeFlower", CancellationToken.None)), "disk cache preserves full dependency bundle");
        var changedAssembly = Path.Combine(directory, "changed.dll");
        File.Copy(FixtureAssembly, changedAssembly);
        File.AppendAllText(changedAssembly, "\n"); // Valid PE with different bytes; never alter the real assembly.
        var changed = await new EventKnowledge(changedAssembly, directory).Lookup("KnowledgeFlower", CancellationToken.None);
        Check(changed.Text("status") == "ok" && changed.Text("assembly_sha256") != rules.Text("assembly_sha256"), "changed game bytes use a different cache");

        var ambiguous = await lookup.Lookup("Knowledge", CancellationToken.None);
        Check(ambiguous.Text("status") == "ambiguous" && !ambiguous["matches"].Items().Any(), "ambiguous names never select an arbitrary event");
        Check((await lookup.Lookup("no-such-event", CancellationToken.None)).Text("status") == "unavailable", "unknown event remains unknown");
        var missing = await new EventKnowledge("missing.dll", Temp()).Lookup("KnowledgeFlower", CancellationToken.None);
        Check(missing.Text("status") == "unavailable" && missing.Text("decompile_error").Length > 0, "missing assembly is explicit, not empty success");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await lookup.Lookup("KnowledgeFlower", cancelled.Token); throw new Exception("cancel was ignored"); }
        catch (OperationCanceledException) { }
        var partial = await lookup.Lookup("KnowledgeCrowded", CancellationToken.None);
        Check(partial.Text("status") == "partial" && partial["omitted_sources"].Items().Any()
            && EventKnowledge.Brief(partial).Contains("[Missing source]"), "bounded lookups explicitly report missing reward rules");

        Briefs(rules, partial, missing);
        foreach (var api in new[] { "responses", "chat_completions" })
        {
            await Gameplay(api, false, FixtureAssembly);
            await Chat(api);
        }
        await Gameplay("responses", true, FixtureAssembly);
        await Gameplay("responses", false, "missing.dll");
        var installed = Environment.GetEnvironmentVariable("STS2_GAME_ASSEMBLY");
        if (!string.IsNullOrWhiteSpace(installed)) await InstalledGame(installed);
        Console.WriteLine("PASS event branches, recursive reward rules, cache versioning, missing knowledge, both model transports, Jev and rollover");
    }

    static void Briefs(JsonNode rules, JsonNode partial, JsonNode missing)
    {
        var state = State();
        var original = state.WriteString();
        state["seed"] = "HIDDEN_SEED";
        state["event"]!["next_reward"] = "HIDDEN_REWARD";
        var memory = new BriefMemory();
        string Brief(BriefMemory m, JsonNode r) => GameState.Brief("test", "win", "", GameState.Public(state)!, GameState.Actions(state), m, r);
        var brief = Brief(memory, rules);
        foreach (var text in new[] { "id=KNOWLEDGE_FLOWER", "key=KNOWLEDGE_FLOWER.REACH_DEEPER_1", "KnowledgeCore", "KnowledgeDraw", "HP 40/70", "Gold 100", "Visible boss", "[Path 0]", "RestSite", "Deal 6 damage." })
            Check(brief.Contains(text), "event decision needs " + text);
        Check(!brief.Contains("HIDDEN_SEED") && !brief.Contains("HIDDEN_REWARD"), "static rules never expose hidden live state");
        Check(!Brief(memory, rules).Contains("[Event rules]"), "unchanged source is retained rather than repeated in one history");
        Check(Brief(new BriefMemory(), rules).Contains("KnowledgeCore"), "fresh/rolled-over histories receive event rules again");
        Check(Brief(new BriefMemory(), partial).Contains("status=partial") && Brief(new BriefMemory(), missing).Contains("status=unavailable"), "coverage limitations reach the model");
        var jev = GameState.JevBrief(GameState.Public(state)!, "win", "", new JsonArray(), "", eventRules: rules);
        Check(jev.Contains("KnowledgeCore") && jev.Contains("KnowledgeDraw") && jev.Split("[Path 0]").Length == 2, "Jev receives full rules and nonduplicated route context");
        Check(JevContext.Question(state)!.Contains("sunk cost") && JevContext.Question(state)!.Contains("CURRENT visible stage"), "event question evaluates remaining costs");
        state.AsObject().Remove("seed"); state["event"]!.AsObject().Remove("next_reward");
        Check(state.WriteString() == original, "building event context never changes the live fingerprint");
    }

    static async Task<JsonNode> Wait(BotRuntime runtime)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await runtime.Dispatch("GET", "/status", null);
            if (!status.Flag("thread_alive") && !status.Flag("chat_busy")) return status;
            await Task.Delay(20);
        }
        throw new Exception("Event scenario did not settle.");
    }

    static async Task Gameplay(string api, bool jev, string assembly, string id = "KNOWLEDGE_FLOWER", string reward = "KnowledgeCore")
    {
        using var handler = new SessionHandler(api) { State = State(id: id) };
        using var runtime = new BotRuntime(Temp(), assembly, handler.Game, handler);
        handler.Runtime = runtime;
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["api_type"] = api, ["api_endpoint"] = "https://buddy.test/v1", ["use_jev_strategy"] = jev, ["jev_endpoint"] = "https://jev.test/evaluate" });
        bool lookedUp = false, rolledOver = false;
        handler.AfterAction = command =>
        {
            Check(command.Text("action") == "choose_event_option" && command.Num("index") == 1, "only the fresh advertised deeper option executes");
            return handler.Commands < 3 ? State(handler.Commands, id) : new JsonObject { ["state_type"] = "game_over" };
        };
        handler.Respond = (body, _) =>
        {
            Check((body["questions"] != null) == jev, "event uses configured provider");
            var history = jev ? new JsonArray() : handler.History(body);
            var content = jev ? body.Text("state") : string.Join("\n", history.Items().Select(n => n.Text("content")));
            Check(content.Contains(assembly == "missing.dll" ? "status=unavailable" : reward), "event knowledge is present before EVERY decision, including after rollover");
            Check(content.Contains("id=" + id) && content.Contains("REACH_DEEPER_" + (handler.Commands + 1)), "current stage remains visible");
            if (!jev && handler.Commands == 1 && !rolledOver)
            {
                rolledOver = true;
                throw new ContextLimitException();
            }
            if (jev) return Task.FromResult<JsonNode>(new JsonObject { ["answers"] = new JsonObject { ["action0"] = new JsonObject { ["type"] = "choice", ["choice"] = "a1" } } });
            Check(body["tools"].Items().Any(t => (api == "responses" ? t : t["function"]).Text("name") == "lookup_event"), "event lookup tool is advertised");
            if (!lookedUp)
            {
                lookedUp = true;
                return Task.FromResult(handler.Call("lookup_event", new JsonObject { ["event_id"] = "" }));
            }
            if (handler.Commands == 0)
                Check(history.Items().Any(n => n.Text("output", n.Text("content")).Contains(assembly == "missing.dll" ? "unavailable" : "decompiled_source")), "gameplay tool call dispatches a lookup");
            return Task.FromResult(handler.Call("take_action", new JsonObject
            {
                ["snapshot_id"] = GameState.Fingerprint(handler.State), ["action_ids"] = new JsonArray("a1"),
                ["message"] = "Continue deeper.", ["rationale"] = "Remaining costs and reward checked.", ["plan"] = "Reassess at the next stage."
            }));
        };
        runtime.StartGameplay("Continue this run.", "run");
        var final = await Wait(runtime);
        Check(final.Text("status") == "idle" && handler.Commands == 3, "event sequence failed: " + final["agents"].Text("last_report"));
    }

    static async Task Chat(string api)
    {
        using var handler = new SessionHandler(api) { State = State() };
        using var runtime = new BotRuntime(Temp(), FixtureAssembly, handler.Game, handler);
        handler.Runtime = runtime;
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["api_type"] = api, ["api_endpoint"] = "https://buddy.test/v1" });
        handler.Respond = (body, _) =>
        {
            var history = handler.History(body);
            if (!handler.HasResult(history)) return Task.FromResult(handler.Call("lookup_event", new JsonObject { ["event_id"] = "Knowledge Flower" }));
            Check(history.Items().Any(n => n.Text("output", n.Text("content")).Contains("KnowledgeDraw")), "chat can inspect reward effects");
            return Task.FromResult(handler.Answer("The full path has later costs and a draw relic."));
        };
        await runtime.Dispatch("POST", "/message", new JsonObject { ["message"] = "Explain this event." });
        var final = await Wait(runtime);
        Check(handler.Requests.Count == 2 && handler.Commands == 0 && !final["messages"].Items().Any(n => n.Text("event") == "chat_error"), "chat event lookup is read-only");
    }

    static async Task InstalledGame(string assembly)
    {
        var rules = await new EventKnowledge(assembly, Temp()).Lookup("COLOSSAL_FLOWER", CancellationToken.None);
        Check(rules.Text("status") == "ok", "installed Colossal Flower rules are complete within lookup scope: " + rules);
        var source = rules["matches"].Items().Single().Text("decompiled_source");
        foreach (var pattern in new[] { @"5,\s*6,\s*7", @"35,\s*75,\s*135", @"CurrentHp >= 19", "Obtain<PollinousCore>", "NumberOfDigs" })
            Check(Regex.IsMatch(source, pattern), "installed event exposes cost/exit/condition/stage rule: " + pattern);
        var relic = rules["related_models"].Items().Single(n => n.Text("name") == "PollinousCore").Text("decompiled_source");
        Check(Regex.IsMatch(relic, @"CardsVar\s*\(2\)") && relic.Contains("\"Turns\", 4m") && relic.Contains("ModifyHandDraw"), "final reward's actual effect accompanies its name");
        var dolls = await new EventKnowledge(assembly, Temp()).Lookup("DOLL_ROOM", CancellationToken.None);
        Check(new[] { "BingBong", "DaughterOfTheWind", "MrStruggles" }.All(name => dolls["related_models"].Items().Any(n => n.Text("name") == name)), "a random/choice event supplies all named possible relics");
        await Gameplay("responses", false, assembly, "COLOSSAL_FLOWER", "PollinousCore");
        await Gameplay("responses", true, assembly, "COLOSSAL_FLOWER", "PollinousCore");
        Console.WriteLine("PASS installed Colossal Flower costs, exits, eligibility, Pollinous Core effect, Doll Room reward pool and provider context");
    }
}
