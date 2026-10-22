using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SpireBuddy.Runtime;

// Reads rules as metadata/source only. Never instantiate an event, generate its
// options, or evaluate a reward: doing so could consume RNG or reveal a live roll.
internal sealed class EventKnowledge(string assembly, string directory)
{
    internal const string DecisionGuidance = "Use the event's static rules and referenced reward effects to compare complete paths from the CURRENT visible stage, including later costs, exit rewards, conditions and drawbacks. Match visible option keys to source branches. Add only remaining costs; HP/gold already spent is a sunk cost. Distinguish an event's appearance conditions from current option requirements. Compare survival after paying with current HP, deck/relic synergy, reachable healing, shops, fights and the visible boss. Static random reward pools/probabilities are possibilities, never the actual future roll. Live displayed costs and locked options take precedence over base constants. If rules, dependencies or modifiers are unavailable/truncated, acknowledge the uncertainty and do not invent outcomes or assume a favorable reward. Choose only an advertised action and reassess after each stage.";
    const string EventNamespace = "MegaCrit.Sts2.Core.Models.Events";
    const int MaxSources = 12, MaxSourceChars = 24000, MaxTotalChars = 72000;
    readonly SemaphoreSlim gate = new(1);
    JsonObject? cache;
    string? cacheFile, assemblyHash;

    static string Normalize(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "");

    internal async Task<JsonNode> Lookup(string name, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await Task.Run(() => LookupCore(name, ct), ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Unavailable("Decompilation exceeded 15 seconds."); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return Unavailable(ex.Message); }
        finally { gate.Release(); }
    }

    static JsonObject Unavailable(string reason) => new()
    {
        ["status"] = "unavailable", ["decompile_error"] = reason,
        ["matches"] = new JsonArray(), ["related_models"] = new JsonArray(),
        ["limitations"] = "Event rules are missing; this does not imply there are no later costs or rewards."
    };

    JsonNode LookupCore(string name, CancellationToken ct)
    {
        var query = Normalize(name);
        if (query.Length == 0) return Unavailable("Use the stable event_id from the current brief, or an English event name.");
        ct.ThrowIfCancellationRequested();
        if (cache == null)
        {
            using var stream = File.OpenRead(assembly);
            assemblyHash = Convert.ToHexString(SHA256.HashData(stream));
            cacheFile = Path.Combine(directory, "event-source-v1-" + assemblyHash + ".json");
            try { cache = JsonNode.Parse(File.ReadAllText(cacheFile)) as JsonObject; }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            cache ??= new JsonObject();
        }
        if (cache[query] is JsonNode cached) return cached.DeepClone();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var decompiler = new CSharpDecompiler(assembly, new DecompilerSettings { ThrowOnAssemblyResolveErrors = false })
            { CancellationToken = deadline.Token };
        var types = decompiler.TypeSystem.MainModule.TypeDefinitions.ToArray();
        var events = types.Where(t => t.Namespace == EventNamespace).ToArray();
        var exact = events.Where(t => Normalize(t.Name) == query).ToArray();
        var matches = exact.Length > 0 ? exact : events.Where(t => Normalize(t.Name).Contains(query, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return Unavailable("No matching event type in the installed game.");
        if (matches.Length != 1) return new JsonObject
        {
            ["status"] = "ambiguous", ["matches"] = new JsonArray(),
            ["candidates"] = new JsonArray(matches.Take(20).Select(t => (JsonNode)JsonValue.Create(t.Name)!).ToArray()),
            ["limitations"] = "Multiple events match; repeat lookup_event with an exact event_id."
        };

        // Syntax identifiers, not strings/comments, identify referenced models.
        // Include reward pools, cards, relics, powers and event-specific helpers,
        // recursively, so a named reward is accompanied by its implementation.
        var relatedTypes = types.Where(t => RelatedNamespace(t.Namespace)).GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var primary = new JsonArray();
        var related = new JsonArray();
        var omitted = new JsonArray();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<ITypeDefinition>();
        pending.Enqueue(matches[0]);
        int remaining = MaxTotalChars;
        while (pending.TryDequeue(out var type))
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!visited.Add(type.FullName)) continue;
            if (primary.Count + related.Count >= MaxSources || remaining == 0)
            {
                omitted.Add(new JsonObject { ["name"] = type.FullName, ["reason"] = "Lookup source budget reached." });
                continue;
            }
            try
            {
                var tree = decompiler.DecompileType(new FullTypeName(type.FullName));
                var source = tree.ToString();
                var identifiers = tree.Descendants.Select(n => n switch
                {
                    SimpleType s => s.Identifier,
                    MemberType m => m.MemberName,
                    IdentifierExpression i => i.Identifier,
                    _ => ""
                }).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                foreach (var identifier in identifiers)
                    if (relatedTypes.TryGetValue(identifier, out var dependencies))
                        foreach (var dependency in dependencies.OrderBy(t => t.FullName, StringComparer.Ordinal))
                            if (!visited.Contains(dependency.FullName)) pending.Enqueue(dependency);

                var length = Math.Min(source.Length, Math.Min(MaxSourceChars, remaining));
                var item = new JsonObject
                {
                    ["name"] = type.Name, ["full_name"] = type.FullName,
                    ["source_kind"] = "decompiled_game_assembly", ["truncated"] = length < source.Length,
                    ["decompiled_source"] = source[..length]
                };
                if (length < source.Length) omitted.Add(new JsonObject { ["name"] = type.FullName, ["reason"] = "Source truncated; later methods may be missing." });
                (type == matches[0] ? primary : related).Add(item);
                remaining -= length;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                omitted.Add(new JsonObject { ["name"] = type.FullName, ["reason"] = ex.Message });
            }
        }
        var result = new JsonObject
        {
            ["status"] = primary.Count == 0 ? "unavailable" : omitted.Count > 0 ? "partial" : "ok",
            ["assembly_sha256"] = assemblyHash,
            ["knowledge_scope"] = "Static event branches, costs, conditions and referenced model rules from the installed game. No live event fields, RNG state or rolled rewards are read.",
            ["matches"] = primary, ["related_models"] = related, ["omitted_sources"] = omitted,
            ["limitations"] = "Source coverage is not a proof of complete outcomes. Localization text, general engine helpers, dynamically selected models and live modifiers are not evaluated. Use current visible options/state; missing effects remain unknown.",
            ["decision_guidance"] = DecisionGuidance
        };
        // Keep incomplete/error results retryable; only persist successful bundles.
        if (result.Text("status") == "ok")
        {
            cache[query] = result.DeepClone();
            try
            {
                Directory.CreateDirectory(directory);
                var staged = cacheFile + "." + Guid.NewGuid().ToString("N");
                File.WriteAllText(staged, cache.WriteString());
                File.Move(staged, cacheFile!, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    static bool RelatedNamespace(string ns) => ns is
        "MegaCrit.Sts2.Core.Models.Relics" or "MegaCrit.Sts2.Core.Models.Cards" or
        "MegaCrit.Sts2.Core.Models.Potions" or "MegaCrit.Sts2.Core.Models.Powers" or
        "MegaCrit.Sts2.Core.Models.Enchantments" or "MegaCrit.Sts2.Core.Models.Afflictions" or
        "MegaCrit.Sts2.Core.Models.RelicPools" or "MegaCrit.Sts2.Core.Models.CardPools" or
        "MegaCrit.Sts2.Core.Models.PotionPools" or "MegaCrit.Sts2.Core.Models.Encounters" or
        "MegaCrit.Sts2.Core.Models.Monsters" or EventNamespace or "MegaCrit.Sts2.Core.Events"
        || ns.StartsWith("MegaCrit.Sts2.Core.Events.", StringComparison.Ordinal);

    internal static string Brief(JsonNode rules)
    {
        var text = new StringBuilder("[Event rules] status=" + rules.Text("status") + "\n");
        if (rules.Text("decompile_error").Length > 0) text.AppendLine(rules.Text("decompile_error"));
        text.AppendLine(rules.Text("knowledge_scope"));
        text.AppendLine(rules.Text("limitations"));
        if (rules["candidates"] is JsonArray candidates) text.AppendLine("Candidates: " + candidates.WriteString());
        foreach (var source in rules["matches"].Items().Concat(rules["related_models"].Items()))
        {
            text.AppendLine("[Static source " + source.Text("full_name", source.Text("name")) + "]");
            text.AppendLine(source.Text("decompiled_source"));
            if (source.Flag("truncated")) text.AppendLine("[TRUNCATED: do not assume omitted effects are absent]");
        }
        if (rules["omitted_sources"].Items().Any()) text.AppendLine("[Missing source] " + rules["omitted_sources"]!.WriteString());
        return text.ToString();
    }
}
