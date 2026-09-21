using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class CrystalSphereChecks
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    static JsonNode Board(int left = 4, string tool = "big")
    {
        var cells = new JsonArray();
        for (int y = 0; y < 11; y++)
            for (int x = 0; x < 11; x++)
                cells.Add(new JsonObject { ["x"] = x, ["y"] = y, ["is_hidden"] = true, ["is_clickable"] = true });
        return new JsonObject
        {
            ["state_type"] = "crystal_sphere",
            ["crystal_sphere"] = new JsonObject
            {
                ["grid_width"] = 11, ["grid_height"] = 11, ["divinations_left"] = left,
                ["can_divine"] = true, ["tool"] = tool, ["can_use_big_tool"] = true, ["can_use_small_tool"] = true,
                ["can_proceed"] = false, ["cells"] = cells, ["revealed_items"] = new JsonArray(), ["clickable_cells"] = cells.DeepClone()
            },
            ["player"] = new JsonObject { ["potions"] = new JsonArray(new JsonObject { ["slot"] = 0, ["name"] = "Swift Potion" }) }
        };
    }

    static JsonNode Cell(JsonNode state, int x, int y) => state["crystal_sphere"]!["cells"].Items()
        .Single(c => c.Num("x") == x && c.Num("y") == y);

    static void Item(JsonNode state, string type, int x, int y, int w, int h, params (int X, int Y)[] hidden)
    {
        state["crystal_sphere"]!["revealed_items"]!.AsArray().Add(new JsonObject
        {
            ["item_type"] = "CrystalSphere" + type, ["is_good"] = type != "Curse",
            ["x"] = x, ["y"] = y, ["width"] = w, ["height"] = h
        });
        for (int cy = y; cy < y + h; cy++)
            for (int cx = x; cx < x + w; cx++)
            {
                var cell = Cell(state, cx, cy);
                bool covered = hidden.Contains((cx, cy));
                cell["is_hidden"] = covered;
                cell["is_clickable"] = covered;
                if (!covered) { cell["item_type"] = "CrystalSphere" + type; cell["is_good"] = type != "Curse"; }
            }
        RefreshClicks(state);
    }

    static void RefreshClicks(JsonNode state) => state["crystal_sphere"]!["clickable_cells"] = new JsonArray(
        state["crystal_sphere"]!["cells"].Items().Where(c => c.Flag("is_hidden")).Select(c => (JsonNode)new JsonObject
        { ["x"] = c["x"]!.DeepClone(), ["y"] = c["y"]!.DeepClone() }).ToArray());

    static string Brief(JsonNode state) => GameState.Brief("test", "win", "", state, GameState.Actions(state));

    internal static async Task Run()
    {
        // Reconstruct the pasted trace immediately before its last small click.
        var trace = Board(1, "small");
        Item(trace, "Relic", 2, 2, 4, 4, (5, 2), (5, 3), (2, 5), (3, 5));
        var original = trace.WriteString();
        var brief = Brief(trace);
        Check(brief.Contains("cleared 12/16; INCOMPLETE") && brief.Contains("big needs at least 2")
            && brief.Contains("CANNOT finish with remaining divinations"), "trace relic must not be mistaken for a secured reward or finishable last-click target");
        Check(brief.Contains("BOTH cost 1") && brief.Contains("switching tools is free") && brief.Contains("?=unknown hidden")
            && brief.Contains("y=2:") && brief.Contains("5:a"), "board rules and compact action coordinates must reach the model");
        var view = new CrystalSphereView(trace["crystal_sphere"]!);
        var last = view.At(5, 2, "small");
        Check(last.NewCells == 1 && last.UnknownCells == 0 && last.Rewards == 0 && last.Summary.Contains("3 left"),
            "last safe fragment is explicitly zero completed rewards");
        Check(last.Summary.Contains("no known reward completed") && !brief.Contains("    small (5,2)"),
            "last-click partial relic progress is legal but not promoted as a completion opportunity");
        Check(trace.WriteString() == original, "geometry, legal actions and brief must not mutate their input");
        var jev = GameState.JevBrief(trace, "win", "", new JsonArray(), "");
        Check(jev.Contains("cleared 12/16; INCOMPLETE") && jev.Contains("Candidate previews")
            && JevContext.Question(trace)!.Contains("visible fragment is not a secured reward"), "Jev gets the same geometry and event-specific objective");
        var described = JevContext.DescribeActions(trace, GameState.Actions(trace));
        Check(described.Items().Where(a => a["command"].Text("action") == "crystal_sphere_click_cell")
            .All(a => a["jev_criteria"].Text("action").Contains("unknown=")), "every Jev click criterion includes its coverage and uncertainty");

        // Large tool reaches outside a relic: a click can finish both reward and curse.
        var overlap = Board(2);
        Item(overlap, "Relic", 2, 2, 4, 4, (5, 3), (5, 4));
        Item(overlap, "Curse", 6, 3, 2, 2, (6, 3), (6, 4));
        view = new CrystalSphereView(overlap["crystal_sphere"]!);
        var big = view.At(5, 3, "big");
        var small = view.At(5, 3, "small");
        Check(big.NewCells == 5 && big.UnknownCells == 1 && big.Rewards == 1 && big.Curses == 1
            && big.Summary.Contains("CURSE TRIGGERS"), "big footprint must account for adjacent curse and unknown space");
        Check(small.NewCells == 1 && small.UnknownCells == 0 && small.Rewards == 0 && small.Curses == 0
            && small.Summary.Contains("1 left"), "small can make precise progress without completing a neighboring curse");
        Check(!GameState.Actions(overlap).Items().Any(a => a["command"].Num("x") == 4 && a["command"].Num("y") == 3),
            "cleared centers cannot be offered as large-tool clicks");

        var partialCurse = Board();
        Item(partialCurse, "Curse", 6, 3, 2, 2, (6, 3), (6, 4), (7, 4));
        var touched = new CrystalSphereView(partialCurse["crystal_sphere"]!).At(6, 3, "small");
        Check(touched.BadProgress == 1 && touched.Curses == 0 && touched.Summary.Contains("2 left"),
            "a curse fragment is not an already-triggered curse");
        var complete = Board();
        Item(complete, "Relic", 2, 2, 4, 4);
        Item(complete, "Curse", 7, 3, 2, 2);
        Check(Brief(complete).Contains("COMPLETE (reward secured)") && Brief(complete).Contains("curse already triggered"),
            "finished rewards and curses are distinguished from fragments");
        Check(new CrystalSphereView(complete["crystal_sphere"]!).At(6, 3, "big").Curses == 0,
            "an already exposed curse cannot trigger a second time in a preview");

        var empty = Board();
        view = new CrystalSphereView(empty["crystal_sphere"]!);
        Check(view.At(0, 0, "big").NewCells == 4 && view.At(0, 5, "big").NewCells == 6
            && view.At(5, 5, "big").NewCells == 9, "large footprints clip correctly at corners and edges");
        Check(view.At(5, 5, "big").UnknownCells == 9 && view.At(5, 5, "small").UnknownCells == 1,
            "unknown content remains uncertain for either tool");
        var publicBrief = Brief(empty);
        var publicActions = GameState.Actions(empty).WriteString();
        Cell(empty, 5, 5)["item_type"] = "SECRET_CURSE";
        Cell(empty, 5, 5)["is_good"] = false;
        empty["crystal_sphere"]!["revealed_items"]!.AsArray().Add(JsonNode.Parse("""
            {"item_type":"SECRET_CURSE","is_good":false,"x":5,"y":5,"width":2,"height":2}
            """)!);
        Check(Brief(empty) == publicBrief && GameState.Actions(empty).WriteString() == publicActions,
            "neither hidden cell assignments nor wholly hidden items may influence model observations or recommendations");

        var corners = Board(3);
        Item(corners, "Relic", 2, 2, 4, 4, (2, 2), (5, 2), (2, 5), (5, 5));
        Check(Brief(corners).Contains("big needs at least 4") && Brief(corners).Contains("CANNOT finish"),
            "completion budget uses geometry, not ceil(remaining squares / 9)");

        var actions = GameState.Actions(trace);
        var click = actions.Items().First(a => a["command"].Text("action") == "crystal_sphere_click_cell");
        var change = actions.Items().First(a => a["command"].Text("action") == "crystal_sphere_set_tool");
        bool rejected = false;
        try { ActionBatch.Select(trace, actions, new JsonObject { ["action_ids"] = new JsonArray(change.Text("id"), click.Text("id")) }); }
        catch (InvalidOperationException ex) { rejected = ex.Message.Contains("updated board"); }
        Check(rejected, "tool changes and reveals must not batch stale geometry");
        Check(ActionBatch.Select(trace, actions, new JsonObject { ["action_ids"] = new JsonArray(click.Text("id")) }).Count == 1,
            "individual legal clicks remain executable");
        var fingerprint = GameState.Fingerprint(trace);
        trace["crystal_sphere"]!["tool"] = "big";
        Check(GameState.Fingerprint(trace) != fingerprint, "tool changes invalidate previous action IDs and previews");

        // The actual counter is language-independent and gates both clicks and tools,
        // even when stale UI buttons/clickable cells persist through reward animation.
        var exhausted = Board(0);
        exhausted["crystal_sphere"]!["divinations_left_text"] = "还剩下0次占卜。";
        Check(GameState.Actions(exhausted).Items().All(a => a["command"].Text("action") == "discard_potion")
            && !GameState.Ready(exhausted), "zero divinations cannot publish phantom actions or a settled state");
        exhausted["crystal_sphere"]!["divinations_left"] = 2;
        Check(GameState.Ready(exhausted), "live numeric counter takes precedence over a stale localized label");
        exhausted["crystal_sphere"]!["can_divine"] = false;
        Check(!GameState.Ready(exhausted), "unavailable minigame stays noninteractive even with positive count");
        exhausted["crystal_sphere"]!["divinations_left"] = 0;
        var finished = exhausted.DeepClone(); finished["crystal_sphere"]!["can_proceed"] = true;
        Check(GameState.ForcedAction(finished, GameState.Actions(finished))?["command"].Text("action") == "crystal_sphere_proceed",
            "finished sphere proceeds automatically despite optional potion disposal");
        int reads = 0;
        var adapter = new ScheduledGameAdapter(a => a(), () => ++reads <= 6 ? exhausted.DeepClone() : finished.DeepClone(),
            _ => throw new Exception("settlement must not execute extra clicks"), (_, _, _, _, _, _) => new JsonObject());
        using (var runtime = new BotRuntime(Path.Combine(Path.GetTempPath(), "spire-crystal-" + Guid.NewGuid().ToString("N")), "missing.dll", adapter))
        {
            var settled = await runtime.Stable(CancellationToken.None, fingerprint, click["command"]);
            Check(settled["crystal_sphere"].Flag("can_proceed") && reads >= 9,
                "last click waits through repeated zero-count frames until proceed is enabled");
        }
        Console.WriteLine("PASS Crystal Sphere trace, completion budgets, tool footprints, curses, hidden information, action mapping and settlement");
        if (Environment.GetEnvironmentVariable("SPIRE_CRYSTAL_BRIEF") is string path)
            File.WriteAllText(path, brief);
    }
}
