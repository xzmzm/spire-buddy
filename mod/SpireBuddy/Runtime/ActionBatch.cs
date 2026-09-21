using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

internal static class ActionBatch
{
    // Maps a command to the state container holding the item it refers to. The
    // controller uses this to re-resolve queued batch members against fresh state
    // after every mutation, on any screen.
    static (JsonNode? List, string CommandKey, string ItemKey) Source(JsonNode state, string name)
    {
        return name switch
        {
            "play_card" => (state["player"]?["hand"], "card_index", "index"),
            "use_potion" or "discard_potion" => (state["player"]?["potions"], "slot", "slot"),
            "claim_reward" => (state["rewards"]?["items"], "index", "index"),
            "shop_purchase" => (state["shop"]?["items"] ?? state["fake_merchant"]?["shop"]?["items"], "index", "index"),
            "select_card_reward" => (state["card_reward"]?["cards"], "card_index", "index"),
            "select_card" => (state["card_select"]?["cards"], "index", "index"),
            "combat_select_card" => (state["hand_select"]?["cards"], "card_index", "index"),
            "select_bundle" => (state["bundle_select"]?["bundles"], "index", "index"),
            "select_relic" => (state["relic_select"]?["relics"], "index", "index"),
            "claim_treasure_relic" => (state["treasure"]?["relics"], "index", "index"),
            "choose_rest_option" => (state["rest_site"]?["options"], "index", "index"),
            "choose_event_option" => (state["event"]?["options"], "index", "index"),
            "choose_map_node" => (state["map"]?["next_options"], "index", "index"),
            _ => (null, "", ""),
        };
    }
    static JsonNode? Item(JsonNode state, JsonNode command)
    {
        var (list, commandKey, itemKey) = Source(state, command.Text("action"));
        return list.Items().FirstOrDefault(n => JsonNode.DeepEquals(n[itemKey], command[commandKey]));
    }
    static string Identity(JsonNode item, bool combatCard = false)
    {
        var obj = (JsonObject)item.DeepClone();
        // Availability changes when preceding plays spend energy; it is not card identity.
        foreach (var key in new[] { "index", "can_play", "unplayable_reason", "cost" }) obj.Remove(key);
        // Hand descriptions include live damage/block previews (for example Vigor).
        if (combatCard)
            foreach (var key in new[] { "description", "target_type", "selected", "can_select" }) obj.Remove(key);
        return GameState.Public(obj)!.WriteString();
    }
    static bool Terminal(JsonNode state, string name) => name switch
    {
        "end_turn" => GameState.Combat(state),
        "proceed" => !GameState.Combat(state),
        "close_shop" => state.Text("state_type") is "shop" or "fake_merchant",
        "confirm_selection" => state.Text("state_type") == "card_select",
        "combat_confirm_selection" => state.Text("state_type") == "hand_select",
        "confirm_bundle_selection" => state.Text("state_type") == "bundle_select",
        _ => false
    };
    internal static List<JsonNode> Select(JsonNode state, JsonArray actions, JsonNode decision)
    {
        var ids = decision["action_ids"] is JsonArray a
            ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var id) ? id : throw new InvalidOperationException("Action IDs must be strings.")).ToArray()
            : [decision.Text("action_id")];
        if (ids.Length == 0 || ids.Length > 32 || ids.Distinct().Count() != ids.Length) throw new InvalidOperationException("Invalid action batch.");
        var selected = new List<JsonNode>();
        if (state.Text("state_type") == "crystal_sphere" && ids.Length > 1)
            throw new InvalidOperationException("Crystal Sphere reveals new information. Submit one tool change or cell click, then inspect the updated board.");
        var steps = ids.Length;
        foreach (var id in ids)
        {
            var tokens = id.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) throw new InvalidOperationException("Empty action ID.");
            var action = actions.FirstOrDefault(a => a.Text("id") == tokens[0] || GameState.CardActionId(a!) == tokens[0]);
            // A bare card alias is also convenient when only one target is legal.
            if (action == null && tokens[0].StartsWith('c') && !tokens[0].Contains('@'))
            {
                var matches = actions.Where(a => GameState.CardActionId(a!).Split('@')[0] == tokens[0]).ToList();
                if (matches.Count == 1) action = matches[0];
            }
            if (action == null) throw new InvalidOperationException("Unknown or ambiguous action ID: " + tokens[0]);
            var planned = action.DeepClone();
            if (tokens.Length > 1)
            {
                if (!GameState.Combat(state) || action["command"].Text("action") != "play_card")
                    throw new InvalidOperationException("Card choices must follow a combat play.");
                var choices = new JsonArray();
                foreach (var token in tokens.Skip(1))
                {
                    var card = state["player"]?["hand"].Items().FirstOrDefault(c => GameState.CardId(c) == token);
                    if (card == null) throw new InvalidOperationException("Unknown choice card: " + token);
                    choices.Add(card.DeepClone());
                }
                planned["choices"] = choices;
                steps += choices.Count + 1; // Include the implicit confirmation.
            }
            selected.Add(planned);
        }
        if (steps > 32) throw new InvalidOperationException("Action batch exceeds 32 steps including choices and confirmation.");
        if (selected.Count == 1 && selected[0]["choices"] == null) return selected;
        // Batches are allowed on every screen; the controller still re-resolves
        // each queued member against fresh state. Only explicit hand choices can
        // bridge a screen change; unavailable or changed items stop execution.
        var combat = GameState.Combat(state);
        var used = new HashSet<string>();
        for (int i = 0; i < selected.Count; i++)
        {
            var command = selected[i]["command"]!; var name = command.Text("action");
            if (Terminal(state, name))
            { if (i != selected.Count - 1) throw new InvalidOperationException("Close/confirm/proceed/end turn must be last."); continue; }
            if (name is "end_turn" or "proceed") throw new InvalidOperationException("Unsupported batch action.");
            if (combat && name != "play_card") throw new InvalidOperationException("Combat batches only play cards; submit potions one at a time.");
            var item = Item(state, command) ?? throw new InvalidOperationException("Missing batch item.");
            if (!used.Add(name + (item["index"] ?? item["slot"])?.ToString())) throw new InvalidOperationException("Repeated batch item.");
            foreach (var choice in selected[i]["choices"].Items())
                if (!used.Add("play_card" + choice["index"])) throw new InvalidOperationException("A chosen card is already played or selected in this batch.");
            if (name == "play_card" && i != selected.Count - 1 && UncertainCard(item, selected[i]["choices"] != null)) throw new InvalidOperationException("Uncertain card must end the batch.");
        }
        return selected;
    }
    internal static JsonNode? Resolve(JsonNode initial, JsonNode planned, JsonNode current)
    {
        if (initial.Text("state_type") != current.Text("state_type")) return null;
        var command = planned["command"]!; var name = command.Text("action");
        var actions = GameState.Actions(current);
        if (Terminal(current, name)) return actions.FirstOrDefault(a => a?["command"].Text("action") == name);
        var original = Item(initial, command); if (original == null) return null;
        var resolved = actions.FirstOrDefault(a => a?["command"].Text("action") == name && JsonNode.DeepEquals(a?["command"]?["target"], command["target"]) && Item(current, a!["command"]!) is JsonNode item && Identity(item, name == "play_card") == Identity(original, name == "play_card"));
        if (resolved != null && planned["choices"] is JsonArray choices) resolved["choices"] = choices.DeepClone();
        return resolved;
    }
    internal static JsonNode? ForcedEndTurn(JsonNode before, JsonNode after, JsonNode action)
    {
        // Only finish a verified card sequence. A usable potion or any playable
        // card still needs strategic judgment, regardless of remaining energy.
        if (!GameState.Combat(after) || action["command"].Text("action") != "play_card" ||
            !CanContinue(before, after, action)) return null;
        var item = Item(before, action["command"]!);
        if (item == null || UncertainCard(item, action["choices"] != null)) return null;
        var actions = GameState.Actions(after);
        if (actions.Any(a => a?["command"].Text("action") is not ("end_turn" or "discard_potion"))) return null;
        return actions.FirstOrDefault(a => a?["command"].Text("action") == "end_turn");
    }
    static bool UncertainCard(JsonNode item, bool choices = false)
    {
        var text = (item.Text("name") + " " + item.Text("description")).ToLowerInvariant();
        return new[] { "draw", "random", "shuffle", "discover", "transform", "add a card", "add 1 card", "create", "generate" }.Any(text.Contains)
            || !choices && new[] { "choose", "select", "exhaust a card", "discard a card" }.Any(text.Contains);
    }
    internal static bool CanContinue(JsonNode before, JsonNode after, JsonNode action)
    {
        // Any screen change ends the batch; otherwise non-combat actions (reward
        // claims, purchases, selections) may continue while the screen persists.
        if (before.Text("state_type") != after.Text("state_type")) return false;
        if (action["command"]!.Text("action") != "play_card") return true;
        if (!JsonNode.DeepEquals(before["battle"]?["round"], after["battle"]?["round"]) || !JsonNode.DeepEquals(before["player"]?["draw_pile_count"], after["player"]?["draw_pile_count"])) return false;
        var played = Item(before, action["command"]!); if (played == null) return false;
        var expected = before["player"]?["hand"].Items().Select(card => Identity(card, true)).ToList() ?? [];
        if (!expected.Remove(Identity(played, true))) return false;
        foreach (var choice in action["choices"].Items())
            if (!expected.Remove(Identity(choice, true))) return false;
        var actual = after["player"]?["hand"].Items().Select(card => Identity(card, true)).ToList() ?? [];
        return expected.Order().SequenceEqual(actual.Order());
    }

    // Only bridge the known hand-selection opened by this play. All mutations
    // still come from freshly enumerated legal actions, never synthetic commands.
    internal sealed class HandChoices(JsonNode before, JsonNode play)
    {
        readonly List<JsonNode> choices = play["choices"].Items().ToList();
        int chosen;
        bool confirmed;

        internal JsonNode? Next(JsonNode current)
        {
            if (confirmed || current.Text("state_type") != "hand_select" ||
                !JsonNode.DeepEquals(before["battle"]?["round"], current["battle"]?["round"]) ||
                !JsonNode.DeepEquals(before["player"]?["draw_pile_count"], current["player"]?["draw_pile_count"])) return null;
            var data = current["hand_select"]!;
            if (data.Text("mode") is not ("" or "simple_select") ||
                data.Num("min_select") is int min && choices.Count < min ||
                data.Num("max_select") is int max && choices.Count > max) return null;
            var cards = data["cards"].Items().ToList();
            var selected = data["selected_cards"]?.Items().ToList() ?? cards.Where(c => c.Flag("selected")).ToList();
            var expected = before["player"]?["hand"].Items().Select(c => Identity(c, true)).ToList() ?? [];
            var played = Item(before, play["command"]!);
            if (played == null || !expected.Remove(Identity(played, true))) return null;
            var actual = current["player"]?["hand"].Items().Select(c => Identity(c, true)) ?? [];
            if (!expected.Order().SequenceEqual(actual.Order()) || selected.Count != chosen ||
                !choices.Take(chosen).Select(c => Identity(c, true)).Order().SequenceEqual(selected.Select(c => Identity(c, true)).Order())) return null;
            var actions = GameState.Actions(current);
            if (chosen == choices.Count)
                return actions.FirstOrDefault(a => a?["command"].Text("action") == "combat_confirm_selection");
            var matches = cards.Where(c => !c.Flag("selected") && Identity(c, true) == Identity(choices[chosen], true)).ToList();
            if (matches.Count != 1) return null; // Never guess between indistinguishable copies.
            return actions.FirstOrDefault(a => a?["command"].Text("action") == "combat_select_card" &&
                JsonNode.DeepEquals(a?["command"]?["card_index"], matches[0]["index"]));
        }

        internal void Accepted(JsonNode action)
        {
            if (action["command"].Text("action") == "combat_confirm_selection") confirmed = true;
            else chosen++;
        }

        // The game may select all eligible cards without opening the UI. Accept
        // that only when the exact planned cards disappeared and no others did.
        internal bool Complete(JsonNode current) => (chosen == 0 || chosen == choices.Count) &&
            !UncertainCard(Item(before, play["command"]!)!, true) && CanContinue(before, current, play);
    }
}
