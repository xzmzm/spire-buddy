namespace SpireBuddy.Runtime;

// Bot UI strings follow the live game language. The mod layer owns detection
// (LocManager) and assigns Language here; everything else renders through T so
// English stays the fallback for languages without a translation.
internal static class L10n
{
    static string language = "en";
    internal static string Language
    {
        get => language;
        set { if (!string.Equals(value, language, StringComparison.OrdinalIgnoreCase)) { language = value; Version++; } }
    }
    // Sessions bake their prompt at restart; Version tells them the UI language
    // changed underneath so live sessions can append a language-change note.
    internal static long Version { get; private set; }
    internal static bool Chinese => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    internal static string T(string en, string zh) => Chinese ? zh : en;
    // Only the Chinese UI carries a directive: with the English UI the model
    // naturally mirrors the operator's own language in chat.
    internal static string GameplayLanguageDirective => Chinese
        ? " The operator's interface language is Chinese: write your visible message and rationale in Chinese. Game data and identifiers may appear in other languages."
        : "";
    internal static string ChatLanguageDirective => Chinese
        ? " The operator's interface language is Chinese: always reply in Chinese. Game data and identifiers may appear in other languages."
        : "";
    // Mid-session switches append these as user turns instead of rewriting the
    // system prompt, so the provider's cached prefix stays valid.
    internal static string GameplaySwitchNote => Chinese
        ? "The operator switched the interface language to Chinese. Write your visible message and rationale in Chinese from now on. Game data and identifiers may appear in other languages."
        : "The operator switched the interface language back to English. Write your visible message and rationale in English from now on.";
    internal static string ChatSwitchNote => Chinese
        ? "The operator switched the interface language to Chinese. Reply in Chinese from now on. Game data and identifiers may appear in other languages."
        : "The operator switched the interface language back to English. Reply in English from now on.";
}
