using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

// The Fool (v0.113.0): arrows, Melody, Dread/Blitz run modes and enemy notes.
internal static class FoolChecks
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    internal static void Run()
    {
        var state = JsonNode.Parse("""
            {"state_type":"monster","run":{"act":1,"floor":3,"ascension":3,"game_mode":"Blitz",
             "ascension_effects":["WearyTraveler","TightBelt","ToughEnemies"]},
             "player":{"character":"The Fool","hp":70,"max_hp":77,"block":0,"energy":3,"max_energy":3,
              "arrows_available":0,"arrow_slots":1,"arrows_away":1,"melody_played_this_turn":["Wah","Ki"],
              "hand":[{"id":"WAH_CHO","name":"Wah Cho","type":"Skill","cost":"1","description":"x","melody":["Wah","Cho"],"index":0}],
              "status":[]},
             "battle":{"enemies":[]}}
            """)!;
        var brief = GameState.Brief("fool", "win", "", state, new JsonArray(), new BriefMemory());
        Check(brief.Contains("Arrows 0/1 ready, 1 Impaled in enemies"), "arrow slots and impaled arrows are briefed: " + brief);
        Check(brief.Contains("Melody this turn: Wah+Ki"), "melody played this turn is briefed");
        Check(brief.Contains("Wah+Cho"), "melody tags appear on cards");
        Check(brief.Contains("Blitz mode") && brief.Contains("WearyTraveler"), "non-standard mode and active ascension effects are briefed");

        var standard = JsonNode.Parse("""{"state_type":"monster","run":{"act":1,"floor":3,"ascension":3},"player":{"character":"Ironclad","energy":3,"max_energy":3,"status":[]}}""")!;
        var plain = GameState.Brief("std", "win", "", standard, new JsonArray(), new BriefMemory());
        Check(!plain.Contains("Arrows") && !plain.Contains("Melody") && !plain.Contains(" mode"), "other characters' briefs are unchanged");

        var decompiled = new JsonObject { ["matches"] = new JsonArray(new JsonObject
            { ["decompiled_source"] = "int Hp => AscensionHelper.GetValueIfAscension(AscensionLevel.ToughEnemies, 262, 252) + (AscensionHelper.IsDread() ? 30 : 0);" }) };
        var noted = EnemyDecompiler.AddContextualNotes(decompiled);
        Check(noted["decompilation_notes"]?["AscensionHelper"] is JsonArray, "enemy rules explain ascension and Dread expressions");
        Console.WriteLine("PASS Fool arrows/melody brief, Blitz/Dread run modes, ascension decompilation note");
    }
}
