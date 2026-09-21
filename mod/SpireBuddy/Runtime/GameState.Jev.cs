using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

internal static partial class GameState
{
    // No BriefMemory: Jev cannot retrieve details later or recall earlier calls.
    // Start with the compact brief, then add rules normally available via tools.
    // Choices live only in the API criteria, not in a duplicate action listing.
    internal static string JevBrief(JsonNode raw, string instructions, string plan, JsonArray recent, string feedback, JevStrategy? strategy = null)
    {
        var state = Public(raw)!;
        var fingerprint = Fingerprint(state);
        strategy?.FilterRewards(state);
        if (JevStrategy.Shop(state) is JsonNode shop)
            shop["items"] = new JsonArray(shop["items"].Items().Where(i => JevStrategy.Affordable(state, i)).Select(i => i.DeepClone()).ToArray());
        var kind = state.Text("state_type");
        var combat = Combat(state) || state.Flag("in_combat") || kind == "hand_select";
        var brief = Brief(fingerprint, instructions, plan, state, new JsonArray());
        var lines = new List<string> { "screen=" + kind, brief.TrimEnd() };
        if (kind == "menu" && state[kind] == null) Menu(lines, state);
        foreach (var card in state[kind]?["cards"].Items() ?? [])
            if (card.Text("upgrade_description").Length > 0)
                lines.Add($"[Upgrade {card["index"]}] " + (card["upgrade_cost"] == null ? "" : "energy=" + card["upgrade_cost"] + " ")
                    + (card["upgrade_star_cost"] == null ? "" : "stars=" + card["upgrade_star_cost"] + " ") + OneLine(card.Text("upgrade_description")));
        // Keep a unique rule for every held card variant, including generated
        // cards outside the permanent deck. Hand previews already have full text.
        string RuleKey(JsonNode c) => string.Join('|', CardName(c), c.Text("type"), c.Text("cost"), c.Text("star_cost"), c.Text("description"),
            string.Join(';', c["keywords"].Items().Select(k => k.Text("name")).Order(StringComparer.Ordinal)));
        var shown = new HashSet<string>(state["player"]?["hand"].Items().Select(RuleKey) ?? []);
        foreach (var pile in new[] { "deck", "draw_pile", "discard_pile", "exhaust_pile" })
            foreach (var card in state["player"]?[pile].Items() ?? [])
            {
                var rule = CardText(card, false);
                if (shown.Add(RuleKey(card))) lines.Add("[Card rule] " + rule + (pile == "deck" ? " (permanent deck)" : " (combat pile preview)"));
            }
        foreach (var enemy in state["battle"]?["enemies"].Items() ?? [])
            foreach (var power in enemy["status"].Items())
                if (power.Text("description").Length > 0) lines.Add($"[Enemy {enemy["entity_id"]} effect] " + PowerText(power));
        foreach (var pet in state["player"]?["pets"].Items() ?? [])
            foreach (var power in pet["status"].Items())
                if (power.Text("description").Length > 0) lines.Add("[Pet effect] " + pet.Text("name") + " " + PowerText(power));
        foreach (var orb in state["player"]?["orbs"].Items() ?? [])
        {
            var rule = orb.Text("name") + ": " + OneLine(orb.Text("description"));
            if (shown.Add(rule)) lines.Add("[Orb rule] " + rule);
        }
        if (!combat && state["map"] is JsonNode map)
        {
            if (kind != "map") Map(lines, map);
            // Visible future topology is public. Omit only nodes behind the
            // player or outside all currently offered paths, never future rolls.
            var nodes = map["nodes"].Items().GroupBy(MapKey).ToDictionary(g => g.Key, g => g.First());
            var reachable = new HashSet<string>();
            var queue = new Queue<string>(map["next_options"].Items().Select(MapKey));
            while (queue.TryDequeue(out var key))
            {
                if (!reachable.Add(key) || !nodes.TryGetValue(key, out var node)) continue;
                foreach (var child in node["children"].Items()) queue.Enqueue(MapKey(child));
            }
            var currentRow = map["current_position"].Num("row") ?? -1;
            foreach (var node in nodes.Values.OrderBy(n => n.Num("row")).ThenBy(n => n.Num("col")))
                if (reachable.Count > 0 ? reachable.Contains(MapKey(node)) : (node.Num("row") ?? 0) > currentRow)
                    lines.Add("[Route] " + MapKey(node) + " " + node.Text("type") + " -> " + string.Join(" ", node["children"].Items().Select(MapKey)));
        }
        // Commands are already observed outcomes, not future actions to execute.
        foreach (var outcome in recent.Items().TakeLast(4))
            lines.Add("[Recent] " + OneLine(outcome.Text("summary"))
                + (outcome["hp_before"] != null && !JsonNode.DeepEquals(outcome["hp_before"], outcome["hp_after"]) ? $"; HP {outcome["hp_before"]} -> {outcome["hp_after"]}" : "")
                + (outcome["gold_before"] != null && !JsonNode.DeepEquals(outcome["gold_before"], outcome["gold_after"]) ? $"; gold {outcome["gold_before"]} -> {outcome["gold_after"]}" : ""));
        lines.AddRange(JevContext.Facts(state));
        if (feedback.Length > 0) lines.Add("[Feedback] " + OneLine(feedback));
        return JevContext.Normalize(string.Join("\n", lines));
    }

    static string MapKey(JsonNode node) => node is JsonArray coords
        ? $"{coords[0]},{coords[1]}" : $"{node["col"]},{node["row"]}";
}
