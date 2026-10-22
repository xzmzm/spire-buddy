using System.Text;
using System.Text.Json.Nodes;
using SpireBuddy.Runtime;

internal static class CompactStateChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static void Run()
    {
        var nodes = new JsonArray();
        for (int row = 0; row < 16; row++)
            for (int col = 0; col < 4; col++)
                nodes.Add(new JsonObject { ["col"] = col, ["row"] = row, ["type"] = row % 4 == 0 ? "Unknown" : "Monster",
                    ["children"] = new JsonArray(new JsonArray(col, row + 1), new JsonArray((col + 1) % 4, row + 1)) });
        nodes.Add(new JsonObject { ["col"] = 0, ["row"] = 16, ["type"] = "Boss", ["children"] = new JsonArray(),
            ["id"] = "BOSS", ["name"] = "瀑布巨兽" });
        var nodeText = RoundTrip(nodes);
        Check(Encoding.UTF8.GetByteCount(nodeText) < Encoding.UTF8.GetByteCount(nodes.WriteString()) * .65, "map table must materially reduce repeated JSON overhead, including its legend");

        var card = JsonNode.Parse("""
            {"id":"DEFEND","name":"防御","description":"Gain 5 Block.\nLiteral ,~ and \"quotes\".","cost":"1","star_cost":null,
             "is_upgraded":false,"keywords":[{"name":"Block","description":"Prevents damage."}],"optional":null,"":false,"odd,key":"~"}
            """)!;
        var cards = new JsonArray(Enumerable.Range(0, 5).Select(_ => card.DeepClone()).ToArray());
        var upgrade = card.DeepClone(); upgrade["is_upgraded"] = true; upgrade["description"] = "Gain 8 Block.";
        cards.Add(upgrade);
        var cheaper = card.DeepClone(); cheaper["cost"] = "0"; cards.Add(cheaper);
        var absent = card.DeepClone(); absent.AsObject().Remove("optional"); cards.Add(absent);
        // Nonadjacent copies stay in their original positions, including hands
        // and selections: this serializer does not invent interchangeable IDs.
        cards.Add(card.DeepClone());
        var cardText = RoundTrip(cards);
        Check(cardText.Contains("*5 [") && cardText.Contains("~,"), "exact duplicates repeat, while absent fields remain distinct from null");
        Check(cardText.Contains("false") && cardText.Contains("null") && cardText.Contains("\"odd,key\""), "false, null and quoted column names survive compaction");

        var state = new JsonObject { ["game"] = new JsonObject { ["state_type"] = "event",
            ["map"] = new JsonObject { ["nodes"] = nodes.DeepClone() },
            ["player"] = new JsonObject { ["hp"] = 42, ["gold"] = 99, ["deck"] = cards.DeepClone() } },
            ["agents"] = new JsonObject { ["combat_solver"] = false, ["game"] = null } };
        var before = state.WriteString();
        var fingerprint = GameState.Fingerprint(state);
        var session = new AgentSession("test"); session.Restart("test"); session.State(state);
        var full = session.History.Last()!.Text("content");
        Check(full.Contains("nodes[65]") && full.Contains("deck[9]") && full.Contains("*5 ["), "Buddy full-state anchors use compact map and deck tables");
        Check(state.WriteString() == before && GameState.Fingerprint(state) == fingerprint, "compaction does not mutate snapshots or action fingerprints");
        session.State(state);
        Check(session.History.Count == 2, "unchanged compact state adds nothing");
        state["game"]!["player"]!["hp"] = 40;
        state["game"]!["player"]!.AsObject().Remove("gold");
        session.State(state);
        var diff = session.History.Last()!.Text("content");
        Check(diff.StartsWith("Public state changes") && diff.Contains("/game/player/hp") && diff.Contains("/game/player/gold") && diff.Contains("remove"), "deltas retain original JSON paths and explicit deletions");
        Check(!diff.Contains("nodes[") && !diff.Contains("DEFEND"), "small updates do not resend tables");
        state["game"]!["player"]!["deck"]!.AsArray().Add(upgrade.DeepClone());
        session.State(state);
        var deckDiff = session.History.Last()!.Text("content");
        Check(deckDiff.StartsWith("Public state changes") && deckDiff.Contains("/game/player/deck")
            && deckDiff.Contains("value[10]") && deckDiff.Contains("*5 [") && !deckDiff.Contains("nodes["),
            "a changed deck is compact inside a set operation, without resending the map");
        session.State(state, force: true);
        Check(session.History.Last()!.Text("content").Contains("nodes[65]"), "forced re-anchors restore compact state in full");
        session.Restart("test"); session.State(state);
        Check(session.History.Last()!.Text("content").Contains("Compact tables:"), "fresh histories include the format legend and all definitions");

        var small = JsonNode.Parse("""{"state_type":"menu","options":["continue",null,1,false,{}]}""")!;
        Check(CompactState.Write(small) == small.WriteString(), "small and mixed payloads keep ordinary JSON when it is shorter");
        var emptyKey = new JsonObject { [""] = nodes.DeepClone() };
        Check(CompactState.Write(emptyKey).Contains("\"\"[65]"), "empty property names cannot be confused with the root");
        RouteHistory();
        Console.WriteLine("PASS compact state lossless map/card tables, variants, sparse fields, diffs, restart and route history");
    }

    static void RouteHistory()
    {
        var state = JsonNode.Parse("""
            {"state_type":"event","event":{"event_name":"Test"},"map":{
             "current_position":{"col":4,"row":2},"next_options":[{"index":0,"col":4,"row":3,"type":"Unknown"}],
             "nodes":[{"col":4,"row":3,"type":"Unknown","children":[[5,4],[6,5]]},
                      {"col":5,"row":4,"type":"RestSite","children":[]},
                      {"col":6,"row":5,"type":"Boss","children":[]},
                      {"col":0,"row":8,"type":"Unreachable","children":[]}]}}
            """)!;
        var memory = new BriefMemory();
        string Brief(BriefMemory? m) => GameState.Brief("test", "win", "", state, new JsonArray(), m);
        var first = Brief(memory);
        Check(first.Contains("children(col,row)") && first.Contains("4,3 Unknown -> 5,4 6,5") && !first.Contains("Unreachable"), "route coordinates and cross-row edges remain explicit, with only reachable future nodes");
        Check(!Brief(memory).Contains("[Route]"), "unchanged topology is not repeated during event stages");
        state["map"]!["nodes"]![0]!["children"]!.AsArray().RemoveAt(1);
        var changed = Brief(memory);
        Check(changed.Contains("[Route]") && !changed.Contains("6,5 Boss"), "changed topology replaces the previous route");
        Check(Brief(new BriefMemory()).Contains("[Route]") && Brief(null).Contains("[Route]"), "rollover and stateless decisions restore complete route context");
        state["map"]!["next_options"]!.AsArray().Clear();
        state["map"]!["nodes"]!.AsArray().Clear();
        Check(Brief(memory).Contains("[Route] none visible"), "an emptied topology explicitly clears the prior route");
    }

    // Decode the documented table grammar independently so assertions compare
    // every original value, missing field, variant, item count and array position.
    static string RoundTrip(JsonArray original)
    {
        var text = CompactState.Write(original);
        Check(text.StartsWith("Compact tables:"), "large fixtures should use tables");
        var lines = text.Split('\n');
        var header = lines[1];
        var columns = Cells(header[(header.IndexOf('{') + 1)..^2]).Select(c => c.StartsWith('"') ? JsonNode.Parse(c)!.GetValue<string>() : c).ToArray();
        var decoded = new JsonArray();
        foreach (var line in lines.Skip(2))
        {
            var row = line.Trim(); int copies = 1;
            if (row.StartsWith('*')) { int end = row.IndexOf(' '); copies = int.Parse(row[1..end]); row = row[(end + 1)..]; }
            var cells = Cells(row[1..^1]);
            Check(cells.Count <= columns.Length, "table rows do not exceed their column header; missing trailing fields stay absent");
            var obj = new JsonObject();
            for (int i = 0; i < cells.Count; i++) if (cells[i] != "~") obj[columns[i]] = JsonNode.Parse(cells[i]);
            for (int i = 0; i < copies; i++) decoded.Add(obj.DeepClone());
        }
        Check(JsonNode.DeepEquals(original, decoded), "compact table must round-trip without losing or inventing public information");
        return text;
    }

    static List<string> Cells(string text)
    {
        var result = new List<string>();
        if (text.Length == 0) return result;
        int start = 0, depth = 0; bool quoted = false, escaped = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
            else if (c == ',' && depth == 0) { result.Add(text[start..i]); start = i + 1; }
        }
        result.Add(text[start..]);
        return result;
    }
}
