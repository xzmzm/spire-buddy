using System.Numerics;
using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

// Pure public-state geometry. No game objects, hidden item assignments, reward
// rolls or RNG: the same analysis serves the regular model and Jev.
internal sealed class CrystalSphereView
{
    readonly JsonNode data;
    readonly Dictionary<(int X, int Y), JsonNode> cells;
    readonly List<(int X, int Y)> clickable;
    readonly List<Item> items = [];
    readonly record struct Item(string Key, string Name, bool Good, HashSet<(int X, int Y)> Area, HashSet<(int X, int Y)> Hidden);
    internal sealed record Preview(int X, int Y, string Tool, int NewCells, int UnknownCells,
        int Rewards, int Curses, int GoodProgress, int BadProgress, string Summary);

    internal CrystalSphereView(JsonNode data)
    {
        this.data = data;
        cells = data["cells"].Items().Where(c => c.Num("x") != null && c.Num("y") != null)
            .GroupBy(c => (c.Num("x")!.Value, c.Num("y")!.Value)).ToDictionary(g => g.Key, g => g.First());
        clickable = data["clickable_cells"].Items().Where(c => c.Num("x") != null && c.Num("y") != null)
            .Select(c => (X: c.Num("x")!.Value, Y: c.Num("y")!.Value))
            .Where(p => cells.TryGetValue(p, out var cell) && cell.Flag("is_hidden") && cell.Flag("is_clickable", true))
            .Distinct().OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
        foreach (var item in data["revealed_items"].Items().OrderBy(i => i.Num("y")).ThenBy(i => i.Num("x")))
        {
            if (item.Num("x") is not int x || item.Num("y") is not int y ||
                item.Num("width") is not int w || item.Num("height") is not int h || w <= 0 || h <= 0) continue;
            var area = cells.Keys.Where(p => p.X >= x && p.X < x + w && p.Y >= y && p.Y < y + h).ToHashSet();
            // Fail closed on incomplete snapshots and wholly hidden objects.
            // Never use an item_type attached to a still-covered cell.
            if (area.Count != w * h || !area.Any(p => !cells[p].Flag("is_hidden") &&
                    cells[p].Text("item_type") == item.Text("item_type"))) continue;
            var hidden = area.Where(p => cells[p].Flag("is_hidden")).ToHashSet();
            items.Add(new Item(((char)('A' + items.Count)).ToString(), Name(item), item.Flag("is_good"), area, hidden));
        }
    }

    static string Name(JsonNode item) => item.Text("item_type") switch
    {
        "CrystalSphereRelic" => "relic",
        "CrystalSphereGold" => item.Num("width") == 2 ? "30 gold" : "10 gold",
        "CrystalSpherePotion" => (item.Text("rarity") + " potion").Trim(),
        "CrystalSphereCardReward" => (item.Text("rarity") + " card reward").Trim(),
        "CrystalSphereCurse" => "Doubt curse",
        _ => item.Text("item_type", "item")
    };

    internal static bool CanDivine(JsonNode? data) => data != null && data.Flag("can_divine", true)
        && data.Num("divinations_left") is not <= 0 && !data.Flag("can_proceed");

    static bool Hits((int X, int Y) center, (int X, int Y) cell, string tool) =>
        Math.Abs(center.X - cell.X) <= (tool == "big" ? 1 : 0) && Math.Abs(center.Y - cell.Y) <= (tool == "big" ? 1 : 0);

    internal Preview At(int x, int y, string tool)
    {
        var cleared = cells.Where(c => c.Value.Flag("is_hidden") && Hits((x, y), c.Key, tool)).Select(c => c.Key).ToHashSet();
        var unknown = cleared.Except(items.SelectMany(i => i.Area)).Count();
        int rewards = 0, curses = 0, good = 0, bad = 0;
        var effects = new List<string>();
        foreach (var item in items)
        {
            var count = item.Hidden.Count(cleared.Contains);
            if (count == 0) continue;
            bool complete = count == item.Hidden.Count;
            if (item.Good) { good += count; if (complete) rewards++; }
            else { bad += count; if (complete) curses++; }
            effects.Add($"{item.Key} {item.Name} +{count}: " + (complete
                ? item.Good ? "COMPLETE" : "CURSE TRIGGERS"
                : $"{item.Hidden.Count - count} left"));
        }
        var summary = $"{tool} ({x},{y}): new={cleared.Count}, unknown={unknown}";
        if (effects.Count > 0) summary += "; " + string.Join("; ", effects);
        if (data.Num("divinations_left") == 1 && rewards == 0) summary += "; no known reward completed";
        return new Preview(x, y, tool, cleared.Count, unknown, rewards, curses, good, bad, summary);
    }

    internal static JsonArray Actions(JsonNode? data)
    {
        var result = new JsonArray();
        void Add(JsonObject command, string summary) => result.Add(new JsonObject
            { ["id"] = "a" + result.Count, ["command"] = command, ["summary"] = summary });
        if (data == null) return result;
        if (CanDivine(data))
        {
            var view = new CrystalSphereView(data);
            foreach (var tool in new[] { "big", "small" })
                if (data.Flag("can_use_" + tool + "_tool") && data.Text("tool") != tool)
                    Add(new JsonObject { ["action"] = "crystal_sphere_set_tool", ["tool"] = tool },
                        $"crystal_sphere_set_tool {tool} (free; next click clears {(tool == "big" ? "3x3" : "1 cell")})");
            if (data.Text("tool") is "big" or "small")
                foreach (var cell in view.clickable)
                    Add(new JsonObject { ["action"] = "crystal_sphere_click_cell", ["x"] = cell.X, ["y"] = cell.Y },
                        "crystal_sphere_click_cell " + view.At(cell.X, cell.Y, data.Text("tool")).Summary);
        }
        if (data.Flag("can_proceed")) Add(new JsonObject { ["action"] = "crystal_sphere_proceed" }, "crystal_sphere_proceed");
        return result;
    }

    // Optimistic set cover: geometry alone can prove an item unfinishable in the
    // remaining budget. Ignore curse exposure and centers becoming unclickable,
    // so this is explicitly a lower bound, never a promised executable plan.
    int? BigClickLowerBound(Item item)
    {
        var hidden = item.Hidden.ToArray();
        if (hidden.Length == 0) return 0;
        if (hidden.Length > 20) return (hidden.Length + 8) / 9;
        var masks = clickable.Select(p => hidden.Select((c, i) => Hits(p, c, "big") ? 1 << i : 0)
            .Aggregate(0, (a, b) => a | b)).Where(m => m != 0).Distinct().ToArray();
        masks = masks.Where(m => !masks.Any(other => m != other && (m | other) == other)).ToArray();
        var memo = new Dictionary<int, int>();
        int Cover(int remaining)
        {
            if (remaining == 0) return 0;
            if (memo.TryGetValue(remaining, out int found)) return found;
            int bit = 1 << BitOperations.TrailingZeroCount((uint)remaining);
            int best = hidden.Length + 1;
            foreach (var mask in masks.Where(m => (m & bit) != 0)) best = Math.Min(best, 1 + Cover(remaining & ~mask));
            return memo[remaining] = best;
        }
        var result = Cover((1 << hidden.Length) - 1);
        return result <= hidden.Length ? result : null;
    }

    internal void Brief(List<string> lines)
    {
        var left = data.Num("divinations_left");
        lines.Add($"[Crystal sphere] {data["grid_width"]}x{data["grid_height"]} | tool={data.Text("tool")} | divinations left="
            + (left?.ToString() ?? data.Text("divinations_left_text", "unknown")));
        lines.Add("  Rules: small clears 1 cell; big clears the centered 3x3 (clipped at edges). BOTH cost 1 divination; switching tools is free. Only hidden cells can be clicked.");
        lines.Add("  A fragment is NOT a reward. Clear ALL cells of an item to earn it; incomplete items give nothing. Completing the 2x2 curse adds Doubt immediately; completed good items pay out at the end.");
        lines.Add("  Board: x increases right, y down. ?=unknown hidden, .=cleared empty, #=unavailable, lowercase=hidden part of known item, uppercase=cleared part. Letters refer to this snapshot's items only.");
        int width = Math.Clamp(data.Num("grid_width") ?? 0, 0, 30), height = Math.Clamp(data.Num("grid_height") ?? 0, 0, 30);
        lines.Add("  y\\x " + string.Join(" ", Enumerable.Range(0, width).Select(x => x.ToString().PadLeft(2))));
        for (int y = 0; y < height; y++)
        {
            var row = new List<string>();
            for (int x = 0; x < width; x++)
            {
                var p = (x, y);
                var item = items.FirstOrDefault(i => i.Area.Contains(p));
                var mark = !cells.TryGetValue(p, out var cell) ? "#" : item.Key != null
                    ? cell.Flag("is_hidden") ? item.Key.ToLowerInvariant() : item.Key
                    : cell.Flag("is_hidden") ? "?" : ".";
                row.Add(mark.PadLeft(2));
            }
            lines.Add("  " + y.ToString().PadLeft(2) + "  " + string.Join(" ", row));
        }
        var finishable = new List<Item>();
        foreach (var item in items)
        {
            var label = $"  {item.Key}: {item.Name}; cleared {item.Area.Count - item.Hidden.Count}/{item.Area.Count}; ";
            if (item.Hidden.Count == 0) label += item.Good ? "COMPLETE (reward secured)" : "COMPLETE (curse already triggered)";
            else
            {
                var lower = BigClickLowerBound(item);
                var minimum = data.Flag("can_use_big_tool") || data.Text("tool") == "big" ? lower : item.Hidden.Count;
                label += $"INCOMPLETE; small needs {item.Hidden.Count} clicks; " + (lower == null ? "big unavailable"
                    : $"big needs at least {lower} (geometry lower bound, not a safe plan)");
                if (left != null && (minimum == null || minimum > left))
                    label += "; CANNOT finish with remaining divinations";
                else if (item.Good) finishable.Add(item);
                label += "; missing " + string.Join(" ", item.Hidden.OrderBy(p => p.Y).ThenBy(p => p.X).Select(p => $"({p.X},{p.Y})"));
            }
            lines.Add(label);
        }
        if (!CanDivine(data))
        {
            lines.Add(data.Flag("can_proceed") ? "  Divination finished; proceed." : "  No clicks available; waiting for rewards/proceed.");
            return;
        }
        lines.Add("  Plan: compare completions and the remaining budget, not just safe-looking fragments. Use big for efficient coverage when acceptable; small for precision near a curse. Unknown cells have unknown risk; zero known curse completions does not mean safe. Reassess after EVERY click; submit tool changes separately. Actual relic/potion/card identities are not known until rewards appear; decide potion replacement then, not while uncovering fragments.");
        lines.Add("  Candidate previews (examples, all legal centers remain available):");
        foreach (var tool in new[] { "big", "small" }.Where(t => data.Flag("can_use_" + t + "_tool") || data.Text("tool") == t))
        {
            var previews = clickable.Select(p => At(p.X, p.Y, tool)).ToList();
            // Offer both focused progress and broad exploration. These examples
            // do not prune legal actions or make a strategic choice for the model.
            var focused = previews.Where(p => p.Rewards > 0 || finishable.Any(i => i.Hidden.Any(c => Hits((p.X, p.Y), c, tool))))
                .OrderBy(p => p.Curses).ThenByDescending(p => p.Rewards)
                .ThenByDescending(p => p.GoodProgress).ThenBy(p => p.BadProgress).ThenBy(p => p.UnknownCells).Take(3);
            var exploration = previews.OrderBy(p => p.Curses).ThenBy(p => p.BadProgress).ThenByDescending(p => p.UnknownCells).Take(1);
            foreach (var preview in focused.Concat(exploration).Distinct()) lines.Add("    " + preview.Summary);
        }
    }

    internal static void ActionLines(List<string> lines, JsonArray actions)
    {
        foreach (var a in actions.Items().Where(a => a["command"].Text("action") != "crystal_sphere_click_cell"))
            lines.Add("  " + a.Text("id") + " " + a.Text("summary"));
        var clicks = actions.Items().Where(a => a["command"].Text("action") == "crystal_sphere_click_cell").ToList();
        if (clicks.Count == 0) return;
        lines.Add("  Click IDs for CURRENT tool, grouped by y (x:id). One click per decision:");
        foreach (var row in clicks.GroupBy(a => a["command"].Num("y")))
            lines.Add($"    y={row.Key}: " + string.Join(" ", row.Select(a => $"{a["command"]!["x"]}:{a.Text("id")}")));
    }
}
