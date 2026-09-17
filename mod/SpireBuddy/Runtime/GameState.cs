using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

internal static partial class GameState
{
    internal static string Text(this JsonNode? node, string key, string fallback = "") => node?[key]?.ToString() ?? fallback;
    internal static bool Flag(this JsonNode? node, string key, bool fallback = false) => node?[key]?.GetValue<bool>() ?? fallback;
    internal static int? Num(this JsonNode? node, string key) => int.TryParse(node?[key]?.ToString(), out var value) ? value : null;
    internal static IEnumerable<JsonNode> Items(this JsonNode? node) => node is JsonArray a ? a.OfType<JsonNode>() : [];
    static readonly HashSet<string> Hidden = new("seed random_seed rng rng_state draw_order deck_order shuffle_order next_move rolled_move next_reward next_card next_card_reward next_relic next_artifact next_potion next_encounter future_rewards future_encounters".Split(' '), StringComparer.OrdinalIgnoreCase);
    internal static JsonNode? Public(JsonNode? node, string key = "")
    {
        if (node is JsonObject obj) return new JsonObject(obj.Where(p => !Hidden.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Public(p.Value, p.Key))));
        if (node is JsonArray arr)
        {
            var items = arr.Select(n => Public(n)).ToList();
            if (key is "draw_pile" or "discard_pile" or "exhaust_pile") items = items.OrderBy(n => n?.WriteString(), StringComparer.Ordinal).ToList();
            return new JsonArray(items.ToArray());
        }
        return node?.DeepClone();
    }
    // 16 hex chars (64 bits) is ample for accidental-collision protection here; the
    // short form is cheaper for the model to echo back in take_action.
    internal static string Fingerprint(JsonNode state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Public(state)!.WriteString())))[..16];
    internal static bool Combat(JsonNode s) => s.Text("state_type") is "monster" or "elite" or "boss";
    internal static bool Ready(JsonNode state, JsonNode? command = null)
    {
        // Embark disables the main buttons before the character screen disappears.
        // Repeated copies of that frame are not a completed scene transition.
        if (state.Text("state_type") == "menu" && state.Text("menu_screen") == "character_select")
        {
            if (command.Text("action") == "menu_select" && command.Text("option") is "embark" or "confirm") return false;
            if (!Actions(state).Any(a => a?["command"].Text("option") is "embark" or "confirm" or "back")) return false;
        }
        if (state.Flag("loading") || Combat(state) && state["battle"].Flag("actions_pending")) return false;
        // Potion disposal remains available during room animations. It does not
        // mean the room is ready: wait for a real choice or an enabled proceed
        // button before publishing a settled snapshot (or auto-navigation).
        return HasSuitableActions(Actions(state)) || state.Text("state_type") == "game_over";
    }
    internal static bool HasSuitableActions(JsonArray actions) =>
        actions.Any(a => a?["command"].Text("action") is string action && action.Length > 0 && action != "discard_potion");

    internal static string CardId(JsonNode card) => card.Num("index") is int index ? "c" + (index + 1) : "";
    internal static string CardActionId(JsonNode action)
    {
        var command = action["command"];
        if (command.Text("action") is not ("play_card" or "combat_select_card")) return "";
        if (command.Num("card_index") is not int index) return "";
        return "c" + (index + 1) + (command?["target"] is JsonNode target ? "@" + target : "");
    }

    internal static JsonArray Actions(JsonNode s)
    {
        var result = new JsonArray();
        void Add(string action, JsonNode? item = null, string? argument = null, JsonNode? value = null)
        {
            var command = new JsonObject { ["action"] = action };
            if (argument != null) command[argument] = value?.DeepClone() ?? item?["index"]?.DeepClone();
            if (argument != null && command[argument] == null) return;
            result.Add(new JsonObject { ["id"] = "a" + result.Count, ["command"] = command, ["summary"] = Summary(action, item, value) });
        }
        void At(JsonNode target)
        {
            var last = result.Last()!;
            last["command"]!["target"] = target.DeepClone();
            last["summary"] = last.Text("summary") + " @" + target;
        }
        var kind = s.Text("state_type"); var data = s[kind];
        if (Combat(s) && s["battle"].Flag("actions_pending")) return result;
        if (s["legal_actions"] is JsonArray supplied) return (JsonArray)supplied.DeepClone();
        switch (kind)
        {
            case "menu":
                foreach (var option in s["options"].Items())
                {
                    var name = option is JsonValue ? option.ToString() : option.Text("name");
                    if (name.Length > 0 && name.ToLowerInvariant() is not ("quit" or "abandon") && (option is JsonValue || option.Flag("enabled", true))) Add("menu_select", argument: "option", value: JsonValue.Create(name));
                }
                break;
            case "monster": case "elite": case "boss":
                if (s["battle"].Text("turn", "player") != "player" || !s["battle"].Flag("is_play_phase", true)) break;
                foreach (var card in s["player"]?["hand"].Items() ?? [])
                {
                    if (!card.Flag("can_play", true) || card["index"] == null) continue;
                    var target = card.Text("target_type").ToLowerInvariant();
                    if (target.Contains("enemy") && !target.Contains("all"))
                    {
                        foreach (var enemy in s["battle"]?["enemies"].Items() ?? [])
                            if ((enemy["hp"]?.GetValue<double>() ?? 0) > 0 && enemy["entity_id"] != null)
                            { Add("play_card", card, "card_index"); At(enemy["entity_id"]!); }
                    }
                    else Add("play_card", card, "card_index");
                }
                Potions(true); if (s["battle"].Flag("can_end_turn", true)) Add("end_turn"); break;
            case "event":
                if (data.Flag("in_dialogue")) Add("advance_dialogue");
                else foreach (var item in data?["options"].Items() ?? []) if (!item.Flag("is_locked")) Add("choose_event_option", item, "index");
                break;
            case "shop": case "fake_merchant":
                var shop = kind == "shop" ? data : data?["shop"];
                foreach (var item in shop?["items"].Items() ?? []) if (item.Flag("is_stocked", true) && item.Flag("can_afford")) Add("shop_purchase", item, "index");
                if (shop.Flag("can_open")) Add("open_shop");
                if (shop.Flag("can_close")) Add("close_shop");
                if (shop.Flag("can_proceed")) Add("proceed"); break;
            case "map": foreach (var item in data?["next_options"].Items() ?? []) Add("choose_map_node", item, "index"); break;
            case "rewards": Rows("items", "claim_reward"); Proceed(); break;
            case "card_reward": Rows("cards", "select_card_reward", "card_index"); If("can_skip", "skip_card_reward"); break;
            case "rest_site":
                foreach (var item in data?["options"].Items() ?? []) if (item.Flag("is_enabled", true)) Add("choose_rest_option", item, "index");
                Proceed(); break;
            case "treasure": If("can_open", "open_chest"); Rows("relics", "claim_treasure_relic"); Proceed(); break;
            case "hand_select":
                foreach (var card in data?["cards"].Items() ?? [])
                    if (card.Flag("can_select", true)) Add("combat_select_card", card, "card_index");
                If("can_confirm", "combat_confirm_selection"); break;
            case "card_select":
                if (!data.Flag("preview_showing")) Rows("cards", "select_card");
                If("can_confirm", "confirm_selection"); if (data.Flag("can_cancel") || data.Flag("can_skip")) Add("cancel_selection"); break;
            case "bundle_select": Rows("bundles", "select_bundle"); If("can_confirm", "confirm_bundle_selection"); If("can_cancel", "cancel_bundle_selection"); break;
            case "relic_select": Rows("relics", "select_relic"); If("can_skip", "skip_relic_selection"); break;
            case "crystal_sphere":
                foreach (var tool in new[] { "big", "small" }) if (data.Flag("can_use_" + tool + "_tool") && data.Text("tool") != tool) Add("crystal_sphere_set_tool", argument: "tool", value: JsonValue.Create(tool));
                foreach (var cell in data?["clickable_cells"].Items() ?? []) if (cell["x"] != null && cell["y"] != null) { Add("crystal_sphere_click_cell", argument: "x", value: cell["x"]); result.Last()!["command"]!["y"] = cell["y"]!.DeepClone(); result.Last()!["summary"] = result.Last().Text("summary") + "," + cell["y"]; }
                If("can_proceed", "crystal_sphere_proceed"); break;
        }
        if (kind is not ("menu" or "game_over" or "unknown" or "overlay" or "transition" or "unsupported") && !Combat(s)) Potions(false);
        return result;
        void Rows(string field, string action, string argument = "index") { foreach (var item in data?[field].Items() ?? []) Add(action, item, argument); }
        void If(string flag, string action) { if (data.Flag(flag)) Add(action); }
        void Proceed() => If("can_proceed", "proceed");
        void Potions(bool use)
        {
            foreach (var p in s["player"]?["potions"].Items() ?? [])
            {
                if (p["slot"] == null) continue;
                var target = p.Text("target_type").ToLowerInvariant();
                if (use && p.Flag("can_use_in_combat", true))
                {
                    if (target.Contains("enemy") && !target.Contains("all"))
                    { foreach (var e in s["battle"]?["enemies"].Items() ?? []) if ((e["hp"]?.GetValue<double>() ?? 0) > 0 && e["entity_id"] != null) { Add("use_potion", p, "slot", p["slot"]); At(e["entity_id"]!); } }
                    else if (!target.Contains("enemy") || (s["battle"]?["enemies"].Items() ?? []).Any(e => (e["hp"]?.GetValue<double>() ?? 0) > 0)) Add("use_potion", p, "slot", p["slot"]);
                }
                Add("discard_potion", p, "slot", p["slot"]);
            }
        }
    }

    /// <summary>
    /// Returns a non-strategic action that the controller can execute without a
    /// model round trip. Optional potion discards never make a screen strategic.
    /// </summary>
    internal static JsonNode? ForcedAction(JsonNode state, JsonArray actions, bool merchantOpened = false)
    {
        if (actions.Count == 0) return null;

        var kind = state.Text("state_type");
        if (kind == "map" && HasActiveWingedBoots(state)) return null;

        JsonNode? First(string name) => actions.FirstOrDefault(a => a?["command"].Text("action") == name);
        bool Has(string name) => actions.Any(a => a?["command"].Text("action") == name);
        bool IsDiscard(JsonNode? action) => action?["command"].Text("action") == "discard_potion";

        // Entering a merchant is a UI handshake, not a shop decision. Prefer the
        // merchant button even when the room's underlying proceed button is also
        // briefly exposed during the transition.
        if (kind is "shop" or "fake_merchant")
        {
            if (!merchantOpened && First("open_shop") is JsonNode open) return open;

            // Once every item is sold or unaffordable, closing the inventory is
            // deterministic. The following room transition is handled on the next
            // loop by the same routine-action path. After the inventory has been
            // opened once, proceed when it is already closed instead of reopening
            // the merchant indefinitely.
            if (!Has("shop_purchase"))
            {
                if (First("close_shop") is JsonNode close) return close;
                if (merchantOpened && First("proceed") is JsonNode proceed) return proceed;
            }
        }

        // A completed selection preview (card transform/upgrade/removal, bundle)
        // only needs the mechanical confirm: the model already chose, and the
        // preview shows either deterministic results or an undecided random
        // transform, so no new judgment can change the answer.
        if (kind is "card_select" or "bundle_select" && state[kind]?.Flag("preview_showing") == true
            && First(kind == "card_select" ? "confirm_selection" : "confirm_bundle_selection") is JsonNode previewConfirm)
            return previewConfirm;

        // A single non-discard action is forced when potion disposal is the only
        // alternative. Keep purchases and reward/card choices model-owned even if
        // a malformed or unusual screen exposes only one of them.
        var nonDiscard = actions.OfType<JsonNode>().Where(a => !IsDiscard(a)).ToList();
        if (nonDiscard.Count == 1)
        {
            var name = nonDiscard[0]["command"].Text("action");
            if (name is "proceed" or "open_shop" or "close_shop" or "advance_dialogue" or "crystal_sphere_proceed")
                return nonDiscard[0];
            if (kind == "map" && name == "choose_map_node")
                return nonDiscard[0];
            if (actions.Count == 1 && kind is not ("monster" or "elite" or "boss" or "hand_select") && name is not ("shop_purchase" or "claim_reward" or "select_card_reward" or "claim_treasure_relic" or "select_relic"))
                return nonDiscard[0];
        }

        return null;
    }

    internal static bool HasActiveWingedBoots(JsonNode state)
    {
        IEnumerable<JsonNode> relics()
        {
            foreach (var relic in state["player"]?["relics"]?.Items() ?? []) yield return relic;
            foreach (var relic in state["relics"]?.Items() ?? []) yield return relic;
        }

        foreach (var relic in relics())
        {
            // Model ids are registry data and do not change with the game's
            // display language. Names and descriptions are localized, so they
            // must never decide whether map travel is strategic.
            if (!string.Equals(relic.Text("id"), "WINGED_BOOTS", StringComparison.OrdinalIgnoreCase))
                continue;

            // Winged Boots carries a visible number of uses. Treat an explicit
            // zero as exhausted; absent counters keep the relic active for older
            // snapshots and test fixtures that only expose its identity.
            if (relic["counter"] is JsonValue counter && int.TryParse(counter.ToString(), out var remaining) && remaining <= 0)
                continue;
            return true;
        }
        return false;
    }

    // Action summaries stay short: the item itself is already rendered in the brief's
    // sections, so only verb, identifying name/slot and target are repeated here.
    internal static string Summary(string action, JsonNode? item, JsonNode? value)
    {
        var text = new StringBuilder(Verb(action));
        var name = new[] { "name", "title", "card_name", "relic_name", "potion_name", "type" }.Select(k => item.Text(k)).FirstOrDefault(n => n.Length > 0) ?? value?.ToString();
        if (!string.IsNullOrEmpty(name)) text.Append(' ').Append(name);
        var slot = item?["index"] ?? item?["slot"];
        if (slot != null) text.Append('[').Append(slot).Append(']');
        if (item?["price"] != null) text.Append('(').Append(item["price"]).Append('g').Append(')');
        return text.ToString();
    }
    static string Verb(string action) => action switch
    {
        "play_card" => "play",
        "use_potion" => "use",
        "choose_map_node" => "map",
        "choose_event_option" => "choose",
        "choose_rest_option" => "rest",
        "shop_purchase" => "buy",
        "claim_reward" => "claim",
        "claim_treasure_relic" => "take",
        "select_card_reward" => "take",
        "select_card" => "select",
        "combat_select_card" => "select",
        "select_bundle" => "select",
        "select_relic" => "take",
        _ => action,
    };
}
