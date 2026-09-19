using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class SolverChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static async Task Run()
    {
        await SettingsRoundTrip();
        await SolverPlaysTheFight();
        await MissingSolverFallsBack();
        await DisabledSolverRetriesNextFight();
        await FailedTakeOverFallsBack();
        await TransientFramesDoNotEndTheFight();
        Console.WriteLine("PASS Combat Solver settings, delegation, and fallbacks");
    }

    static async Task SettingsRoundTrip()
    {
        using var handler = new SessionHandler("responses");
        using var runtime = await Runtime(handler, new JsonObject { ["use_combat_solver"] = true, ["hide_combat_solver_ui"] = true });
        var config = (await runtime.Dispatch("GET", "/status", null))["config"]!;
        Check(config.Flag("use_combat_solver") && config.Flag("hide_combat_solver_ui"), "solver settings persist");

        using var plainHandler = new SessionHandler("responses");
        using var plain = await Runtime(plainHandler);
        var defaults = (await plain.Dispatch("GET", "/status", null))["config"]!;
        Check(!defaults.Flag("use_combat_solver") && !defaults.Flag("hide_combat_solver_ui"), "solver settings default to off");
    }

    static async Task SolverPlaysTheFight()
    {
        // Map decision, then the solver fights without any model call, then the
        // game agent resumes on the rewards screen and the run ends.
        using var handler = new SessionHandler("responses") { State = SessionHandler.Map() };
        handler.AfterAction = _ => handler.Commands switch
        {
            1 => SessionHandler.Combat(),
            _ => new JsonObject { ["state_type"] = "game_over" }
        };
        handler.Respond = (body, _) => Task.FromResult(handler.IsBuddy(body) ? handler.Answer("On it.") : handler.Action(body));
        handler.Solver = op => op switch
        {
            // Enabling arms the solver and resolves the scripted fight at once.
            "enable" => SolveTheFight(handler),
            "disable" => new JsonObject { ["available"] = true, ["enabled"] = false },
            _ => new JsonObject { ["available"] = true, ["full_auto"] = false, ["solver_disabled"] = false },
        };
        using var runtime = await Runtime(handler, new JsonObject { ["use_combat_solver"] = true });
        runtime.StartGameplay("win this run", "run");
        var result = await Wait(runtime);
        // The map needs one model decision; the scripted rewards screen proceeds
        // as a forced action, so the solver fight never opens a model session.
        Check(result.Text("status") == "idle" && handler.Commands == 2, "map decision and forced proceed bracket the solver fight");
        Check(handler.Requests.Count == 1 && handler.Requests.All(r => r.Text("prompt_cache_key").StartsWith("game-")),
            "a solver-driven fight never opens a model combat session");
        var visible = result["messages"].Items().Where(e => e.Text("event") == "agent_message").Select(e => e.Text("message")).ToArray();
        Check(visible.Any(m => m.Contains("Combat Solver", StringComparison.Ordinal)), "the hand-over is player-visible");
        Check(handler.SolverOps.Contains("disable"), "the solver is disarmed when its fight ends");
        Check(result["agents"]?["combat"] == null, "no combat session is reported");
    }

    static async Task MissingSolverFallsBack()
    {
        // The mod being absent is permanent for the run: the first fight probes
        // once and every fight, including later ones, is played by the model.
        using var handler = new SessionHandler("responses") { State = SessionHandler.Combat() };
        handler.AfterAction = _ => handler.Commands switch
        {
            // A fresh fight differs from the settled one so the state settles.
            1 => NextFight(),
            2 => SessionHandler.Rewards(),
            _ => new JsonObject { ["state_type"] = "game_over" }
        };
        handler.Respond = (body, _) => Task.FromResult(handler.Action(body));
        handler.Solver = _ => new JsonObject { ["available"] = false };
        using var runtime = await Runtime(handler, new JsonObject { ["use_combat_solver"] = true });
        runtime.StartGameplay("win this run", "run");
        var result = await Wait(runtime);
        Check(result.Text("status") == "idle" && handler.Commands == 3, "both fights and the rewards screen are handled");
        Check(handler.SolverOps.Count(op => op == "status") == 1, "an unavailable solver is probed exactly once per run");
        Check(handler.Requests.Count(r => r.Text("prompt_cache_key").StartsWith("combat-")) == 2, "each fight falls back to the model combat session");
        var visible = result["messages"].Items().Where(e => e.Text("event") == "agent_message").Select(e => e.Text("message")).ToArray();
        Check(visible.Any(m => m.Contains("not available", StringComparison.Ordinal)), "the fallback is player-visible");

        static JsonNode NextFight() { var fight = SessionHandler.Combat(); fight["battle"]!["round"] = 2; return fight; }
    }

    static async Task DisabledSolverRetriesNextFight()
    {
        // A solver disabled in its own settings only bypasses the current
        // fight; the next fight is offered to it again.
        bool firstStatus = true;
        using var handler = new SessionHandler("responses") { State = SessionHandler.Combat() };
        handler.AfterAction = _ => handler.Commands switch
        {
            1 => SessionHandler.Rewards(),
            2 => SessionHandler.Combat(),
            _ => new JsonObject { ["state_type"] = "game_over" }
        };
        handler.Respond = (body, _) => Task.FromResult(handler.Action(body));
        handler.Solver = op => op switch
        {
            "enable" => SolveTheFight(handler),
            "disable" => new JsonObject { ["available"] = true, ["enabled"] = false },
            // The first status call reports the solver's own disable switch;
            // later calls see it re-enabled in its settings.
            _ => new JsonObject { ["available"] = true, ["full_auto"] = false, ["solver_disabled"] = Take(ref firstStatus) },
        };
        using var runtime = await Runtime(handler, new JsonObject { ["use_combat_solver"] = true });
        runtime.StartGameplay("win this run", "run");
        var result = await Wait(runtime);
        // Fight one: model plays after the refusal. Rewards: model. Fight two:
        // solver-driven. Rewards: model again.
        Check(result.Text("status") == "idle" && handler.Commands == 3, "first fight refused, second fight handed to the solver");
        Check(handler.Requests.Count(r => r.Text("prompt_cache_key").StartsWith("combat-")) == 1, "only the refused fight reaches the model");
        Check(handler.SolverOps.Contains("enable"), "the next fight is offered to the solver again");

        static bool Take(ref bool first) { var value = first; first = false; return value; }
    }

    static async Task FailedTakeOverFallsBack()
    {
        // The solver never accepts the take-over; after the bounded retries the
        // model combat agent plays instead.
        var previousLimit = BotRuntime.SolverEnableFailureLimit;
        BotRuntime.SolverEnableFailureLimit = 2;
        try
        {
            using var handler = new SessionHandler("responses") { State = SessionHandler.Combat() };
            handler.AfterAction = _ => handler.Commands == 1 ? SessionHandler.Rewards() : new JsonObject { ["state_type"] = "game_over" };
            handler.Respond = (body, _) => Task.FromResult(handler.Action(body));
            handler.Solver = op => op switch
            {
                "enable" => new JsonObject { ["available"] = true, ["enabled"] = false },
                "disable" => new JsonObject { ["available"] = true, ["enabled"] = false },
                _ => new JsonObject { ["available"] = true, ["full_auto"] = false, ["solver_disabled"] = false },
            };
            using var runtime = await Runtime(handler, new JsonObject { ["use_combat_solver"] = true });
            runtime.StartGameplay("win this fight", "fight");
            var result = await Wait(runtime);
            Check(result.Text("status") == "idle" && handler.Commands == 1, "the model finishes the fight the solver could not take");
            Check(handler.SolverOps.Count(op => op == "enable") == 2, "take-over retries are bounded");
            var visible = result["messages"].Items().Where(e => e.Text("event") == "agent_message").Select(e => e.Text("message")).ToArray();
            Check(visible.Any(m => m.Contains("could not take this fight", StringComparison.Ordinal)), "the give-up is player-visible");
        }
        finally { BotRuntime.SolverEnableFailureLimit = previousLimit; }
    }

    static async Task TransientFramesDoNotEndTheFight()
    {
        // Right after a map choice the screen can flash a non-combat frame
        // while the game still reports the fight in progress. The hand-off must
        // keep waiting instead of handing back and re-entering the same fight,
        // which duplicated the hand-over message and the finished note.
        int statuses = 0;
        using var handler = new SessionHandler("responses") { State = SessionHandler.Combat() };
        handler.Respond = (body, _) => Task.FromResult(handler.Action(body));
        handler.Solver = op => op switch
        {
            "disable" => new JsonObject { ["available"] = true, ["enabled"] = false },
            "status" => Status(++statuses),
            _ => new JsonObject { ["available"] = false },
        };
        using var runtime = await Runtime(handler, new JsonObject { ["use_combat_solver"] = true });
        runtime.StartGameplay("win this fight", "fight");
        var result = await Wait(runtime);
        var handovers = result["messages"].Items()
            .Where(e => e.Text("event") == "agent_message" && e.Text("message").Contains("Combat Solver", StringComparison.Ordinal))
            .Select(e => e.Text("message")).ToArray();
        Check(result.Text("status") == "idle" && handler.Commands == 0, "the solver fight ends without any model action");
        Check(handovers.Length == 1, "one hand-over message per fight");
        Check(statuses >= 4, "a non-combat frame while the fight is still in progress does not end the hand-off");
        Console.WriteLine("PASS Combat Solver transient frames wait for the fight to truly end");

        // Full auto is armed throughout; the second poll flashes a rewards-like
        // frame, and only the game's combat flag ending (fourth poll) finishes
        // the fight.
        JsonNode Status(int n)
        {
            if (n == 2) handler.State = SessionHandler.Rewards();
            return new JsonObject { ["available"] = true, ["full_auto"] = true, ["solver_disabled"] = false, ["combat"] = n < 4 };
        }
    }

    static JsonNode SolveTheFight(SessionHandler handler)
    {
        handler.State = SessionHandler.Rewards();
        return new JsonObject { ["available"] = true, ["enabled"] = true };
    }

    static async Task<BotRuntime> Runtime(SessionHandler handler, JsonObject? extra = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "spire-solver-" + Guid.NewGuid().ToString("N"));
        var runtime = new BotRuntime(directory, "missing.dll", handler.Game, handler);
        handler.Runtime = runtime;
        var settings = new JsonObject
        {
            ["base_url"] = "http://model/v1", ["model"] = "test", ["api_type"] = handler.Api, ["max_context_tokens"] = 250000
        };
        foreach (var (key, value) in extra ?? []) settings[key] = value?.DeepClone();
        await runtime.Dispatch("PUT", "/settings", settings);
        return runtime;
    }

    static async Task<JsonNode> Wait(BotRuntime runtime)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await runtime.Dispatch("GET", "/status", null);
            if (!status.Flag("chat_busy") && !status.Flag("thread_alive")) return status;
            await Task.Delay(20);
        }
        throw new Exception("Solver scenario did not settle: " + await runtime.Dispatch("GET", "/status", null));
    }
}
