using System.Globalization;
using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

// A deliberately small proof search, not a replacement combat simulator. Only
// adapter-audited attacks and hook combinations qualify. A missing proof says
// nothing about whether a better line involving other cards/potions exists.
internal static class JevCombat
{
    const int NodeLimit = 20000;
    static readonly HashSet<string> PlayerPowers = new("STRENGTH_POWER WEAK_POWER VULNERABLE_POWER DEXTERITY_POWER FRAIL_POWER VIGOR_POWER FREE_ATTACK_POWER CRUELTY_POWER".Split(' '));
    static readonly HashSet<string> EnemyPowers = new("STRENGTH_POWER WEAK_POWER VULNERABLE_POWER SLOW_POWER".Split(' '));
    sealed record Attack(int Index, string Name, decimal Base, int Hits, int Cost, int Stars, int Vulnerable, int FreeAttack);
    sealed record Enemy(string Id, int Hp, int Block, bool Vulnerable, int? Slow);

    internal static JsonObject? Lethal(JsonNode state, JsonArray actions)
    {
        if (!GameState.Combat(state) || !state["battle"].Flag("lethal_check_supported")
            || state["battle"].Flag("actions_pending") || !state["battle"].Flag("is_play_phase", true)
            || state["battle"].Text("turn") != "player") return null;
        var player = state["player"];
        if (player.Num("energy") is not int energy || energy < 0 || player.Num("hp") is not > 0
            || player?["status"] is not JsonArray || !Known(player, PlayerPowers)) return null;
        var rawEnemies = state["battle"]?["enemies"].Items().ToArray() ?? [];
        if (rawEnemies.Length is < 1 or > 4 || rawEnemies.Any(e => e.Num("hp") is not > 0 || e.Num("block") is not >= 0
            || e["status"] is not JsonArray || !Known(e, EnemyPowers) || e.Text("entity_id").Length == 0)) return null;
        var enemies = rawEnemies.Select(e => new Enemy(e.Text("entity_id"), e.Num("hp")!.Value, e.Num("block")!.Value,
            Amount(e, "VULNERABLE_POWER") > 0, Has(e, "SLOW_POWER") ? Amount(e, "SLOW_POWER") : null)).ToArray();
        if (enemies.Select(e => e.Id).Distinct().Count() != enemies.Length || enemies.Any(e => e.Slow is < 0)) return null;
        var strength = Amount(player, "STRENGTH_POWER");
        var vigor = Amount(player, "VIGOR_POWER");
        var free = Amount(player, "FREE_ATTACK_POWER");
        var weak = Amount(player, "WEAK_POWER") > 0 ? .75m : 1m;
        var vulnerableMultiplier = 1.5m + Amount(player, "CRUELTY_POWER") / 100m
            + (player?["relics"].Items().Any(r => r.Text("id") == "PAPER_PHROG") == true ? .25m : 0);
        var stars = player.Num("stars") ?? 0;
        if (vigor < 0 || free < 0 || stars < 0 || vulnerableMultiplier < 1) return null;
        var cards = new List<Attack>();
        foreach (var card in player?["hand"].Items() ?? [])
        {
            var p = card["lethal_profile"];
            if (p == null || card.Num("index") is not int index || index < 0
                || !card.Flag("can_play") && card.Text("unplayable_reason") != "EnergyCostTooHigh"
                || !decimal.TryParse(p["base_damage"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var damage)
                || damage < 0 || damage > 1000000 || p.Num("hits") is not int hits || hits is < 1 or > 10
                || p.Num("energy_cost") is not int cost || cost is < 0 or > 99 || p.Num("star_cost") is not int starCost || starCost < 0
                || p.Num("vulnerable") is not int vulnerable || vulnerable < 0 || p.Num("free_attack") is not int freeAttack || freeAttack is < 0 or > 1) continue;
            var attack = new Attack(index, card.Text("name"), damage, hits, cost, starCost, vulnerable, freeAttack);
            // The independent arithmetic must agree with the native game preview
            // for EVERY target before it is allowed to prove a future sequence.
            if (card.Num("cost") != (free > 0 ? 0 : cost) || enemies.Any(e =>
                card["target_previews"].Items().FirstOrDefault(t => t.Text("target") == e.Id)?["damage_values"] is not JsonObject values
                || values.Count != 1 || !int.TryParse(values.First().Value?.ToString(), out var native)
                || native != PerHit(attack, e, vigor))) continue;
            cards.Add(attack);
        }
        if (cards.Count is < 1 or > 15 || cards.Select(c => c.Index).Distinct().Count() != cards.Count) return null;
        var nodes = 0;
        for (int depth = 1; depth <= Math.Min(cards.Count, 8) && nodes < NodeLimit; depth++)
        {
            var seen = new HashSet<string>();
            if (Search(enemies, energy, stars, vigor, free, 0, depth, seen) is not List<JsonNode> steps) continue;
            var first = steps[0];
            var action = actions.Items().FirstOrDefault(a => a["command"].Text("action") == "play_card"
                && a["command"].Num("card_index") == first.Num("card_index") && a["command"].Text("target") == first.Text("target"));
            if (action == null) return null;
            return new JsonObject { ["action_id"] = action.Text("id"), ["steps"] = new JsonArray(steps.ToArray()),
                ["reason"] = "Verified attack sequence defeats every enemy before its intent. Execute only its first action, then re-read the game and recompute.",
                ["source"] = "jev_lethal", ["prompt_version"] = JevContext.PromptVersion };
        }
        return null;

        int PerHit(Attack card, Enemy enemy, int currentVigor)
        {
            var damage = Math.Floor(Math.Max(0, (card.Base + strength + currentVigor) * weak
                * (enemy.Vulnerable ? vulnerableMultiplier : 1m) * (1m + (enemy.Slow ?? 0) / 100m)));
            return damage <= int.MaxValue ? (int)damage : -1;
        }

        List<JsonNode>? Search(Enemy[] remaining, int leftEnergy, int leftStars, int currentVigor, int freeAttacks,
            int used, int leftDepth, HashSet<string> seen)
        {
            if (++nodes > NodeLimit) return null;
            if (remaining.All(e => e.Hp <= 0)) return [];
            if (leftDepth == 0) return null;
            var key = $"{used}/{leftEnergy}/{leftStars}/{currentVigor}/{freeAttacks}/{leftDepth}/" + string.Join(';', remaining.Select(e => $"{e.Hp},{e.Block},{e.Vulnerable},{e.Slow}"));
            if (!seen.Add(key)) return null;
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i]; var cost = freeAttacks > 0 ? 0 : card.Cost;
                if ((used & 1 << i) != 0 || cost > leftEnergy || card.Stars > leftStars) continue;
                for (int target = 0; target < remaining.Length; target++)
                {
                    var enemy = remaining[target];
                    if (enemy.Hp <= 0) continue;
                    // Only the first step must be currently legal; later costs
                    // can become affordable through Unrelenting's discount.
                    if (used == 0 && !actions.Items().Any(a => a["command"].Text("action") == "play_card"
                        && a["command"].Num("card_index") == card.Index && a["command"].Text("target") == enemy.Id)) continue;
                    var next = (Enemy[])remaining.Clone();
                    var perHit = PerHit(card, enemy, currentVigor); var hp = enemy.Hp; var block = enemy.Block; var dealt = 0;
                    if (perHit < 0 || (long)perHit * card.Hits > int.MaxValue) continue;
                    for (int hit = 0; hit < card.Hits && hp > 0; hit++)
                    {
                        dealt += perHit;
                        hp = Math.Max(0, hp - Math.Max(0, perHit - block));
                        block = Math.Max(0, block - perHit);
                    }
                    next[target] = enemy with { Hp = hp, Block = block, Vulnerable = enemy.Vulnerable || card.Vulnerable > 0 };
                    // Slow grows AFTER the entire card, including all its hits.
                    for (int j = 0; j < next.Length; j++)
                        if (next[j].Slow is int slow) next[j] = next[j] with { Slow = slow + 10 };
                    var tail = Search(next, leftEnergy - cost, leftStars - card.Stars, 0,
                        Math.Max(0, freeAttacks - 1) + card.FreeAttack, used | 1 << i, leftDepth - 1, seen);
                    if (tail == null) continue;
                    tail.Insert(0, new JsonObject { ["card_index"] = card.Index, ["card"] = card.Name, ["target"] = enemy.Id,
                        ["energy_cost"] = cost, ["damage"] = dealt, ["hp_damage"] = enemy.Hp - hp, ["enemy_hp_after"] = hp });
                    return tail;
                }
            }
            return null;
        }
    }

    static bool Known(JsonNode node, HashSet<string> known) => node["status"].Items().All(p => known.Contains(p.Text("id")) && p.Num("amount") != null);
    static bool Has(JsonNode? node, string id) => node?["status"].Items().Any(p => p.Text("id") == id) == true;
    static int Amount(JsonNode? node, string id) => node?["status"].Items().FirstOrDefault(p => p.Text("id") == id).Num("amount") ?? 0;
}
