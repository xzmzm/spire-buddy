using Godot;

namespace SpireBuddy;

public static partial class Mod
{
    // PopupMenu in the game's Godot build expands to its full item height.
    // A real ScrollContainer keeps both players' model lists inside the window.
    sealed class ModelMenu
    {
        readonly Button trigger;
        readonly LineEdit field;
        readonly PopupPanel popup = new();
        readonly ScrollContainer scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        readonly VBoxContainer items = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        internal ModelMenu(Node parent, LineEdit field, Action load, string tooltip = "")
        {
            this.field = field;
            trigger = new Button { Text = "▾", TooltipText = tooltip }; parent.AddChild(trigger);
            trigger.AddChild(popup); popup.AddChild(scroll); scroll.AddChild(items);
            trigger.Pressed += () => { if (popup.Visible) popup.Hide(); else load(); };
        }

        internal bool Disabled { get => trigger.Disabled; set => trigger.Disabled = value; }
        internal void Hide() => popup.Hide();
        internal void Loading(string text) { Populate([], text); Open(); }
        internal void Models(IEnumerable<string> models, string empty)
        {
            Populate(models.ToArray(), empty);
            if (popup.Visible) Open();
        }

        void Populate(string[] models, string empty)
        {
            foreach (var child in items.GetChildren()) { items.RemoveChild(child); child.QueueFree(); }
            foreach (var model in models)
            {
                var option = new Button { Text = model, TooltipText = model, Alignment = HorizontalAlignment.Left,
                    ClipText = true, CustomMinimumSize = new Vector2(0, 34), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                option.Pressed += () => { field.Text = model; popup.Hide(); };
                items.AddChild(option);
            }
            if (models.Length == 0)
                items.AddChild(new Button { Text = empty, Disabled = true, ClipText = true, CustomMinimumSize = new Vector2(0, 34) });
            scroll.ScrollVertical = 0;
        }

        void Open()
        {
            var available = popup.IsEmbedded() ? Tree.Root.GetVisibleRect().Size : (Vector2)Tree.Root.Size;
            int width = Math.Max(1, (int)Math.Min(520, available.X - 24));
            int height = Math.Max(1, (int)Math.Min(Math.Min(480, available.Y * .6f), items.GetChildCount() * 38 + 12));
            scroll.CustomMinimumSize = new Vector2(Math.Max(1, width - 12), Math.Max(1, height - 12));
            var position = (Vector2I)(trigger.GetScreenTransform() * new Vector2(0, trigger.Size.Y));
            popup.Popup(new Rect2I(position, new Vector2I(width, height)));
        }
    }
}
