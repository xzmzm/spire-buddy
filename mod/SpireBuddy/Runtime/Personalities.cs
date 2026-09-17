using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

internal static class Personalities
{
    // Persist stable IDs, independently of the labels shown in the panel.
    internal static readonly string[] Ids = ["witty_streamer", "calm_teacher", "dramatic_narrator", "concise_analyst", "narcissist", "custom"];

    internal static string Label(string id) => id switch
    {
        "witty_streamer" => L10n.T("Witty streamer", "风趣主播"),
        "calm_teacher" => L10n.T("Calm teacher", "耐心导师"),
        "dramatic_narrator" => L10n.T("Dramatic narrator", "戏剧旁白"),
        "concise_analyst" => L10n.T("Concise analyst", "简洁分析师"),
        "narcissist" => L10n.T("Narcissist", "自恋型人格"),
        "custom" => L10n.T("Custom", "自定义"),
        _ => id
    };

    internal static string CustomExample => L10n.T(
        "Be a warm, encouraging companion with a dry sense of humor. Explain the key tactical tradeoff in one or two sentences, celebrate clever plays, and stay grounded in the visible game state.",
        "做一个温暖、善于鼓励、偶尔讲点冷笑话的伙伴。用一两句话说明关键的战术取舍，为精彩操作喝彩，并根据可见的游戏状态进行解说。");

    internal static string CustomText(JsonNode config)
        => string.IsNullOrWhiteSpace(config.Text("custom_personality")) ? CustomExample : config.Text("custom_personality");

    internal static string Instructions(JsonNode config) => config.Text("personality") switch
    {
        "calm_teacher" => "Be patient and teach the key tactical tradeoff clearly.",
        "dramatic_narrator" => "Use energetic fantasy commentary while keeping every claim accurate.",
        "concise_analyst" => "Be dry, sharp and concise; emphasize decisive facts.",
        "narcissist" => L10n.T("Narcissist", "自恋型人格"),
        "custom" => CustomText(config),
        _ => "Be playful and witty, grounded in the visible game state, with a useful tactical takeaway."
    };
}
