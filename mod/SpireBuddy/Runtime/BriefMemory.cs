using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

// Owned by exactly one model history. Discard whenever that history is cleared;
// a definition seen by the run strategist is not known by a fresh combat agent.
internal sealed class BriefMemory
{
    readonly Dictionary<string, JsonNode?> sections = new();
    readonly Dictionary<(string Kind, string Name), string> definitions = new();
    Dictionary<string, string> potionSlots = new();

    internal bool Changed(string section, JsonNode? value)
    {
        if (sections.TryGetValue(section, out var previous) && JsonNode.DeepEquals(previous, value)) return false;
        sections[section] = value?.DeepClone();
        return true;
    }

    internal bool Definition(string kind, string name, string description)
    {
        var key = (kind, name);
        if (definitions.TryGetValue(key, out var previous) && previous == description) return false;
        definitions[key] = description;
        return true;
    }

    internal List<string> RemovedPotions(JsonArray potions)
    {
        var current = potions.Items().ToDictionary(p => p.Text("slot"), p => p.Text("name"));
        var removed = potionSlots.Where(p => !current.TryGetValue(p.Key, out var name) || name != p.Value)
            .Select(p => $"[Potion {p.Key}] {p.Value} no longer held").ToList();
        potionSlots = current;
        return removed;
    }
}
