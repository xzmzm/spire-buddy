using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SpireBuddy.Runtime;

internal sealed class EnemyDecompiler(string assembly, string directory)
{
    readonly SemaphoreSlim gate = new(1);
    JsonObject? cache;
    string? cacheFile;
    static readonly JsonObject AddBranchNote = new()
    {
        ["RandomBranchState.AddBranch"] = new JsonObject
        {
            ["signatures"] = new JsonArray(
                "AddBranch(MonsterState state, int cooldown, MoveRepeatType repeatType, Func<float> weight)",
                "AddBranch(MonsterState state, int cooldown, int maxRepeats, Func<float> weight)",
                "AddBranch(MonsterState state, int maxRepeats, Func<float> weight)",
                "AddBranch(MonsterState state, int cooldown, MoveRepeatType repeatType, float weight)",
                "AddBranch(MonsterState state, MoveRepeatType repeatType, float weight)",
                "AddBranch(MonsterState state, MoveRepeatType repeatType, Func<float> weight)",
                "AddBranch(MonsterState state, int maxRepeats, float weight)",
                "AddBranch(MonsterState state, int cooldown, MoveRepeatType repeatType)",
                "AddBranch(MonsterState state, int maxRepeats)",
                "AddBranch(MonsterState state, MoveRepeatType repeatType)"),
            ["parameters"] = new JsonObject
            {
                ["state"] = "Candidate state selected when this branch wins.",
                ["cooldown"] = "Number of most recent logged move states in which this state must not appear; 0 disables cooldown.",
                ["repeatType"] = "UseOnlyOnce forbids the state after any prior use; CannotRepeat forbids it when it was the previous state; CanRepeatForever adds no repeat restriction; CanRepeatXTimes is represented by the maxRepeats overloads.",
                ["maxRepeats"] = "Maximum consecutive uses of this state; for example, 2 permits two consecutive uses before excluding it from the next choice.",
                ["weight"] = "Relative random-selection weight. A Func<float> is evaluated for each selection; a float is a constant weight."
            },
            ["defaults"] = new JsonArray(
                "cooldown = 0 when omitted.",
                "weight = 1f when omitted.",
                "An int argument without a MoveRepeatType argument is maxRepeats, not cooldown, and selects CanRepeatXTimes.")
        }
    };
    static string Normalize(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "");
    static string[] QueryVariants(string name)
    {
        var variants = new List<string>();
        void Add(string value)
        {
            var normalized = Normalize(value);
            if (normalized.Length > 0 && !variants.Contains(normalized, StringComparer.Ordinal)) variants.Add(normalized);
        }

        Add(name);
        // Combat labels can append an instance marker to the model name, such
        // as "Test Subject #C10". The suffix identifies this encounter, not a
        // different static monster type.
        Add(Regex.Replace(name, @"\s*#\s*[a-z]*\d+\s*$", "", RegexOptions.IgnoreCase));
        Add(Regex.Replace(name, @"\s+[a-z]*\d+\s*$", "", RegexOptions.IgnoreCase));
        Add(Regex.Replace(name, @"\s+\d+\s*$", "", RegexOptions.IgnoreCase));
        return variants.ToArray();
    }
    internal async Task<JsonNode> Lookup(string name, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => LookupCore(name, ct), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new JsonObject { ["matches"] = new JsonArray(), ["decompile_error"] = "Decompilation exceeded 15 seconds." }; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new JsonObject { ["matches"] = new JsonArray(), ["decompile_error"] = ex.Message }; }
        finally { gate.Release(); }
    }
    JsonNode LookupCore(string name, CancellationToken ct)
    {
        var queries = QueryVariants(name); if (queries.Length == 0) throw new ArgumentException("Enemy name must not be empty.");
        var query = queries[0];
        if (cache == null)
        {
            using var stream = File.OpenRead(assembly);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            cacheFile = Path.Combine(directory, "enemy-source-" + hash + ".json");
            try { cache = JsonNode.Parse(File.ReadAllText(cacheFile)) as JsonObject; } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            cache ??= new JsonObject();
        }
        if (cache[query] != null) return AddContextualNotes(cache[query]!.DeepClone());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var decompiler = new CSharpDecompiler(assembly, new DecompilerSettings { ThrowOnAssemblyResolveErrors = false }) { CancellationToken = deadline.Token };
        var types = decompiler.TypeSystem.MainModule.TypeDefinitions.Where(t => t.Namespace == "MegaCrit.Sts2.Core.Models.Monsters").ToArray();
        var exact = types.Where(t => queries.Contains(Normalize(t.Name), StringComparer.Ordinal)).ToArray();
        var matches = exact.Length > 0
            ? exact
            : types.Where(t => queries.Any(q => Normalize(t.Name).Contains(q, StringComparison.Ordinal)
                || q.Contains(Normalize(t.Name), StringComparison.Ordinal))).Take(5).ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("No matching monster type in the installed game.");
        var result = new JsonObject { ["knowledge_scope"] = "Static public move rules only; no live objects, future rolls or RNG state.", ["matches"] = new JsonArray() };
        foreach (var type in matches)
        {
            var source = decompiler.DecompileTypeAsString(new FullTypeName(type.FullName));
            result["matches"]!.AsArray().Add(new JsonObject { ["name"] = type.Name, ["source_kind"] = "decompiled_game_assembly", ["decompiled_source"] = source.Length > 24000 ? source[..24000] + "\n[truncated]" : source });
        }
        cache[query] = result.DeepClone();
        try { Directory.CreateDirectory(directory); var staged = cacheFile + "." + Guid.NewGuid().ToString("N"); File.WriteAllText(staged, cache.WriteString()); File.Move(staged, cacheFile!, true); } catch (IOException) { }
        return AddContextualNotes(result);
    }

    internal static JsonNode AddContextualNotes(JsonNode result)
    {
        if (result["matches"].Items().Any(match =>
            Regex.IsMatch(match.Text("decompiled_source"), @"\.AddBranch\s*\(")))
            result["decompilation_notes"] = AddBranchNote.DeepClone();
        return result;
    }
}
