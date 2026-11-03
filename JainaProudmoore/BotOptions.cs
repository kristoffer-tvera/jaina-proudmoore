namespace JainaProudmoore;

public sealed class BotOptions
{
    public const string SectionName = "Bot";

    public string Token { get; set; } = string.Empty;
    public ulong AuditChannelId { get; set; } = 971060030923235378;
    public ulong ToggleRoleId { get; set; } = 710781184375914548;

    // The voice sidecar's base URL and optional shared token. Reached by Docker container name on
    // the shared network; no external port is published. Bound from Bot:SidecarBaseUrl /
    // Bot:SidecarToken (env Bot__SidecarBaseUrl / Bot__SidecarToken).
    public string SidecarBaseUrl { get; set; } = "http://jaina-voice-sidecar:8080";
    public string SidecarToken { get; set; } = string.Empty;

    public string GeminiApiKey { get; set; } = string.Empty;
    public string GeminiVoice { get; set; } = "Kore";

    // Text models tried in order: primary, then fallback 1, then fallback 2. Rate limits are
    // per-model, so when the primary is throttled the fallbacks can still answer.
    public string GeminiTextModel { get; set; } = GeminiDefaults.TextModel;
    public string GeminiTextModelFallback1 { get; set; } = GeminiDefaults.TextModelFallback1;
    public string GeminiTextModelFallback2 { get; set; } = GeminiDefaults.TextModelFallback2;

    // TTS models tried in the same primary -> fallback 1 -> fallback 2 order.
    public string GeminiTtsModel { get; set; } = GeminiDefaults.TtsModel;
    public string GeminiTtsModelFallback1 { get; set; } = GeminiDefaults.TtsModelFallback1;
    public string GeminiTtsModelFallback2 { get; set; } = GeminiDefaults.TtsModelFallback2;
    public string GeminiPersonaPrompt { get; set; } =
        "You are Jaina Proudmoore from World of Warcraft, and a member of the Discord "
        + "guild \"Amused to Death\". Speak in character: poised, intelligent, a touch wry. "
        + "Only the asker hears the original question, so the rest of the channel hears you "
        + "cold - restate or clearly reference what was asked as part of your reply so your "
        + "answer stands on its own and never sounds like a disembodied fragment. Answer in a "
        + "couple of sentences (about two to three); never a bare one- or two-word reply. Do "
        + "not announce that you are an AI, a bot, or a guild member - simply speak as Jaina.";
}
