using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class JevCombatChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static JsonNode Effigy(int slow = 0, int energy = 3) => JsonNode.Parse($$$"""
        {"state_type":"elite","run":{"floor":8},"player":{"hp":3,"max_hp":87,"energy":{{{energy}}},"block":0,"status":[],
          "hand":[{"index":0,"name":"Defend","type":"Skill","cost":"1","can_play":true,"description":"Gain 5 Block."},
            {"index":1,"name":"Strike","type":"Attack","target_type":"AnyEnemy","cost":"1","can_play":true,"description":"Deal 6 damage.",
              "lethal_profile":{"base_damage":6,"hits":1,"energy_cost":1,"star_cost":0,"vulnerable":0,"free_attack":0},
              "target_previews":[{"target":"1","description":"Deal 9 damage.","damage_values":{"Damage":9}}]},
            {"index":2,"name":"Strike","type":"Attack","target_type":"AnyEnemy","cost":"1","can_play":true,"description":"Deal 6 damage.",
              "lethal_profile":{"base_damage":6,"hits":1,"energy_cost":1,"star_cost":0,"vulnerable":0,"free_attack":0},
              "target_previews":[{"target":"1","description":"Deal 9 damage.","damage_values":{"Damage":9}}]}],
          "relics":[{"id":"BURNING_BLOOD"},{"id":"PHIAL_HOLSTER"}],
          "potions":[{"slot":0,"name":"Dexterity Potion","can_use_in_combat":true}]},
         "battle":{"round":8,"turn":"player","is_play_phase":true,"can_end_turn":true,"lethal_check_supported":true,"enemies":[
           {"entity_id":"1","enemy_id":"BYGONE_EFFIGY","name":"Bygone Effigy","hp":18,"max_hp":132,"block":0,
            "status":[{"id":"VULNERABLE_POWER","amount":1},{"id":"SLOW_POWER","amount":{{{slow}}}},{"id":"STRENGTH_POWER","amount":10}],
            "intents":[{"type":"Attack","label":"25"}]}]}}
        """)!;
    static JsonObject? Find(JsonNode state) => JevCombat.Lethal(state, GameState.Actions(state));
    static JsonNode Enemy(JsonNode state) => state["battle"]!["enemies"]![0]!;
    static JsonNode Card(JsonNode state, int i) => state["player"]!["hand"]![i + 1]!;
    static void Power(JsonNode node, string id, int amount) => node["status"]!.AsArray().Add(new JsonObject { ["id"] = id, ["amount"] = amount });
    static void Preview(JsonNode card, int amount) => card["target_previews"]![0]!["damage_values"]!["Damage"] = amount;
    static void Change(JsonNode card, int damage, int cost = 1, int hits = 1, int vulnerable = 0, int free = 0)
    {
        card["cost"] = cost.ToString();
        var p = card["lethal_profile"]!;
        p["base_damage"] = damage; p["energy_cost"] = cost; p["hits"] = hits; p["vulnerable"] = vulnerable; p["free_attack"] = free;
        Preview(card, damage);
    }
    static JsonNode Plain(int hp, int energy = 3)
    {
        var state = Effigy(energy: energy); Enemy(state)["hp"] = hp; Enemy(state)["status"] = new JsonArray();
        Preview(Card(state, 0), 6); Preview(Card(state, 1), 6);
        return state;
    }

    internal static void Run()
    {
        // Reconstructed public positions from proxy logs 47705 and 47707. Native
        // preview/profile fields come from the audited Strike/Slow/Vulnerable rules.
        foreach (var slow in new[] { 0, 10 })
        {
            var state = Effigy(slow, slow == 0 ? 3 : 2); var before = state.WriteString();
            var proof = Find(state)!;
            Check(proof != null && proof["steps"]!.AsArray().Count == 2 && proof["steps"]![0].Num("card_index") == 1, "lethal starts with Strike, not Defend or a potion");
            Check(proof!["steps"]![0].Num("damage") == 9 && proof["steps"]![1].Num("damage") == (slow == 0 ? 9 : 10), "Vulnerable/Slow round down per attack and update after the card");
            Check(proof["steps"]![1].Num("enemy_hp_after") == 0 && before == state.WriteString(), "proof kills without mutating the public snapshot");
            var described = JevContext.DescribeActions(state, GameState.Actions(state));
            Check(described.Items().First(a => a["command"].Num("card_index") == 1)["jev_criteria"]?["target_preview"]?["damage_values"].Num("Damage") == 9, "native target damage is exposed beside the legal action");
            state["map"] = JsonNode.Parse("""{"next_options":[{"index":0,"col":0,"row":9,"type":"RestSite"}],"nodes":[{"col":0,"row":9,"type":"RestSite","children":[]}]}""");
            var brief = GameState.JevBrief(state, "Win.", "", new JsonArray(), "");
            Check(!brief.Contains("[Route]") && !brief.Contains("[Path") && !brief.Contains("[Deck facts]"), "combat questions omit route-search and deck-building arithmetic");
        }
        var s = Effigy(energy: 1);
        Check(Find(s) == null, "one energy cannot buy two Strikes");
        s = Effigy(); s["battle"]!["lethal_check_supported"] = false;
        Check(Find(s) == null, "unmodelled hooks cannot produce a guaranteed lethal");
        s["battle"]!.AsObject().Remove("lethal_check_supported");
        Check(Find(s) == null, "legacy snapshots do not silently enable the proof");
        s = Effigy(); Preview(Card(s, 0), 6); Preview(Card(s, 1), 6);
        Check(Find(s) == null, "stale or discrepant native previews fail closed");
        foreach (var power in new[] { "THORNS_POWER", "INTANGIBLE_POWER", "MALLEABLE_POWER", "ARTIFACT_POWER", "REBIRTH_POWER" })
        {
            s = Effigy(); Power(Enemy(s), power, 1);
            Check(Find(s) == null, "unknown damage/death/debuff rule requires normal decision: " + power);
        }
        s = Plain(20); Power(s["player"]!, "VIGOR_POWER", 5);
        Preview(Card(s, 0), 11); Preview(Card(s, 1), 11);
        Check(Find(s) == null, "Vigor is not counted on both attacks");
        Enemy(s)["hp"] = 17;
        Check(Find(s)?["steps"]![1].Num("damage") == 6, "Vigor is consumed after the first attack");
        s = Plain(12, 0); Power(s["player"]!, "FREE_ATTACK_POWER", 1);
        Card(s, 0)["cost"] = "0"; Card(s, 1)["cost"] = "0";
        Check(Find(s) == null, "one free attack does not make the whole hand free");
        s["player"]!["energy"] = 1;
        Check(Find(s)?["steps"]![1].Num("energy_cost") == 1, "later attack reverts to its actual local cost");
        s = Plain(22, 2); Change(Card(s, 0), 14, 2, free: 1); Change(Card(s, 1), 8, 2, vulnerable: 2);
        Check(Find(s)?["steps"]![1].Num("energy_cost") == 0, "Unrelenting can pay for the next costly attack");
        s = Plain(26, 2); Change(Card(s, 0), 14, 2, free: 1); Change(Card(s, 1), 12, 3);
        Card(s, 1)["can_play"] = false; Card(s, 1)["unplayable_reason"] = "EnergyCostTooHigh";
        Check(Find(s)?["steps"]![1].Num("energy_cost") == 0, "a currently unaffordable attack can become a later free play");
        s = Plain(17); Change(Card(s, 1), 8, 2, vulnerable: 2);
        Check(Find(s)?["steps"]![0].Num("card_index") == 2, "search chooses Bash before Strike when only that order is lethal");
        s = Plain(22, 1); Change(Card(s, 0), 10, 1, 2); Card(s, 1)["can_play"] = false;
        Power(Enemy(s), "SLOW_POWER", 0);
        Check(Find(s) == null, "Slow must not increase between hits of one card");
        Enemy(s)["hp"] = 18; Enemy(s)["block"] = 3;
        Check(Find(s) == null, "Block is consumed across hits, not ignored or reset");
        Enemy(s)["hp"] = 17;
        Check(Find(s)?["steps"]![0].Num("hp_damage") == 17, "multi-hit damage removes Block once");
        s = Plain(12); Power(s["player"]!, "WEAK_POWER", 1); Preview(Card(s, 0), 4); Preview(Card(s, 1), 4);
        Check(Find(s) == null, "Weak and integer damage rounding prevent false lethal");
        s = Plain(12); Card(s, 1)["lethal_profile"]!["star_cost"] = 1;
        Check(Find(s) == null, "star cost is a real resource constraint");
        s = Plain(12); Card(s, 1)["can_play"] = false; Card(s, 1)["unplayable_reason"] = "BlockedByHook";
        Check(Find(s) == null, "blocked cards cannot be a future step");
        s = Plain(12); s["battle"]!["enemies"]!.AsArray().Add(Enemy(s).DeepClone());
        Check(Find(s) == null, "ambiguous target identities are rejected");
        s = Plain(6, 1); var other = Enemy(s).DeepClone(); other["entity_id"] = "2"; other["hp"] = 40;
        s["battle"]!["enemies"]!.AsArray().Add(other);
        foreach (var card in new[] { Card(s, 0), Card(s, 1) }) card["target_previews"]!.AsArray().Add(JsonNode.Parse("""{"target":"2","damage_values":{"Damage":6}}"""));
        Check(Find(s) == null, "killing only one of several enemies is not a combat lethal");
        other = s["battle"]!["enemies"]![1]!; other["hp"] = 6; s["player"]!["energy"] = 2;
        var multi = Find(s)!;
        Check(multi["steps"]![0].Text("target") != multi["steps"]![1].Text("target"), "search allocates attacks across all living targets");
        s = Plain(6, 1); var legal = GameState.Actions(s);
        legal = new JsonArray(legal.Items().Where(a => a["command"].Text("action") != "play_card").Select(a => a.DeepClone()).ToArray());
        Check(JevCombat.Lethal(s, legal) == null, "proof never invents a currently unavailable first action");
        s = Plain(12); Power(s["player"]!, "STRENGTH_POWER", int.MaxValue);
        Preview(Card(s, 0), 999999999); Preview(Card(s, 1), 999999999);
        Check(Find(s) == null, "extreme damage values decline the proof without overflowing");
        Console.WriteLine("PASS missed-lethal replays, attack ordering, native preview agreement, resource consumption, multiple targets and unsupported effects");
    }
}
