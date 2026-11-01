namespace JainaProudmoore;

/// <summary>
/// Dependency-free constants for the Gemini integration. Kept separate from
/// <see cref="GeminiClient"/> so <see cref="BotOptions"/> can default from these without
/// transitively loading the HTTP client (keeps the options-defaults tests network-free).
/// </summary>
public static class GeminiDefaults
{
    /// <summary>Base URL for the Gemini REST API.</summary>
    public const string BaseUrl = "https://generativelanguage.googleapis.com";

    /// <summary>
    /// Default stable text model id. Known-real and config-overridable via
    /// <c>Bot:GeminiTextModel</c>.
    /// </summary>
    public const string TextModel = "gemini-2.5-flash";

    /// <summary>
    /// Default stable TTS model id. Known-real and config-overridable via
    /// <c>Bot:GeminiTtsModel</c>.
    /// </summary>
    public const string TtsModel = "gemini-2.5-flash-preview-tts";

    /// <summary>
    /// Opt-in text model id advertised by the current live docs. This is an ASSUMPTION /
    /// opt-in override, NOT a verified default for every account; set <c>Bot:GeminiTextModel</c>
    /// to this value only if your account has 3.x model access.
    /// </summary>
    public const string OptInTextModel = "gemini-3.8-flash";

    /// <summary>
    /// Opt-in TTS model id advertised by the current live docs. This is an ASSUMPTION /
    /// opt-in override, NOT a verified default for every account; set <c>Bot:GeminiTtsModel</c>
    /// to this value only if your account has 3.x model access.
    /// </summary>
    public const string OptInTtsModel = "gemini-3.8-flash-tts";
}
