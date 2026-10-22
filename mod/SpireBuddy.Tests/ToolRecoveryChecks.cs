using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class ToolRecoveryChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    static JsonNode Shop(bool blocked = false) => new JsonObject
    {
        ["state_type"] = "shop",
        ["shop"] = new JsonObject
        {
            ["inventory_open"] = true, ["input_blocked"] = blocked, ["can_close"] = false, ["can_proceed"] = false,
            ["items"] = JsonNode.Parse("""[{"index":10,"category":"potion","potion_name":"Flex Potion","price":49,"is_stocked":true,"can_afford":true}]""")
        },
        ["player"] = JsonNode.Parse("""{"hp":67,"max_hp":75,"gold":119,"max_potion_slots":2,"potions":[{"slot":0,"name":"Swift Potion"},{"slot":1,"name":"Power Potion"}]}""")
    };

    static JsonNode Removal() => new JsonObject
    {
        ["state_type"] = "card_select", ["player"] = Shop()["player"]!.DeepClone(),
        ["card_select"] = JsonNode.Parse("""
            {"screen_type":"select","prompt":"Select 1 card to remove.","preview_showing":false,
            "can_confirm":false,"can_cancel":false,"cards":[
            {"index":0,"id":"STRIKE_REGENT","name":"Strike","description":"Deal 6 damage."},
            {"index":1,"id":"DEFEND_REGENT","name":"Defend","description":"Gain 5 Block."}]}
            """)
    };

    static JsonNode End() => new JsonObject { ["state_type"] = "game_over" };

    static async Task<BotRuntime> Runtime(SessionHandler handler, IGameAdapter? game = null)
    {
        var runtime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-tool-recovery-" + Guid.NewGuid().ToString("N")),
            "missing.dll", game ?? handler.Game, handler);
        handler.Runtime = runtime;
        await runtime.Dispatch("PUT", "/settings", new JsonObject
            { ["api_endpoint"] = "http://model/v1", ["model"] = "test", ["api_type"] = handler.Api });
        return runtime;
    }

    static async Task<JsonNode> Wait(BotRuntime runtime)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await runtime.Dispatch("GET", "/status", null);
            if (!status.Flag("thread_alive") && !status.Flag("chat_busy")) return status;
            await Task.Delay(20);
        }
        throw new Exception("Tool recovery did not settle.");
    }

    internal static async Task Run()
    {
        foreach (var kind in new[] { "shop", "fake_merchant" })
        {
            var state = Shop(blocked: true);
            if (kind == "fake_merchant")
            {
                var inventory = state["shop"]!.DeepClone();
                state.AsObject().Remove("shop");
                state["state_type"] = kind;
                state[kind] = new JsonObject { ["shop"] = inventory };
            }
            Check(GameState.Actions(state).Count == 0 && !GameState.Ready(state),
                "blocked " + kind + " must not advertise purchases or potion discards");
            state["legal_actions"] = GameState.Actions(Shop());
            Check(GameState.Actions(state).Count == 0, "blocked input overrides supplied cached actions");
        }
        foreach (var api in new[] { "responses", "chat_completions" })
        {
            await Parasol(api);
            await MissingCall(api, empty: false);
            await MissingCall(api, empty: true);
            await RepeatedMissingCalls(api);
            await RefreshScreen(api, inspect: true);
            await RefreshScreen(api, inspect: false);
            await FinishedDuringReply(api);
            await StopDuringRetry(api);
            await ConversationalChat(api);
            Console.WriteLine($"PASS {api} Parasol removal, live inspections, bounded tool recovery, stop and chat");
        }
    }

    static async Task Parasol(string api)
    {
        // Full potion slots leave affordable potions stocked while the relic
        // collects everything else. Several identical frames used to settle
        // as a shop decision, hiding the free removal that opened afterward.
        JsonNode state = Shop(blocked: true);
        int reads = 0;
        var commands = new List<string>();
        using var handler = new SessionHandler(api);
        handler.Respond = (body, _) =>
        {
            Check(state.Text("state_type") == "card_select" && reads >= 6, "only ask the model after the removal appears");
            Check(body.Text("tool_choice") == "required", "gameplay requires tool calls");
            return Task.FromResult(handler.Action(body));
        };
        var game = new ScheduledGameAdapter(a => a(), () =>
        {
            if (++reads == 6) state = Removal();
            return state.DeepClone();
        }, command =>
        {
            var action = command.Text("action");
            commands.Add(action);
            if (commands.Count == 1)
            {
                Check(action == "select_card" && command.Num("index") == 0, "choose the advertised Strike for removal");
                state["card_select"]!["preview_showing"] = true;
                state["card_select"]!["can_confirm"] = true;
            }
            else
            {
                Check(action == "confirm_selection", "confirm the pending removal exactly once");
                state = End();
            }
            return new JsonObject { ["status"] = "ok" };
        }, (_, _, _, _, _, _) => new JsonObject());
        using var runtime = await Runtime(handler, game);
        runtime.StartGameplay("Continue this run.", "run");
        var status = await Wait(runtime);
        Check(status.Text("status") == "idle" && handler.Requests.Count == 1, "Parasol waits locally then needs one decision");
        Check(commands.SequenceEqual(new[] { "select_card", "confirm_selection" }), "removal completes without shop purchases or discarded potions");
    }

    static async Task MissingCall(string api, bool empty)
    {
        using var handler = new SessionHandler(api) { State = Removal() };
        int calls = 0;
        handler.Respond = (body, _) =>
        {
            Check(body.Text("tool_choice") == "required", "require tools on initial and recovery requests");
            if (++calls == 1) return Task.FromResult(handler.Answer(empty ? "" : "I will remove a Strike."));
            var history = handler.History(body);
            Check(history.Any(n => n.Text("content").Contains("no game action was executed")), "retry explains the missing tool call");
            Check(!handler.HasResult(history), "text replies do not fabricate tool results");
            Check(handler.Commands == 0, "no action is inferred from prose");
            return Task.FromResult(handler.Action(body));
        };
        handler.AfterAction = command => { Check(command.Text("action") == "select_card", "retry selects the visible card"); return End(); };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Continue.", "run");
        var status = await Wait(runtime);
        Check(status.Text("status") == "idle" && calls == 2 && handler.Commands == 1, "text/empty reply recovers with one mutation");
        Check(handler.Requests[0].Text("prompt_cache_key") == handler.Requests[1].Text("prompt_cache_key"), "recovery retains session history");
    }

    static async Task RepeatedMissingCalls(string api)
    {
        using var handler = new SessionHandler(api) { State = Removal() };
        handler.Respond = (_, _) => Task.FromResult(handler.Answer("No controls are visible."));
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Continue.", "run");
        var status = await Wait(runtime);
        Check(status.Text("status") == "error" && handler.Requests.Count == 3 && handler.Commands == 0,
            "providers that ignore required tools stop after three replies without a mutation");
        Check(status["messages"].Items().Any(n => n.Text("message").Contains("No controls are visible.")),
            "final error retains the model's explanation");
    }

    static async Task RefreshScreen(string api, bool inspect)
    {
        using var handler = new SessionHandler(api) { State = Shop() };
        int calls = 0;
        handler.Respond = (body, _) =>
        {
            if (++calls == 1)
            {
                handler.State = Removal();
                return Task.FromResult(inspect ? handler.Call("list_legal_actions", new JsonObject())
                    : handler.Answer("The shop has no close button."));
            }
            var history = handler.History(body);
            Check(history.Any(n => n.Text("role") == "user" && n.Text("content").StartsWith("snapshot=" + GameState.Fingerprint(handler.State))),
                "the next decision receives the live removal snapshot");
            if (inspect)
            {
                var result = history.Last(n => n.Text("type") == "function_call_output" || n.Text("role") == "tool")!;
                var output = JsonNode.Parse(result.Text(api == "responses" ? "output" : "content"))!;
                Check(output.Flag("refreshed") && output["state"].Text("state_type") == "card_select",
                    "inspection refreshes even though the old shop had affordable purchases");
                Check(output["actions"].Items().Any(n => n["command"].Text("action") == "select_card"), "inspection returns removal choices");
            }
            return Task.FromResult(handler.Action(body));
        };
        handler.AfterAction = command => { Check(command.Text("action") == "select_card", "never buy a potion from the stale shop"); return End(); };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Continue.", "run");
        var status = await Wait(runtime);
        Check(status.Text("status") == "idle" && calls == 2 && handler.Commands == 1, "changed screen re-decides and acts once");
    }

    static async Task FinishedDuringReply(string api)
    {
        using var handler = new SessionHandler(api) { State = Removal() };
        handler.Respond = (_, _) => { handler.State = End(); return Task.FromResult(handler.Answer("Finished.")); };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Continue.", "run");
        var status = await Wait(runtime);
        Check(status.Text("status") == "idle" && handler.Requests.Count == 1 && handler.Commands == 0,
            "a finished run does not demand another tool call");
    }

    static async Task StopDuringRetry(string api)
    {
        using var handler = new SessionHandler(api) { State = Removal() };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        handler.Respond = async (_, ct) =>
        {
            if (++calls == 1) return handler.Answer("Thinking about the removal.");
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new Exception("Retry ignored cancellation.");
        };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Continue.", "run");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.Dispatch("POST", "/stop", null);
        var status = await Wait(runtime);
        Check(status.Text("status") == "idle" && calls == 2 && handler.Commands == 0, "stop cancels recovery before any action");
    }

    static async Task ConversationalChat(string api)
    {
        using var handler = new SessionHandler(api) { State = Removal() };
        handler.Respond = (body, _) =>
        {
            Check(body["tool_choice"] == null, "ordinary chat and connection tests may reply without tools");
            return Task.FromResult(handler.Answer("Pick a card to remove."));
        };
        using var runtime = await Runtime(handler);
        await runtime.Dispatch("POST", "/message", new JsonObject { ["message"] = "What is this screen?" });
        var status = await Wait(runtime);
        Check(status["messages"].Items().Any(n => n.Text("event") == "chat_reply" && n.Text("message") == "Pick a card to remove."),
            "ordinary chat accepts a text answer");
        await runtime.Dispatch("POST", "/settings/test", null);
        Check(handler.Requests.Count == 2 && handler.Commands == 0, "chat and connection test remain read-only");
    }
}
