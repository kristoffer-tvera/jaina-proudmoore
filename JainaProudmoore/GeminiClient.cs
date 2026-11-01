using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JainaProudmoore;

/// <summary>
/// Thin, non-DI wrapper over an <see cref="HttpClient"/> resolved per invocation from the
/// named "gemini" <see cref="IHttpClientFactory"/> client. The <c>x-goog-api-key</c> header,
/// base URL, and timeout are configured on the resolved client; this type only builds request
/// bodies and parses responses. The request builders are pure and static so they can be unit
/// tested without any client or network.
/// </summary>
public sealed class GeminiClient(HttpClient http)
{
    private readonly HttpClient _http = http;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Asks Gemini the user's question, persona-constrained. Returns the raw answer text
    /// (uncapped; the caller applies <see cref="AnswerFormatting.CapSentences"/>).
    /// </summary>
    public async Task<string> GenerateAnswerAsync(
        string model,
        string persona,
        string question,
        CancellationToken ct
    )
    {
        var request = BuildTextRequest(persona, question);
        using var response = await _http.PostAsJsonAsync(
            $"/v1beta/models/{model}:generateContent",
            request,
            SerializerOptions,
            ct
        );
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<GenerateContentResponse>(
            SerializerOptions,
            ct
        );

        var text = payload
            ?.Candidates?.FirstOrDefault()
            ?.Content?.Parts?.FirstOrDefault(part => !string.IsNullOrEmpty(part.Text))
            ?.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new GeminiException("Text response contained no answer");
        }

        return text;
    }

    /// <summary>
    /// Synthesizes <paramref name="text"/> as speech via the Gemini Interactions API and returns
    /// the complete WAV bytes (RIFF, 24 kHz mono 16-bit signed little-endian PCM).
    /// </summary>
    public async Task<byte[]> SynthesizeSpeechAsync(
        string model,
        string voice,
        string text,
        CancellationToken ct
    )
    {
        var request = BuildTtsRequest(model, voice, text);
        using var response = await _http.PostAsJsonAsync(
            "/v1beta/interactions",
            request,
            SerializerOptions,
            ct
        );
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<InteractionsTtsResponse>(
            SerializerOptions,
            ct
        );

        // Shape A (confirmed default, verified against the live docs): the last audio block
        // inside a model_output step carries base64 WAV at steps[].content[].data.
        var base64 = payload
            ?.Steps?.Where(step => step.Type == "model_output")
            .SelectMany(step => step.Content ?? [])
            .Where(content => content.Type == "audio" && !string.IsNullOrEmpty(content.Data))
            .Select(content => content.Data)
            .LastOrDefault();

        // Shape B contingency (documented, not the default): if the account instead returns
        // a generateContent-style modalities response, the audio would live at
        // candidates[0].content.parts[0].inlineData.data. Switching to that is a localized
        // change to this parser + BuildTtsRequest, not a crash.
        if (string.IsNullOrEmpty(base64))
        {
            throw new GeminiException("TTS response contained no audio");
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new GeminiException("TTS audio was not valid base64", ex);
        }
    }

    /// <summary>
    /// Pure builder for the <c>generateContent</c> request body. No <see cref="HttpClient"/>
    /// dependency so it is directly unit testable.
    /// </summary>
    public static GenerateContentRequest BuildTextRequest(string persona, string question) =>
        new(
            SystemInstruction: new ContentPayload(
                Role: null,
                Parts: [new TextPart(persona)]
            ),
            Contents: [new ContentPayload(Role: "user", Parts: [new TextPart(question)])],
            GenerationConfig: new TextGenerationConfig(Temperature: 0.7, MaxOutputTokens: 160)
        );

    /// <summary>
    /// Pure builder for the Interactions TTS request body (Shape A). No
    /// <see cref="HttpClient"/> dependency so it is directly unit testable.
    /// </summary>
    public static InteractionsTtsRequest BuildTtsRequest(string model, string voice, string text) =>
        new(
            Model: model,
            Input:
            [
                new InteractionInput(
                    Type: "user_input",
                    Content: [new InteractionContent(Type: "text", Text: text)]
                ),
            ],
            ResponseFormat: new ResponseFormat(Type: "audio"),
            GenerationConfig: new TtsGenerationConfig(SpeechConfig: [new SpeechConfigEntry(voice)])
        );
}

/// <summary>Raised when a Gemini response cannot be interpreted into a usable result.</summary>
public sealed class GeminiException : Exception
{
    public GeminiException(string message)
        : base(message) { }

    public GeminiException(string message, Exception innerException)
        : base(message, innerException) { }
}

// ---- generateContent request DTOs ----

public sealed record GenerateContentRequest(
    [property: JsonPropertyName("systemInstruction")] ContentPayload SystemInstruction,
    [property: JsonPropertyName("contents")] IReadOnlyList<ContentPayload> Contents,
    [property: JsonPropertyName("generationConfig")] TextGenerationConfig GenerationConfig
);

public sealed record ContentPayload(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("parts")] IReadOnlyList<TextPart> Parts
);

public sealed record TextPart([property: JsonPropertyName("text")] string Text);

public sealed record TextGenerationConfig(
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens
);

// ---- generateContent response DTOs ----

public sealed record GenerateContentResponse(
    [property: JsonPropertyName("candidates")] IReadOnlyList<GenerateContentCandidate>? Candidates
);

public sealed record GenerateContentCandidate(
    [property: JsonPropertyName("content")] GenerateContentContent? Content
);

public sealed record GenerateContentContent(
    [property: JsonPropertyName("parts")] IReadOnlyList<TextPart>? Parts
);

// ---- Interactions TTS request DTOs (Shape A) ----

public sealed record InteractionsTtsRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] IReadOnlyList<InteractionInput> Input,
    [property: JsonPropertyName("response_format")] ResponseFormat ResponseFormat,
    [property: JsonPropertyName("generation_config")] TtsGenerationConfig GenerationConfig
);

public sealed record InteractionInput(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] IReadOnlyList<InteractionContent> Content
);

public sealed record InteractionContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text
);

public sealed record ResponseFormat([property: JsonPropertyName("type")] string Type);

public sealed record TtsGenerationConfig(
    [property: JsonPropertyName("speech_config")] IReadOnlyList<SpeechConfigEntry> SpeechConfig
);

public sealed record SpeechConfigEntry([property: JsonPropertyName("voice")] string Voice);

// ---- Interactions TTS response DTOs (Shape A) ----

public sealed record InteractionsTtsResponse(
    [property: JsonPropertyName("steps")] IReadOnlyList<InteractionStep>? Steps
);

public sealed record InteractionStep(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("content")] IReadOnlyList<InteractionResponseContent>? Content
);

public sealed record InteractionResponseContent(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("data")] string? Data
);
