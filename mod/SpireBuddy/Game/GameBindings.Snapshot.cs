// Gameplay bindings adapted from STS2 MCP (MIT); see SpireBuddy.third-party-notices.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using Godot;

namespace SpireBuddy.Game;

    internal static partial class GameBindings
    {
        // Scene fades and NetLoadingHandle scopes (new-run embark, map/room
        // entry, ancient-event intros) can run far longer than the runtime's
        // settle budget on slow loads. The game's own transition markers tell
        // the controller this delay is progress, not a stuck screen.
        private static bool TransitionInProgress()
        {
            try
            {
                if (NGame.Instance?.Transition?.InTransition == true) return true;
                return RunManager.Instance.NetService?.IsGameLoading == true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (NullReferenceException) { return false; }
        }

        internal static Dictionary<string, object?> ReadState()
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            var result = new Dictionary<string, object?>();
            if (pendingOperation is { IsCompleted: true })
            {
                var completed = pendingOperation;
                pendingOperation = null;
                completed.GetAwaiter().GetResult(); // Surface failures without issuing the action again.
            }
            var popup = BuildVisibleFtueState(tree.Root);
            if (popup != null) return popup;
            if (!RunManager.Instance.IsInProgress)
            {
                var menu = BuildMenu(tree.Root);
                if (TransitionInProgress()) menu["loading"] = true;
                return menu;
            }
        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null) return new() { ["state_type"] = "unknown" };
        if (runState.Players.Count != 1)
            return new() { ["state_type"] = "unsupported", ["message"] = "Buddy supports singleplayer runs only." };
        // Overlays can appear on top of any room (events, rest sites, combat).
        // Rewards/card-reward overlays defer to the map - they may linger on the
        // overlay stack while the map opens after the player clicks proceed.
        var topOverlay = NOverlayStack.Instance?.Peek();
        var currentRoom = runState.CurrentRoom;
        bool liveCombat = currentRoom is CombatRoom && CombatManager.Instance.IsInProgress;
        // Selection overlays replace state_type even during combat (including
        // Choices Paradox before the first turn). Keep fight ownership separate
        // from the visible screen so the solver also owns these choices.
        result["in_combat"] = liveCombat;
        bool mapIsVisible = IsNodeVisible(NMapScreen.Instance);
        // NMapScreen.IsOpen can briefly linger after a combat map view is closed.
        // During a live fight, visibility is the authoritative signal; otherwise a
        // stale IsOpen flag can keep hiding the actual combat forever.
        bool mapIsOpen = liveCombat ? mapIsVisible : IsMapScreenOpenOrVisible();
        // Opening the map during a live combat is only a player-owned reference
        // view, not a transition to the run map. Treat it as a pause overlay so
        // Buddy neither mistakes the fight for finished nor tries to act through it.
        bool combatMapView = liveCombat && mapIsVisible;
        if (combatMapView)
        {
            result["state_type"] = "overlay";
            result["overlay"] = new Dictionary<string, object?>
            {
                ["screen_type"] = "combat_map",
                ["message"] = "The map is open over combat. Waiting for the player to close it.",
                ["wait_for_player"] = true
            };
        }
        else if (topOverlay is NCardGridSelectionScreen cardSelectScreen)
        {
            result["state_type"] = "card_select";
            result["card_select"] = BuildCardSelectState(cardSelectScreen, runState);
        }
        else if (topOverlay is NChooseACardSelectionScreen chooseCardScreen)
        {
            result["state_type"] = "card_select";
            result["card_select"] = BuildChooseCardState(chooseCardScreen, runState);
        }
        else if (topOverlay is NChooseABundleSelectionScreen bundleScreen)
        {
            result["state_type"] = "bundle_select";
            result["bundle_select"] = BuildBundleSelectState(bundleScreen, runState);
        }
        else if (topOverlay is NChooseARelicSelection relicSelectScreen)
        {
            result["state_type"] = "relic_select";
            result["relic_select"] = BuildRelicSelectState(relicSelectScreen, runState);
        }
        else if (!mapIsOpen && topOverlay is NCrystalSphereScreen crystalSphereScreen)
        {
            // Mirror the NRewardsScreen guard below: the Crystal Sphere overlay
            // lingers on the stack after proceed is clicked (only ClearScreens
            // on the next room transition pops it). Once the map opens on top,
            // report "map" so state matches the screen the player can interact
            // with. See issue #73.
            result["state_type"] = "crystal_sphere";
            result["crystal_sphere"] = BuildCrystalSphereState(crystalSphereScreen, runState);
        }
        else if (!mapIsOpen && topOverlay is NCardRewardSelectionScreen cardRewardScreen)
        {
            result["state_type"] = "card_reward";
            result["card_reward"] = BuildCardRewardState(cardRewardScreen);
        }
        else if (!mapIsOpen && topOverlay is NRewardsScreen rewardsScreen)
        {
            result["state_type"] = "rewards";
            result["rewards"] = BuildRewardsState(rewardsScreen, runState);
        }
        else if (topOverlay is NGameOverScreen gameOverScreen)
        {
            result["state_type"] = "game_over";
            // The game records the finished run's history on the way to this
            // screen; its public outcome decides victory wording.
            var finished = RunManager.Instance.History;
            var gameOver = new Dictionary<string, object?>
            {
                ["message"] = finished == null ? "Run ended." : finished.Win ? "Victory! The run is won." : "The run ended in defeat.",
                ["victory"] = finished?.Win == true,
                ["options"] = new List<string> { "main_menu" }
            };
            if (finished != null)
            {
                gameOver["floors_reached"] = finished.MapPointHistory?.Sum(points => points.Count) ?? 0;
                var encounter = finished.KilledByEncounter;
                var killer = SafeGetText(() => encounter.Entry) ?? "";
                if (killer.Length > 0) gameOver["killed_by"] = killer;
                var killerEvent = finished.KilledByEvent;
                var eventKill = SafeGetText(() => killerEvent.Entry) ?? "";
                if (eventKill.Length > 0) gameOver["killed_by_event"] = eventKill;
            }
            result["game_over"] = gameOver;
        }
        else if (topOverlay is IOverlayScreen
                 && topOverlay is CanvasItem overlayCanvas
                 && IsNodeVisible(overlayCanvas)
                 && topOverlay is not NRewardsScreen
                 && topOverlay is not NCardRewardSelectionScreen
                 && topOverlay is not NCrystalSphereScreen)
        {
            // Catch-all for visible, unhandled overlays. Hidden screens can linger
            // on the overlay stack after closing, so they must not mask the real
            // room state. Visible unhandled overlays are player-owned pauses and
            // should not trip the gameplay settle watchdog.
            result["state_type"] = "overlay";
            result["overlay"] = new Dictionary<string, object?>
            {
                ["screen_type"] = topOverlay.GetType().Name,
                ["message"] = $"An overlay ({topOverlay.GetType().Name}) is active. It may require manual interaction in-game.",
                ["wait_for_player"] = true
            };
        }
        else if (mapIsOpen)
        {
            result["state_type"] = "map";
            result["map"] = BuildMapState(runState);
        }
        else if (currentRoom is CombatRoom combatRoom)
        {
            if (CombatManager.Instance.IsInProgress)
            {
                // Check for in-combat hand card selection (e.g., "Select a card to exhaust")
                var playerHand = NPlayerHand.Instance;
                if (playerHand != null && playerHand.IsInCardSelection)
                {
                    result["state_type"] = "hand_select";
                    result["hand_select"] = BuildHandSelectState(playerHand, runState);
                }
                else
                {
                    result["state_type"] = combatRoom.RoomType.ToString().ToLower(); // monster, elite, boss
                }
            }
            else
            {
                // After combat ends - reward/card overlays are caught by top-level checks above.
                // Only handle map and the brief transition before rewards appear.
                if (IsMapScreenOpenOrVisible())
                {
                    result["state_type"] = "map";
                    result["map"] = BuildMapState(runState);
                }
                else
                {
                    result["state_type"] = "transition";
                    result["message"] = "Combat ended. Waiting for rewards...";
                }
            }
        }
        else if (currentRoom is EventRoom eventRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else if (eventRoom.CanonicalEvent is FakeMerchant)
            {
                result["state_type"] = "fake_merchant";
                result["fake_merchant"] = BuildFakeMerchantState(eventRoom, runState);
            }
            else
            {
                result["state_type"] = "event";
                result["event"] = BuildEventState(eventRoom, runState);
            }
        }
        else if (currentRoom is MapRoom)
        {
            result["state_type"] = "map";
            result["map"] = BuildMapState(runState);
        }
        else if (currentRoom is MerchantRoom merchantRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                result["state_type"] = "shop";
                result["shop"] = BuildShopState(merchantRoom, runState);
            }
        }
        else if (currentRoom is RestSiteRoom restSiteRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                result["state_type"] = "rest_site";
                result["rest_site"] = BuildRestSiteState(restSiteRoom, runState);
            }
        }
        else if (currentRoom is TreasureRoom treasureRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                result["state_type"] = "treasure";
                result["treasure"] = BuildTreasureState(treasureRoom, runState);
            }
        }
        else if (currentRoom == null && tree?.Root != null)
        {
            var activeCharSelect = FindFirst<NCharacterSelectScreen>(tree.Root);
            if (activeCharSelect != null && IsNodeVisible(activeCharSelect))
            {
                return BuildMenu(tree.Root);
            }
            else
            {
                result["state_type"] = "unknown";
                result["room_type"] = currentRoom?.GetType().Name;
            }
        }
        else
        {
            result["state_type"] = "unknown";
            result["room_type"] = currentRoom?.GetType().Name;
        }

        if (liveCombat && currentRoom is CombatRoom activeCombatRoom)
            result["battle"] = BuildBattleState(runState, activeCombatRoom);

        // Common run info
        result["run"] = new Dictionary<string, object?>
        {
            ["act"] = runState.CurrentActIndex + 1,
            ["floor"] = runState.TotalFloor,
            ["ascension"] = runState.AscensionLevel
        };

        if (TransitionInProgress()) result["loading"] = true;

        // Always include full player data (relics, potions, deck, etc.) on every screen
        var _player = LocalContext.GetMe(runState);
        if (_player != null)
        {
            result["player"] = BuildPlayerState(_player);
        }

        return result;
    }

}
