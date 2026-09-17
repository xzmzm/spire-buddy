using Godot;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireBuddy.Game;

internal static partial class GameBindings
{
    private static Task? pendingOperation;
    private static void Track(Task task)
    {
        // Purchases may wait for a card-selection screen, so don't block the main thread.
        // Include any outstanding operation so subsequent reads observe all failures.
        pendingOperation = pendingOperation == null ? task : Task.WhenAll(pendingOperation, task);
    }

    private static void AddShopControls(Dictionary<string, object?> state, Node? room, NMerchantInventory? inventory, NClickableControl? merchant)
    {
        bool open = inventory?.IsOpen == true;
        state["inventory_open"] = open;
        state["can_open"] = !open && inventory != null && IsControlVisibleOrActionable(merchant);
        state["can_close"] = open && room != null && FindAll<NBackButton>(room).Any(IsControlVisibleOrActionable);
    }

    private static Dictionary<string, object?> ExecuteShopControl(bool open)
    {
        Node? room = RunManager.Instance.DebugOnlyGetState()?.CurrentRoom switch
        {
            MerchantRoom => NMerchantRoom.Instance,
            EventRoom { CanonicalEvent: FakeMerchant } => NEventRoom.Instance == null ? null : FindFirst<NFakeMerchant>(NEventRoom.Instance),
            _ => null
        };
        if (room == null) return Error("Shop is not available.");
        NClickableControl? button = open
            ? room switch { NMerchantRoom merchant => merchant.MerchantButton, NFakeMerchant fake => fake.MerchantButton, _ => null }
            : FindAll<NBackButton>(room).FirstOrDefault(IsControlVisibleOrActionable);
        if (!IsControlVisibleOrActionable(button)) return Error("Shop control is not available.");
        button!.ForceClick();
        return new() { ["status"] = "ok" };
    }

    private static Dictionary<string, object?> ExecuteOpenChest()
    {
        var room = FindFirst<NTreasureRoom>(((SceneTree)Engine.GetMainLoop()).Root);
        var button = room?.GetNodeOrNull<NClickableControl>("Chest");
        if (!IsControlVisibleOrActionable(button)) return Error("Chest is not available.");
        button!.ForceClick();
        return new() { ["status"] = "ok" };
    }
}
