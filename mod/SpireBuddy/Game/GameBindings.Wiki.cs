// Gameplay bindings adapted from STS2 MCP (MIT); see SpireBuddy.third-party-notices.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;

namespace SpireBuddy.Game;

internal static partial class GameBindings
{
    private const int DefaultWikiCount = 50;
    private const string WikiScope = "active_profile_discovered_cards_relics_and_potions";

    // Union of CardRarity, RelicRarity and PotionRarity member names from the game
    // assembly; matching against each item's rarity is case-insensitive.
    private static readonly HashSet<string> WikiRarities = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "basic", "common", "uncommon", "rare", "ancient", "event", "token",
        "status", "curse", "quest", "starter", "shop"
    };

    internal static object SearchWiki(string query, string itemType = "all", string rarity = "all", string? character = null, int? offset = null, int? count = null)
    {
        var normalizedItemType = NormalizeWikiItemType(itemType);
        if (normalizedItemType == null)
            return Error("item_type must be one of: all, card, relic, potion.");
        var normalizedRarity = NormalizeWikiRarity(rarity);
        if (normalizedRarity == null)
            return Error("rarity must be one of: " + string.Join(", ", WikiRarities.OrderBy(r => r, StringComparer.Ordinal)) + ".");
        var scopes = BuildWikiPoolScopes();
        var characterKey = NormalizeWikiCharacter(character, scopes);
        if (characterKey == null)
            return Error("character must be \"all\" (default), \"colorless\" (items shared by every character), or a character name/id: "
                + string.Join(", ", scopes.Characters.Select(c => c.Name).Where(name => name.Length > 0).Order(StringComparer.OrdinalIgnoreCase)) + ".");
        var page = Math.Max(offset ?? 0, 0);
        return string.IsNullOrWhiteSpace(query)
            ? BuildWikiList(normalizedItemType!, normalizedRarity!, characterKey, page, count ?? DefaultWikiCount, scopes)
            : BuildWikiSearch(query, normalizedItemType!, normalizedRarity!, characterKey, page, count ?? DefaultWikiCount, scopes);
    }

    private static Dictionary<string, object?> BuildWikiSearch(string query, string itemType, string rarity, string character, int offset, int count, WikiPoolScopes scopes)
    {
        var saveManager = SaveManager.Instance;
        var sets = DiscoveredSets();
        if (saveManager == null || sets == null)
            return Error("No profile data available.");
        var discovered = sets.Value;

        var candidates = Candidates(itemType, rarity, character, discovered, scopes);
        var glossary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var matches = candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = ScoreWikiCandidate(query, candidate)
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(offset)
            .Take(count)
            .Select(match => BuildWikiResult(match.Candidate, glossary, itemType == "all", PoolLabel(match.Candidate, scopes), match.Score))
            .ToList();

        var result = new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["query"] = query,
            ["item_type"] = itemType,
            ["scope"] = WikiScope,
            ["selection_policy"] = "Discovered items only; best fuzzy matches first; page with offset and count.",
            ["counts"] = CountsWith(discovered, candidates.Count, matches.Count),
            ["results"] = matches
        };
        if (rarity != "all") result["rarity"] = rarity;
        if (character.Length > 0) result["character"] = character;
        if (offset > 0) result["offset"] = offset;
        if (glossary.Count > 0) result["keyword_glossary"] = glossary;
        return result;
    }

    private static Dictionary<string, object?> BuildWikiList(string itemType, string rarity, string character, int offset, int count, WikiPoolScopes scopes)
    {
        if (itemType == "all")
            return Error("An empty query lists a single item type; set item_type to card, relic, or potion.");
        var saveManager = SaveManager.Instance;
        var sets = DiscoveredSets();
        if (saveManager == null || sets == null)
            return Error("No profile data available.");
        var discovered = sets.Value;

        var ordered = Candidates(itemType, rarity, character, discovered, scopes)
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToList();
        var glossary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var results = ordered.Skip(offset).Take(count)
            .Select(candidate => BuildWikiResult(candidate, glossary, includeKind: false, PoolLabel(candidate, scopes)))
            .ToList();
        var nextOffset = offset + results.Count;
        var result = new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["item_type"] = itemType,
            ["scope"] = WikiScope,
            ["selection_policy"] = $"Lists the active profile's discovered {itemType}s in name order; filter with rarity and character, page with offset and count.",
            ["counts"] = CountsWith(discovered, ordered.Count, results.Count),
            ["total"] = ordered.Count,
            ["returned"] = results.Count,
            ["results"] = results
        };
        if (rarity != "all") result["rarity"] = rarity;
        if (character.Length > 0) result["character"] = character;
        if (glossary.Count > 0) result["keyword_glossary"] = glossary;
        if (nextOffset < ordered.Count) result["next_offset"] = nextOffset;
        return result;
    }

    private static (HashSet<string> Cards, HashSet<string> Relics, HashSet<string> Potions)? DiscoveredSets()
    {
        var progress = SaveManager.Instance?.Progress;
        if (progress == null) return null;
        return (Ids(progress.DiscoveredCards), Ids(progress.DiscoveredRelics), Ids(progress.DiscoveredPotions));
    }

    private static HashSet<string> Ids(IEnumerable<ModelId> ids)
        => ids.Select(id => id.Entry)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static List<WikiCandidate> Candidates(string itemType, string rarity, string character, (HashSet<string> Cards, HashSet<string> Relics, HashSet<string> Potions) discovered, WikiPoolScopes scopes)
    {
        var candidates = new List<WikiCandidate>();
        if (itemType is "all" or "card")
            candidates.AddRange(BuildCardWikiCandidates(discovered.Cards));
        if (itemType is "all" or "relic")
            candidates.AddRange(BuildRelicWikiCandidates(discovered.Relics));
        if (itemType is "all" or "potion")
            candidates.AddRange(BuildPotionWikiCandidates(discovered.Potions));
        if (rarity != "all")
            candidates = candidates.Where(candidate => string.Equals(candidate.Rarity, rarity, StringComparison.OrdinalIgnoreCase)).ToList();
        // "" keeps every pool; a character keeps its own pool plus items shared
        // by every character (what that character can actually encounter);
        // "colorless" keeps only the shared pools.
        if (character.Length > 0)
            candidates = candidates.Where(candidate => InScope(candidate, character, scopes)).ToList();
        return candidates;
    }

    private static bool InScope(WikiCandidate candidate, string character, WikiPoolScopes scopes)
    {
        var (own, shared) = candidate.Kind switch
        {
            "card" => (scopes.CardPool.GetValueOrDefault(character), scopes.SharedCardPools),
            "relic" => (scopes.RelicPool.GetValueOrDefault(character), scopes.SharedRelicPools),
            _ => (scopes.PotionPool.GetValueOrDefault(character), scopes.SharedPotionPools),
        };
        if (candidate.ColorlessPool) return true;
        if (candidate.PoolId == null || candidate.PoolId.Length == 0) return false;
        if (own is { Length: > 0 } && string.Equals(candidate.PoolId, own, StringComparison.OrdinalIgnoreCase)) return true;
        return shared.Contains(candidate.PoolId);
    }

    // Display label for an item's pool: the owning character, or shared/colorless
    // for common items. Only included on mixed "all" listings, where the pool is
    // otherwise invisible.
    private static string? PoolLabel(WikiCandidate candidate, WikiPoolScopes scopes)
    {
        if (candidate.PoolId == null || candidate.PoolId.Length == 0) return null;
        var (owner, shared) = candidate.Kind switch
        {
            "card" => (scopes.CardPoolOwner.GetValueOrDefault(candidate.PoolId), scopes.SharedCardPools),
            "relic" => (scopes.RelicPoolOwner.GetValueOrDefault(candidate.PoolId), scopes.SharedRelicPools),
            _ => (scopes.PotionPoolOwner.GetValueOrDefault(candidate.PoolId), scopes.SharedPotionPools),
        };
        if (owner is { Length: > 0 }) return owner;
        return shared.Contains(candidate.PoolId) || candidate.ColorlessPool ? (candidate.Kind == "card" ? "colorless" : "shared") : "other";
    }

    // Pools are registry singletons, so matching by pool id is stable and
    // localization-independent. Built per call: the registries are small and
    // this avoids caching across mod injections. Relic and potion pools have
    // no dedicated shared registry: everything outside the per-character
    // pools is shared (obtainable by every character).
    private static WikiPoolScopes BuildWikiPoolScopes()
    {
        var scopes = new WikiPoolScopes();
        foreach (var pool in ModelDb.AllSharedCardPools) AddId(scopes.SharedCardPools, pool);
        var characterRelicPools = Ids(ModelDb.AllCharacterRelicPools);
        foreach (var pool in ModelDb.AllRelicPools) AddId(scopes.SharedRelicPools, pool, characterRelicPools);
        var characterPotionPools = Ids(ModelDb.AllCharacterPotionPools);
        foreach (var pool in ModelDb.AllPotionPools) AddId(scopes.SharedPotionPools, pool, characterPotionPools);
        foreach (var model in ModelDb.AllCharacters)
        {
            if (model == null) continue;
            var id = SafeGetText(() => model.Id.Entry);
            if (id.Length == 0) continue;
            var title = SafeGetText(() => model.Title);
            var label = title.Length > 0 ? title : id;
            scopes.Characters.Add((id, title));
            var cardPool = SafeGetText(() => model.CardPool?.Id.Entry);
            var relicPool = SafeGetText(() => model.RelicPool?.Id.Entry);
            var potionPool = SafeGetText(() => model.PotionPool?.Id.Entry);
            if (cardPool.Length > 0) { scopes.CardPool[id] = cardPool; scopes.CardPoolOwner[cardPool] = label; }
            if (relicPool.Length > 0) { scopes.RelicPool[id] = relicPool; scopes.RelicPoolOwner[relicPool] = label; }
            if (potionPool.Length > 0) { scopes.PotionPool[id] = potionPool; scopes.PotionPoolOwner[potionPool] = label; }
        }
        return scopes;

        static void AddId(HashSet<string> target, AbstractModel? pool, HashSet<string>? exclude = null)
        {
            var id = pool == null ? null : SafeGetText(() => pool.Id.Entry);
            if (id is { Length: > 0 } && (exclude == null || !exclude.Contains(id))) target.Add(id);
        }
        static HashSet<string> Ids(IEnumerable<AbstractModel?> pools)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pool in pools) AddId(result, pool);
            return result;
        }
    }

    // Returns "" for no filter, "colorless" for shared pools only, or a canonical
    // character id; null when the value matches no known character.
    private static string? NormalizeWikiCharacter(string? character, WikiPoolScopes scopes)
    {
        var value = NormalizeSearchText(character ?? "");
        if (value.Length == 0 || value is "all" or "any") return "";
        var compact = value.Replace(" ", "", StringComparison.Ordinal);
        if (compact is "colorless" or "shared" or "common" or "commontoall") return "colorless";
        foreach (var (id, title) in scopes.Characters)
            if (CompactSearchText(id).Equals(compact, StringComparison.OrdinalIgnoreCase)
                || CompactSearchText(title).Equals(compact, StringComparison.OrdinalIgnoreCase))
                return id;
        return null;
    }

    private static Dictionary<string, object?> CountsWith((HashSet<string> Cards, HashSet<string> Relics, HashSet<string> Potions) discovered, int searched, int returned)
        => new()
        {
            ["discovered_cards"] = discovered.Cards.Count,
            ["discovered_relics"] = discovered.Relics.Count,
            ["discovered_potions"] = discovered.Potions.Count,
            ["searched"] = searched,
            ["returned"] = returned
        };

    private static string? NormalizeWikiItemType(string? itemType)
    {
        var value = (itemType ?? "all").Trim().ToLowerInvariant();
        return value switch
        {
            "" or "all" or "any" => "all",
            "card" or "cards" => "card",
            "relic" or "relics" => "relic",
            "potion" or "potions" => "potion",
            _ => null
        };
    }

    private static string? NormalizeWikiRarity(string? rarity)
    {
        var value = (rarity ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0) value = "all";
        return WikiRarities.Contains(value) ? value : null;
    }

    // Enumerate the game's canonical model registry rather than reflectively
    // constructing fresh instances per-type. Fresh instances never have
    // InitId() called on them (that only happens for instances stored in
    // ModelDb._contentById), so card.Id.Entry comes back empty and the
    // discoveredIds filter drops everything. ModelDb.AllCards also naturally
    // includes mod-injected cards.
    private static IEnumerable<WikiCandidate> BuildCardWikiCandidates(HashSet<string> discoveredIds)
    {
        var byId = new Dictionary<string, WikiCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in ModelDb.AllCards)
        {
            var id = SafeGetText(() => card.Id.Entry);
            if (string.IsNullOrWhiteSpace(id) || !discoveredIds.Contains(id))
                continue;

            byId.TryAdd(id, new WikiCandidate(
                Kind: "card",
                Id: id,
                Name: SafeGetText(() => card.Title) ?? id,
                SearchText: BuildWikiSearchText("card", id, SafeGetText(() => card.Title), SafeGetText(() => card.Type), SafeGetText(() => card.Rarity)),
                Rarity: SafeGetText(() => card.Rarity),
                Card: card,
                Relic: null,
                PoolId: SafeGetText(() => card.Pool?.Id.Entry),
                ColorlessPool: card.Pool?.IsColorless == true));
        }

        return byId.Values;
    }

    private static IEnumerable<WikiCandidate> BuildRelicWikiCandidates(HashSet<string> discoveredIds)
    {
        var byId = new Dictionary<string, WikiCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var relic in ModelDb.AllRelics)
        {
            var id = SafeGetText(() => relic.Id.Entry);
            if (string.IsNullOrWhiteSpace(id) || !discoveredIds.Contains(id))
                continue;

            byId.TryAdd(id, new WikiCandidate(
                Kind: "relic",
                Id: id,
                Name: SafeGetText(() => relic.Title) ?? id,
                SearchText: BuildWikiSearchText("relic", id, SafeGetText(() => relic.Title), SafeGetText(() => relic.Rarity)),
                Rarity: SafeGetText(() => relic.Rarity),
                Card: null,
                Relic: relic,
                PoolId: SafeGetText(() => relic.Pool?.Id.Entry)));
        }

        return byId.Values;
    }

    private static IEnumerable<WikiCandidate> BuildPotionWikiCandidates(HashSet<string> discoveredIds)
    {
        var byId = new Dictionary<string, WikiCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var potion in ModelDb.AllPotions)
        {
            var id = SafeGetText(() => potion.Id.Entry);
            if (string.IsNullOrWhiteSpace(id) || !discoveredIds.Contains(id))
                continue;

            byId.TryAdd(id, new WikiCandidate(
                Kind: "potion",
                Id: id,
                Name: SafeGetText(() => potion.Title) ?? id,
                SearchText: BuildWikiSearchText("potion", id, SafeGetText(() => potion.Title), SafeGetText(() => potion.Rarity)),
                Rarity: SafeGetText(() => potion.Rarity),
                Card: null,
                Relic: null,
                Potion: potion,
                PoolId: SafeGetText(() => potion.Pool?.Id.Entry)));
        }

        return byId.Values;
    }

    // Results are compact on purpose: listings can span hundreds of items.
    // Variant wrappers and fields duplicated between base and upgraded forms
    // are collapsed; an `upgraded` object lists only the fields that change,
    // and its absence means the card cannot be upgraded. Keyword definitions
    // are hoisted into a per-response glossary; items keep only the names.
    private static Dictionary<string, object?> BuildWikiResult(WikiCandidate candidate, Dictionary<string, string> glossary, bool includeKind, string? poolLabel, double? score = null)
    {
        Dictionary<string, object?> result;
        if (candidate.Card != null)
            result = BuildWikiCardResult(candidate, glossary, poolLabel);
        else if (candidate.Relic != null)
            result = BuildWikiRelicResult(candidate, glossary, poolLabel);
        else if (candidate.Potion != null)
            result = BuildWikiPotionResult(candidate, glossary, poolLabel);
        else
            result = new Dictionary<string, object?> { ["id"] = candidate.Id, ["name"] = candidate.Name };
        if (includeKind) result["item_type"] = candidate.Kind;
        if (score != null) result["score"] = Math.Round(score.Value, 3);
        return result;
    }

    private static Dictionary<string, object?> BuildWikiCardResult(WikiCandidate candidate, Dictionary<string, string> glossary, string? poolLabel)
    {
        var card = candidate.Card!;
        var baseDescription = SafeGetCardDescription(card) ?? "";
        var baseCost = GetCostDisplay(card);
        var baseStarCost = GetStarCostDisplay(card);
        var keywords = new List<string>();
        CollectHoverTips(card.HoverTips, keywords, glossary);

        var result = new Dictionary<string, object?>
        {
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["type"] = SafeGetText(() => card.Type),
            ["rarity"] = SafeGetText(() => card.Rarity),
            ["cost"] = baseCost,
            ["description"] = baseDescription
        };
        if (!string.IsNullOrEmpty(baseStarCost)) result["star_cost"] = baseStarCost;
        if (card.MaxUpgradeLevel > 1) result["max_upgrades"] = card.MaxUpgradeLevel;
        if (poolLabel != null) result["pool"] = poolLabel;

        var upgraded = SafeBuildUpgradedCardPreview(card);
        if (upgraded != null)
        {
            var diff = new Dictionary<string, object?>();
            var upgradedCost = GetCostDisplay(upgraded);
            if (!string.Equals(upgradedCost, baseCost, StringComparison.Ordinal)) diff["cost"] = upgradedCost;
            var upgradedStarCost = GetStarCostDisplay(upgraded);
            if (!string.Equals(upgradedStarCost, baseStarCost, StringComparison.Ordinal) && !string.IsNullOrEmpty(upgradedStarCost)) diff["star_cost"] = upgradedStarCost;
            var upgradedDescription = SafeGetCardDescription(upgraded) ?? "";
            if (!string.Equals(upgradedDescription, baseDescription, StringComparison.Ordinal)) diff["description"] = upgradedDescription;
            var upgradedKeywords = new List<string>();
            CollectHoverTips(upgraded.HoverTips, upgradedKeywords, glossary);
            if (!upgradedKeywords.SequenceEqual(keywords, StringComparer.OrdinalIgnoreCase)) diff["keywords"] = upgradedKeywords;
            // A preview that matches the base in every listed field still means
            // "upgradable"; say so instead of an empty object.
            result["upgraded"] = diff.Count > 0 ? diff : new Dictionary<string, object?> { ["description"] = baseDescription };
        }
        else if (card.IsUpgradable)
            result["upgraded"] = new Dictionary<string, object?> { ["description"] = SafeGetCardUpgradePreviewDescription(card), ["preview_available"] = false };

        if (keywords.Count > 0) result["keywords"] = keywords;
        return result;
    }

    private static Dictionary<string, object?> BuildWikiRelicResult(WikiCandidate candidate, Dictionary<string, string> glossary, string? poolLabel)
    {
        var relic = candidate.Relic!;
        var keywords = new List<string>();
        CollectHoverTips(relic.HoverTipsExcludingRelic, keywords, glossary);
        var result = new Dictionary<string, object?>
        {
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["rarity"] = SafeGetText(() => relic.Rarity),
            ["description"] = SafeGetText(() => relic.DynamicDescription)
        };
        if (poolLabel != null) result["pool"] = poolLabel;
        if (keywords.Count > 0) result["keywords"] = keywords;
        return result;
    }

    private static Dictionary<string, object?> BuildWikiPotionResult(WikiCandidate candidate, Dictionary<string, string> glossary, string? poolLabel)
    {
        var potion = candidate.Potion!;
        var keywords = new List<string>();
        CollectHoverTips(potion.ExtraHoverTips, keywords, glossary);
        var result = new Dictionary<string, object?>
        {
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["rarity"] = SafeGetText(() => potion.Rarity),
            ["target_type"] = SafeGetText(() => potion.TargetType.ToString()),
            ["description"] = SafeGetText(() => potion.DynamicDescription)
        };
        if (poolLabel != null) result["pool"] = poolLabel;
        if (keywords.Count > 0) result["keywords"] = keywords;
        return result;
    }

    // Keyword and referenced-card definitions repeat across many items; keep the
    // names on the item and the definition once per response.
    private static void CollectHoverTips(IEnumerable<IHoverTip> tips, List<string> names, Dictionary<string, string> glossary)
    {
        try
        {
            foreach (var tip in IHoverTip.RemoveDupes(tips))
            {
                try
                {
                    string? title = null;
                    string? description = null;
                    if (tip is HoverTip ht)
                    {
                        title = ht.Title != null ? StripRichTextTags(ht.Title) : null;
                        description = StripRichTextTags(ht.Description);
                    }
                    else if (tip is CardHoverTip cardTip)
                    {
                        title = SafeGetText(() => cardTip.Card.Title);
                        description = SafeGetCardDescription(cardTip.Card);
                    }
                    if (title == null && description == null) continue;
                    var name = title ?? description!;
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                    glossary.TryAdd(name, description ?? "");
                }
                catch { /* skip individual tip on error */ }
            }
        }
        catch { /* keep partial results */ }
    }

    private static string BuildWikiSearchText(params string?[] values)
        => string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static double ScoreWikiCandidate(string query, WikiCandidate candidate)
    {
        var queryNormalized = NormalizeSearchText(query);
        var candidateNormalized = NormalizeSearchText(candidate.SearchText);
        if (string.IsNullOrWhiteSpace(queryNormalized) || string.IsNullOrWhiteSpace(candidateNormalized))
            return 0;

        var queryCompact = CompactSearchText(queryNormalized);
        var candidateCompact = CompactSearchText(candidateNormalized);
        var nameCompact = CompactSearchText(candidate.Name);
        var idCompact = CompactSearchText(candidate.Id);

        if (queryCompact.Equals(nameCompact, StringComparison.OrdinalIgnoreCase)
            || queryCompact.Equals(idCompact, StringComparison.OrdinalIgnoreCase))
            return 1000;

        if (nameCompact.Contains(queryCompact, StringComparison.OrdinalIgnoreCase)
            || idCompact.Contains(queryCompact, StringComparison.OrdinalIgnoreCase))
            return 850;

        var queryTokens = TokenizeSearchText(queryNormalized);
        var candidateTokens = TokenizeSearchText(candidateNormalized);
        if (queryTokens.Count == 0 || candidateTokens.Count == 0)
            return 0;

        double total = 0;
        var strongMatches = 0;
        foreach (var queryToken in queryTokens)
        {
            var best = candidateTokens.Max(candidateToken => TokenSimilarity(queryToken, candidateToken));
            total += best;
            if (best >= 0.72)
                strongMatches++;
        }

        var average = total / queryTokens.Count;
        var score = average * 700;
        if (strongMatches > 0)
            score += strongMatches * 35;

        return score >= 300 ? score : 0;
    }

    private static string NormalizeSearchText(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = true;
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static string CompactSearchText(string value)
        => NormalizeSearchText(value).Replace(" ", "", StringComparison.Ordinal);

    private static List<string> TokenizeSearchText(string value)
        => NormalizeSearchText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static double TokenSimilarity(string queryToken, string candidateToken)
    {
        if (queryToken.Equals(candidateToken, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        // Require >=3 chars on the candidate token for the substring shortcut:
        // single-letter words like "a"/"I" otherwise trivially substring-match
        // arbitrary queries and produce spurious results for gibberish input.
        if (candidateToken.Length >= 3
            && (candidateToken.Contains(queryToken, StringComparison.OrdinalIgnoreCase)
                || queryToken.Contains(candidateToken, StringComparison.OrdinalIgnoreCase)))
            return 0.88;

        var distance = LevenshteinDistance(queryToken, candidateToken);
        var maxLength = Math.Max(queryToken.Length, candidateToken.Length);
        return maxLength == 0 ? 0 : 1.0 - ((double)distance / maxLength);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private sealed record WikiCandidate(
        string Kind,
        string Id,
        string Name,
        string SearchText,
        string? Rarity,
        CardModel? Card,
        RelicModel? Relic,
        PotionModel? Potion = null,
        string? PoolId = null,
        bool ColorlessPool = false);

    private sealed class WikiPoolScopes
    {
        internal readonly List<(string Id, string Name)> Characters = [];
        internal readonly Dictionary<string, string> CardPool = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> RelicPool = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> PotionPool = new(StringComparer.OrdinalIgnoreCase);
        // Pool id -> owning character's display name, for mixed listings.
        internal readonly Dictionary<string, string> CardPoolOwner = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> RelicPoolOwner = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> PotionPoolOwner = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> SharedCardPools = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> SharedRelicPools = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> SharedPotionPools = new(StringComparer.OrdinalIgnoreCase);
    }
}
