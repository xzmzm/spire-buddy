using System.Reflection;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace SpireBuddy.Game;

// Reflection bridge into the optional Combat Solver workshop mod. The game's
// mod loader puts every mod assembly in its own load context, which is the
// game's default context here, so the solver's static controller is reachable
// by name once its DLL has loaded. Every member is optional: with the solver
// absent each call degrades to a no-op or an "unavailable" report instead of
// an error, and the caller falls back to Buddy's own combat play.
internal static class CombatSolverBridge
{
    static Type? controller;
    static PropertyInfo? fullAuto, solverDisabled;
    static MethodInfo? setFullAuto, hideOverlay;

    // Mods load one by one at startup; Spire Buddy may initialize before the
    // solver's DLL exists, so the probe repeats until it succeeds once.
    internal static bool Available
    {
        get
        {
            if (controller != null) return true;
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "CombatSolver");
                controller = assembly?.GetType("CombatSolver.SolverController");
                var overlay = assembly?.GetType("CombatSolver.SolverOverlay");
                if (controller == null || overlay == null) { controller = null; return false; }
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                fullAuto = controller.GetProperty("FullAutoEnabled", flags);
                solverDisabled = controller.GetProperty("SolverDisabled", flags);
                setFullAuto = controller.GetMethod("SetFullAuto", flags);
                hideOverlay = overlay.GetMethod("Hide", flags);
                if (setFullAuto == null || hideOverlay == null || fullAuto == null || solverDisabled == null)
                {
                    controller = null;
                    return false;
                }
                return true;
            }
            catch { return false; }
        }
    }

    static bool FullAuto => fullAuto!.GetValue(null) is true;
    static bool Disabled => solverDisabled!.GetValue(null) is true;

    // Main-thread dispatch entry used by the scheduled game adapter. Ops stay
    // tiny JSON objects so the worker thread never touches game objects.
    internal static JsonNode Dispatch(string op)
    {
        if (!Available) return new JsonObject { ["available"] = false };
        try
        {
            switch (op)
            {
                case "status":
                    return new JsonObject { ["available"] = true, ["full_auto"] = FullAuto, ["solver_disabled"] = Disabled, ["combat"] = CombatManager.Instance?.IsInProgress == true };
                case "enable":
                case "disable":
                    return new JsonObject { ["available"] = true, ["enabled"] = SetFullAuto(op == "enable") };
                default:
                    return new JsonObject { ["available"] = false };
            }
        }
        catch (Exception error)
        {
            return new JsonObject { ["available"] = true, ["error"] = error.Message };
        }
    }

    // The solver's own take-over check can reject (enemy turn, disabled, ...),
    // so the caller retries; success is confirmed through the live flag.
    static bool SetFullAuto(bool enabled)
    {
        if (CombatManager.Instance?.DebugOnlyGetState() is not { } state) return false;
        if (NGame.Instance is not { } host) return false;
        setFullAuto!.Invoke(null, [host, state, enabled]);
        return enabled == FullAuto;
    }

    // The solver re-shows its overlay on every combat event, so suppression is
    // retried every frame while the hide setting is on; deferred from the panel
    // frame it runs after solver processing and before drawing.
    internal static void SuppressCombatOverlay()
    {
        if (!Available || CombatManager.Instance is not { IsInProgress: true }) return;
        try { hideOverlay!.Invoke(null, null); } catch { }
    }
}
