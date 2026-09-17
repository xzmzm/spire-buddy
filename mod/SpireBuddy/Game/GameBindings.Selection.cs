using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace SpireBuddy.Game;

internal static partial class GameBindings
{
    private static void AddSelectionState(NCardGridSelectionScreen screen, Dictionary<string, object?> state)
    {
        // Read actual selected models, including multi-select intermediate states.
        if (GetInstanceFieldValue(screen, "_selectedCards") is IEnumerable<CardModel> selectedModels)
        {
            var selected = selectedModels.ToHashSet();
            var models = FindAllSortedByPosition<NGridCardHolder>(screen).Where(h => h.CardModel != null).Select(h => h.CardModel!).ToList();
            if (state["cards"] is List<Dictionary<string, object?>> cards && models.Count == cards.Count)
            {
                var indices = new List<int>();
                for (int i = 0; i < cards.Count; i++)
                {
                    cards[i]["selected"] = selected.Contains(models[i]);
                    if (selected.Contains(models[i])) indices.Add(i);
                }
                state["selected_indices"] = indices;
                state["selected_count"] = indices.Count;
                state["selection_state_source"] = "game_ui";
            }
        }
        if (screen is not NDeckEnchantSelectScreen) return;
        var single = screen.GetNodeOrNull<Control>("%EnchantSinglePreviewContainer");
        var multi = screen.GetNodeOrNull<Control>("%EnchantMultiPreviewContainer");
        state["preview_showing"] = single?.Visible == true || multi?.Visible == true;
        var preview = single?.Visible == true ? single : multi?.Visible == true ? multi : null;
        if (preview == null) return;
        state["can_confirm"] = IsControlVisibleOrActionable(preview.GetNodeOrNull<NConfirmButton>("Confirm"));
        state["can_cancel"] = IsControlVisibleOrActionable(preview.GetNodeOrNull<NBackButton>("Cancel"));
        var previewCards = new List<Dictionary<string, object?>>();
        AddPreviewCardsFromContainer(preview, previewCards);
        state["preview_cards"] = previewCards;
    }
}
