using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class DialogueChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    static JsonNode Dialogue(int line)
    {
        // The event description, options, player and enabled hitbox are identical
        // between intermediate lines. Only the layout's current line changes.
        var state = JsonNode.Parse("""
            {"state_type":"event","run":{"act":3,"floor":34},
            "player":{"hp":63,"max_hp":75,"gold":98,"potions":[{"slot":0,"name":"Potion"}]},
            "event":{"event_id":"TANX","is_ancient":true,"body":"Ancient event",
            "options":[{"index":0,"title":"First gift"},{"index":1,"title":"Second gift"}]}}
            """)!;
        state["event"]!["in_dialogue"] = line < 3;
        state["event"]!["dialogue_line"] = line;
        return state;
    }

    static ScheduledGameAdapter Adapter(Func<JsonNode> read, Func<JsonNode, JsonNode>? execute = null) =>
        new(a => a(), read, execute ?? (_ => throw new Exception("Settling must not retry a dialogue click")),
            (_, _, _, _, _, _) => new JsonObject());

    static BotRuntime Runtime(IGameAdapter game, HttpMessageHandler? handler = null) =>
        new(Path.Combine(Path.GetTempPath(), "spire-dialogue-" + Guid.NewGuid().ToString("N")), "missing.dll", game, handler);

    internal static async Task Run()
    {
        var before = Dialogue(0);
        var after = Dialogue(1);
        var command = JsonNode.Parse("""{"action":"advance_dialogue"}""")!;
        Check(JsonNode.DeepEquals(GameState.Actions(before), GameState.Actions(after)),
            "consecutive ancient lines expose the same actions");
        using (var runtime = Runtime(Adapter(() => after.DeepClone())))
        {
            var settled = await runtime.Stable(CancellationToken.None, GameState.Fingerprint(before), command,
                idleWindow: TimeSpan.FromSeconds(1), hardCap: TimeSpan.FromSeconds(5));
            Check(GameState.Fingerprint(settled) == GameState.Fingerprint(after),
                "the next dialogue line settles even while the hitbox stays enabled");
        }

        // A click that did not advance still must not be accepted or retried.
        using (var runtime = Runtime(Adapter(() => before.DeepClone())))
        {
            bool stopped = false;
            try
            {
                await runtime.Stable(CancellationToken.None, GameState.Fingerprint(before), command,
                    idleWindow: TimeSpan.FromSeconds(1), hardCap: TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException) { stopped = true; }
            Check(stopped, "an unchanged dialogue line still stops without another click");
        }

        await ArchitectSettles();
        await AutomaticArchitect();
        foreach (var api in new[] { "responses", "chat_completions" }) await ContinueDialogue(api);
        foreach (var api in new[] { "responses", "chat_completions" }) await ModelOwnedArchitect(api);
        Console.WriteLine("PASS ancient and Architect dialogue advance every line and settle in both transports");
    }

    // The Architect regenerates an identical-looking single option for every
    // dialogue line, including the final run-winning choice: same title,
    // description, locked and proceed flags. Only the option's text key moves.
    static JsonNode Architect(int line, bool potion = false)
    {
        var state = JsonNode.Parse("""
            {"state_type":"event","run":{"act":3,"floor":49},
            "player":{"hp":76,"max_hp":76,"gold":4},
            "event":{"event_id":"THE_ARCHITECT","event_name":"The Architect","is_ancient":false,"body":"",
            "options":[{"index":0,"title":"继续","is_locked":false,"is_proceed":false,"was_chosen":false}]}}
            """)!;
        if (potion) state["player"]!["potions"] = JsonNode.Parse("""[{"slot":0,"name":"Calamity Potion"}]""");
        state["event"]!["options"]![0]!["text_key"] = line < 2 ? $"THE_ARCHITECT.dialogue.{line}" : "PROCEED";
        return state;
    }

    static async Task ArchitectSettles()
    {
        var choose = JsonNode.Parse("""{"action":"choose_event_option","index":0}""")!;
        Check(JsonNode.DeepEquals(GameState.Actions(Architect(0)), GameState.Actions(Architect(1))),
            "consecutive Architect lines expose the same actions");
        Check(GameState.Fingerprint(Architect(0)) != GameState.Fingerprint(Architect(1))
            && GameState.Fingerprint(Architect(1)) != GameState.Fingerprint(Architect(2)),
            "the option text key distinguishes identical-looking lines");
        using (var runtime = Runtime(Adapter(() => Architect(1).DeepClone())))
        {
            var settled = await runtime.Stable(CancellationToken.None, GameState.Fingerprint(Architect(0)), choose,
                idleWindow: TimeSpan.FromSeconds(1), hardCap: TimeSpan.FromSeconds(5));
            Check(GameState.Fingerprint(settled) == GameState.Fingerprint(Architect(1)),
                "the next Architect line settles through its text key");
        }
        using (var runtime = Runtime(Adapter(() => Architect(0).DeepClone())))
        {
            bool stopped = false;
            try
            {
                await runtime.Stable(CancellationToken.None, GameState.Fingerprint(Architect(0)), choose,
                    idleWindow: TimeSpan.FromSeconds(1), hardCap: TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException) { stopped = true; }
            Check(stopped, "an unchanged Architect line still stops without another click");
        }
    }

    // Without held potions the single option is automatic: every line advances
    // locally, no model request is made, and the final choice ends the run.
    static async Task AutomaticArchitect()
    {
        int line = 0, clicks = 0;
        var state = Architect(line);
        var game = Adapter(() => state.DeepClone(), command =>
        {
            Check(command.Text("action") == "choose_event_option" && command.Num("index") == 0,
                "each Architect line clicks its only option");
            clicks++;
            state = ++line < 3 ? Architect(line) : new JsonObject { ["state_type"] = "game_over" };
            return new JsonObject { ["status"] = "ok" };
        });
        using var runtime = Runtime(game);
        runtime.StartGameplay("Answer the Architect and win.", "run");
        JsonNode status;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        do { await Task.Delay(20); status = await runtime.Dispatch("GET", "/status", null); }
        while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
        Check(!status.Flag("thread_alive") && status.Text("status") == "idle",
            "automatic Architect dialogue completes: " + status.WriteString());
        Check(clicks == 3, "every Architect line advances exactly once, ending on the proceed choice");
    }

    // A held potion keeps the option model-owned: the exact losing scenario,
    // where every click must still settle before the next decision.
    static async Task ModelOwnedArchitect(string api)
    {
        int line = 0, clicks = 0;
        var state = Architect(line, potion: true);
        using var handler = new SessionHandler(api);
        handler.Respond = (body, _) => Task.FromResult(handler.Action(body));
        var game = Adapter(() => state.DeepClone(), command =>
        {
            Check(command.Text("action") == "choose_event_option" && command.Num("index") == 0,
                "the model clicks the only option at every Architect line");
            clicks++;
            state = ++line < 3 ? Architect(line, potion: true) : new JsonObject { ["state_type"] = "game_over" };
            return new JsonObject { ["status"] = "ok" };
        });
        using var runtime = Runtime(game, handler);
        await runtime.Dispatch("PUT", "/settings", new JsonObject
            { ["api_endpoint"] = "http://model/v1", ["model"] = "test", ["api_type"] = api });
        runtime.StartGameplay("Answer the Architect and win.", "run");
        JsonNode status;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        do { await Task.Delay(20); status = await runtime.Dispatch("GET", "/status", null); }
        while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
        Check(!status.Flag("thread_alive") && status.Text("status") == "idle",
            "model-owned Architect dialogue completes " + api + ": " + status.WriteString());
        Check(clicks == 3 && handler.Requests.Count == 3,
            "each of the three identical-looking lines gets its own settled decision");
    }

    static async Task ContinueDialogue(string api)
    {
        int line = 0, clicks = 0, choices = 0;
        var state = Dialogue(line);
        using var handler = new SessionHandler(api);
        handler.Respond = (body, _) =>
        {
            Check(line == 3 && clicks == 3 && handler.Requests.Count == 1,
                "only the final gift choice needs a model request");
            return Task.FromResult(handler.Action(body));
        };
        var game = Adapter(() => state.DeepClone(), command =>
        {
            if (line < 3)
            {
                Check(command.Text("action") == "advance_dialogue", "each intermediate line continues automatically despite the held potion");
                clicks++;
                state = Dialogue(++line);
            }
            else
            {
                Check(command.Text("action") == "choose_event_option" && command.Num("index") == 0,
                    "the model chooses the gift after the last line");
                choices++;
                state = new JsonObject { ["state_type"] = "game_over" };
            }
            return new JsonObject { ["status"] = "ok" };
        });
        using var runtime = Runtime(game, handler);
        await runtime.Dispatch("PUT", "/settings", new JsonObject
            { ["api_endpoint"] = "http://model/v1", ["model"] = "test", ["api_type"] = api });
        runtime.StartGameplay("Continue through the ancient and choose a gift.", "run");
        JsonNode status;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        do { await Task.Delay(20); status = await runtime.Dispatch("GET", "/status", null); }
        while (status.Flag("thread_alive") && DateTime.UtcNow < deadline);
        Check(!status.Flag("thread_alive") && status.Text("status") == "idle",
            "ancient dialogue completes " + api + ": " + status.WriteString());
        Check(clicks == 3 && choices == 1 && handler.Requests.Count == 1,
            "every line advances exactly once before the strategic choice");
    }
}
