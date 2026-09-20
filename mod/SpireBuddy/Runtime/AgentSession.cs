using System.Text;
using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

// A session belongs to one agent and one serial caller. Provider protocol items
// (including encrypted reasoning) remain intact until the whole session rolls.
internal sealed class AgentSession(string role)
{
    internal JsonArray History { get; } = new();
    internal BriefMemory Brief { get; private set; } = new();
    internal string Id { get; private set; } = role + "-" + Guid.NewGuid().ToString("N");
    JsonNode? previousState;
    long measuredTokens, measuredBytes;

    internal void Restart(string prompt)
    {
        History.Clear();
        History.Add(ModelClient.Message("developer", prompt));
        Brief = new(); previousState = null; measuredTokens = measuredBytes = 0;
        Id = role + "-" + Guid.NewGuid().ToString("N");
    }

    internal void ObserveUsage(JsonNode? usage)
    {
        measuredTokens = (usage?["input_tokens"] ?? usage?["prompt_tokens"])?.GetValue<long>() ?? 0;
        measuredTokens += (usage?["output_tokens"] ?? usage?["completion_tokens"])?.GetValue<long>() ?? 0;
        measuredBytes = Bytes(History);
    }

    internal bool OverBudget(JsonObject settings, JsonArray tools)
    {
        // Keep the existing conservative UTF-8 estimate when a provider supplies
        // no tokenizer/usage, and also respect actual usage when it is larger.
        var bytes = Bytes(History);
        var estimate = (bytes + Bytes(tools) + 1) / 2;
        var measured = measuredTokens + Math.Max(0, bytes - measuredBytes + 1) / 2;
        return Math.Max(estimate, measured) > settings["max_context_tokens"]!.GetValue<int>();
    }

    // force re-anchors the session after play-status changes: the unchanged
    // state is repeated in full so the next decision carries a current picture
    // even when a trailing diff already delivered the same bytes.
    internal void State(JsonNode state, bool force = false)
    {
        var unchanged = JsonNode.DeepEquals(previousState, state);
        if (unchanged && !force) return;
        var full = "Current public game state:\n" + state.WriteString();
        var message = full;
        if (previousState != null && !unchanged)
        {
            var changes = new JsonArray();
            Diff(previousState, state, "", changes);
            var delta = "Public state changes (JSON Pointer paths; set replaces the value, remove deletes it; other values are unchanged):\n" + changes.WriteString();
            if (Bytes(delta) < Bytes(full)) message = delta;
        }
        History.Add(ModelClient.Message("user", message));
        previousState = state.DeepClone();
    }

    static void Diff(JsonNode? before, JsonNode? after, string path, JsonArray changes)
    {
        if (JsonNode.DeepEquals(before, after)) return;
        if (before is JsonObject oldObject && after is JsonObject newObject)
        {
            foreach (var key in oldObject.Select(p => p.Key).Union(newObject.Select(p => p.Key)))
            {
                var child = path + "/" + key.Replace("~", "~0").Replace("/", "~1");
                if (!newObject.ContainsKey(key)) changes.Add(new JsonObject { ["op"] = "remove", ["path"] = child });
                else if (!oldObject.ContainsKey(key)) changes.Add(new JsonObject { ["op"] = "set", ["path"] = child, ["value"] = newObject[key]?.DeepClone() });
                else Diff(oldObject[key], newObject[key], child, changes);
            }
        }
        else changes.Add(new JsonObject { ["op"] = "set", ["path"] = path, ["value"] = after?.DeepClone() });
    }

    static long Bytes(JsonNode node) => Bytes(node.WriteString());
    static long Bytes(string value) => Encoding.UTF8.GetByteCount(value);
}

internal sealed class GameplayAgent(string role, string instructions)
{
    internal AgentSession Session { get; } = new(role);
    internal string Instructions { get; set; } = instructions;
    internal string Plan { get; set; } = "";
    internal string Feedback { get; set; } = "";
    internal Queue<string> Inbox { get; } = new();
}
