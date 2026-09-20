using System.Text;
using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

// Renders the per-decision text brief sent to the model: a compact line-based
// report of the public snapshot plus the legal action list. The canonical JSON
// state (Public/Fingerprint/inspect_game_state) is unchanged; this layer only
// compresses what the model reads each turn. Repeated cards are aggregated,
// keyword definitions are deduplicated into a trailing glossary, and sections
// render values inline instead of repeating JSON keys for every item.
internal static partial class GameState
{
    internal static string Brief(string fingerprint, string instructions, string plan, JsonNode state, JsonArray actions, BriefMemory? memory = null)
    {
        var glossary = new SortedDictionary<string, string>();
        Harvest(state, glossary);
        var lines = new List<string>();
        void L(string line) { if (line.Length > 0) lines.Add(line); }
        L("snapshot=" + fingerprint);
        if (memory == null || memory.Changed("instructions", JsonValue.Create(instructions))) L("instructions=" + OneLine(instructions));
        L("plan=" + OneLine(plan));
        var player = state["player"];
        var run = state["run"];
        var kind = state.Text("state_type");
        if (run != null || player != null)
        {
            var parts = new List<string>();
            if (player != null && player.Text("character").Length > 0) parts.Add(player.Text("character"));
            if (run != null)
            {
                var where = $"Act {run["act"]} Floor {run["floor"]}";
                if ((run["ascension"]?.GetValue<int>() ?? 0) > 0) where += $" Asc {run["ascension"]}";
                parts.Add(where);
            }
            if (player?["hp"] != null) parts.Add($"HP {player["hp"]}/{player["max_hp"]}");
            if (player?["gold"] != null) parts.Add($"Gold {player["gold"]}");
            L("[Run] " + string.Join(" | ", parts));
        }
        if (state["battle"] is JsonNode battle) Battle(lines, battle);
        Screen(lines, kind, state[kind]);
        if (player is JsonObject po) Player(lines, po, memory, kind != "hand_select");
        if (actions.Count > 0)
        {
            L("[Actions]");
            var cards = actions.Items().Select(CardActionId).Where(id => id.Length > 0).ToList();
            if (cards.Count > 0) L("  " + (kind == "hand_select" ? "select: " : "play: ") + string.Join(" ", cards));
            foreach (var a in actions.OfType<JsonObject>().Where(a => CardActionId(a).Length == 0))
                L("  " + a.Text("id") + " " + a.Text("summary"));
        }
        var tips = glossary.Where(kv => kv.Value.Length > 0 && (memory == null || memory.Definition("keyword", kv.Key, kv.Value))).Select(kv => kv.Key + ": " + kv.Value).ToList();
        if (tips.Count > 0) L("[Keywords] " + string.Join(" | ", tips));
        return string.Join("\n", lines) + "\n";
    }

    static void Battle(List<string> lines, JsonNode battle)
    {
        var parts = new List<string> { "Round " + battle["round"] };
        var turn = battle.Text("turn");
        parts.Add(turn is "player" or "" ? "your turn" : turn + " turn");
        if (!battle.Flag("is_play_phase", true)) parts.Add("not play phase");
        lines.Add("[Combat] " + string.Join(" | ", parts));
        foreach (var e in battle["enemies"].Items().OfType<JsonObject>())
        {
            var segs = new List<string> { $"{e.Text("name")} {e["hp"]}/{e["max_hp"]}" };
            if (e.Text("enemy_id").Length > 0) segs.Add("id=" + e.Text("enemy_id"));
            if ((e["block"]?.GetValue<double>() ?? 0) > 0) segs.Add("block " + e["block"]);
            var powers = string.Join(", ", e["status"].Items().Select(PowerShort).Where(s => s.Length > 0));
            if (powers.Length > 0) segs.Add(powers);
            var intents = string.Join("; ", e["intents"].Items().Select(IntentText).Where(s => s.Length > 0));
            if (intents.Length > 0) segs.Add("intent: " + intents);
            lines.Add($"[Enemy {e["entity_id"]}] " + string.Join(" | ", segs));
        }
    }

    static void Screen(List<string> lines, string kind, JsonNode? data)
    {
        switch (kind)
        {
            case "monster" or "elite" or "boss":
                break; // rendered by Battle
            case "hand_select":
                Select(lines, "Hand select", data, true);
                break;
            case "map" when data != null:
                Map(lines, data);
                break;
            case "event":
                Event(lines, data);
                break;
            case "shop":
                Shop(lines, data);
                break;
            case "fake_merchant":
                if (data != null)
                {
                    var segs = new List<string> { data.Text("event_name") };
                    if (data.Flag("started_fight")) segs.Add("fight started");
                    if (data.Text("message").Length > 0) segs.Add(OneLine(data.Text("message")));
                    lines.Add("[Event] " + string.Join(" | ", segs.Where(s => s.Length > 0)));
                    Shop(lines, data["shop"]);
                }
                break;
            case "rewards":
                Rewards(lines, data);
                break;
            case "card_reward":
                if (data != null)
                {
                    lines.Add("[Card reward] take one" + (data.Flag("can_skip") ? " or skip" : ""));
                    foreach (var card in data["cards"].Items()) lines.Add("  " + card["index"] + ": " + CardText(card, true));
                }
                break;
            case "card_select":
                Select(lines, "Select", data);
                break;
            case "bundle_select":
                if (data != null)
                {
                    lines.Add("[Bundles] " + OneLine(data.Text("prompt")));
                    foreach (var bundle in data["bundles"].Items())
                        lines.Add("  " + bundle["index"] + ": " + string.Join("; ", bundle["cards"].Items().Select(c => CardText(c, false))));
                    Previews(lines, data);
                }
                break;
            case "relic_select":
                RelicList(lines, "[Choose a relic]", data?["relics"]);
                break;
            case "rest_site":
                if (data != null)
                {
                    lines.Add("[Rest site]");
                    foreach (var opt in data["options"].Items())
                        lines.Add("  " + opt["index"] + ": " + opt.Text("name") + (opt.Text("description").Length > 0 ? " — " + OneLine(opt.Text("description")) : "") + (opt.Flag("is_enabled", true) ? "" : " (disabled)"));
                }
                break;
            case "treasure":
                Treasure(lines, data);
                break;
            case "crystal_sphere":
                Crystal(lines, data);
                break;
            case "menu":
                Menu(lines, data);
                break;
            case "game_over" when data != null:
                lines.Add("[Game over] " + OneLine(data.Text("message")));
                break;
            default:
                // transition / overlay / unknown / future screens: keep whatever
                // scalars exist so the model never sees a fully blank screen.
                var parts = new List<string> { kind };
                parts.AddRange(new[] { "message", "room_type", "screen_type" }.Select(k => OneLine(data.Text(k))).Where(s => s.Length > 0));
                lines.Add("[Screen] " + string.Join(" | ", parts.Where(s => s.Length > 0)));
                break;
        }
    }

    static void Map(List<string> lines, JsonNode data)
    {
        var segs = new List<string>();
        var cur = data["current_position"];
        if (cur != null) segs.Add($"at c{cur["col"]},r{cur["row"]} ({cur.Text("type")})");
        var bosses = string.Join(", ", data["bosses"].Items().Select(x => x.Text("name").Length > 0 ? x.Text("name") : x.Text("id")).Where(s => s.Length > 0));
        if (bosses.Length > 0) segs.Add("boss: " + bosses);
        if (data["visited"] is JsonArray visited && visited.Count > 0) segs.Add(visited.Count + " visited");
        if (segs.Count > 0) lines.Add("[Map] " + string.Join(" | ", segs));
        foreach (var opt in data["next_options"].Items())
        {
            var leads = string.Join(", ", opt["leads_to"].Items().Select(c => $"{c.Text("type")} (c{c["col"]},r{c["row"]})"));
            lines.Add($"  {opt["index"]}: {opt.Text("type")} (c{opt["col"]},r{opt["row"]})" + (leads.Length > 0 ? " -> " + leads : ""));
        }
    }

    static void Event(List<string> lines, JsonNode? data)
    {
        if (data == null) return;
        var segs = new List<string>();
        if (data.Text("event_name").Length > 0) segs.Add(data.Text("event_name"));
        if (data.Flag("is_ancient")) segs.Add("ancient");
        if (data.Flag("in_dialogue")) segs.Add("dialogue — advance to continue");
        if (segs.Count > 0) lines.Add("[Event] " + string.Join(" | ", segs));
        var body = OneLine(data.Text("body"));
        if (body.Length > 0) lines.Add("  " + body);
        foreach (var opt in data["options"].Items())
        {
            var text = "  " + opt["index"] + ": " + opt.Text("title");
            var desc = OneLine(opt.Text("description"));
            if (desc.Length > 0) text += " — " + desc;
            if (opt.Flag("is_locked")) text += " (locked)";
            if (opt.Flag("was_chosen")) text += " (chosen)";
            var relic = opt.Text("relic_name");
            if (relic.Length > 0)
            {
                text += " [relic: " + relic;
                var relicDesc = OneLine(opt.Text("relic_description"));
                if (relicDesc.Length > 0) text += " — " + relicDesc;
                text += "]";
            }
            lines.Add(text);
        }
    }

    static void Shop(List<string> lines, JsonNode? shop)
    {
        if (shop == null) return;
        var header = shop["error"] != null ? "[Shop] " + OneLine(shop.Text("error")) : "[Shop]";
        var stocked = shop["items"].Items().Where(i => i.Flag("is_stocked", true)).ToList();
        if (stocked.Count == 0 && header == "[Shop]") return;
        lines.Add(header);
        foreach (var item in stocked)
        {
            var what = item.Text("category") switch
            {
                "card" => item.Text("card_name") + $" ({item.Text("card_cost")}/{item.Text("card_type")}/{item.Text("card_rarity")}) — " + OneLine(item.Text("card_description")),
                "relic" => item.Text("relic_name") + " — " + OneLine(item.Text("relic_description")),
                "potion" => item.Text("potion_name") + " — " + OneLine(item.Text("potion_description")),
                "card_removal" => "remove a card from your deck",
                _ => item.Text("category"),
            };
            var text = $"  {item["index"]}: {what} — {item["price"]}g";
            if (item.Flag("on_sale")) text += " (sale)";
            if (!item.Flag("can_afford", true)) text += " (cannot afford)";
            lines.Add(text);
        }
    }

    static void Rewards(List<string> lines, JsonNode? data)
    {
        if (data == null) return;
        lines.Add("[Rewards]");
        foreach (var item in data["items"].Items())
        {
            var what = item.Text("type");
            if (item["gold_amount"] != null) what = "gold " + item["gold_amount"];
            else if (item.Text("potion_name").Length > 0) what = "potion " + item.Text("potion_name") + " — " + OneLine(item.Text("potion_description"));
            else
            {
                var desc = OneLine(item.Text("description"));
                if (desc.Length > 0) what += " — " + desc;
            }
            lines.Add("  " + item["index"] + ": " + what);
        }
    }

    static void Select(List<string> lines, string header, JsonNode? data, bool hand = false)
    {
        if (data == null) return;
        var segs = new List<string>();
        if (data.Text("screen_type").Length > 0) segs.Add(data.Text("screen_type"));
        if (data.Text("mode").Length > 0) segs.Add(data.Text("mode"));
        var prompt = OneLine(data.Text("prompt"));
        if (prompt.Length > 0) segs.Add(prompt);
        if (data["selected_count"] != null) segs.Add("selected " + data["selected_count"]);
        if (data["min_select"] != null) segs.Add($"pick {data["min_select"]}-{data["max_select"]}");
        if (segs.Count > 0) lines.Add("[" + header + "] " + string.Join(" | ", segs));
        foreach (var card in data["cards"].Items()) lines.Add("  " + (hand ? CardId(card) : card["index"]?.ToString()) + ": " + CardText(card, true));
        var chosen = string.Join(", ", data["selected_cards"].Items().Select(c => c.Text("name")).Where(s => s.Length > 0));
        if (chosen.Length > 0) lines.Add("  chosen: " + chosen);
        Previews(lines, data);
    }

    static void Previews(List<string> lines, JsonNode data)
    {
        if (data.Flag("preview_showing")) foreach (var card in data["preview_cards"].Items()) lines.Add("  preview: " + CardText(card, false));
    }

    static void RelicList(List<string> lines, string header, JsonNode? relics)
    {
        if (relics == null) return;
        lines.Add(header);
        foreach (var relic in relics.Items())
            lines.Add("  " + relic["index"] + ": " + relic.Text("name") + $" ({relic.Text("rarity")}) — " + OneLine(relic.Text("description")));
    }

    static void Treasure(List<string> lines, JsonNode? data)
    {
        if (data == null) return;
        if (data.Text("message").Length > 0) { lines.Add("[Treasure] " + OneLine(data.Text("message"))); return; }
        if (data.Flag("can_open")) lines.Add("[Treasure] chest unopened");
        RelicList(lines, "[Treasure relics]", data["relics"]);
    }

    static void Crystal(List<string> lines, JsonNode? data)
    {
        if (data == null) return;
        var segs = new List<string>();
        if (data["grid_width"] != null) segs.Add($"{data["grid_width"]}x{data["grid_height"]} grid");
        var tool = data.Text("tool");
        if (tool.Length > 0 && tool != "none") segs.Add("tool: " + tool);
        var left = OneLine(data.Text("divinations_left_text"));
        if (left.Length > 0) segs.Add(left);
        var instructions = OneLine(data.Text("instructions_description"));
        if (instructions.Length > 0) segs.Add(instructions);
        if (segs.Count > 0) lines.Add("[Crystal sphere] " + string.Join(" | ", segs));
        var hidden = string.Join(" ", data["clickable_cells"].Items().Select(c => $"({c["x"]},{c["y"]})"));
        if (hidden.Length > 0) lines.Add("  hidden: " + hidden);
        foreach (var item in data["revealed_items"].Items())
            lines.Add($"  revealed: {(item.Flag("is_good") ? "good" : "bad")} {item.Text("item_type")} at ({item["x"]},{item["y"]}) {item["width"]}x{item["height"]}");
    }

    static void Menu(List<string> lines, JsonNode? state)
    {
        if (state == null) return;
        var segs = new List<string>();
        if (state.Text("menu_screen").Length > 0) segs.Add(state.Text("menu_screen"));
        if (state.Text("selected_character").Length > 0) segs.Add("selected: " + state.Text("selected_character"));
        if (segs.Count > 0) lines.Add("[Menu] " + string.Join(" | ", segs));
        var characters = string.Join("; ", state["characters"].Items().Select(c => (c.Text("name").Length > 0 ? c.Text("name") : c.Text("id")) + (c.Flag("locked") ? " (locked)" : "")));
        if (characters.Length > 0) lines.Add("  characters: " + characters);
        var options = string.Join("; ", state["options"].Items().Select(o => o is JsonValue ? o.ToString() : o.Text("name")));
        if (options.Length > 0) lines.Add("  options: " + options);
    }

    static void Player(List<string> lines, JsonObject player, BriefMemory? memory, bool showHand)
    {
        if (player["energy"] != null)
        {
            var segs = new List<string> { $"Energy {player["energy"]}/{player["max_energy"]}" };
            if ((player["block"]?.GetValue<double>() ?? 0) > 0) segs.Add("Block " + player["block"]);
            if (player["stars"] != null) segs.Add("Stars " + player["stars"]);
            var powers = string.Join(", ", player["status"].Items().Select(PowerText).Where(s => s.Length > 0));
            if (powers.Length > 0) segs.Add(powers);
            lines.Add("[You] " + string.Join(" | ", segs));
        }
        else if (player["status"] is JsonArray status && status.Count > 0)
            lines.Add("[You] " + string.Join(" | ", status.Items().Select(PowerText)));
        if (showHand && player["hand"] is JsonArray hand && hand.Count > 0)
        {
            lines.Add($"[Hand {hand.Count}]");
            foreach (var card in hand.Items()) lines.Add("  " + CardId(card) + ": " + CardText(card, false));
        }
        Pile(lines, "Draw", player["draw_pile"], player["draw_pile_count"]);
        Pile(lines, "Discard", player["discard_pile"], player["discard_pile_count"]);
        Pile(lines, "Exhaust", player["exhaust_pile"], player["exhaust_pile_count"]);
        if (player["deck"] is JsonArray deck && (memory == null || memory.Changed("deck", deck)))
        {
            var groups = deck.Items().GroupBy(c => (name: CardName(c), desc: c.Text("description"), up: c.Flag("is_upgraded")))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.name, StringComparer.Ordinal);
            lines.Add($"[Deck {deck.Count}] " + string.Join(", ", groups.Select(g =>
                (g.Count() == 1 ? "" : g.Count() + "x ") + g.Key.name + (g.Key.up && g.Key.desc.Length > 0 ? " (" + OneLine(g.Key.desc) + ")" : ""))));
        }
        if (player["orbs"] is JsonArray orbs && orbs.Count > 0)
            lines.Add("[Orbs] " + string.Join("; ", orbs.Items().Select(o => $"{o.Text("name")} ({o["passive_val"]}/{o["evoke_val"]})"))
                + $" | {player["orb_empty_slots"]}/{player["orb_slots"]} empty");
        foreach (var pet in player["pets"].Items().OfType<JsonObject>())
        {
            var segs = new List<string> { $"{pet.Text("name")} {pet["hp"]}/{pet["max_hp"]}" };
            if ((pet["block"]?.GetValue<double>() ?? 0) > 0) segs.Add("block " + pet["block"]);
            var powers = string.Join(", ", pet["status"].Items().Select(PowerShort).Where(s => s.Length > 0));
            if (powers.Length > 0) segs.Add(powers);
            lines.Add("[Pet] " + string.Join(" | ", segs));
        }
        if (player["relics"] is JsonArray relics && (memory == null || memory.Changed("relics", relics)))
        {
            lines.Add($"[Relics {relics.Count}]" + (relics.Count == 0 ? " none" : ""));
            foreach (var relic in relics.Items().OfType<JsonObject>())
            {
                var counter = relic["counter"] != null ? $" ({relic["counter"]})" : "";
                var description = OneLine(relic.Text("description"));
                var show = memory == null || memory.Definition("relic", relic.Text("id") + ":" + relic.Text("name"), description);
                lines.Add("  " + relic.Text("name") + counter + (show && description.Length > 0 ? ": " + description : ""));
            }
        }
        if (player["potions"] is JsonArray potions)
        {
            if (memory != null)
                foreach (var removed in memory.RemovedPotions(potions)) lines.Add(removed);
            // Held slot indexes alone don't reveal capacity or which slots a reward can fill.
            var capacity = player["max_potion_slots"]?.GetValue<int>() ?? potions.Count;
            var occupied = potions.Items().Select(p => p["slot"]?.GetValue<int>() ?? -1).ToHashSet();
            var free = Enumerable.Range(0, capacity).Where(i => !occupied.Contains(i)).ToList();
            if (potions.Count == 0) lines.Add($"[Potions] none ({capacity} slots)");
            else
            {
                lines.Add($"[Potions {potions.Count}/{capacity}]" + (free.Count > 0
                    ? " free slots: " + string.Join(", ", free)
                    : " all slots full"));
                foreach (var potion in potions.Items())
                {
                    var description = OneLine(potion.Text("description"));
                    var show = memory == null || memory.Definition("potion", potion.Text("id") + ":" + potion.Text("name"), description);
                    lines.Add($"[Potion {potion["slot"]}] {potion.Text("name")}" + (show && description.Length > 0 ? " — " + description : ""));
                }
            }
        }
    }

    static void Pile(List<string> lines, string label, JsonNode? pile, JsonNode? count)
    {
        if (pile == null && count == null) return;
        var items = pile.Items().ToList();
        // Same-named groups differ only by description (usually upgrades);
        // disambiguate those instead of emitting two identical labels.
        bool Shared(string name, int groupCount) => items.Count(c => c.Text("name") == name) > groupCount;
        var text = items.Count == 0
            ? "empty"
            : string.Join(", ", items.GroupBy(c => (name: c.Text("name"), desc: c.Text("description")))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.name, StringComparer.Ordinal)
                .Select(g =>
                {
                    var entry = (g.Count() == 1 ? "" : g.Count() + "x ") + g.Key.name;
                    return Shared(g.Key.name, g.Count()) && g.Key.desc.Length > 0 ? entry + " (" + Clip(OneLine(g.Key.desc), 48) + ")" : entry;
                }));
        lines.Add($"[{label} {(count != null ? count.ToString() : items.Count.ToString())}] {text}");
    }

    static string CardText(JsonNode card, bool detail)
    {
        var cost = card.Text("cost");
        if (card.Text("star_cost").Length > 0)
            cost = (cost.Length > 0 ? cost + " energy + " : "") + card.Text("star_cost") + " stars";
        var meta = new List<string>();
        if (cost.Length > 0) meta.Add(cost);
        if (card.Text("type").Length > 0) meta.Add(card.Text("type"));
        if (detail && card.Text("rarity").Length > 0) meta.Add(card.Text("rarity"));
        var head = CardName(card);
        if (meta.Count > 0) head += " (" + string.Join("/", meta) + ")";
        var parts = new List<string> { head, OneLine(card.Text("description")) };
        var keywords = string.Join(", ", card["keywords"].Items().Select(k => k.Text("name")).Where(n => n.Length > 0));
        if (keywords.Length > 0) parts.Add("[" + keywords + "]");
        if (card["unplayable_reason"] != null) parts.Add("(unplayable: " + card.Text("unplayable_reason") + ")");
        if (card.Flag("selected")) parts.Add("*selected*");
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    static string CardName(JsonNode card) => card.Text("name") + (card.Flag("is_upgraded") ? "+" : "");

    static string PowerShort(JsonNode p) => p.Text("name") + (p["amount"] != null ? " " + p["amount"] : "");

    static string PowerText(JsonNode p)
    {
        var desc = OneLine(p.Text("description"));
        var s = PowerShort(p);
        return desc.Length > 0 ? s + " — " + desc : s;
    }

    static string IntentText(JsonNode i)
    {
        var s = i.Text("type");
        var label = i.Text("label");
        if (label.Length > 0) s += " " + label;
        var title = i.Text("title");
        if (title.Length > 0 && title != label) s += " (" + title + ")";
        return s;
    }

    static string OneLine(string s) => s.Replace('\n', ' ').Replace('\r', ' ').Trim();

    static string Clip(string s, int length) => s.Length <= length ? s : s[..(length - 3)] + "...";

    // Deduplicate every {name, description} hover-tip pair in the state into one
    // glossary, so repeated keywords (Channel, Block, ...) are defined exactly once.
    static void Harvest(JsonNode? node, SortedDictionary<string, string> glossary)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "keywords" && value is JsonArray tips)
                        foreach (var tip in tips.OfType<JsonObject>())
                        {
                            var name = tip.Text("name");
                            if (name.Length > 0 && !glossary.ContainsKey(name)) glossary[name] = OneLine(tip.Text("description"));
                        }
                    else Harvest(value, glossary);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) Harvest(item, glossary);
                break;
        }
    }
}
