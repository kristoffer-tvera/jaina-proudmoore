namespace JainaProudmoore;

public sealed class BotOptions
{
    public const string SectionName = "Bot";

    public string Token { get; set; } = string.Empty;
    public ulong AuditChannelId { get; set; } = 971060030923235378;
    public ulong ToggleRoleId { get; set; } = 710781184375914548;

    public string GeminiApiKey { get; set; } = string.Empty;
    public string GeminiVoice { get; set; } = "Kore";
    public string GeminiTextModel { get; set; } = GeminiDefaults.TextModel;
    public string GeminiTtsModel { get; set; } = GeminiDefaults.TtsModel;
    public string GeminiPersonaPrompt { get; set; } =
        "You are Jaina Proudmoore from World of Warcraft. Answer in character - "
        + "poised, intelligent, a touch wry. Keep answers to at most 2-3 short sentences.";
}
