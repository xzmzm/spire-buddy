using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SpireBuddy.Runtime;

internal static class SessionChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task Run()
    {
        foreach (var api in new[] { "responses", "chat_completions" })
        {
            await Conversation(api);
            await Delegation(api, "fight");
            await Delegation(api, "run");
            await StopBeforeStart(api);
            await StopDuringCombat(api);
            await Steering(api);
            await ScopeChange(api);
            await NoOpRecovery(api);
            await NoOpStop(api);
            await ControlRollover(api);
            await PlayerFacingVoice(api);
            await CustomPersonality(api);
            await SettingsContinuity(api);
            await RunEndBrief(api);
            await Rollover(api);
            await LocalizedPlay(api);
            await LocalizedToolRounds(api);
            Console.WriteLine($"PASS {api} persistent Buddy, delegation, stop races, steering, context rollover");
        }
        await LocalizedStop();
    }

    static async Task LocalizedStop()
    {
        using var handler = new SessionHandler("responses");
        handler.Respond = (body, _) => Task.FromResult(handler.Answer("你好。"));
        using var runtime = await Runtime(handler);
        L10n.Language = "zhs";
        try
        {
            await Say(runtime, "停止。");
            var result = await Wait(runtime);
            var replies = result["messages"].Items().Where(e => e.Text("event") == "chat_reply").Select(e => e.Text("message")).ToArray();
            Check(handler.Requests.Count == 0, "Chinese stop command bypasses the model");
            Check(replies.Any(message => message.Contains("已停止", StringComparison.Ordinal)), "stop reply follows the UI language");
            await Say(runtime, "你好"); await Wait(runtime);
            Check(handler.Requests.Count == 1, "other Chinese messages still reach the model");
        }
        finally { L10n.Language = "en"; }
        Console.WriteLine("PASS localized stop command and reply");
    }

    static async Task LocalizedPlay(string api)
    {
        // A mid-session switch must not rewrite the system prompt: the note is
        // appended so the provider's cached prefix stays valid.
        using var handler = new SessionHandler(api) { State = SessionHandler.Map() };
        handler.AfterAction = _ =>
        {
            if (handler.Commands != 1) return new JsonObject { ["state_type"] = "game_over" };
            var next = SessionHandler.Map(); next["player"]!["hp"] = 45; return next;
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        handler.Respond = async (body, _) =>
        {
            if (handler.IsBuddy(body)) return handler.Answer("On it.");
            if (++calls == 1) { entered.TrySetResult(); await release.Task; }
            return handler.Action(body);
        };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("win this run", "run");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Say(runtime, "hi"); await Wait(runtime, s => !s.Flag("chat_busy"));
        L10n.Language = "zhs";
        try
        {
            release.SetResult();
            await Wait(runtime);
            var play = handler.Requests.Where(r => !handler.IsBuddy(r)).ToArray();
            Check(play.Length == 2, "two gameplay decisions");
            var firstPrompt = handler.History(play[0])[0]!.Text("content");
            var secondPrompt = handler.History(play[1])[0]!.Text("content");
            Check(firstPrompt == secondPrompt, "system prompt unchanged across the switch");
            Check(play[0].Text("prompt_cache_key") == play[1].Text("prompt_cache_key"), "language switches preserve the gameplay session");
            Check(!firstPrompt.Contains("Chinese"), "English session carries no language directive");
            Check(handler.History(play[1]).WriteString().Contains("switched the interface language to Chinese"), "switch note appended to the live session");
            await Say(runtime, "你好"); await Wait(runtime);
            var chat = handler.Requests.Where(r => handler.IsBuddy(r)).ToArray();
            Check(handler.History(chat[0])[0]!.Text("content") == handler.History(chat[1])[0]!.Text("content"), "chat prompt unchanged across the switch");
            Check(handler.History(chat[1]).WriteString().Contains("switched the interface language to Chinese"), "chat switch note appended");
            await Say(runtime, "停止。"); await Wait(runtime);
        }
        finally { L10n.Language = "en"; }

        // A session started after the switch bakes the directive into its prompt.
        using var fresh = new SessionHandler(api) { State = SessionHandler.Map() };
        fresh.AfterAction = _ => fresh.Commands == 1 ? SessionHandler.Combat() : new JsonObject { ["state_type"] = "game_over" };
        fresh.Respond = (body, _) => Task.FromResult(fresh.Action(body));
        using var freshRuntime = await Runtime(fresh);
        L10n.Language = "zhs";
        try
        {
            freshRuntime.StartGameplay("赢下这局", "run");
            await Wait(freshRuntime);
            Check(fresh.Commands == 2 && fresh.Requests.Count == 2, "fresh game and combat sessions both make a decision");
            foreach (var role in new[] { "game", "combat" })
            {
                var request = fresh.Requests.Single(r => r.Text("prompt_cache_key").StartsWith(role + "-"));
                var prompt = fresh.History(request)[0]!.Text("content");
                Check(prompt.Contains("interface language is Chinese") && prompt.Contains("message and rationale in Chinese"), $"fresh {role} session bakes the language directive");
            }
        }
        finally { L10n.Language = "en"; }
        Console.WriteLine($"PASS {api} language switches keep session prefixes and append notes");
    }

    static async Task LocalizedToolRounds(string api)
    {
        using var handler = new SessionHandler(api) { State = SessionHandler.Map() };
        handler.AfterAction = _ => handler.Commands switch
        {
            1 => SessionHandler.Combat(),
            2 => SessionHandler.Map(),
            _ => new JsonObject { ["state_type"] = "game_over" }
        };
        int gameCalls = 0, combatCalls = 0;
        handler.Respond = (body, _) =>
        {
            bool combat = body.Text("prompt_cache_key").StartsWith("combat-");
            int calls = combat ? ++combatCalls : ++gameCalls;
            if (calls == 1)
            {
                // Switch while a request is in flight. Its tool follow-up must
                // see the note before any action or new decision has occurred.
                L10n.Language = combat ? "en" : "zhs";
                return Task.FromResult(handler.Call("inspect_game_state", new JsonObject { ["section"] = "overview" }));
            }
            return Task.FromResult(handler.Action(body));
        };
        using var runtime = await Runtime(handler);
        var previousLanguage = L10n.Language;
        L10n.Language = "en";
        try
        {
            runtime.StartGameplay("win this run", "run");
            var result = await Wait(runtime);
            Check(result.Text("status") == "idle" && handler.Commands == 3, "language switches during tools preserve game/combat action execution");
            var game = handler.Requests.Where(r => r.Text("prompt_cache_key").StartsWith("game-")).ToArray();
            var combat = handler.Requests.Where(r => r.Text("prompt_cache_key").StartsWith("combat-")).ToArray();
            Check(game.Length == 3 && combat.Length == 2, "both subagents complete an inspection before acting, then the game resumes");
            var gameSwitch = Notes(game[1]);
            Check(gameSwitch.Length == 1 && gameSwitch[0].Contains("message and rationale in Chinese"), "game tool follow-up receives the Chinese switch immediately");
            Check(handler.History(combat[0])[0]!.Text("content").Contains("interface language is Chinese"), "new combat session uses the current interface language");
            Check(Notes(combat[0]).Length == 0, "fresh combat prompt needs no old switch notes");
            var combatSwitch = Notes(combat[1]);
            Check(combatSwitch.Length == 1 && combatSwitch[0].Contains("message and rationale in English"), "combat tool follow-up receives the English switch immediately");
            var gameNotes = Notes(game[2]);
            Check(gameNotes.Length == 2 && gameNotes[0].Contains("to Chinese") && gameNotes[1].Contains("back to English"), "waiting game agent receives the combat-time switch once and in order");
            foreach (var requests in new[] { game, combat })
            {
                Check(requests.Select(r => r.Text("prompt_cache_key")).Distinct().Count() == 1, "language notes preserve each subagent's cache identity");
                for (int i = 1; i < requests.Length; i++)
                {
                    var before = handler.History(requests[i - 1]);
                    var after = handler.History(requests[i]);
                    Check(after.Take(before.Count).Select(n => n!.WriteString()).SequenceEqual(before.Select(n => n!.WriteString())), "language notes retain the full cached history prefix");
                }
            }
        }
        finally { L10n.Language = previousLanguage; }
        Console.WriteLine($"PASS {api} game and combat language changes during tool rounds and handoffs");

        string[] Notes(JsonNode request) => handler.History(request)
            .Where(n => n.Text("role") == "user" && n.Text("content").Contains("switched the interface language"))
            .Select(n => n!.Text("content")).ToArray();
    }

    static async Task<BotRuntime> Runtime(SessionHandler handler, int limit = 250000)
    {
        var directory = Path.Combine(Path.GetTempPath(), "spire-session-" + Guid.NewGuid().ToString("N"));
        var runtime = new BotRuntime(directory, "missing.dll", handler.Game, handler);
        handler.Runtime = runtime;
        await runtime.Dispatch("PUT", "/settings", new JsonObject
        {
            ["base_url"] = "http://model/v1", ["model"] = "test", ["api_type"] = handler.Api, ["max_context_tokens"] = limit
        });
        return runtime;
    }

    static Task Say(BotRuntime runtime, string text) => runtime.Dispatch("POST", "/message", new JsonObject { ["message"] = text });
    static async Task<JsonNode> Wait(BotRuntime runtime, Func<JsonNode, bool>? done = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await runtime.Dispatch("GET", "/status", null);
            if (done?.Invoke(status) ?? (!status.Flag("chat_busy") && !status.Flag("thread_alive"))) return status;
            await Task.Delay(20);
        }
        throw new Exception("Session did not settle: " + await runtime.Dispatch("GET", "/status", null));
    }

    static async Task Conversation(string api)
    {
        using var handler = new SessionHandler(api);
        handler.State = SessionHandler.Map();
        handler.State["player"]!["deck"] = new JsonArray(new JsonObject { ["name"] = "Strike", ["description"] = new string('x', 1000) });
        handler.Respond = async (body, ct) =>
        {
            await Task.Delay(40, ct);
            var history = handler.History(body);
            var question = history.Last(n => n.Text("role") == "user")!.Text("content");
            if (question == "first" && !handler.HasResult(history)) return handler.Call("check_game_odds", new JsonObject());
            return handler.Answer("answer:" + question);
        };
        using var runtime = await Runtime(handler);
        var conversation = (await runtime.Dispatch("GET", "/status", null)).Text("conversation_id");
        await Say(runtime, "first");
        await Say(runtime, "second");
        var result = await Wait(runtime);
        Check(handler.MaxBuddyCalls == 1, "Buddy serializes messages and tool calls");
        Check(handler.Requests.Count == 3 && handler.Commands == 0, "chat never starts gameplay on its own");
        var first = handler.Requests[0]; var second = handler.Requests[2];
        Check(first.Text("prompt_cache_key") == second.Text("prompt_cache_key"), "chat reuses session/cache key");
        var previous = handler.History(handler.Requests[1]); var continued = handler.History(second);
        Check(continued.Take(previous.Count).Select(n => n!.WriteString()).SequenceEqual(previous.Select(n => n!.WriteString())), "history prefix and tool protocol retained");
        Check(continued.Count(n => n.Text("role") == "user" && n.Text("content").StartsWith("Current public game state:")) == 1, "unchanged state not prefixed again");
        Check(continued.Last()!.Text("content") == "second", "user message remains plain text");
        Check(continued.WriteString().Contains("answer:first") && handler.HasResult(continued), "assistant replies and tool outputs retained");

        handler.State["player"]!["hp"] = 42;
        handler.State["player"]!.AsObject().Remove("gold");
        await Say(runtime, "third"); await Wait(runtime);
        var changed = handler.History(handler.Requests.Last());
        var delta = changed[^2]!.Text("content");
        Check(delta.StartsWith("Public state changes") && delta.Contains("/game/player/hp") && delta.Contains("/game/player/gold") && delta.Contains("remove"), "small diff reports replacement and deletion");
        Check(!delta.Contains(new string('x', 100)), "diff omits unchanged deck");
        Check((await runtime.Dispatch("GET", "/status", null)).Text("conversation_id") == conversation, "state changes preserve visible conversation");
        await Say(runtime, "stop"); await Wait(runtime);
        await Say(runtime, "fourth"); await Wait(runtime);
        Check(handler.History(handler.Requests.Last()).WriteString().Contains("answer:first"), "stop retains chat context");
        int inspections = 0;
        handler.Respond = (_, _) =>
        {
            handler.State["player"]!["hp"] = 30;
            return Task.FromResult(++inspections == 1 ? handler.Call("inspect_game_state", new JsonObject { ["section"] = "overview" }) : handler.Answer("30 HP now."));
        };
        await Say(runtime, "inspect live HP"); await Wait(runtime);
        handler.State["player"]!["hp"] = 42;
        handler.Respond = (_, _) => Task.FromResult(handler.Answer("42 HP again."));
        await Say(runtime, "and now"); await Wait(runtime);
        Check(handler.History(handler.Requests.Last())[^2]!.Text("content").Contains("/game/player/hp"), "diff baseline follows inspected state, including changes back to an earlier value");
        handler.Respond = (_, _) => throw new HttpRequestException("Test outage");
        await Say(runtime, "failed request"); await Wait(runtime);
        await Say(runtime, "stop"); await Wait(runtime);
        handler.Respond = (_, _) => Task.FromResult(handler.Answer("Recovered."));
        await Say(runtime, "after outage"); await Wait(runtime);
        Check(handler.History(handler.Requests.Last()).WriteString().Contains("answer:first"), "stop after a failed request preserves earlier chats for reconstruction");
    }

    static async Task Delegation(string api, string scope)
    {
        using var handler = new SessionHandler(api) { State = SessionHandler.Combat() };
        handler.AfterAction = _ => handler.Commands == 1 ? SessionHandler.Rewards() : new JsonObject { ["state_type"] = "game_over" };
        handler.Respond = (body, _) =>
        {
            if (handler.IsBuddy(body))
                return Task.FromResult(handler.HasResult(handler.History(body)) ? handler.Answer("On it.") :
                    handler.Call("start_game", new JsonObject { ["instructions"] = scope == "fight" ? "Win this fight" : "Win with ironclad", ["scope"] = scope }, duplicate: true));
            return Task.FromResult(handler.Action(body));
        };
        using var runtime = await Runtime(handler);
        if (scope == "run") await Say(runtime, "Win with ironclad");
        else await Say(runtime, "win this fight");
        var result = await Wait(runtime);
        Check(handler.Commands == (scope == "fight" ? 1 : 2), "fight scope must stop before claiming rewards; run scope resumes navigation");
        Check(result.Text("status") == "idle" && result["agents"]?["game"] == null && result["agents"]?["combat"] == null, "subagents terminate when their assignment ends");
        Check(handler.Requests.Count(r => !handler.IsBuddy(r)) == 1, "one combat subagent despite duplicate start calls");
        var buddyReply = handler.History(handler.Requests.First(r => handler.IsBuddy(r) && handler.HasResult(handler.History(r))));
        Check(buddyReply.WriteString().Contains("already active"), "duplicate start is explicitly rejected");
        Check(buddyReply.WriteString().Contains("do not carry over"), "a started game retires the previous game's strategy for Buddy");
        Check(handler.SawCombatOwner, "game agent waits while its single combat agent owns actions");
        if (scope == "run") Check(handler.History(handler.Requests[0]).Last()!.Text("content") == "Win with ironclad", "run request reaches Buddy as plain chat");
        else Check(result["agents"].Text("last_report").Contains("before rewards"), "fight completion report reaches Buddy");

        // The next game starts with an empty action log and no old strategy.
        handler.State = JsonNode.Parse("""{"state_type":"game_over"}""")!;
        runtime.StartGameplay("a brand-new run", "run");
        await Wait(runtime);
        bool asked = false;
        handler.Respond = (body, _) =>
        {
            if (asked) return Task.FromResult(handler.Answer("Nothing recent."));
            asked = true;
            return Task.FromResult(handler.Call("review_recent_actions", new JsonObject()));
        };
        await Say(runtime, "any recent actions?"); await Wait(runtime);
        var toolOutput = handler.History(handler.Requests.Last()).Last(n => n.Text("type") == "function_call_output" || n.Text("role") == "tool");
        Check(toolOutput.Text("output") == "[]" || toolOutput.Text("content") == "[]", "a new game's recent-action log is empty");
    }

    static async Task StopBeforeStart(string api)
    {
        using var handler = new SessionHandler(api);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Respond = async (body, _) =>
        {
            if (handler.HasResult(handler.History(body))) return handler.Answer("Stopped.");
            entered.TrySetResult(); await release.Task;
            return handler.Call("start_game", new JsonObject { ["instructions"] = "win", ["scope"] = "run" });
        };
        using var runtime = await Runtime(handler);
        await Say(runtime, "continue this run");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Say(runtime, "stop");
        release.SetResult();
        var result = await Wait(runtime);
        Check(handler.Commands == 0 && !result.Flag("thread_alive"), "late Buddy control cannot undo Stop");
        Check(handler.History(handler.Requests.Last()).WriteString().Contains("Superseded"), "late call gets explicit stop rejection");
    }

    static async Task StopDuringCombat(string api)
    {
        using var handler = new SessionHandler(api) { State = SessionHandler.Combat() };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        handler.Respond = async (body, ct) =>
        {
            if (handler.IsBuddy(body)) return handler.Answer("Still here.");
            entered.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return handler.Action(body);
        };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("win", "fight");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Say(runtime, "STOP!");
        var result = await Wait(runtime);
        Check(cancelled && handler.Commands == 0 && result.Text("status") == "idle", "stop cancels combat reasoning before mutation");
        await Say(runtime, "hello"); await Wait(runtime);
        Check(handler.History(handler.Requests.Last()).First()!.Text("role") is "developer" or "system", "stop before first chat still initializes Buddy instructions");
    }

    static async Task Steering(string api)
    {
        using var handler = new SessionHandler(api) { State = SessionHandler.Combat() };
        handler.AfterAction = _ => SessionHandler.Rewards();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int combatCalls = 0;
        handler.Respond = async (body, _) =>
        {
            if (handler.IsBuddy(body)) return handler.HasResult(handler.History(body)) ? handler.Answer("Relayed.") :
                handler.Call("message_agent", new JsonObject { ["agent"] = "game", ["message"] = "Save the potion." });
            if (++combatCalls == 1) { entered.SetResult(); await release.Task; }
            else Check(handler.History(body).WriteString().Contains("Save the potion."), "game guidance reaches active combat session");
            return handler.Action(body);
        };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("win", "fight");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Say(runtime, "save the potion");
        await Wait(runtime, s => !s.Flag("chat_busy"));
        Check(handler.Commands == 0, "steering waits for a new decision");
        release.SetResult();
        var result = await Wait(runtime);
        Check(combatCalls == 2 && handler.Commands == 1 && result.Text("status") == "idle", "old decision rejected after guidance changed");
    }

    static async Task RunEndBrief(string api)
    {
        // A run ending between chats must reach Buddy as an explicit note plus a
        // current-state anchor. The anchor is repeated in full even when a
        // trailing diff already delivered the identical snapshot, so a request
        // like starting the next character never lands on a bare message with
        // only stale run discussion behind it.
        var previousLanguage = L10n.Language;
        L10n.Language = "en";
        try
        {
            using var handler = new SessionHandler(api) { State = JsonNode.Parse("""{"state_type":"game_over"}""")! };
            handler.Respond = (body, _) => Task.FromResult(handler.IsBuddy(body) ? handler.Answer("Noted.") : handler.Action(body));
            using var runtime = await Runtime(handler);
            await Say(runtime, "hello"); await Wait(runtime);

            runtime.StartGameplay("win this run", "run");
            var ended = await Wait(runtime);
            Check(ended.Text("status") == "idle" && handler.Commands == 0, "a run found already over ends without a decision");
            await Say(runtime, "打骨妹"); await Wait(runtime);
            var first = handler.History(handler.Requests.Last());
            Check(first[^1]!.Text("content") == "打骨妹", "user message stays last");
            Check(first[^2]!.Text("content").StartsWith("Current public game state:") || first[^2]!.Text("content").StartsWith("Public state changes"), "current state accompanies the first message after a run ended");
            Check(first[^3]!.Text("content").StartsWith("Play update: Run ended.", StringComparison.Ordinal), "run end is briefed as an in-order note");

            // A second run ends with the identical public snapshot: the previous
            // turn already delivered these exact bytes, so only the play-status
            // change can force the re-anchor.
            runtime.StartGameplay("run again", "run");
            await Wait(runtime);
            await Say(runtime, "再来一局"); await Wait(runtime);
            var second = handler.History(handler.Requests.Last());
            Check(second[^1]!.Text("content") == "再来一局", "user message stays last");
            Check(second[^2]!.Text("content").StartsWith("Current public game state:") && second[^2]!.Text("content").Contains("game_over"), "unchanged state is re-anchored in full after another run ends");
            Check(second[^3]!.Text("content").StartsWith("Play update: Run ended.", StringComparison.Ordinal), "second run end is briefed before the re-anchor");
            Check(second.Count(n => n.Text("role") == "user" && n.Text("content").StartsWith("Play update: ")) == 2, "exactly one note per finished run");

            // With no play-status change, an unchanged state still sends nothing.
            await Say(runtime, "just chatting"); await Wait(runtime);
            var third = handler.History(handler.Requests.Last());
            int Anchors(JsonArray history) => history.Count(n => n.Text("role") == "user" && n.Text("content").StartsWith("Current public game state:"));
            Check(Anchors(third) == Anchors(second) && third.Count(n => n.Text("role") == "user" && n.Text("content").StartsWith("Play update: ")) == 2, "no play change keeps the no-prefix behavior");
        }
        finally { L10n.Language = previousLanguage; }
        Console.WriteLine($"PASS {api} run-end briefs and state re-anchors reach the chat session");
    }

    static async Task Rollover(string api)
    {
        using var handler = new SessionHandler(api);
        handler.Respond = (body, _) => Task.FromResult(handler.Answer("Remember this reply."));
        using var runtime = await Runtime(handler);
        await Say(runtime, "earlier chat"); await Wait(runtime);
        var firstId = handler.Requests[0].Text("prompt_cache_key");
        int rejected = 0;
        handler.Respond = (body, _) =>
        {
            if (rejected++ < 3) throw new ContextLimitException();
            return Task.FromResult(handler.Answer("Current state."));
        };
        await Say(runtime, "latest chat");
        var result = await Wait(runtime);
        Check(!result["messages"].Items().Any(e => e.Text("event") == "chat_error"), "context overflow falls back through all levels");
        var retries = handler.Requests.Skip(2).ToArray();
        Check(retries.Length == 3 && retries[0].Text("prompt_cache_key") != firstId, "rollover opens a new session");
        Check(handler.History(retries[0]).WriteString().Contains("earlier chat") && handler.History(retries[0]).WriteString().Contains("Remember this reply."), "first fallback retains previous chats");
        Check(!handler.History(retries[1]).Any(n => n.Text("role") == "user" && n.Text("content") == "earlier chat") && handler.History(retries[1]).Last()!.Text("content") == "latest chat", "second fallback keeps latest chat");
        Check(handler.History(retries[2]).Count == 2 && handler.History(retries[2])[1]!.Text("content").StartsWith("Current public game state:"), "last fallback keeps state only");
        Check(retries.Select(r => r.Text("prompt_cache_key")).Distinct().Count() == 3, "each fallback has a fresh session identity");

        // Usage can force rollover even if the locally estimated text is small.
        handler.Respond = (body, _) =>
        {
            var answer = handler.Answer("usage");
            answer["usage"] = new JsonObject { ["input_tokens"] = 300000, ["output_tokens"] = 1 };
            return Task.FromResult(answer);
        };
        await Say(runtime, "usage one"); await Wait(runtime);
        var before = handler.Requests.Last().Text("prompt_cache_key");
        handler.Respond = (body, _) => Task.FromResult(handler.Answer("usage two"));
        await Say(runtime, "usage two"); await Wait(runtime);
        Check(handler.Requests.Last().Text("prompt_cache_key") != before, "reported token usage triggers restart");

        using var tinyHandler = new SessionHandler(api);
        using var tiny = await Runtime(tinyHandler, 1);
        await Say(tiny, "hello"); var tooSmall = await Wait(tiny);
        Check(tinyHandler.Requests.Count == 0 && tooSmall["messages"].Items().Any(e => e.Text("event") == "chat_error"), "state-only overflow is bounded before sending");
    }

    static async Task ScopeChange(string api)
    {
        using var handler = new SessionHandler(api) { State = SessionHandler.Combat() };
        handler.AfterAction = _ => handler.Commands == 1 ? SessionHandler.Rewards() : new JsonObject { ["state_type"] = "game_over" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Respond = async (body, _) =>
        {
            if (handler.IsBuddy(body)) return handler.HasResult(handler.History(body)) ? handler.Answer("Continuing the run.") :
                handler.Call("message_agent", new JsonObject { ["agent"] = "game", ["message"] = "Continue this run.", ["scope"] = "run" });
            entered.TrySetResult(); await release.Task;
            return handler.Action(body);
        };
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("win this fight", "fight");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Say(runtime, "continue this run"); await Wait(runtime, s => !s.Flag("chat_busy"));
        release.SetResult();
        var result = await Wait(runtime);
        Check(handler.Commands == 2 && result["agents"].Text("scope") == "run", "Buddy can expand fight-only play into continuing the run");
    }

    // A dispatched click the game silently ignores (e.g. a potion reward with
    // full slots) must come back as a rejected submission the model can
    // correct, not as a fatal "Game did not settle" stop.
    static async Task NoOpRecovery(string api)
    {
        var rewards = JsonNode.Parse("""{"state_type":"rewards","rewards":{"items":[{"index":0,"type":"potion","potion_name":"Power Potion"},{"index":1,"type":"card"}],"can_proceed":true},"player":{"hp":50,"potions":[{"slot":0,"name":"Speed Potion"}],"max_potion_slots":1}}""")!;
        using var handler = new SessionHandler(api) { State = rewards.DeepClone() };
        handler.AfterAction = _ => handler.Commands == 1 ? rewards.DeepClone() : new JsonObject { ["state_type"] = "game_over" };
        handler.Respond = (body, _) => Task.FromResult(handler.IsBuddy(body) ? handler.Answer("On it.") : handler.Action(body));
        using var runtime = await Runtime(handler);
        var previous = BotRuntime.MutationIdleWindow;
        BotRuntime.MutationIdleWindow = TimeSpan.FromMilliseconds(900);
        try
        {
            runtime.StartGameplay("win this run", "run");
            var result = await Wait(runtime);
            Check(handler.Commands == 2, "the ineffective click is followed by a fresh decision");
            Check(result.Text("status") == "idle" && result["agents"].Text("last_report").Contains("Run ended"), "play continues past a no-op click and ends normally");
            var corrected = handler.History(handler.Requests.First(r => !handler.IsBuddy(r) && handler.HasResult(handler.History(r))));
            Check(corrected.WriteString().Contains("no effect"), "the no-op rejection reaches the deciding agent");
        }
        finally { BotRuntime.MutationIdleWindow = previous; }
    }

    // A forced action that never takes effect must stop with a precise report
    // instead of repeating the same ineffective click forever.
    static async Task NoOpStop(string api)
    {
        using var handler = new SessionHandler(api) { State = SessionHandler.Rewards() };
        handler.AfterAction = _ => SessionHandler.Rewards();
        using var runtime = await Runtime(handler);
        var previous = BotRuntime.MutationIdleWindow;
        BotRuntime.MutationIdleWindow = TimeSpan.FromMilliseconds(300);
        try
        {
            runtime.StartGameplay("win this run", "run");
            var result = await Wait(runtime);
            Check(handler.Commands == 3, "the no-op streak is capped after three ineffective clicks");
            Check(result.Text("status") == "error" && result["agents"].Text("last_report").Contains("no effect"), "repeated no-ops stop with an explanatory report");
        }
        finally { BotRuntime.MutationIdleWindow = previous; }
    }

    static async Task ControlRollover(string api)
    {
        using var handler = new SessionHandler(api) { State = MapWithOnePath() };
        int calls = 0;
        handler.Respond = async (body, _) =>
        {
            if (++calls == 2)
            {
                await Wait(handler.Runtime!, s => !s.Flag("thread_alive"));
                throw new ContextLimitException();
            }
            return calls is 1 or 3 ? handler.Call("start_game", new JsonObject { ["instructions"] = "continue", ["scope"] = "run" }) : handler.Answer("Finished.");
        };
        using var runtime = await Runtime(handler);
        await Say(runtime, "continue this run");
        var result = await Wait(runtime);
        Check(calls == 4 && handler.Commands == 1 && result.Text("status") == "idle", "rollover cannot repeat a completed gameplay assignment");
        Check(handler.History(handler.Requests.Last()).WriteString().Contains("Do not start it again"), "completed assignment retry returns a player-safe rejection");

        static JsonNode MapWithOnePath()
        {
            var state = SessionHandler.Map(); state["map"]!["next_options"]!.AsArray().RemoveAt(1); return state;
        }
    }

    static async Task PlayerFacingVoice(string api)
    {
        using var handler = new SessionHandler(api) { State = SessionHandler.Combat(), ActionMessage = "The subagent is back in the driver's seat to handle this fight." };
        handler.Respond = (body, _) => Task.FromResult(handler.Action(body));
        using var runtime = await Runtime(handler);
        runtime.StartGameplay("Win this fight", "fight");
        var result = await Wait(runtime);
        var visible = result["messages"].Items().Where(e => e.Text("event") == "agent_message").Select(e => e.Text("message")).ToArray();
        Check(visible.Any(message => message.Contains("Buddy is back", StringComparison.Ordinal)), "gameplay commentary speaks as Buddy");
        Check(visible.All(message => !Regex.IsMatch(message, "subagent|game agent|combat agent", RegexOptions.IgnoreCase)), "private role names are filtered from the player feed");
        var prompt = handler.History(handler.Requests.First(r => !handler.IsBuddy(r)))[0]!.Text("content");
        Check(!Regex.IsMatch(prompt, "subagent|game agent|combat agent", RegexOptions.IgnoreCase), "gameplay role prompt does not teach private names to the model");
    }

    static async Task CustomPersonality(string api)
    {
        var directory = Path.Combine(Path.GetTempPath(), "spire-personality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Existing settings have only a preset ID and must load unchanged.
        File.WriteAllText(Path.Combine(directory, "settings.json"), new JsonObject
        {
            ["base_url"] = "http://model/v1", ["model"] = "test", ["api_type"] = api, ["personality"] = "calm_teacher"
        }.WriteString());
        const string custom = "Speak like a cheerful cartographer.\n用地图比喻解释选择，偶尔说一句：‘前路可期！’";
        using (var firstHandler = new SessionHandler(api))
        using (var first = new BotRuntime(directory, "missing.dll", firstHandler.Game, firstHandler))
        {
            var legacy = (await first.Dispatch("GET", "/status", null))["config"]!;
            Check(legacy.Text("personality") == "calm_teacher" && legacy.Text("custom_personality") == "", "old preset settings load without a custom field");
            await first.Dispatch("PUT", "/settings", new JsonObject { ["personality"] = "custom", ["custom_personality"] = custom });
        }

        using var handler = new SessionHandler(api);
        using var runtime = new BotRuntime(directory, "missing.dll", handler.Game, handler);
        handler.Runtime = runtime;
        var restored = (await runtime.Dispatch("GET", "/status", null))["config"]!;
        Check(restored.Text("personality") == "custom" && restored.Text("custom_personality") == custom, "custom choice and multiline Unicode description survive restart");
        handler.Respond = (body, _) => Task.FromResult(handler.IsBuddy(body) ? handler.Answer("Ready.") : handler.Action(body));
        handler.AfterAction = _ => handler.Commands == 1 ? SessionHandler.Combat() : new JsonObject { ["state_type"] = "game_over" };
        await Say(runtime, "hello"); await Wait(runtime);
        runtime.StartGameplay("win this run", "run");
        await Wait(runtime);
        Check(handler.Commands == 2 && handler.SawCombatOwner, "custom personality exercised in run and combat sessions");
        Check(handler.Requests.Any(handler.IsBuddy) && handler.Requests.All(r => handler.History(r)[0]!.Text("content").Contains(custom)), "chat, run and combat prompts all use the saved custom voice");

        const string edited = "Use gentle botanical metaphors, with a concise tactical takeaway.";
        var firstChat = handler.Requests.First(handler.IsBuddy);
        var originalPrompt = handler.History(firstChat)[0]!.Text("content");
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["custom_personality"] = edited });
        await Say(runtime, "hello again"); await Wait(runtime);
        CheckUpdate(edited, 1);
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["personality"] = "concise_analyst" });
        await Say(runtime, "be brief"); await Wait(runtime);
        CheckUpdate("emphasize decisive facts", 2);
        Check(!PersonalityNotes(handler.History(handler.Requests.Last())).Last().Contains(edited), "a preset note ignores stored custom prose");
        Check((await runtime.Dispatch("GET", "/status", null))["config"].Text("custom_personality") == edited, "switching to a preset retains custom prose");
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["personality"] = "custom" });
        await Say(runtime, "use my voice again"); await Wait(runtime);
        CheckUpdate(edited, 3);

        // New game and combat sessions use the saved personality directly.
        handler.State = SessionHandler.Map();
        int commandsBefore = handler.Commands, requestsBefore = handler.Requests.Count;
        handler.AfterAction = _ => handler.Commands == commandsBefore + 1 ? SessionHandler.Combat() : new JsonObject { ["state_type"] = "game_over" };
        runtime.StartGameplay("continue this run", "run"); await Wait(runtime);
        var freshPlay = handler.Requests.Skip(requestsBefore).ToArray();
        Check(freshPlay.Length == 2 && freshPlay.All(r => handler.History(r)[0]!.Text("content").Contains(edited)), "fresh game and combat prompts use the edited personality");

        var previousLanguage = L10n.Language;
        try
        {
            foreach (var language in new[] { "en", "zhs" })
            {
                L10n.Language = language;
                foreach (var empty in new string?[] { null, "", " \n\t " })
                {
                    await runtime.Dispatch("PUT", "/settings", new JsonObject { ["custom_personality"] = empty });
                    await Say(runtime, "hello"); await Wait(runtime);
                    CheckUpdate(Personalities.CustomExample, language == "zhs" ? 5 : 4);
                    if (language == "zhs")
                        Check(handler.History(handler.Requests.Last()).Any(n => n.Text("content").Contains("switched the interface language to Chinese")), "personality changes preserve the language-change note");
                }
                handler.State = SessionHandler.Combat();
                runtime.StartGameplay("win this fight", "fight"); await Wait(runtime);
                Check(handler.History(handler.Requests.Last())[0]!.Text("content").Contains(Personalities.CustomExample), "gameplay also uses the localized default for a blank description");
            }
        }
        finally { L10n.Language = previousLanguage; }

        // A genuine context rollover bakes the latest personality into a fresh
        // prompt instead of replaying obsolete personality-change notes.
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["custom_personality"] = edited });
        int attempts = 0;
        handler.Respond = (_, _) => ++attempts == 1 ? throw new ContextLimitException() : Task.FromResult(handler.Answer("Ready again."));
        await Say(runtime, "after rollover"); await Wait(runtime);
        var rebuilt = handler.Requests.Last();
        var prompt = handler.History(rebuilt)[0]!.Text("content");
        Check(attempts == 2 && rebuilt.Text("prompt_cache_key") != firstChat.Text("prompt_cache_key"), "context rollover starts a new session");
        Check(prompt.Contains(edited) && !prompt.Contains(custom), "rollover bakes the latest personality into its prompt");
        Check(PersonalityNotes(handler.History(rebuilt)).Length == 0, "rollover does not retain outdated personality notes");
        handler.Respond = (_, _) => Task.FromResult(handler.Answer("Still ready."));
        await Say(runtime, "after rebuilding"); await Wait(runtime);
        Check(PersonalityNotes(handler.History(handler.Requests.Last())).Length == 0, "new session already knows its personality");
        Console.WriteLine($"PASS {api} custom personality persistence, cached change notes, presets, localized defaults and rollover");

        void CheckUpdate(string expected, int count)
        {
            var request = handler.Requests.Last();
            var history = handler.History(request);
            var notes = PersonalityNotes(history);
            Check(history[0]!.Text("content") == originalPrompt, "personality changes preserve the original prompt");
            Check(request.Text("prompt_cache_key") == firstChat.Text("prompt_cache_key"), "personality changes preserve the cache key");
            Check(notes.Length == count && notes.Last().Contains(expected), "effective personality changes append exactly one current instruction");
            Check(history.Last()!.Text("content") != notes.Last(), "personality notes precede the current user message");
        }
    }

    static async Task SettingsContinuity(string api)
    {
        using var handler = new SessionHandler(api);
        handler.Respond = (body, _) =>
        {
            if (handler.HasResult(handler.History(body))) return Task.FromResult(handler.Answer("Remember these odds."));
            var call = handler.Call("check_game_odds", new JsonObject());
            if (api == "responses") call["output"]!.AsArray().Insert(0, new JsonObject
            {
                ["type"] = "reasoning", ["summary"] = new JsonArray(), ["encrypted_content"] = "test-encrypted-reasoning"
            });
            return Task.FromResult(call);
        };
        using var runtime = await Runtime(handler);
        await Say(runtime, "check the odds"); await Wait(runtime);
        var conversation = (await runtime.Dispatch("GET", "/status", null)).Text("conversation_id");
        var previous = handler.Requests.Last();
        handler.Respond = (_, _) => Task.FromResult(handler.Answer("Remember these odds."));
        var saves = new (JsonObject Settings, int Notes)[]
        {
            (new JsonObject(), 0),
            (new JsonObject { ["reasoning_effort"] = "high" }, 0),
            (new JsonObject { ["max_context_tokens"] = 240000 }, 0),
            (new JsonObject { ["api_key"] = "replacement-test-key" }, 0),
            (new JsonObject { ["custom_personality"] = "Unused while a preset is active." }, 0),
            (new JsonObject { ["personality"] = "calm_teacher" }, 1),
            (new JsonObject { ["personality"] = "calm_teacher" }, 1)
        };
        foreach (var (settings, notes) in saves)
        {
            await runtime.Dispatch("PUT", "/settings", settings);
            await Say(runtime, "continue chatting"); await Wait(runtime);
            var current = handler.Requests.Last();
            var before = handler.History(previous); var after = handler.History(current);
            Check(current.Text("prompt_cache_key") == previous.Text("prompt_cache_key"), "ordinary settings saves preserve the session/cache key");
            Check(after.Take(before.Count).Select(n => n!.WriteString()).SequenceEqual(before.Select(n => n!.WriteString())), "settings saves preserve the complete cached history prefix");
            Check(handler.HasResult(after) && after.WriteString().Contains("Remember these odds."), "settings saves retain tool protocols and assistant replies");
            if (api == "responses") Check(after.WriteString().Contains("test-encrypted-reasoning"), "settings saves retain encrypted reasoning");
            Check(PersonalityNotes(after).Length == notes, "no-op and unrelated settings saves do not add personality notes");
            Check(!after.WriteString().Contains("replacement-test-key"), "settings notes do not expose credentials");
            previous = current;
        }
        Check((api == "responses" ? previous["reasoning"].Text("effort") : previous.Text("reasoning_effort")) == "high", "updated reasoning effort reaches the request without resetting history");

        foreach (var settings in new[] { new JsonObject { ["model"] = "other-model" }, new JsonObject { ["base_url"] = "http://other-model/v1" } })
        {
            await runtime.Dispatch("PUT", "/settings", settings);
            await Say(runtime, "continue on this backend"); await Wait(runtime);
            var current = handler.Requests.Last(); var history = handler.History(current);
            Check(current.Text("prompt_cache_key") != previous.Text("prompt_cache_key"), "model and endpoint changes start a fresh provider session");
            Check(!handler.HasResult(history) && !history.WriteString().Contains("test-encrypted-reasoning"), "provider changes do not replay provider-specific tool/reasoning transcripts");
            Check(history.WriteString().Contains("Remember these odds."), "provider changes retain completed conversation text");
            Check(history[0]!.Text("content").Contains("Be patient and teach"), "fresh provider session bakes the latest personality into its prompt");
            Check(PersonalityNotes(history).Length == 0, "fresh provider session needs no personality change notes");
            previous = current;
        }

        var otherApi = api == "responses" ? "chat_completions" : "responses";
        using var codec = new SessionHandler(otherApi);
        handler.Respond = (_, _) => Task.FromResult(codec.Answer("New format, same conversation."));
        await runtime.Dispatch("PUT", "/settings", new JsonObject { ["api_type"] = otherApi });
        await Say(runtime, "continue in the new format");
        var result = await Wait(runtime);
        var switched = handler.Requests.Last(); var switchedHistory = codec.History(switched);
        Check(switched.Text("prompt_cache_key") != previous.Text("prompt_cache_key"), "API format change starts a fresh session");
        Check(switchedHistory[0]!.Text("role") == (otherApi == "responses" ? "developer" : "system"), "new session uses the selected API format");
        Check(!codec.HasResult(switchedHistory) && switchedHistory.WriteString().Contains("Remember these odds."), "API format change reconstructs completed chats without old tool items");
        Check(result.Text("conversation_id") == conversation && !result["messages"].Items().Any(e => e.Text("event") == "chat_error"), "settings changes preserve the visible conversation without errors");
        Console.WriteLine($"PASS {api} settings preserve cached transcripts and backend changes safely rebuild");
    }

    static string[] PersonalityNotes(JsonArray history) => history
        .Where(n => n.Text("role") == "user" && n.Text("content").StartsWith("The operator changed your commentary personality."))
        .Select(n => n!.Text("content")).ToArray();
}

internal sealed class SessionHandler(string api) : HttpMessageHandler
{
    internal string Api => api;
    internal BotRuntime? Runtime;
    internal JsonNode State = Map();
    internal Func<JsonNode, CancellationToken, Task<JsonNode>>? Respond;
    internal Func<JsonNode, JsonNode>? AfterAction;
    internal Func<string, JsonNode>? Solver;
    internal List<string> SolverOps = [];
    internal List<JsonNode> Requests = [];
    internal int Commands, MaxBuddyCalls;
    internal string ActionMessage = "Playing.";
    int buddyCalls;
    internal bool SawCombatOwner;
    internal IGameAdapter Game => new ScheduledGameAdapter(a => a(), () => State.DeepClone(), command =>
    {
        Commands++;
        var agents = Runtime!.Dispatch("GET", "/status", null).GetAwaiter().GetResult()["agents"]!;
        if (State.Text("state_type") == "monster") SawCombatOwner = agents["combat"] != null && agents["game"].Text("status") == "waiting_for_combat";
        State = AfterAction?.Invoke(command) ?? new JsonObject { ["state_type"] = "game_over" };
        return new JsonObject { ["status"] = "ok" };
    }, (_, _, _, _, _, _) => new JsonObject(), () => new JsonObject { ["odds"] = new JsonObject { ["chance"] = "40%" } }, op =>
    {
        SolverOps.Add(op);
        return Solver == null ? new JsonObject { ["available"] = false } : Solver(op);
    });

    internal static JsonNode Map() => JsonNode.Parse("""{"state_type":"map","map":{"next_options":[{"index":0},{"index":1}]},"player":{"hp":50,"gold":20,"potions":[]}}""")!;
    internal static JsonNode Combat() => JsonNode.Parse("""{"state_type":"monster","battle":{"round":1,"turn":"player"},"player":{"hp":50,"potions":[],"hand":[]}}""")!;
    internal static JsonNode Rewards() => JsonNode.Parse("""{"state_type":"rewards","rewards":{"items":[],"can_proceed":true},"player":{"hp":50,"potions":[]}}""")!;
    internal JsonArray History(JsonNode body) => body[api == "responses" ? "input" : "messages"]!.AsArray();
    internal bool HasResult(JsonArray history) => history.Any(n => n.Text("type") == "function_call_output" || n.Text("role") == "tool");
    internal bool IsBuddy(JsonNode body) => body.Text("prompt_cache_key").StartsWith("buddy-");
    internal JsonNode Answer(string text) => api == "responses"
        ? new JsonObject { ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) }) }
        : new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = ModelClient.Message("assistant", text) }) };
    internal JsonNode Call(string name, JsonObject args, bool duplicate = false)
    {
        var calls = new JsonArray();
        for (int i = 0; i < (duplicate ? 2 : 1); i++)
        {
            var id = Guid.NewGuid().ToString("N");
            calls.Add(api == "responses"
                ? new JsonObject { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = args.WriteString() }
                : new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args.WriteString() } });
        }
        return api == "responses" ? new JsonObject { ["output"] = calls }
            : new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = calls } }) };
    }
    internal JsonNode Action(JsonNode body)
    {
        var brief = History(body).Last(n => n.Text("role") == "user" && n.Text("content").Contains("snapshot="))!.Text("content");
        return Call("take_action", new JsonObject { ["snapshot_id"] = Regex.Match(brief, "snapshot=([0-9A-Fa-f]{16})").Groups[1].Value, ["action_ids"] = new JsonArray("a0"), ["message"] = ActionMessage, ["rationale"] = "test", ["plan"] = "Preserve HP." });
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
        lock (Requests) Requests.Add(body.DeepClone());
        var buddy = IsBuddy(body);
        if (buddy) MaxBuddyCalls = Math.Max(MaxBuddyCalls, Interlocked.Increment(ref buddyCalls));
        try
        {
            JsonNode response;
            try { response = Respond == null ? Answer("Hello.") : await Respond(body, ct); }
            catch (ContextLimitException)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"code\":\"context_length_exceeded\"}}") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.WriteString(), Encoding.UTF8, "application/json") };
        }
        finally { if (buddy) Interlocked.Decrement(ref buddyCalls); }
    }
}
