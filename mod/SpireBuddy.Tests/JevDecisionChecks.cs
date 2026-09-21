using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class JevDecisionChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run()
    {
        // Minimal public-state reproductions of logs 47324/47327, including the
        // potion-triggered Strength exception. These test facts and escalation,
        // not a universal assertion that any particular card is always best.
        var combat = JsonNode.Parse("""
            {"state_type":"monster","run":{"floor":8},"battle":{"turn":"player","enemies":[
              {"entity_id":"mecha","name":"Mecha Knight","hp":40,"intents":[{"type":"Attack","label":"30"}]}]},
             "player":{"hp":93,"max_hp":99,"energy":5,"block":0,
               "hand":[{"index":0,"name":"Defend","cost":1,"description":"Gain 5 Block.","can_play":true}],
               "potions":[{"slot":0,"id":"FORTIFIER","name":"Fortifier","description":"Triple your Block.","can_use_in_combat":true}],
               "relics":[{"name":"Reptile Trinket","description":"Whenever you drink a Potion, gain 3 temporary Strength."}]}}
            """)!;
        var original = combat.WriteString();
        var actions = JevContext.DescribeActions(GameState.Public(combat)!, GameState.Actions(combat));
        JsonObject Choice(string command) => new() { ["action_id"] = actions.Items().Single(a => a["command"].Text("action") == command).Text("id") };
        Check(JevContext.Incoming(combat) == 30, "visible Mecha Knight damage is explicit");
        var end = actions.Items().Single(a => a["command"].Text("action") == "end_turn")["jev_criteria"]!;
        Check(end.Num("energy_left_unused") == 5 && end.Num("current_block") == 0 && end.Num("visible_attack_damage") == 30, "end-turn criterion describes unused energy and exposure");
        Check(JevContext.ReviewReason(combat, actions, Choice("end_turn"), true).Length > 0, "avoidable exposure triggers a review even without confidence");
        Check(JevContext.ReviewReason(combat, actions, Choice("use_potion"), true).Contains("relic effects"), "zero-Block Fortifier asks about ordering while retaining trigger benefits");
        var brief = GameState.JevBrief(combat, "Win.", "", new JsonArray(), "");
        Check(brief.Contains("Block 0") && brief.Contains("not an exact HP-loss forecast") && brief.Contains("3 temporary Strength"), "brief shows zero Block and preserves exceptions to simple arithmetic");
        Check(original == combat.WriteString(), "fact extraction and option descriptions never mutate game state");
        combat["player"]!["block"] = 30;
        Check(JevContext.ReviewReason(combat, actions, Choice("end_turn"), true) == "", "no unconditional ban on ending a turn with playable cards");
        combat["battle"]!["enemies"]![0]!["intents"]![0]!["label"] = "8×2";
        Check(JevContext.Incoming(combat) == 16, "multi-hit damage is multiplied once");
        combat["battle"]!["enemies"]![0]!["intents"]![0]!["label"] = "?";
        Check(JevContext.Incoming(combat) == null, "unknown damage is never reported as zero");

        var map = JsonNode.Parse("""
            {"next_options":[{"index":0,"col":0,"row":1,"type":"Monster"}],"nodes":[
             {"col":0,"row":1,"type":"Monster","children":[[0,2],[1,2]]},
             {"col":0,"row":2,"type":"Elite","children":[[0,3]]},
             {"col":1,"row":2,"type":"Shop","children":[[0,3]]},
             {"col":0,"row":3,"type":"RestSite","children":[[0,4]]},
             {"col":0,"row":4,"type":"Elite","children":[[0,5]]},
             {"col":0,"row":5,"type":"Boss","children":[]}],"bosses":[{"name":"Visible Boss"}]}
            """)!;
        var route = JevContext.Route(map, map["next_options"]![0]!);
        Check(route.Num("nearest_rest_entries") == 3 && route.Num("nearest_shop_entries") == 2, "distances include destination and consider every reachable branch");
        Check(route.Num("elites_before_rest_or_boss_min") == 0 && route.Num("elites_before_rest_or_boss_max") == 1, "elite range distinguishes optional fights and stops at recovery");
        var reward = JsonNode.Parse("""
            {"state_type":"card_reward","player":{"deck":[
               {"name":"Defend+","is_upgraded":true,"cost":1,"description":"Gain 8 Block."},
               {"name":"Blade Dance","cost":1,"description":"Add 3 Shivs into your Hand."}]},
             "card_reward":{"can_skip":true,"cards":[{"index":0,"name":"Mirage","cost":1,"description":"Gain Block equal to enemy Poison."}]}}
            """)!;
        reward["map"] = map;
        brief = GameState.JevBrief(reward, "Win.", "", new JsonArray(), "");
        Check(brief.Contains("Visible Boss") && brief.Contains("nearest_rest_entries") && brief.Contains("Shiv generation: Blade Dance"), "reward decisions see public routes, boss and concrete support");
        Check(brief.Contains("Poison application: no matching deck text") && brief.Contains("not exhaustive") && !brief.Contains("Defend++"), "limited dependency scan is qualified and card titles are not upgraded twice");
        var choices = JevContext.DescribeActions(reward, GameState.Actions(reward));
        Check(choices.Items().Single(a => a["command"].Text("action") == "skip_card_reward")["jev_criteria"].Text("effect").Contains("keep the deck unchanged"), "skip has a concrete positive alternative to adding a card");
        Check(JevContext.Question(reward)!.Contains("missing dependencies"), "card-reward question does not speculate about future enablers");
        var tied = new JsonObject { ["action_id"] = choices[0]!.Text("id"),
            ["evaluations"] = JsonNode.Parse("""[{"answers":{"action0":{"probabilities":{"a0":0.525,"a1":0.475}}}}]""") };
        Check(JevContext.ReviewReason(reward, choices, tied, false).Contains("nearly tied"), "a narrow probability margin triggers review even when confidence is missing");
        reward["player"]!["deck"]![0]!["description"] = "获得 8 点格挡。";
        Check(!string.Join("\n", JevContext.Facts(reward)).Contains("no matching deck text"), "localized rules do not falsely imply missing synergies");

        var rest = JsonNode.Parse("""{"state_type":"rest_site","rest_site":{"upgrade_candidates":[{"name":"Prepared","cost":0,"description":"Draw 1 card.","upgrade_cost":0,"upgrade_description":"Draw 2 cards."}]}}""")!;
        brief = GameState.JevBrief(rest, "Win.", "", new JsonArray(), "");
        Check(brief.Contains("Available upgrade") && brief.Contains("Draw 1 card.") && brief.Contains("Draw 2 cards."), "rest decisions can compare actual marginal upgrade benefits before choosing Smith");
        Check(JevContext.Normalize("Gain 4[energy_icon.png]. [energy_icon.png][energy_icon.png]") == "Gain 4 Energy. 2 Energy", "explicit amounts and repeated energy icons remain readable quantities");
        Check(JevContext.Normalize("The next Attack costs 0 [energy_icon.png]. Gain 2 [ironclad_energy_icon.png].") == "The next Attack costs 0 Energy. Gain 2 Energy.", "spaced amounts, including zero, bind to their energy icon");

        var memory = new JevRunMemory();
        memory.Observe(combat);
        var aftermath = JsonNode.Parse("""{"state_type":"rewards","run":{"floor":8},"player":{"hp":63,"potions":[]}}""")!;
        memory.Observe(aftermath);
        Check(memory.LastFight.Contains("93 -> 63") && memory.LastFight.Contains("1 -> 0") && memory.LastFight.Contains("includes healing"), "actual last-fight HP and potion costs survive short action history");
        memory.Observe(JsonNode.Parse("""{"state_type":"menu"}""")!);
        Check(memory.LastFight == "", "run outcomes reset at the menu");
        Console.WriteLine("PASS Jev decision facts, route distances, card dependencies, upgrade previews, combat review triggers and run memory");
    }
}
