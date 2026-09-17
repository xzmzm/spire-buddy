// Gameplay bindings adapted from STS2 MCP (MIT); see SpireBuddy.third-party-notices.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
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

    internal static object SearchWiki(string query, string itemType = "all", string rarity = "all", int? offset = null, int? count = null)
    {
        var normalizedItemType = NormalizeWikiItemType(itemType);
        if (normalizedItemType == null)
            return Error("item_type must be one of: all, card, relic, potion.");
        var normalizedRarity = NormalizeWikiRarity(rarity);
        if (normalizedRarity == null)
            return Error("rarity must be one of: " + string.Join(", ", WikiRarities.OrderBy(r => r, StringComparer.Ordinal)) + ".");
        var page = Math.Max(offset ?? 0, 0);
        return string.IsNullOrWhiteSpace(query)
            ? BuildWikiList(normalizedItemType!, normalizedRarity!, page, count ?? DefaultWikiCount)
            : BuildWikiSearch(query, normalizedItemType!, normalizedRarity!, page, count ?? DefaultWikiCount);
    }

    private static Dictionary<string, object?> BuildWikiSearch(string query, string itemType, string rarity, int offset, int count)
    {
        var saveManager = SaveManager.Instance;
        var sets = DiscoveredSets();
        if (saveManager == null || sets == null)
            return Error("No profile data available.");
        var discovered = sets.Value;

        var candidates = Candidates(itemType, rarity, discovered);

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
            .Select(match => BuildWikiResult(match.Candidate, match.Score))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["profile_id"] = saveManager.CurrentProfileId,
            ["query"] = query,
            ["item_type"] = itemType,
            ["rarity"] = rarity,
            ["offset"] = offset,
            ["count"] = count,
            ["scope"] = WikiScope,
            ["selection_policy"] = "Searches only cards, relics and potions discovered by the active profile, filtered by item_type and rarity, then returns the best fuzzy matches instead of exposing the full catalog. More matches can be paged with offset and count.",
            ["counts"] = CountsWith(discovered, candidates.Count, matches.Count),
            ["results"] = matches
        };
    }

    private static Dictionary<string, object?> BuildWikiList(string itemType, string rarity, int offset, int count)
    {
        if (itemType == "all")
            return Error("An empty query lists a single item type; set item_type to card, relic, or potion.");
        var saveManager = SaveManager.Instance;
        var sets = DiscoveredSets();
        if (saveManager == null || sets == null)
            return Error("No profile data available.");
        var discovered = sets.Value;

        var ordered = Candidates(itemType, rarity, discovered)
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToList();
        var results = ordered.Skip(offset).Take(count).Select(candidate => BuildWikiResult(candidate)).ToList();
        var nextOffset = offset + results.Count;
        var result = new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["profile_id"] = saveManager.CurrentProfileId,
            ["query"] = "",
            ["item_type"] = itemType,
            ["rarity"] = rarity,
            ["offset"] = offset,
            ["count"] = count,
            ["scope"] = WikiScope,
            ["selection_policy"] = $"Lists the active profile's discovered {itemType}s in name order; filter with rarity, page with offset and count.",
            ["counts"] = CountsWith(discovered, ordered.Count, results.Count),
            ["total"] = ordered.Count,
            ["returned"] = results.Count,
            ["results"] = results
        };
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

    private static List<WikiCandidate> Candidates(string itemType, string rarity, (HashSet<string> Cards, HashSet<string> Relics, HashSet<string> Potions) discovered)
    {
        var candidates = new List<WikiCandidate>();
        if (itemType is "all" or "card")
            candidates.AddRange(BuildCardWikiCandidates(discovered.Cards));
        if (itemType is "all" or "relic")
            candidates.AddRange(BuildRelicWikiCandidates(discovered.Relics));
        if (itemType is "all" or "potion")
            candidates.AddRange(BuildPotionWikiCandidates(discovered.Potions));
        return rarity == "all"
            ? candidates
            : candidates.Where(candidate => string.Equals(candidate.Rarity, rarity, StringComparison.OrdinalIgnoreCase)).ToList();
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
                Relic: null));
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
                Relic: relic));
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
                Potion: potion));
        }

        return byId.Values;
    }

    private static Dictionary<string, object?> BuildWikiResult(WikiCandidate candidate, double? score = null)
    {
        if (candidate.Card != null)
            return BuildWikiCardResult(candidate, score);
        if (candidate.Relic != null)
            return BuildWikiRelicResult(candidate, score);
        if (candidate.Potion != null)
            return BuildWikiPotionResult(candidate, score);

        var fallback = new Dictionary<string, object?>
        {
            ["item_type"] = candidate.Kind,
            ["id"] = candidate.Id,
            ["name"] = candidate.Name
        };
        if (score != null) fallback["score"] = Math.Round(score.Value, 3);
        return fallback;
    }

    private static Dictionary<string, object?> BuildWikiCardResult(WikiCandidate candidate, double? score)
    {
        var card = candidate.Card!;
        var upgraded = SafeBuildUpgradedCardPreview(card);
        var upgradedVariant = upgraded != null
            ? BuildWikiCardVariant(upgraded, "upgraded")
            : card.IsUpgradable
                ? new Dictionary<string, object?>
                {
                    ["variant"] = "upgraded",
                    ["description"] = SafeGetCardUpgradePreviewDescription(card),
                    ["preview_available"] = false
                }
                : null;

        var result = new Dictionary<string, object?>
        {
            ["item_type"] = "card",
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["rarity"] = SafeGetText(() => card.Rarity),
            ["type"] = SafeGetText(() => card.Type),
            ["is_upgradable"] = card.IsUpgradable,
            ["current_upgrade_level"] = card.CurrentUpgradeLevel,
            ["max_upgrade_level"] = card.MaxUpgradeLevel,
            ["base"] = BuildWikiCardVariant(card, "base"),
            ["upgraded"] = upgradedVariant
        };
        if (score != null) result["score"] = Math.Round(score.Value, 3);

        return result;
    }

    private static Dictionary<string, object?> BuildWikiCardVariant(CardModel card, string variant)
    {
        return new Dictionary<string, object?>
        {
            ["variant"] = variant,
            ["id"] = SafeGetText(() => card.Id.Entry),
            ["name"] = SafeGetText(() => card.Title),
            ["type"] = SafeGetText(() => card.Type),
            ["rarity"] = SafeGetText(() => card.Rarity),
            ["cost"] = GetCostDisplay(card),
            ["star_cost"] = GetStarCostDisplay(card),
            ["description"] = SafeGetCardDescription(card),
            ["is_upgraded"] = card.IsUpgraded,
            ["is_upgradable"] = card.IsUpgradable,
            ["current_upgrade_level"] = card.CurrentUpgradeLevel,
            ["max_upgrade_level"] = card.MaxUpgradeLevel,
            ["keywords"] = BuildHoverTips(card.HoverTips)
        };
    }

    private static Dictionary<string, object?> BuildWikiRelicResult(WikiCandidate candidate, double? score)
    {
        var relic = candidate.Relic!;
        var result = new Dictionary<string, object?>
        {
            ["item_type"] = "relic",
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["rarity"] = SafeGetText(() => relic.Rarity),
            ["description"] = SafeGetText(() => relic.DynamicDescription),
            ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
        };
        if (score != null) result["score"] = Math.Round(score.Value, 3);
        return result;
    }

    private static Dictionary<string, object?> BuildWikiPotionResult(WikiCandidate candidate, double? score)
    {
        var potion = candidate.Potion!;
        var result = new Dictionary<string, object?>
        {
            ["item_type"] = "potion",
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["rarity"] = SafeGetText(() => potion.Rarity),
            ["target_type"] = SafeGetText(() => potion.TargetType.ToString()),
            ["description"] = SafeGetText(() => potion.DynamicDescription),
            ["keywords"] = BuildHoverTips(potion.ExtraHoverTips)
        };
        if (score != null) result["score"] = Math.Round(score.Value, 3);
        return result;
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
        PotionModel? Potion = null);
}
