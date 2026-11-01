using JainaProudmoore;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder
    .Services.AddOptions<BotOptions>()
    .Bind(builder.Configuration.GetSection(BotOptions.SectionName))
    .PostConfigure(options =>
    {
        options.Token =
            builder.Configuration["TOKEN"]
            ?? builder.Configuration["DISCORD_TOKEN"]
            ?? options.Token;
        options.GeminiApiKey = builder.Configuration["GEMINI_API_KEY"] ?? options.GeminiApiKey;
    })
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Token),
        "Set TOKEN, DISCORD_TOKEN, or Bot:Token."
    )
    .Validate(
        options => options.AuditChannelId > 0 && options.ToggleRoleId > 0,
        "Audit channel and role IDs must be nonzero."
    )
    .ValidateOnStart();

builder.Services.AddHttpClient(
    "gemini",
    (sp, http) =>
    {
        var opts = sp.GetRequiredService<IOptions<BotOptions>>().Value;
        http.BaseAddress = new Uri(GeminiDefaults.BaseUrl);
        http.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrWhiteSpace(opts.GeminiApiKey))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "x-goog-api-key",
                opts.GeminiApiKey.Trim()
            );
        }
    }
);

builder.Services.AddHostedService<Worker>();

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Starting Jaina Proudmoore Discord Bot...");

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    logger.LogInformation("Shutting down Jaina Proudmoore Discord Bot...");
}
