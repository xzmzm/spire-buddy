using System.Text;
using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

// A model-facing serialization only. Snapshots, fingerprints, inspection tools
// and diff paths still use the original JSON. Tables keep every field/value and
// item in order; only consecutive, exactly equal objects can share a repeat row.
internal static class CompactState
{
    const string Legend = "Compact tables: indentation nests fields; '-' list items form arrays; key[N]{columns} contains N items. Rows follow column order; ~ or missing trailing cells mean absent fields; *N = N copies of a row. Other values are JSON.\n";

    internal static string Write(JsonNode node)
    {
        var json = node.WriteString();
        var compact = Render(node, null, 0);
        var text = Legend + compact;
        return Bytes(text) < Bytes(json) ? text : json;
    }

    static string Render(JsonNode? node, string? key, int depth)
    {
        var pad = new string(' ', depth * 2);
        var label = key == null ? "" : Key(key) + ": ";
        var json = pad + label + (node?.WriteString() ?? "null");
        if (node is JsonObject obj && obj.Count > 0)
        {
            var children = string.Join("\n", obj.Select(p => Render(p.Value, p.Key, key == null ? depth : depth + 1)));
            var block = key == null ? children : pad + Key(key) + ":\n" + children;
            return Bytes(block) < Bytes(json) ? block : json;
        }
        if (node is not JsonArray array || array.Count == 0) return json;

        // A patch often has one op whose value is a large deck/map array. Let
        // nested values use tables too, instead of burying them in a JSON cell.
        var best = json;
        if (array.Any(n => n is JsonObject or JsonArray))
        {
            var itemDepth = key == null ? depth : depth + 1;
            var itemPad = new string(' ', itemDepth * 2);
            var list = (key == null ? "" : pad + Key(key) + ":\n")
                + string.Join("\n", array.Select(n => itemPad + "-\n" + Render(n, null, itemDepth + 1)));
            if (Bytes(list) < Bytes(best)) best = list;
        }
        if (array.Any(n => n is not JsonObject)) return best;

        var objects = array.Cast<JsonObject>().ToArray();
        var columns = objects.SelectMany(o => o.Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToArray();
        var lines = new List<string> { pad + (key == null ? "" : Key(key)) + $"[{array.Count}]{{{string.Join(",", columns.Select(Key))}}}:" };
        for (int i = 0; i < objects.Length;)
        {
            int copies = 1;
            while (i + copies < objects.Length && JsonNode.DeepEquals(objects[i], objects[i + copies])) copies++;
            var values = columns.Select(c => objects[i].TryGetPropertyValue(c, out var value) ? value?.WriteString() ?? "null" : "~").ToList();
            while (values.Count > 0 && values[^1] == "~") values.RemoveAt(values.Count - 1);
            lines.Add(pad + "  " + (copies == 1 ? "" : "*" + copies + " ") + "[" + string.Join(",", values) + "]");
            i += copies;
        }
        var table = string.Join("\n", lines);
        return Bytes(table) < Bytes(best) ? table : best;
    }

    // Quote unusual keys exactly as JSON, including whitespace and delimiters.
    static string Key(string key) => key.Length > 0 && key.All(c => char.IsLetterOrDigit(c) || c == '_')
        ? key : JsonValue.Create(key)!.WriteString();
    static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);
}
