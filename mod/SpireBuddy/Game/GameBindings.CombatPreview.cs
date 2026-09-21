using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SpireBuddy.Game;

internal static partial class GameBindings
{
    // GetDescriptionForPile formats cached PreviewValue fields; it does not
    // recalculate them. Refresh exactly as the game's card UI does, then restore
    // presentation fields so inspecting a different target cannot change the UI.
    private static T WithFreshCardPreview<T>(CardModel card, Creature? target, Func<T> read)
    {
        var saved = card.DynamicVars.Values.Select(v => (Var: v, v.PreviewValue, v.EnchantedValue)).ToArray();
        try
        {
            card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, card.DynamicVars);
            return read();
        }
        finally
        {
            foreach (var entry in saved)
            {
                entry.Var.PreviewValue = entry.PreviewValue;
                entry.Var.EnchantedValue = entry.EnchantedValue;
            }
        }
    }

    private static void AddCombatCardPreview(CardModel card, Dictionary<string, object?> state)
    {
        if (card.Type != CardType.Attack || card.CombatState == null) return;
        var previews = new List<Dictionary<string, object?>>();
        foreach (var target in card.CombatState.HittableEnemies)
        {
            try
            {
                previews.Add(WithFreshCardPreview(card, target, () => new Dictionary<string, object?>
                {
                    ["target"] = target.CombatId.ToString(),
                    ["description"] = StripRichTextTags(card.GetDescriptionForPile(PileType.Hand, target)).Replace("\n", " "),
                    // Each entry is a damage variable, not a summed prediction.
                    // Random targeting, conditions, extra hits and HP-loss hooks
                    // still require the card's rules and the target's powers.
                    ["damage_values"] = card.DynamicVars.Values.Where(v => v is DamageVar or CalculatedDamageVar)
                        .ToDictionary(v => v.Name, v => (int)v.PreviewValue)
                }));
            }
            catch { /* missing preview is unknown, never zero damage */ }
        }
        state["target_previews"] = previews;
        try
        {
            // Only audited, deterministic attacks enter the small lethal search.
            // Other attacks still receive game-calculated target previews above.
            if (card.GetType().Assembly != typeof(CardModel).Assembly || card.Enchantment != null || card.Affliction != null
                || card.GetEnchantedReplayCount() != 0 || card.EnergyCost.CostsX || card.HasStarCostX) return;
            if (card is not (StrikeIronclad or StrikeSilent or StrikeDefect or StrikeRegent or StrikeNecrobinder
                or Bash or TwinStrike or Unrelenting or PerfectedStrike)) return;
            state["lethal_profile"] = new Dictionary<string, object?>
            {
                ["base_damage"] = card is PerfectedStrike ? card.DynamicVars.CalculatedDamage.Calculate(null) : card.DynamicVars.Damage.BaseValue,
                ["hits"] = card is TwinStrike ? 2 : 1,
                ["energy_cost"] = card.EnergyCost.GetWithModifiers(CostModifiers.Local),
                ["star_cost"] = Math.Max(0, card.GetStarCostWithModifiers()),
                ["vulnerable"] = card is Bash ? card.DynamicVars.Vulnerable.IntValue : 0,
                ["free_attack"] = card is Unrelenting ? 1 : 0
            };
        }
        catch { /* omit unsupported projections */ }
    }

    // Fail closed on unmodelled combat hooks, including invisible powers, other
    // cards/piles, monster death phases and mod subscribers. Inspect types only;
    // never inspect hidden rolls or mutate a simulated live game state.
    private static readonly HashSet<Type> LethalKnownHooks =
    [
        typeof(StrengthPower), typeof(WeakPower), typeof(VulnerablePower), typeof(SlowPower),
        typeof(DexterityPower), typeof(FrailPower), typeof(VigorPower), typeof(FreeAttackPower),
        typeof(CrueltyPower), typeof(PaperPhrog), typeof(BurningBlood), typeof(PhialHolster)
    ];
    private static readonly Dictionary<Type, bool> LethalHookTypes = new();
    private static bool SupportsLethalSearch(ICombatState combat)
    {
        if (combat.Players.Count != 1 || combat.Allies.Any(c => c.Player == null)
            || combat.Players.Any(p => p.Relics.Any(r => r.IsMelted))) return false;
        foreach (var model in combat.IterateHookListeners())
        {
            var type = model.GetType();
            if (!LethalHookTypes.TryGetValue(type, out var supported))
            {
                supported = type.Assembly == typeof(AbstractModel).Assembly && (LethalKnownHooks.Contains(type)
                    || !type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m =>
                        !m.IsSpecialName && m.IsVirtual && m.Name != nameof(AbstractModel.CompareTo)
                        && m.DeclaringType != typeof(AbstractModel) && m.GetBaseDefinition().DeclaringType == typeof(AbstractModel))
                    && !(model is MonsterModel && type.GetMethod(nameof(MonsterModel.BeforeRemovedFromRoom))?.DeclaringType != typeof(MonsterModel)));
                LethalHookTypes[type] = supported;
            }
            if (!supported) return false;
        }
        // A missing/hidden power must not silently disappear from the public
        // arithmetic, even when its type is otherwise understood.
        return combat.Allies.Concat(combat.Enemies).All(c => BuildPowersState(c).Count == c.Powers.Count);
    }
}
