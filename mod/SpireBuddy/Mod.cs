using System.Collections.Concurrent;
using SpireBuddy.Runtime;
using SpireBuddy.Game;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace SpireBuddy;

[ModInitializer(nameof(Initialize))]
public static class Mod
{
    private static BotRuntime Runtime = null!;
    private static readonly ConcurrentQueue<Action> Main = new();
    private static SceneTree Tree = null!;
    private static PanelContainer Panel = null!;
    private static VBoxContainer Body = null!, Messages = null!;
    private static ScrollContainer Feed = null!;
    private static Label Status = null!, Notice = null!, Empty = null!, Activity = null!;
    private static Label Title = null!;
    private static string? FontLanguage;
    private static bool UiChinese;
    private static LineEdit Message = null!, Endpoint = null!, Model = null!, Key = null!;
    private static LineEdit JevEndpoint = null!, JevModel = null!, JevKey = null!;
    private static SpinBox MaxTokens = null!;
    private static CheckButton UseCombatSolver = null!, HideCombatSolverUi = null!, AutoTreasure = null!;
    private static CheckButton UseJevStrategy = null!, UseJevCombat = null!, ReviewJev = null!;
    private static Label SolverHint = null!;
    private static bool SolverAvailable, HideSolverOverlay;
    private static ulong NextSolverProbe;
    private static OptionButton Api = null!, Personality = null!, Effort = null!;
    private static VBoxContainer CustomPersonalityFields = null!;
    private static TextEdit CustomPersonality = null!;
    private static MenuButton ModelPicker = null!;
    private static Button Test = null!, TestJev = null!;
    private static bool Probing, ProbingJev;
    private static Button Save = null!, SendButton = null!, ResizeHandle = null!;
    private static bool Connected, Built, Polling, Busy, InitializedSettings, Alive, Dragging, Resizing;
    private static Vector2 DragOffset, ResizeStartMouse, ResizeStartSize, ExpandedSize;
    private static bool FollowChatBottom = true, ChatScrollQueued, AdjustingChatScroll;
    // Width belongs to the user's resize handle; the engine auto-grows the
    // rect to any inflated content minimum, so the intended width is tracked
    // apart from Panel.Size and enforced every frame.
    private static float PanelWidth;
    private static ulong NextPoll;
    private static long LastSequence;
    private static int BuddyMessageCount;
    private static readonly Dictionary<string, Label> Pending = new();
    private static string Conversation = "";
    private const string MaskedKey = BotRuntime.MaskedKey;
    private static bool KeyTouched, JevKeyTouched;
    private static ulong NoticeHideAt;

    public static void Initialize()
    {
        var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(Mod).Assembly)!;
        context.Resolving += (loader, name) =>
        {
            if (name.Name != "ICSharpCode.Decompiler") return null;
            var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Mod).Assembly.Location)!, name.Name + ".dll");
            return System.IO.File.Exists(path) ? loader.LoadFromAssemblyPath(path) : null;
        };
        StartRuntime();
        Tree = (SceneTree)Engine.GetMainLoop();
        Tree.Root.TreeExiting += SavePanelLayout;
        Tree.Root.TreeExiting += Runtime.Dispose;
        Tree.ProcessFrame += Frame;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void StartRuntime()
    {
        Runtime = new BotRuntime(ProjectSettings.GlobalizePath("user://spire-buddy"), typeof(MegaCrit.Sts2.Core.Models.ModelDb).Assembly.Location,
            new ScheduledGameAdapter(Main.Enqueue,
                () => JsonSerializer.SerializeToNode(GameBindings.ReadState())!,
                command => JsonSerializer.SerializeToNode(GameBindings.Execute(command))!,
                (query, itemType, rarity, character, offset, count) => JsonSerializer.SerializeToNode(GameBindings.SearchWiki(query, itemType, rarity, character, offset, count))!,
                () => JsonSerializer.SerializeToNode(GameBindings.ReadKnowledge())!,
                op => JsonSerializer.SerializeToNode(CombatSolverBridge.Dispatch(op))!));
    }

    private static void Frame()
    {
        if (!Built && Tree.Root != null) { Build(); Built = true; }
        while (Main.TryDequeue(out var action)) action();
        if (Built)
        {
            UpdateFonts();
            KeepPanelInView();
            if (!SolverAvailable && Time.GetTicksMsec() >= NextSolverProbe)
            {
                NextSolverProbe = Time.GetTicksMsec() + 1000;
                if (Game.CombatSolverBridge.Available) { SolverAvailable = true; UpdateSolverControls(); }
            }
            // The saved setting drives hiding, not the unsaved form. Deferred so
            // it runs after the solver's own processing each frame and wins
            // against its overlay re-showing right before drawing.
            if (HideSolverOverlay) Callable.From(Game.CombatSolverBridge.SuppressCombatOverlay).CallDeferred();
            if (Notice.Visible && NoticeHideAt > 0 && Time.GetTicksMsec() >= NoticeHideAt) { Notice.Visible = false; NoticeHideAt = 0; }
            var dots = new string('.', (int)(Time.GetTicksMsec() / 400 % 3) + 1);
            Activity.Text = L10n.T("Buddy is thinking", "Buddy 正在思考") + dots;
            foreach (var pending in Pending.Values) pending.Text = L10n.T("Replying", "回复中") + dots;
        }
        if (Built && !Polling && Time.GetTicksMsec() >= NextPoll)
        {
            Polling = true;
            _ = Request("GET", "/status?include_state=false", null, value =>
            {
                Polling = false; NextPoll = Time.GetTicksMsec() + 400;
                Connected = value != null;
                if (value != null) Render(value);
                else { Activity.Visible = false; Status.Text = L10n.T("Offline", "离线"); Save.Disabled = SendButton.Disabled = Test.Disabled = TestJev.Disabled = ModelPicker.Disabled = true; }
            });
        }
    }

    private static StyleBoxFlat Box(string color, int padding = 12, string border = "344254") => new()
    {
        BgColor = new Color(color), BorderColor = new Color(border),
        BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
        CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
        ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding,
    };

    private static void Build()
    {
        var layer = new CanvasLayer { Layer = 100, Name = "SpireBuddy" }; Tree.Root.AddChild(layer);
        var viewport = Tree.Root.GetVisibleRect().Size;
        // Match the reference layout: left edge, below the relics, above the cards.
        Panel = new PanelContainer { CustomMinimumSize = new Vector2(360, 0) };
        // Transforms set before AddChild are discarded when the control enters
        // the tree, so position and size it after adoption; a saved layout
        // overrides both at the end of Build.
        layer.AddChild(Panel);
        Panel.Position = new Vector2(0, viewport.Y * 0.148f);
        Panel.Size = new Vector2(viewport.X * 0.224f, viewport.Y * 0.592f);
        PanelWidth = Mathf.Max(Panel.Size.X, Panel.CustomMinimumSize.X);
        // Only the background is translucent; inherited Control.Modulate would fade text too.
        Panel.AddThemeStyleboxOverride("panel", Box("111923b3", 18, "536275"));
        var theme = new Theme { DefaultFontSize = 18 };
        theme.SetColor("font_color", "Label", new Color("e8edf3"));
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Box("263445", 9));
            theme.SetStylebox("hover", type, Box("35485e", 9, "86b8c3"));
            theme.SetStylebox("pressed", type, Box("355562", 9, "a8d7cf"));
            theme.SetStylebox("disabled", type, Box("1c2632", 9));
        }
        theme.SetStylebox("normal", "LineEdit", Box("0d141e", 10));
        theme.SetStylebox("focus", "LineEdit", Box("0d141e", 10, "83c9bc"));
        theme.SetStylebox("normal", "TextEdit", Box("0d141e", 10));
        theme.SetStylebox("focus", "TextEdit", Box("0d141e", 10, "83c9bc"));
        // Markdown replies are RichTextLabels; keep their look panel-native.
        theme.SetColor("default_color", "RichTextLabel", new Color("e8edf3"));
        theme.SetColor("font_selected_color", "RichTextLabel", new Color("111923"));
        theme.SetColor("selection_color", "RichTextLabel", new Color("3b566e"));
        theme.SetStylebox("normal", "RichTextLabel", new StyleBoxEmpty());
        theme.SetStylebox("focus", "RichTextLabel", new StyleBoxEmpty());
        foreach (var item in new[] { "normal_font_size", "bold_font_size", "italics_font_size" }) theme.SetFontSize(item, "RichTextLabel", 18);
        theme.SetFontSize("mono_font_size", "RichTextLabel", 16);
        Panel.Theme = theme;
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 14); Panel.AddChild(column);
        var header = new HBoxContainer(); column.AddChild(header);
        var title = Label(header, "✦  Spire Buddy"); title.AddThemeFontSizeOverride("font_size", 23);
        Title = title;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.MouseFilter = Control.MouseFilterEnum.Ignore;
        MakeDraggable(header);
        Status = Label(header, L10n.T("Connecting", "连接中")); Status.AddThemeFontSizeOverride("font_size", 14);
        Status.AddThemeColorOverride("font_color", new Color("91b9b1"));
        Body = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        ExpandedSize = new Vector2(Mathf.Max(Panel.Size.X, Panel.CustomMinimumSize.X), Panel.Size.Y);
        var collapse = Button(header, "−", () =>
        {
            if (Body.Visible) ExpandedSize = new Vector2(PanelWidth, Panel.Size.Y);
            Body.Visible = !Body.Visible;
            ResizeHandle.Visible = Body.Visible;
            Panel.Size = Body.Visible ? ExpandedSize : Vector2.Zero;
            if (Body.Visible) PanelWidth = Mathf.Max(ExpandedSize.X, Panel.CustomMinimumSize.X);
            KeepPanelInView();
        });
        collapse.TooltipText = L10n.T("Collapse / expand", "收起 / 展开");
        column.AddChild(Body);
        // The panel container sizes this overlay; the grip anchors independently
        // of the content so it follows both edges at every window size.
        var resizeOverlay = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        Panel.AddChild(resizeOverlay);
        // The overlay has the panel's 18px inset. Cover the entire top
        // padding, including the border, without overlapping header buttons.
        var topDragArea = new Control();
        resizeOverlay.AddChild(topDragArea);
        topDragArea.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        topDragArea.OffsetLeft = -18;
        topDragArea.OffsetRight = 18;
        topDragArea.OffsetTop = -18;
        topDragArea.OffsetBottom = 0;
        MakeDraggable(topDragArea);
        ResizeHandle = new Button
        {
            Text = "◢",
            TooltipText = L10n.T("Drag to resize", "拖动调整大小"),
            Flat = true,
            Alignment = HorizontalAlignment.Right,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(24, 18),
            MouseDefaultCursorShape = Control.CursorShape.Fdiagsize,
        };
        ResizeHandle.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        ResizeHandle.AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
        ResizeHandle.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
        ResizeHandle.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        ResizeHandle.AddThemeFontSizeOverride("font_size", 13);
        ResizeHandle.AddThemeColorOverride("font_color", new Color("71859a"));
        ResizeHandle.AddThemeColorOverride("font_hover_color", new Color("a8d7cf"));
        ResizeHandle.GuiInput += input =>
        {
            if (input is InputEventMouseButton mouse && mouse.ButtonIndex == MouseButton.Left)
            {
                if (mouse.Pressed)
                {
                    Resizing = true;
                    ResizeStartMouse = mouse.GlobalPosition;
                    ResizeStartSize = Panel.Size;
                }
                else if (Resizing)
                {
                    ResizePanel(mouse.GlobalPosition);
                    Resizing = false;
                    SavePanelLayout();
                }
                ResizeHandle.AcceptEvent();
            }
            else if (input is InputEventMouseMotion motion && Resizing)
            {
                ResizePanel(motion.GlobalPosition);
                ResizeHandle.AcceptEvent();
            }
        };
        resizeOverlay.AddChild(ResizeHandle);
        ResizeHandle.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        // Overlay ends at the panel's 18px content inset. Extend to 4px
        // from the outer corner, with a 24px hit target.
        ResizeHandle.OffsetLeft = -10;
        ResizeHandle.OffsetTop = -10;
        ResizeHandle.OffsetRight = 14;
        ResizeHandle.OffsetBottom = 14;
        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; Body.AddChild(tabs);
        tabs.AddThemeStyleboxOverride("tab_selected", Box("304858", 10, "5c897f"));
        tabs.AddThemeStyleboxOverride("tab_unselected", Box("1a2735", 10));
        tabs.AddThemeStyleboxOverride("tab_hovered", Box("293c4d", 10));
        tabs.AddThemeStyleboxOverride("panel", Box("11192300", 0, "11192300"));
        var chat = new VBoxContainer { Name = L10n.T("Chat", "聊天") }; chat.AddThemeConstantOverride("separation", 12); tabs.AddChild(chat);
        chat.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) });
        // ScrollContainer's combined minimum tracks its content while
        // horizontal scrolling is Disabled, so chat text (long words, URLs)
        // would inflate the panel's own minimum and auto-grow its rect.
        // ShowNever keeps the minimum at the design floor and still fits and
        // wraps the feed to the panel width.
        Feed = new ScrollContainer { CustomMinimumSize = new Vector2(0, 80), SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever }; chat.AddChild(Feed);
        var feedBar = Feed.GetVScrollBar();
        // Range changes arrive after container layout and text wrapping. Keep
        // following through every layout pass, until the reader scrolls away.
        feedBar.Changed += () => { if (FollowChatBottom) ScrollFeedToBottom(); };
        feedBar.ValueChanged += _ =>
        {
            if (!AdjustingChatScroll) FollowChatBottom = FeedIsNearBottom();
        };
        Messages = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; Messages.AddThemeConstantOverride("separation", 10); Feed.AddChild(Messages);
        Empty = Wrapped(Messages, L10n.T(
            "Your run, with a little company.\n\nTry one of these:\n• Win with ironclad\n• Win this fight\n• Continue this run\n• What should I do next?\n• What are my potion reward chances?\n• What moves can this enemy use?\n• What should I pick from these rewards?\n• Which card, relic, path, or shop option is best?\n\nSay ‘stop’ to stop playing. Buddy stays here to chat.",
            "你的爬塔之旅，有个伴儿。\n\n试试这些：\n• 用铁甲战士获胜\n• 赢下这场战斗\n• 继续这局游戏\n• 接下来该怎么走？\n• 药水奖励的概率是多少？\n• 这个敌人会用什么招式？\n• 这些奖励里选哪个好？\n• 卡牌、遗物、路线或商店，哪个最值？\n\n说“停”即可停止游玩，Buddy 随时陪你聊天。"));
        Empty.AddThemeColorOverride("font_color", new Color("8d9cae"));
        Activity = Wrapped(Messages, "Buddy is thinking."); Activity.Visible = false;
        Activity.AddThemeColorOverride("font_color", new Color("94cbbf"));
        Activity.CustomMinimumSize = new Vector2(0, 28);
        var composer = new HBoxContainer(); chat.AddChild(composer);
        Message = new LineEdit { PlaceholderText = L10n.T("Message Buddy…", "给 Buddy 发消息…"), MaxLength = 4000, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; composer.AddChild(Message);
        SendButton = Button(composer, L10n.T("Send", "发送"), SendMessage); Message.TextSubmitted += _ => SendMessage();
        var settingsTab = new VBoxContainer { Name = L10n.T("Settings", "设置") };
        settingsTab.AddThemeConstantOverride("separation", 10);
        tabs.AddChild(settingsTab);
        // ShowNever, not Disabled: the settings form must not push the panel's
        // minimum width around either.
        var settingsScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever,
        };
        settingsTab.AddChild(settingsScroll);
        var settings = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; settings.AddThemeConstantOverride("separation", 10); settingsScroll.AddChild(settings);
        Wrapped(settings, L10n.T("Make Buddy yours. Stop before saving changes.", "打造你的 Buddy。保存前请先停止游玩。"));
        var voice = SettingsGroup(settings, L10n.T("Buddy's personality", "Buddy 的性格"));
        Personality = Options(voice, L10n.T("Personality", "性格"), Personalities.Ids, Personalities.Label);
        CustomPersonalityFields = new VBoxContainer { Visible = false };
        CustomPersonalityFields.AddThemeConstantOverride("separation", 10);
        voice.AddChild(CustomPersonalityFields);
        Label(CustomPersonalityFields, L10n.T("Your personality", "你的自定义性格"));
        CustomPersonality = new TextEdit
        {
            Text = Personalities.CustomExample,
            PlaceholderText = Personalities.CustomExample,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 150),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        CustomPersonalityFields.AddChild(CustomPersonality);
        Wrapped(CustomPersonalityFields, L10n.T(
            "Describe how Buddy should talk. Edit the example to make it yours; leave this blank to use the example.",
            "描述你希望 Buddy 如何说话。修改示例，打造你的专属性格；留空则使用示例。"))
            .AddThemeFontSizeOverride("font_size", 14);
        Personality.ItemSelected += _ => CustomPersonalityFields.Visible = Selected(Personality) == "custom";
        var chatModel = SettingsGroup(settings, L10n.T("Buddy model & connection", "Buddy 模型与连接"));
        Wrapped(chatModel, L10n.T("Used for chat and any decisions not assigned to Jev or Combat Solver.", "用于聊天，以及未交给 Jev 或战斗路线求解器的决策。"));
        Endpoint = Input(chatModel, L10n.T("API endpoint", "API 端点"), "https://api.openai.com/v1");
        Key = Input(chatModel, L10n.T("API key", "API 密钥"), ""); Key.Secret = true; Key.SecretCharacter = "*"; Key.PlaceholderText = L10n.T("Enter API key", "输入 API 密钥");
        Key.TextChanged += _ => KeyTouched = true;
        Wrapped(chatModel, L10n.T("The key is stored with the game's user data and kept across restarts.", "密钥保存在游戏用户数据中，重启后依然有效。")).AddThemeFontSizeOverride("font_size", 14);
        Api = Options(chatModel, L10n.T("API format", "API 格式"), ["responses", "chat_completions"]);
        Label(chatModel, L10n.T("AI model · select or enter a custom ID", "AI 模型 · 从列表选择或输入自定义 ID"));
        var modelRow = new HBoxContainer(); chatModel.AddChild(modelRow);
        Model = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = L10n.T("Model ID", "模型 ID") }; modelRow.AddChild(Model);
        ModelPicker = new MenuButton { Text = "▾", TooltipText = L10n.T("Load models using the endpoint and API key above", "使用上面的端点和 API 密钥加载模型列表") }; modelRow.AddChild(ModelPicker);
        ModelPicker.AboutToPopup += LoadModels;
        ModelPicker.GetPopup().IdPressed += id => Model.Text = ModelPicker.GetPopup().GetItemText((int)id);
        Effort = Options(chatModel, L10n.T("Reasoning effort", "推理力度"), ["default", "none", "minimal", "low", "medium", "high", "xhigh", "max"]);
        Test = Button(chatModel, L10n.T("Test Buddy connection", "测试 Buddy 连接"), TestSettings);

        var jev = SettingsGroup(settings, L10n.T("Jev decisions", "Jev 决策"));
        UseJevStrategy = new CheckButton { Text = L10n.T("Use Jev for strategy", "使用 Jev 进行策略决策") }; jev.AddChild(UseJevStrategy);
        Wrapped(jev, L10n.T("Choose paths, rewards, events, shops and other campaign decisions with Jev.", "由 Jev 选择路线、奖励、事件、商店选项及其他战役决策。"));
        UseJevCombat = new CheckButton { Text = L10n.T("Use Jev for combat", "使用 Jev 进行战斗决策") }; jev.AddChild(UseJevCombat);
        Wrapped(jev, L10n.T("Combat Solver takes priority when enabled. Jev handles fights and card choices when the solver is off or unavailable.", "启用时优先使用战斗路线求解器。求解器关闭或不可用时，由 Jev 处理战斗和选牌。"));
        ReviewJev = new CheckButton { Text = L10n.T("Review uncertain Jev choices", "复核 Jev 不确定的选择") }; jev.AddChild(ReviewJev);
        Wrapped(jev, L10n.T("Ask the configured Buddy model to review uncertain or risky choices before acting. Improves oversight but can add time and API cost.", "行动前由已配置的 Buddy 模型复核不确定或风险较高的选择，可能增加等待时间和 API 费用。"));
        JevEndpoint = Input(jev, L10n.T("Jev evaluation URL", "Jev 评估 URL"), JevClient.DefaultEndpoint);
        Wrapped(jev, L10n.T("Full URL, including /v1/systemone for TypeSafe. Custom compatible endpoints are supported.", "填写完整 URL；TypeSafe 需包含 /v1/systemone，也可使用兼容的自定义端点。"));
        JevModel = Input(jev, L10n.T("Jev model", "Jev 模型"), JevClient.DefaultModel);
        JevKey = Input(jev, L10n.T("Jev API key", "Jev API 密钥"), ""); JevKey.Secret = true; JevKey.SecretCharacter = "*";
        JevKey.PlaceholderText = L10n.T("Enter Jev API key", "输入 Jev API 密钥");
        JevKey.TextChanged += _ => JevKeyTouched = true;
        Wrapped(jev, L10n.T("Stored separately from Buddy's key. Each decision sends a compact current state and legal choices.", "与 Buddy 的密钥分开保存。每次决策发送精简的当前状态和合法选项。"));
        TestJev = Button(jev, L10n.T("Test Jev connection", "测试 Jev 连接"), TestJevSettings);

        var automation = SettingsGroup(settings, L10n.T("Gameplay automation", "游玩自动化"));
        UseCombatSolver = new CheckButton { Text = L10n.T("Use Combat Solver", "使用战斗路线求解器") }; automation.AddChild(UseCombatSolver);
        SolverHint = Wrapped(automation, ""); SolverHint.AddThemeFontSizeOverride("font_size", 14);
        HideCombatSolverUi = new CheckButton { Text = L10n.T("Hide Combat Solver UI", "隐藏战斗路线求解器界面") }; automation.AddChild(HideCombatSolverUi);
        Wrapped(automation, L10n.T("The Combat Solver overlay normally appears during combat; this keeps it hidden.", "战斗路线求解器的界面通常会在战斗中出现；开启后保持隐藏。")).AddThemeFontSizeOverride("font_size", 14);
        AutoTreasure = new CheckButton { Text = L10n.T("Auto loot treasure chests", "自动搜刮宝箱") }; automation.AddChild(AutoTreasure);
        Wrapped(automation, L10n.T(
            "Buddy opens chests, takes the relic and moves on without asking.",
            "Buddy 会自动打开宝箱、拿走遗物并继续前进，无需确认。")).AddThemeFontSizeOverride("font_size", 14);
        var context = SettingsGroup(settings, L10n.T("Context limit", "上下文限制"));
        Label(context, L10n.T("Max context tokens", "最大上下文 token 数"));
        MaxTokens = new SpinBox { MinValue = 1, MaxValue = 100000000, Step = 1, Value = 250000 }; context.AddChild(MaxTokens);
        Wrapped(context, L10n.T("Buddy starts a fresh session at this limit. Jev checks each complete state and choices against the same limit.", "Buddy 达到上限时开启新会话。Jev 的每份完整状态和选项也受此上限限制。"));
        // Keep actions at their natural height at the bottom while the form
        // takes the remaining space and scrolls when the window is smaller.
        var settingsActions = new VBoxContainer();
        settingsActions.AddThemeConstantOverride("separation", 10);
        settingsTab.AddChild(settingsActions);
        Save = Button(settingsActions, L10n.T("Save settings", "保存设置"), SaveSettings);
        Notice = Wrapped(Body, ""); Notice.AddThemeFontSizeOverride("font_size", 14); Notice.Visible = false;
        NoticeHideAt = 0;
        RestorePanelLayout();
        KeepPanelInView();
        UpdateSolverControls();
    }

    // The solver toggles only act on the optional Combat Solver mod; without it
    // they grey out and the hint explains what to install. Mods load one by one
    // at startup, so availability is probed until it turns true.
    private static void UpdateSolverControls()
    {
        UseCombatSolver.Disabled = HideCombatSolverUi.Disabled = !SolverAvailable;
        SolverHint.Text = SolverAvailable
            ? L10n.T("Buddy hands combats to the Combat Solver mod, which auto-plays them.", "Buddy 会把战斗交给战斗路线求解器 mod，由它自动出牌。")
            : L10n.T("Install the Combat Solver mod to enable this.", "安装战斗路线求解器 mod 后才能启用此选项。");
    }

    private static string PanelLayoutPath => ProjectSettings.GlobalizePath("user://spire-buddy/panel-layout.json");

    private static void RestorePanelLayout()
    {
        try
        {
            if (!System.IO.File.Exists(PanelLayoutPath)) return;
            var values = JsonSerializer.Deserialize<float[]>(System.IO.File.ReadAllText(PanelLayoutPath));
            if (values == null || values.Length != 4 || values.Any(v => !float.IsFinite(v))
                || values[2] <= 0 || values[3] <= 0) return;
            Panel.Position = new Vector2(values[0], values[1]);
            Panel.Size = new Vector2(values[2], values[3]);
            PanelWidth = Mathf.Max(values[2], Panel.CustomMinimumSize.X);
            ExpandedSize = Panel.Size;
        }
        catch (Exception error)
        {
            GD.PushWarning($"Could not restore Spire Buddy panel layout: {error.Message}");
        }
    }

    private static void SavePanelLayout()
    {
        if (!Built) return;
        try
        {
            // Save the intended width, not a rect the engine may have grown to an
            // inflated content minimum moments before exit.
            var size = Body.Visible ? new Vector2(PanelWidth, Panel.Size.Y) : ExpandedSize;
            var path = PanelLayoutPath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new[]
                { Panel.Position.X, Panel.Position.Y, size.X, size.Y }));
            System.IO.File.Move(path + ".tmp", path, true);
        }
        catch (Exception error)
        {
            GD.PushWarning($"Could not save Spire Buddy panel layout: {error.Message}");
        }
    }

    // Only the design floor may govern width: incoming chat text (long words,
    // URLs) inflates the computed minimum through containers, and Control
    // grows its own rect to any combined minimum that rises above it. Height
    // follows the controls, kept stable by the feed's vertical scrolling.
    private static Vector2 PanelMinimum() =>
        new(Panel.CustomMinimumSize.X, Panel.GetCombinedMinimumSize().Y);

    private static void KeepPanelInView()
    {
        var viewport = Tree.Root.GetVisibleRect().Size;
        if (viewport.X <= 0 || viewport.Y <= 0) return;
        var minimum = PanelMinimum();
        // On very small viewports, scale the controls so even their minimum size fits.
        var scale = Mathf.Min(1f, Mathf.Min(viewport.X / Mathf.Max(1, minimum.X),
            viewport.Y / Mathf.Max(1, minimum.Y)));
        Panel.Scale = new Vector2(scale, scale);
        // The engine grows the rect whenever an inflated content minimum rises
        // above it; pull width back to the user's choice. The viewport bound
        // reproduces the clamp below for widths wider than the window.
        var widthLimit = Mathf.Min(PanelWidth, viewport.X / scale);
        if (Panel.Size.X > widthLimit + 0.5f)
            Panel.Size = new Vector2(widthLimit, Panel.Size.Y);
        Panel.Size = Panel.Size.Clamp(minimum, viewport / scale);
        var limit = (viewport - Panel.Size * scale).Max(Vector2.Zero);
        Panel.Position = Panel.Position.Clamp(Vector2.Zero, limit);
    }

    private static void UpdateFonts()
    {
        // Mods can initialize before localization. Refresh once it is ready and
        // whenever the game language changes, including a switch back to English.
        // The persisted settings language is the source of truth because the
        // game flips LocManager to English temporarily while uploading metrics;
        // LocManager is only a fallback before the save is readable.
        var language = SaveManager.Instance?.SettingsSave?.Language ?? LocManager.Instance?.Language;
        if (string.IsNullOrEmpty(language)) return;
        L10n.Language = language;
        if (language == FontLanguage) return;
        // Chrome strings resolve at build time, so a language-family change
        // rebuilds the panel; the fonts below then land on the fresh controls.
        if (L10n.Chinese != UiChinese)
        {
            UiChinese = L10n.Chinese;
            RebuildUi();
        }
        FontLanguage = language;
        ApplyFonts();
    }

    private static void ApplyFonts()
    {
        var regular = FontManager.GetSubstituteFont(L10n.Language, FontType.Regular)
            ?? ResourceLoader.Load<Font>("res://fonts/kreon_regular.ttf");
        var bold = FontManager.GetSubstituteFont(L10n.Language, FontType.Bold)
            ?? ResourceLoader.Load<Font>("res://fonts/kreon_bold.ttf");
        if (regular != null)
        {
            Panel.Theme.DefaultFont = regular;
            // RichTextLabel picks per-style theme fonts; keep markdown text in
            // the locale font too. Kreon has no italic cut, so italics render regular.
            Panel.Theme.SetFont("normal_font", "RichTextLabel", regular);
            Panel.Theme.SetFont("italics_font", "RichTextLabel", regular);
        }
        if (bold != null)
        {
            Title.AddThemeFontOverride("font", bold);
            Panel.Theme.SetFont("bold_font", "RichTextLabel", bold);
        }
    }

    private static void RebuildUi()
    {
        // Preserve unsaved settings, including custom prose, when translating
        // the controls. The API key is kept only in the local form snapshot.
        var draft = InitializedSettings ? SettingsPayload() : null;
        var keyText = Key.Text;
        var keyTouched = KeyTouched;
        var jevKeyText = JevKey.Text;
        var jevKeyTouched = JevKeyTouched;
        var customText = CustomPersonality.Text == CustomPersonality.PlaceholderText ? null : CustomPersonality.Text;
        // Free the old layer and rebuild every control in the new language.
        // Resetting the render markers lets the next status poll replay chat
        // and fill settings if they had not loaded yet.
        (Panel.GetParent() as Node)?.QueueFree();
        Pending.Clear();
        InitializedSettings = false;
        Conversation = "";
        LastSequence = 0;
        BuddyMessageCount = 0;
        Build();
        if (draft != null)
        {
            PopulateSettings(draft);
            if (customText != null) CustomPersonality.Text = customText;
            Key.Text = keyText;
            KeyTouched = keyTouched;
            JevKey.Text = jevKeyText;
            JevKeyTouched = jevKeyTouched;
        }
    }

    private static void MakeDraggable(Control area)
    {
        area.MouseFilter = Control.MouseFilterEnum.Stop;
        area.MouseDefaultCursorShape = Control.CursorShape.Move;
        area.TooltipText = L10n.T("Drag to move", "拖动移动");
        area.GuiInput += input =>
        {
            if (input is InputEventMouseButton mouse && mouse.ButtonIndex == MouseButton.Left)
            {
                if (mouse.Pressed) { Dragging = true; DragOffset = mouse.GlobalPosition - Panel.Position; }
                else { if (Dragging) { MovePanel(mouse.GlobalPosition); SavePanelLayout(); } Dragging = false; }
                area.AcceptEvent();
            }
            else if (input is InputEventMouseMotion motion && Dragging)
            { MovePanel(motion.GlobalPosition); area.AcceptEvent(); }
        };
    }

    private static void MovePanel(Vector2 mouse)
    {
        var limit = Tree.Root.GetVisibleRect().Size - Panel.Size * Panel.Scale;
        var position = mouse - DragOffset;
        Panel.Position = new Vector2(Mathf.Clamp(position.X, 0, Mathf.Max(0, limit.X)), Mathf.Clamp(position.Y, 0, Mathf.Max(0, limit.Y)));
    }

    private static void ResizePanel(Vector2 mouse)
    {
        var minimum = PanelMinimum();
        var maximum = (Tree.Root.GetVisibleRect().Size - Panel.Position) / Panel.Scale;
        var target = ResizeStartSize + (mouse - ResizeStartMouse) / Panel.Scale;
        Panel.Size = new Vector2(
            Mathf.Clamp(target.X, minimum.X, Mathf.Max(minimum.X, maximum.X)),
            Mathf.Clamp(target.Y, minimum.Y, Mathf.Max(minimum.Y, maximum.Y))
        );
        PanelWidth = Panel.Size.X;
    }

    private static bool FeedIsNearBottom()
    {
        var bar = Feed.GetVScrollBar();
        return bar.Value >= bar.MaxValue - bar.Page - 30;
    }

    private static void ScrollFeedToBottom()
    {
        if (ChatScrollQueued) return;
        ChatScrollQueued = true;
        Callable.From(() =>
        {
            ChatScrollQueued = false;
            // A wheel/drag event may have moved the reader away since queuing.
            if (!FollowChatBottom) return;
            var bar = Feed.GetVScrollBar();
            AdjustingChatScroll = true;
            try { bar.Value = Math.Max(0, bar.MaxValue - bar.Page); }
            finally { AdjustingChatScroll = false; }
        }).CallDeferred();
    }

    private static void SendMessage()
    {
        // Buddy owns both conversation and gameplay delegation.
        if (string.IsNullOrWhiteSpace(Message.Text)) return;
        Send("/message", new JsonObject { ["message"] = Message.Text }, () => Message.Text = "");
    }
    private static Label Label(Node parent, string text) { var n = new Label { Text = text }; parent.AddChild(n); return n; }
    private static Label Wrapped(Node parent, string text)
    {
        var n = Label(parent, text); n.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        n.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; return n;
    }
    private static Button Button(Node parent, string text, Action action)
    { var n = new Button { Text = text }; n.Pressed += action; parent.AddChild(n); return n; }
    private static VBoxContainer SettingsGroup(Node parent, string title)
    {
        var section = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        parent.AddChild(section);
        var toggle = new Button { Text = "▸ " + title, ToggleMode = true, Alignment = HorizontalAlignment.Left };
        section.AddChild(toggle);
        var fields = new VBoxContainer { Visible = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        fields.AddThemeConstantOverride("separation", 10); section.AddChild(fields);
        toggle.Toggled += expanded => { fields.Visible = expanded; toggle.Text = (expanded ? "▾ " : "▸ ") + title; };
        return fields;
    }
    private static LineEdit Input(Node parent, string label, string text)
    { Label(parent, label); var n = new LineEdit { Text = text }; parent.AddChild(n); return n; }
    private static OptionButton Options(Node parent, string label, string[] values, Func<string, string>? display = null)
    {
        Label(parent, label); var n = new OptionButton();
        foreach (var value in values) { n.AddItem(display?.Invoke(value) ?? value); n.SetItemMetadata(n.ItemCount - 1, value); }
        parent.AddChild(n); return n;
    }
    private static string Selected(OptionButton n) => n.GetItemMetadata(n.Selected).AsString();
    private static void Select(OptionButton n, string value)
    { for (int i = 0; i < n.ItemCount; i++) if (n.GetItemMetadata(i).AsString() == value) n.Select(i); }
    private static JsonObject SettingsPayload()
    {
        var payload = new JsonObject { ["api_endpoint"] = Endpoint.Text, ["model"] = Model.Text, ["api_type"] = Selected(Api), ["personality"] = Selected(Personality), ["reasoning_effort"] = Selected(Effort) == "default" ? null : Selected(Effort) };
        // An unchanged example remains unset so it follows future language changes.
        payload["custom_personality"] = CustomPersonality.Text == CustomPersonality.PlaceholderText ? "" : CustomPersonality.Text;
        payload["max_context_tokens"] = (int)MaxTokens.Value;
        // The untouched mask means "keep the stored key", like a blank field.
        if (Key.Text.Length > 0 && Key.Text != MaskedKey) payload["api_key"] = Key.Text;
        payload["use_combat_solver"] = UseCombatSolver.ButtonPressed;
        payload["hide_combat_solver_ui"] = HideCombatSolverUi.ButtonPressed;
        payload["auto_treasure"] = AutoTreasure.ButtonPressed;
        payload["use_jev_strategy"] = UseJevStrategy.ButtonPressed;
        payload["use_jev_combat"] = UseJevCombat.ButtonPressed;
        payload["jev_review_uncertain"] = ReviewJev.ButtonPressed;
        payload["jev_endpoint"] = JevEndpoint.Text;
        payload["jev_model"] = JevModel.Text;
        if (JevKey.Text.Length > 0 && JevKey.Text != MaskedKey) payload["jev_api_key"] = JevKey.Text;
        return payload;
    }
    private static void SaveSettings() => Send("/settings", SettingsPayload(), () => { Key.Text = JevKey.Text = ""; KeyTouched = JevKeyTouched = false; }, "PUT", L10n.T("Settings saved", "设置已保存"));

    private static void PopulateSettings(JsonNode config)
    {
        Endpoint.Text = config["base_url"]?.ToString() ?? config["api_endpoint"]?.ToString() ?? "https://api.openai.com/v1";
        MaxTokens.Value = config["max_context_tokens"]?.GetValue<int>() ?? 250000;
        Model.Text = config["model"]?.ToString() ?? "";
        Select(Api, config.Text("api_type")); Select(Personality, config.Text("personality"));
        CustomPersonality.Text = Personalities.CustomText(config);
        CustomPersonalityFields.Visible = Selected(Personality) == "custom";
        Select(Effort, config["reasoning_effort"]?.ToString() ?? "default");
        UseCombatSolver.ButtonPressed = config.Flag("use_combat_solver");
        HideCombatSolverUi.ButtonPressed = config.Flag("hide_combat_solver_ui");
        // Older saved settings predate the key; the shipped default is on.
        AutoTreasure.ButtonPressed = config.Flag("auto_treasure", true);
        UseJevStrategy.ButtonPressed = config.Flag("use_jev_strategy");
        UseJevCombat.ButtonPressed = config.Flag("use_jev_combat");
        ReviewJev.ButtonPressed = config.Flag("jev_review_uncertain");
        JevEndpoint.Text = config.Text("jev_endpoint", JevClient.DefaultEndpoint);
        JevModel.Text = config.Text("jev_model", JevClient.DefaultModel);
        InitializedSettings = true;
    }

    private static void TestSettings()
    {
        if (Probing) return;
        Probing = true; Test.Disabled = true;
        ShowNotice(L10n.T("Testing connection…", "正在测试连接…"));
        _ = Request("POST", "/settings/test", SettingsPayload(), value =>
        {
            Probing = false; Test.Disabled = !Connected;
            if (value != null) ShowNotice(value["message"]!.ToString());
        });
    }
    private static void TestJevSettings()
    {
        if (ProbingJev) return;
        ProbingJev = true; TestJev.Disabled = true;
        ShowNotice(L10n.T("Testing Jev connection…", "正在测试 Jev 连接…"));
        _ = Request("POST", "/settings/test-jev", SettingsPayload(), value =>
        {
            ProbingJev = false; TestJev.Disabled = !Connected;
            if (value != null) ShowNotice(value["message"]!.ToString());
        });
    }
    private static void LoadModels()
    {
        var popup = ModelPicker.GetPopup(); popup.Clear(); popup.AddItem(L10n.T("Loading models…", "正在加载模型…")); popup.SetItemDisabled(0, true);
        var endpoint = Endpoint.Text; var key = Key.Text;
        var payload = new JsonObject { ["api_endpoint"] = endpoint };
        // The untouched mask means "keep the stored key", like a blank field.
        if (key.Length > 0 && key != MaskedKey) payload["api_key"] = key;
        _ = Request("POST", "/models", payload, value =>
        {
            popup.Clear();
            if (Endpoint.Text != endpoint || Key.Text != key) { popup.Hide(); return; }
            foreach (var model in value?["models"]?.AsArray() ?? new JsonArray()) popup.AddItem(model!.ToString());
            if (popup.ItemCount == 0)
            {
                popup.AddItem(value == null ? L10n.T("Could not load · enter a custom ID", "加载失败 · 请输入自定义 ID") : L10n.T("No models listed · enter a custom ID", "没有模型 · 请输入自定义 ID"));
                popup.SetItemDisabled(0, true);
            }
        });
    }
    private static void Send(string path, JsonNode? payload = null, Action? success = null, string method = "POST", string? saved = null)
    {
        if (Busy) return;
        Busy = true;
        ShowNotice(L10n.T("Sending…", "正在发送…"));
        _ = Request(method, path, payload, value =>
        {
            Busy = false;
            if (value == null) return;
            if (saved == null) Notice.Visible = false;
            else ShowNotice(saved, 3000);
            success?.Invoke(); NextPoll = 0;
        });
    }
    // Notices without a duration stay until replaced; timed ones are hidden by
    // the frame loop so no timer outlives the notice it was set for.
    private static void ShowNotice(string text, ulong durationMs = 0)
    {
        Notice.Visible = true; Notice.Text = text;
        NoticeHideAt = durationMs > 0 ? Time.GetTicksMsec() + durationMs : 0;
    }
    // Capture UI values at the call site; model requests and game-thread dispatch
    // run away from the UI callback.
    private static Task Request(string method, string path, JsonNode? payload, Action<JsonNode?> done)
        => Task.Run(() => RequestAsync(method, path, payload, done));

    private static async Task RequestAsync(string method, string path, JsonNode? payload, Action<JsonNode?> done)
    {
        try
        {
            var json = await Runtime.Dispatch(method, path, payload).ConfigureAwait(false);
            Main.Enqueue(() => done(json));
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            Main.Enqueue(() => { ShowNotice(message); done(null); });
        }
    }
    private static void Render(JsonNode data)
    {
        var status = data["status"]?.ToString() ?? "idle";
        bool chatting = data["chat_busy"]?.GetValue<bool>() == true;
        Status.Text = status == "running" ? L10n.T("● Playing", "● 游玩中") : status == "stopping" ? L10n.T("Stopping…", "正在停止…") : chatting ? L10n.T("● Thinking", "● 思考中") : status == "error" ? L10n.T("● Needs attention", "● 需要注意") : L10n.T("● Ready", "● 就绪");
        Alive = data["thread_alive"]?.GetValue<bool>() == true;
        HideSolverOverlay = data["config"]?.Flag("hide_combat_solver_ui") ?? false;
        Test.Disabled = Probing; TestJev.Disabled = ProbingJev; ModelPicker.Disabled = false;
        Save.Disabled = Alive || chatting || Busy; SendButton.Disabled = Busy;
        if (!InitializedSettings && data["config"] is JsonNode config)
            PopulateSettings(config);
        // Represent a stored key as mask characters; the real key never enters UI
        // text. Refill only while the user has not edited the field.
        if (!KeyTouched && Key.Text.Length == 0 && data["config"]?["has_api_key"]?.GetValue<bool>() == true)
            Key.Text = MaskedKey;
        if (!JevKeyTouched && JevKey.Text.Length == 0 && data["config"]?.Flag("has_jev_api_key") == true)
            JevKey.Text = MaskedKey;
        string conversation = data["conversation_id"]?.ToString() ?? "legacy";
        if (Conversation != conversation)
        {
            foreach (var child in Messages.GetChildren()) if (child != Empty && child != Activity) { Messages.RemoveChild(child); child.QueueFree(); }
            FollowChatBottom = true;
            Conversation = conversation; LastSequence = 0; BuddyMessageCount = 0; Pending.Clear(); Empty.Visible = true;
        }
        bool added = false;
        foreach (var ev in (data["messages"] ?? data["events"])?.AsArray() ?? new JsonArray())
        {
            long sequence = ev?["sequence"]?.GetValue<long>() ?? 0;
            if (sequence <= LastSequence) continue;
            LastSequence = sequence;
            string kind = ev?["event"]?.ToString() ?? "";
            if (kind is not ("operator_message" or "agent_message" or "chat_reply" or "chat_error")) continue;
            var replyTo = ev?["reply_to"]?.ToString() ?? "";
            if (Pending.Remove(replyTo, out var pending)) pending.QueueFree();
            AddMessage(kind, ev?["message"]?.ToString() ?? "", ev?["rationale"]?.ToString() ?? "", ev?["message_id"]?.ToString() ?? "", ev?["reply_to_text"]?.ToString() ?? ""); added = true;
        }
        bool wasActive = Activity.Visible;
        Activity.Visible = chatting || Alive && status is "running" or "stopping";
        Messages.MoveChild(Activity, Messages.GetChildCount() - 1);
        if (Activity.Visible) Empty.Visible = false;
        else if (BuddyMessageCount == 0 && Messages.GetChildCount() == 2) Empty.Visible = true;
        if ((added || Activity.Visible != wasActive) && FollowChatBottom) ScrollFeedToBottom();
    }

    private static void AddMessage(string kind, string message, string rationale, string messageId, string replyText)
    {
        Empty.Visible = false;
        bool user = kind == "operator_message";
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var background = user ? "29435366" : BuddyMessageCount++ % 2 == 0 ? "1b273533" : "202e3d4d";
        card.AddThemeStyleboxOverride("panel", Box(background, 12, user ? "597f94" : "344254"));
        Messages.AddChild(card);
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 7); card.AddChild(body);
        if (replyText.Length > 0)
        {
            var quote = Wrapped(body, "↳ " + replyText);
            quote.AddThemeFontSizeOverride("font_size", 14);
            quote.AddThemeColorOverride("font_color", new Color("95aabb"));
        }
        var messageRow = new HBoxContainer();
        messageRow.AddThemeConstantOverride("separation", 3);
        body.AddChild(messageRow);
        var shown = string.IsNullOrEmpty(message) ? L10n.T("Considering the next move.", "正在考虑下一步。") : message;
        if (user)
        {
            var messageLabel = Wrapped(messageRow, shown);
            messageLabel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        }
        else
        {
            // Buddy replies arrive as Markdown; render styles, lists and tables.
            var rendered = Markdown.Render(shown);
            rendered.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            messageRow.AddChild(rendered);
        }
        if (user && messageId.Length > 0)
        {
            var pending = Label(body, L10n.T("Replying…", "回复中…")); pending.AddThemeFontSizeOverride("font_size", 13);
            pending.AddThemeColorOverride("font_color", new Color("94cbbf")); Pending[messageId] = pending;
        }
        if (string.IsNullOrEmpty(rationale)) return;
        var explanation = new VBoxContainer { Visible = false };
        var expand = new Button
        {
            Text = "▾",
            TooltipText = L10n.T("Show rationale", "显示理由"),
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(20, 20),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        expand.AddThemeFontSizeOverride("font_size", 13);
        expand.AddThemeColorOverride("font_color", new Color("aebccd"));
        expand.AddThemeColorOverride("font_hover_color", new Color("f0d58c"));
        messageRow.AddChild(expand);
        body.AddChild(explanation);
        expand.Pressed += () =>
        {
            explanation.Visible = !explanation.Visible;
            expand.Text = explanation.Visible ? "▴" : "▾";
            expand.TooltipText = explanation.Visible ? L10n.T("Hide rationale", "隐藏理由") : L10n.T("Show rationale", "显示理由");
        };
        var rationaleView = Markdown.Render(rationale, 16, new Color("aebccd"));
        rationaleView.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        explanation.AddChild(rationaleView);
    }
}
