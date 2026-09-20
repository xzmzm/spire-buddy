using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class JevChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static async Task Run()
    {
        Brief();
        await Settings();
        await Routing();
        await SolverPriority();
        await FreshnessAndStop();
        await Guidance();
        await NoOpFeedback();
        await Protocol();
        Console.WriteLine("PASS Jev state, credentials, routing, solver priority, freshness, guidance, cancellation and API failures");
    }

    static void Brief()
    {
        var state = JsonNode.Parse("""
            {"state_type":"card_select","seed":"SECRET","rng_state":"SECRET",
             "card_select":{"screen_type":"upgrade","prompt":"Choose an upgrade","min_select":1,"max_select":1,
               "cards":[{"index":0,"name":"Strike","description":"Deal 6 damage.","upgrade_description":"Deal 9 damage.","upgrade_cost":0}]},
             "run":{"act":2,"floor":20,"ascension":10},
             "player":{"character":"Regent","hp":30,"max_hp":70,"energy":2,"max_energy":3,"stars":4,"gold":150,
               "hand":[{"index":0,"name":"Strike","description":"Deal 6 damage.","cost":1,"star_cost":2}],
               "deck":[],"draw_pile":[{"name":"Generated","description":"Gain 12 Block."}],"discard_pile":[],
               "max_potion_slots":3,"potions":[{"slot":1,"name":"Fire Potion","description":"Deal 20 damage."}],
               "relics":[{"name":"Happy Flower","counter":2,"description":"Every 3 turns gain energy."}],
               "orbs":[{"name":"Lightning","description":"Deal 3 passive damage.","passive_val":3,"evoke_val":8}],
               "pets":[{"name":"Osty","hp":9,"max_hp":10,"status":[{"name":"Strength","amount":2,"description":"Attacks deal 2 more damage."}]}]},
             "battle":{"round":3,"turn":"player","enemies":[{"entity_id":"e1","name":"Frog","hp":20,"max_hp":40,
               "status":[{"name":"Armor","amount":1,"description":"Reduce attack damage by 1."}],
               "intents":[{"type":"Attack","label":"8x2"}],"rolled_move":"SECRET"}]},
             "map":{"current_position":{"col":1,"row":2},"bosses":[{"name":"Visible Boss"}],
               "next_options":[{"index":0,"col":1,"row":3,"type":"Rest"},{"index":1,"col":2,"row":3,"type":"Elite"}],
               "nodes":[{"col":1,"row":1,"type":"Past","children":[]},{"col":1,"row":3,"type":"Rest","children":[[2,4]]},
                 {"col":2,"row":3,"type":"Elite","children":[[2,4]]},{"col":2,"row":4,"type":"Shop","children":[]},
                 {"col":6,"row":5,"type":"Unreachable","children":[]}]}}
            """)!;
        var deck = state["player"]!["deck"]!.AsArray();
        for (int i = 0; i < 12; i++) deck.Add(JsonNode.Parse("""{"name":"Defend","description":"Gain 5 Block.","cost":1,"keywords":[{"name":"Block","description":"Prevents damage."}]}"""));
        var recent = new JsonArray(new JsonObject { ["summary"] = "take Potion" });
        var brief = GameState.JevBrief(state, "Preserve HP. Avoid elites.", "Seek a rest site.", recent, "Do not repeat a0.");
        foreach (var text in new[] { "12x Defend", "Gain 5 Block.", "Gain 12 Block.", "HP 30/70", "Gold 150", "Stars 4", "intent: Attack 8x2", "Reduce attack damage by 1.", "Deal 3 passive damage.", "Osty", "Attacks deal 2 more damage.", "Deal 9 damage.", "free slots: 0, 2", "Happy Flower (2)", "[Route] 2,4 Shop", "Visible Boss", "Avoid elites.", "Do not repeat a0.", "[Recent] take Potion" })
            Check(brief.Contains(text), "Jev brief includes " + text);
        Check(brief.Split("Gain 5 Block.").Length == 2 && brief.Split("Prevents damage.").Length == 2, "duplicate deck and keyword rules are sent once");
        Check(brief.Contains("1 energy + 2 stars") && brief.Contains("[Upgrade 0] energy=0"), "both current resources and upgraded costs remain explicit");
        Check(!brief.Contains("SECRET") && !brief.Contains("Unreachable") && !brief.Contains("Past") && !brief.Contains("[Actions]"), "Jev excludes hidden data, obsolete routes and duplicate choices");
        Check(brief.Length < state.WriteString().Length, "Jev brief is smaller than repeated full JSON");
        Check(brief == GameState.JevBrief(state, "Preserve HP. Avoid elites.", "Seek a rest site.", recent, "Do not repeat a0."), "each stateless decision gets all definitions again");
        Check(state.Text("seed") == "SECRET", "brief does not mutate source state");
    }

    static async Task Settings()
    {
        using var handler = new Handler();
        using var runtime = await Runtime(handler);
        var config = (await runtime.Dispatch("GET", "/status", null))["config"]!;
        Check(!config.Flag("use_jev_strategy") && !config.Flag("use_jev_combat"), "Jev defaults off");
        Check(config.Text("jev_endpoint") == JevClient.DefaultEndpoint && config.Text("jev_model") == JevClient.DefaultModel, "Jev API defaults");
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["use_jev_strategy"] = true, ["jev_endpoint"] = " https://jev.test/custom/evaluate ", ["jev_model"] = " custom-jev ", ["jev_api_key"] = "jev-secret" });
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["api_key"] = BotRuntime.MaskedKey, ["jev_api_key"] = BotRuntime.MaskedKey });
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["jev_api_key"] = "" });
        config = (await runtime.Dispatch("GET", "/status", null))["config"]!;
        Check(config.Flag("has_jev_api_key") && !config.WriteString().Contains("secret"), "keys never appear in status");
        using (var restarted = new BotRuntime(handler.Directory, "missing.dll", handler.Game.Game, new Handler()))
        {
            var saved = (await restarted.Dispatch("GET", "/status", null))["config"]!;
            Check(saved.Flag("use_jev_strategy") && saved.Flag("has_jev_api_key") && saved.Text("jev_model") == "custom-jev", "Jev settings survive restart");
        }
        await runtime.Dispatch("POST", "/settings/test-jev", new JsonObject { ["jev_endpoint"] = "https://draft.test/evaluate", ["jev_model"] = "draft-model", ["jev_api_key"] = BotRuntime.MaskedKey });
        var request = handler.Requests.Last();
        Check(request.Uri == "https://draft.test/evaluate" && request.Key == "jev-secret" && request.Body.Text("model") == "draft-model", "Jev test uses unsaved endpoint/model and stored Jev key");
        await runtime.Dispatch("POST", "/settings/test", new JsonObject { ["jev_endpoint"] = "invalid-draft" });
        Check(handler.Requests.Last().Key == "buddy-secret" && handler.Requests.Last().Uri == "https://buddy.test/v1/responses", "Buddy retains its own credentials and protocol");
        Check((await runtime.Dispatch("GET", "/status", null))["config"].Text("jev_model") == "custom-jev", "connection tests do not save drafts");
        await Throws(() => runtime.Dispatch("PUT", "/settings", new JsonObject { ["jev_endpoint"] = "file:///tmp/test" }), "invalid enabled Jev endpoint");
        await Throws(() => runtime.Dispatch("PUT", "/settings", new JsonObject { ["jev_model"] = " " }), "blank enabled Jev model");
    }

    static async Task Routing()
    {
        foreach (bool strategy in new[] { false, true })
        foreach (bool combat in new[] { false, true })
        {
            using var handler = new Handler();
            using var runtime = await Runtime(handler, new JsonObject { ["use_jev_strategy"] = strategy, ["use_jev_combat"] = combat });
            // Stop after the second decision; the third action is forced proceed.
            handler.Game.AfterAction = _ => handler.Game.Commands switch { 1 => SessionHandler.Combat(), 2 => SessionHandler.Rewards(), _ => Over() };
            runtime.StartGameplay("Win this run.", "run");
            var status = await Wait(runtime);
            Check(status.Text("status") == "idle" && handler.Game.Commands == 3, "all routing combinations complete the run");
            Check(handler.Requests.Count == 2, "forced proceed does not call a provider");
            Check(handler.Requests[0].Jev == strategy && handler.Requests[1].Jev == combat, "independent strategy/combat toggles select the provider");
            foreach (var request in handler.Requests.Where(r => r.Jev))
                Check(request.Body["input"] == null && request.Body["messages"] == null && request.Body["tools"] == null && request.Body["state"] != null, "Jev replaces gameplay history/tool requests");
        }
        foreach (bool inCombat in new[] { false, true })
        {
            using var handler = new Handler();
            handler.Game.State = JsonNode.Parse("""{"state_type":"card_select","card_select":{"cards":[{"index":0,"name":"Strike"},{"index":1,"name":"Defend"}]},"player":{"hp":30}}""")!;
            handler.Game.State["in_combat"] = inCombat;
            handler.Game.AfterAction = _ => inCombat ? SessionHandler.Rewards() : Over();
            using var runtime = await Runtime(handler, new JsonObject { ["use_jev_combat"] = true });
            runtime.StartGameplay("Choose well.", inCombat ? "fight" : "run");
            var status = await Wait(runtime);
            Check(status.Text("status") == "idle" && handler.Requests.Single().Jev == inCombat, "combat ownership includes modal choices and respects fight scope");
            Check(handler.Game.Commands == 1, "fight-only stops before rewards");
        }
    }

    static async Task SolverPriority()
    {
        foreach (bool available in new[] { false, true })
        foreach (bool refused in new[] { false, true })
        {
            using var handler = new Handler();
            handler.Game.State = SessionHandler.Combat();
            handler.Game.AfterAction = _ => SessionHandler.Rewards();
            handler.Game.Solver = op =>
            {
                if (op == "enable") handler.Game.State = SessionHandler.Rewards();
                return new JsonObject { ["available"] = available, ["enabled"] = true, ["full_auto"] = false, ["solver_disabled"] = refused };
            };
            using var runtime = await Runtime(handler, new JsonObject { ["use_combat_solver"] = true, ["use_jev_combat"] = true });
            runtime.StartGameplay("Win this fight.", "fight");
            var status = await Wait(runtime);
            Check(status.Text("status") == "idle", "solver/Jev fight succeeds");
            Check(handler.Requests.Count == (available && !refused ? 0 : 1), "solver takes precedence; Jev handles missing/refused solver");
            Check(handler.Requests.All(r => r.Jev), "fallback does not call the old combat model");
        }
    }

    static async Task FreshnessAndStop()
    {
        using (var handler = new Handler())
        using (var runtime = await Runtime(handler, Enabled()))
        {
            handler.RespondJev = (body, _) =>
            {
                if (handler.Requests.Count == 1) handler.Game.State["player"]!["hp"] = 29;
                return Task.FromResult(Handler.Choice(body));
            };
            runtime.StartGameplay("Win.", "run");
            var status = await Wait(runtime);
            Check(status.Text("status") == "idle" && handler.Requests.Count == 2 && handler.Game.Commands == 1, "stale Jev choice is withheld and re-evaluated");
            Check(handler.Requests[1].Body.Text("state").Contains("HP 29/"), "re-evaluation sees new state");
        }
        using (var handler = new Handler())
        using (var runtime = await Runtime(handler, Enabled()))
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.RespondJev = async (body, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Handler.Choice(body); };
            runtime.StartGameplay("Win.", "run");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.Dispatch("POST", "/stop", null);
            var status = await Wait(runtime);
            Check(status.Text("status") == "idle" && handler.Game.Commands == 0, "stop cancels in-flight Jev without executing an action");
        }
    }

    static async Task Guidance()
    {
        using var handler = new Handler();
        using var runtime = await Runtime(handler, Enabled());
        handler.RespondJev = async (body, _) =>
        {
            if (handler.Requests.Count(r => r.Jev) == 1)
            {
                handler.RespondBuddy = request => handler.Game.HasResult(handler.Game.History(request))
                    ? handler.Game.Answer("Updated.")
                    : handler.Game.Call("message_agent", new JsonObject { ["agent"] = "game", ["message"] = "Avoid elites now.", ["scope"] = "keep" });
                await runtime.Dispatch("POST", "/message", new JsonObject { ["message"] = "Avoid elites." });
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while ((await runtime.Dispatch("GET", "/status", null)).Flag("chat_busy") && DateTime.UtcNow < deadline) await Task.Delay(20);
            }
            return Handler.Choice(body);
        };
        runtime.StartGameplay("Win.", "run");
        var status = await Wait(runtime);
        var jev = handler.Requests.Where(r => r.Jev).ToList();
        Check(status.Text("status") == "idle" && jev.Count == 2 && handler.Game.Commands == 1, "new instructions invalidate pending Jev decisions");
        Check(jev[1].Body.Text("state").Contains("Avoid elites now."), "updated strategy is retained in stateless Jev context");
    }

    static async Task NoOpFeedback()
    {
        var previous = BotRuntime.MutationIdleWindow;
        BotRuntime.MutationIdleWindow = TimeSpan.FromMilliseconds(800);
        try
        {
            using var handler = new Handler();
            using var runtime = await Runtime(handler, Enabled());
            handler.Game.AfterAction = _ => handler.Game.Commands == 1 ? handler.Game.State.DeepClone() : Over();
            handler.RespondJev = (body, _) => Task.FromResult(Handler.Choice(body, handler.Requests.Count == 1 ? "a0" : "a1"));
            runtime.StartGameplay("Win.", "run");
            var status = await Wait(runtime);
            Check(status.Text("status") == "idle" && handler.Game.Commands == 2, "Jev can choose a different action after a confirmed no-op");
            Check(handler.Requests[1].Body.Text("state").Contains("[Feedback] Action [\"a0\"]"), "Jev sees which prior action failed");
        }
        finally { BotRuntime.MutationIdleWindow = previous; }
    }

    static async Task Protocol()
    {
        foreach (var code in new[] { 401, 422, 429, 529 })
        {
            using var handler = new Handler { StatusCode = code };
            using var runtime = await Runtime(handler, Enabled());
            runtime.StartGameplay("Win.", "run");
            var status = await Wait(runtime);
            Check(status.Text("status") == "error" && handler.Game.Commands == 0, "Jev HTTP failures never mutate");
            Check(handler.Requests.Count == (code is 429 or 529 ? 3 : 1), "only documented transient statuses retry, with a bounded count");
            Check(!status.WriteString().Contains("server-secret"), "server error bodies are not exposed");
        }
        foreach (var answer in new[] { "invented", "wrong-type", "missing" })
        {
            using var handler = new Handler();
            using var runtime = await Runtime(handler, Enabled());
            handler.RespondJev = (body, _) =>
            {
                var response = Handler.Choice(body, "invented");
                if (answer == "wrong-type") response["answers"]!["action0"]!["type"] = "score";
                if (answer == "missing") response["answers"] = new JsonObject();
                return Task.FromResult(response);
            };
            runtime.StartGameplay("Win.", "run");
            var status = await Wait(runtime);
            Check(status.Text("status") == "error" && handler.Game.Commands == 0, "malformed or invented Jev choices stop without mutation");
        }
        using (var handler = new Handler())
        using (var http = new HttpClient(handler))
        {
            var client = new JevClient(http);
            var cfg = new JsonObject { ["jev_endpoint"] = JevClient.DefaultEndpoint, ["jev_model"] = "test", ["max_context_tokens"] = 250000 };
            var choices = new JsonArray(Enumerable.Range(0, 256).Select(i => (JsonNode)new JsonObject { ["id"] = "a" + i, ["summary"] = "Choice " + i }).ToArray());
            await client.Decide(cfg, "State.", choices, false, CancellationToken.None);
            var groups = handler.Requests[0].Body["questions"]!.AsObject().Select(q => q.Value!["criteria"]!.AsObject()).ToList();
            Check(groups.Sum(g => g.Count) == 256 && groups.All(g => g.Count <= 255) && handler.Requests.Count == 2, "large screens retain every legal option within the 255-choice API limit");
            cfg["max_context_tokens"] = 1;
            await Throws(() => client.Decide(cfg, "State.", choices, false, CancellationToken.None), "oversized Jev context");
            Check(handler.Requests.Count == 2, "oversized requests fail locally, without dropping state");
        }
    }

    static JsonNode Over() => new JsonObject { ["state_type"] = "game_over" };
    static JsonObject Enabled() => new() { ["use_jev_strategy"] = true, ["use_jev_combat"] = true };
    static async Task Throws(Func<Task> action, string label)
    {
        try { await action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected rejection: " + label);
    }
    static async Task<BotRuntime> Runtime(Handler handler, JsonObject? settings = null)
    {
        var runtime = new BotRuntime(handler.Directory, "missing.dll", handler.Game.Game, handler);
        handler.Game.Runtime = runtime;
        var cfg = settings ?? new JsonObject();
        cfg["api_endpoint"] = "https://buddy.test/v1"; cfg["api_key"] = "buddy-secret";
        await runtime.Dispatch("PUT", "/settings", cfg);
        return runtime;
    }
    static async Task<JsonNode> Wait(BotRuntime runtime)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonNode status;
        do { await Task.Delay(20); status = await runtime.Dispatch("GET", "/status", null); }
        while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
        Check(!status.Flag("thread_alive"), "Jev test run timed out");
        return status;
    }

    sealed class Handler : HttpMessageHandler
    {
        internal string Directory = Path.Combine(Path.GetTempPath(), "spire-buddy-jev-" + Guid.NewGuid().ToString("N"));
        internal SessionHandler Game = new("responses");
        internal List<(string Uri, string Key, JsonNode Body, bool Jev)> Requests = [];
        internal Func<JsonNode, CancellationToken, Task<JsonNode>>? RespondJev;
        internal Func<JsonNode, JsonNode>? RespondBuddy;
        internal int StatusCode = 200;

        internal static JsonNode Choice(JsonNode body, string? id = null)
        {
            var answers = new JsonObject();
            foreach (var (key, question) in body["questions"]!.AsObject())
                answers[key] = new JsonObject { ["type"] = "choice", ["choice"] = id ?? question!["criteria"]!.AsObject().First().Key, ["confidence"] = 0.8 };
            return new JsonObject { ["model"] = body.Text("model"), ["answers"] = answers, ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 4 } };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var isJev = body["questions"] != null;
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.Parameter ?? "", body, isJev));
            var response = new HttpResponseMessage((HttpStatusCode)StatusCode);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            if (StatusCode != 200) response.Content = new StringContent("server-secret");
            else
            {
                var result = isJev ? RespondJev == null ? Choice(body) : await RespondJev(body, ct)
                    : RespondBuddy?.Invoke(body) ?? (Game.IsBuddy(body) || body["tools"] == null ? Game.Answer("OK") : Game.Action(body));
                response.Content = new StringContent(result.WriteString());
            }
            return response;
        }
    }
}
