using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace SpireBuddy.Game;

internal static partial class GameBindings
{
    // Deliberately expose only the singleplayer path, never quit/abandon/profile changes.
    private static List<(string Name, Action Click)> MenuCommands(Node root)
    {
        var commands = new List<(string, Action)>();
        var tutorial = FindVisibleAcceptTutorialsFtue(root);
        if (tutorial != null && IsFtueNodeActive(tutorial))
        {
            if (GetInstanceFieldValue(tutorial, "_verticalPopup") is NVerticalPopup popup)
                foreach (var (name, button) in GetPopupOptions(popup)) commands.Add((name, button.ForceClick));
            return commands;
        }
        var ftue = FindVisibleGenericFtue(root);
        if (ftue != null)
        {
            if (FindFtueAdvanceButton(ftue) is { } button)
            {
                commands.Add(("advance", button.ForceClick));
                // Older game screens and the state contract both use
                // "proceed" for the same tutorial acknowledgement.
                commands.Add(("proceed", button.ForceClick));
            }
            return commands;
        }
        var popupOptions = FindVisibleVerticalPopup(root) is { } modal
            ? GetPopupOptions(modal) : GetVisiblePopupButtonOptions(root);
        if (popupOptions.Count > 0)
        {
            foreach (var (name, button) in popupOptions) commands.Add((name, button.ForceClick));
            return commands;
        }
        var characters = FindAll<NCharacterSelectScreen>(root).FirstOrDefault(IsNodeVisible);
        if (characters != null)
        {
            if (characters.Lobby?.NetService.Type.IsMultiplayer() == true) return commands;
            var selected = GetInstanceFieldValue(characters, "_selectedButton");
            foreach (var button in FindAll<NCharacterSelectButton>(characters).Where(b => IsNodeVisible(b) && !b.IsLocked))
                if (button.Character != null && !ReferenceEquals(button, selected))
                    commands.Add((button.Character.Id.Entry, button.Select));
            Add(characters, "_embarkButton", "embark");
            Add(characters, "_backButton", "back");
            return commands;
        }
        var modes = FindAll<NSingleplayerSubmenu>(root).FirstOrDefault(IsNodeVisible);
        if (modes != null) { Add(modes, "_standardButton", "standard"); Add(modes, "_backButton", "back"); return commands; }
        var main = FindAll<NMainMenu>(root).FirstOrDefault(IsNodeVisible);
        if (main != null) { Add(main, "_continueButton", "continue"); Add(main, "_singleplayerButton", "singleplayer"); }
        return commands;
        void Add(object owner, string field, string name)
        {
            if (GetInstanceFieldValue(owner, field) is NClickableControl button && IsControlVisibleOrActionable(button))
                commands.Add((name, button.ForceClick));
        }
    }

    private static Dictionary<string, object?> BuildMenu(Node root)
    {
        var result = new Dictionary<string, object?>
        {
            ["state_type"] = "menu",
            ["options"] = MenuCommands(root).Select(c => c.Name).ToList()
        };
        var screen = FindAll<NCharacterSelectScreen>(root).FirstOrDefault(IsNodeVisible);
        if (screen != null)
        {
            result["menu_screen"] = "character_select";
            result["selected_character"] = (GetInstanceFieldValue(screen, "_selectedButton") as NCharacterSelectButton)?.Character?.Id.Entry;
            result["characters"] = FindAll<NCharacterSelectButton>(screen).Where(IsNodeVisible).Select(b => new
            {
                id = b.Character?.Id.Entry, name = SafeGetText(() => b.Character?.Title), locked = b.IsLocked
            }).ToList();
        }
        return result;
    }

    internal static Dictionary<string, object?> Execute(JsonNode command)
    {
        string name = command["action"]?.ToString() ?? "";
        if (name == "menu_select")
        {
            string option = command["option"]?.ToString() ?? "";
            var entry = MenuCommands(((SceneTree)Engine.GetMainLoop()).Root).FirstOrDefault(c => c.Name == option);
            if (entry.Click == null) return Error("Menu option is not available.");
            entry.Click();
            return new() { ["status"] = "ok" };
        }
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(command.ToJsonString())!;
        return ExecuteAction(name, data);
    }
}
