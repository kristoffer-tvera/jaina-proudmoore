using Xunit;

namespace JainaProudmoore.Tests;

public sealed class BotTests
{
    [Fact]
    public void DefaultsMatchExpectedChannelAndRole()
    {
        var options = new BotOptions();

        Assert.Equal(971060030923235378ul, options.AuditChannelId);
        Assert.Equal(710781184375914548ul, options.ToggleRoleId);
    }

    [Fact]
    public void TokenDefaultsToEmpty()
    {
        var options = new BotOptions();

        Assert.Equal(string.Empty, options.Token);
    }

    [Fact]
    public void GeminiDefaultsMatchExpected()
    {
        var options = new BotOptions();

        Assert.Equal(string.Empty, options.GeminiApiKey);
        Assert.Equal("Kore", options.GeminiVoice);
        Assert.Equal(GeminiDefaults.TextModel, options.GeminiTextModel);
        Assert.Equal(GeminiDefaults.TtsModel, options.GeminiTtsModel);
        Assert.Equal("gemini-3.8-flash", options.GeminiTextModel);
        Assert.Equal("gemini-3.8-flash-tts", options.GeminiTtsModel);
        Assert.False(string.IsNullOrWhiteSpace(options.GeminiPersonaPrompt));
    }
}
