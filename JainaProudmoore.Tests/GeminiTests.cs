using System.Text.Json;
using Xunit;

namespace JainaProudmoore.Tests;

public sealed class GeminiTests
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    // ---- CapSentences (AC7) ----

    [Fact]
    public void CapSentences_ReducesLongTextToThreeSentences()
    {
        const string input = "One. Two. Three. Four. Five.";

        var result = AnswerFormatting.CapSentences(input, 3);

        Assert.Equal("One. Two. Three.", result);
    }

    [Fact]
    public void CapSentences_LeavesTwoSentenceAnswerUnchanged()
    {
        const string input = "First thing happened. Second thing too.";

        Assert.Equal(input, AnswerFormatting.CapSentences(input, 3));
    }

    [Fact]
    public void CapSentences_LeavesSingleSentenceUnchanged()
    {
        const string input = "Just the one sentence here.";

        Assert.Equal(input, AnswerFormatting.CapSentences(input, 3));
    }

    [Fact]
    public void CapSentences_TreatsNoTerminatorTextAsOneSentence()
    {
        const string input = "No terminator at all";

        Assert.Equal(input, AnswerFormatting.CapSentences(input, 3));
    }

    [Fact]
    public void CapSentences_CountsTerminatorRunAsOneSentence()
    {
        const string input = "Really?! Are you sure... Absolutely yes. Extra beyond cap.";

        // The "?!" run and the "..." run each close exactly one sentence.
        var result = AnswerFormatting.CapSentences(input, 3);

        Assert.Equal("Really?! Are you sure... Absolutely yes.", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CapSentences_ReturnsEmptyForBlankInput(string input)
    {
        Assert.Equal(string.Empty, AnswerFormatting.CapSentences(input, 3));
    }

    [Fact]
    public void CapSentences_DecimalPointBiasesShorterCut_AcceptedBehavior()
    {
        // R6: a decimal's period counts as a terminator, so this is cut shorter than a human
        // would. This asserts the accepted upper-bound behavior rather than ideal segmentation.
        const string input = "Pi is 3.14 and that matters.";

        var result = AnswerFormatting.CapSentences(input, 3);

        // "Pi is 3." / "14 and that matters." are treated as two sentences; both fit under the
        // cap of 3, so the full (trimmed) text is returned unchanged here.
        Assert.Equal("Pi is 3.14 and that matters.", result);
    }

    [Fact]
    public void CapSentences_DecimalCutsEarlyWhenCapIsOne()
    {
        // With a cap of 1, the decimal period ends the first "sentence" at "Pi is 3." —
        // documenting the accepted shorter-cut trade-off for decimals/abbreviations.
        const string input = "Pi is 3.14 and that matters.";

        var result = AnswerFormatting.CapSentences(input, 1);

        Assert.Equal("Pi is 3.", result);
    }

    // ---- Text request body construction (AC8) ----

    [Fact]
    public void BuildTextRequest_ProducesExpectedShape()
    {
        const string persona = "You are Jaina.";
        const string question = "What is the weather?";

        var request = GeminiClient.BuildTextRequest(persona, question);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, SerializerOptions));
        var root = document.RootElement;

        Assert.Equal(
            persona,
            root.GetProperty("systemInstruction")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString()
        );
        Assert.Equal(
            question,
            root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()
        );
        Assert.Equal("user", root.GetProperty("contents")[0].GetProperty("role").GetString());
        Assert.Equal(
            160,
            root.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32()
        );
    }

    // ---- TTS request body construction (Shape A, AC8) ----

    [Fact]
    public void BuildTtsRequest_ProducesExpectedShape()
    {
        const string model = "gemini-2.5-flash-preview-tts";
        const string voice = "Kore";
        const string text = "Have a wonderful day.";

        var request = GeminiClient.BuildTtsRequest(model, voice, text);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, SerializerOptions));
        var root = document.RootElement;

        Assert.Equal(model, root.GetProperty("model").GetString());
        Assert.Equal(
            text,
            root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()
        );
        Assert.Equal(
            "user_input",
            root.GetProperty("input")[0].GetProperty("type").GetString()
        );
        Assert.Equal("audio", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(
            voice,
            root.GetProperty("generation_config")
                .GetProperty("speech_config")[0]
                .GetProperty("voice")
                .GetString()
        );
    }
}
