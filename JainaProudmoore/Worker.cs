using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;

namespace JainaProudmoore;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly BotOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiscordSocketClient _client;
    private CancellationToken _stoppingToken;

    public Worker(
        ILogger<Worker> logger,
        IOptions<BotOptions> options,
        IHttpClientFactory httpClientFactory
    )
    {
        _logger = logger;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _client = new DiscordSocketClient(
            new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
                MessageCacheSize = 1024,
                // Discord enforces the DAVE (E2EE) protocol for voice as of 2026-03-01; without
                // it the voice gateway closes with 4017. Discord.Net negotiates DAVE when this is
                // enabled AND the native libdave.so is resolvable at runtime (see Dockerfile).
                EnableVoiceDaveEncryption = true,
            }
        );
        _client.Log += ClientLog;
        _client.Ready += ClientReady;
        _client.MessageReceived += MessageReceived;
        _client.MessageDeleted += MessageDeleted;
        _client.MessageUpdated += MessageUpdated;
        _client.SlashCommandExecuted += SlashCommandExecuted;
    }

    private async Task MessageUpdated(
        Cacheable<IMessage, ulong> oldMessage,
        SocketMessage newMessage,
        ISocketMessageChannel channel
    )
    {
        if (
            channel.Id != _options.AuditChannelId
            || newMessage.Author.Id == _client.CurrentUser.Id
            || !oldMessage.HasValue
            || oldMessage.Value.Content == newMessage.Content
        )
        {
            return;
        }

        await channel.SendMessageAsync(
            AuditText(newMessage.Author.Username, "changed", oldMessage.Value.Content),
            allowedMentions: AllowedMentions.None
        );
    }

    private async Task MessageDeleted(
        Cacheable<IMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel
    )
    {
        if (
            channel.Id != _options.AuditChannelId
            || !message.HasValue
            || message.Value.Author.Id == _client.CurrentUser.Id
        )
        {
            return;
        }

        var targetChannel = await channel.GetOrDownloadAsync();
        if (targetChannel is not null)
        {
            await targetChannel.SendMessageAsync(
                AuditText(message.Value.Author.Username, "deleted", message.Value.Content),
                allowedMentions: AllowedMentions.None
            );
        }
    }

    private static string AuditText(string username, string action, string content)
    {
        var text = $"{username} just {action} the following message: \"{content}\"";
        return text.Length <= 2000 ? text : text[..1997] + "...";
    }

    private Task MessageReceived(SocketMessage message)
    {
        if (
            message.Author.Id != _client.CurrentUser.Id
            && message.Channel.GetChannelType() == ChannelType.DM
        )
        {
            _logger.LogInformation(
                "Received a direct message from user {UserId}",
                message.Author.Id
            );
        }

        return Task.CompletedTask;
    }

    private async Task SlashCommandExecuted(SocketSlashCommand command)
    {
        try
        {
            await HandleSlashCommand(command);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Command {Command} failed for user {UserId}",
                command.Data.Name,
                command.User.Id
            );
            try
            {
                const string error = "The command could not be completed. Please try again later.";
                if (command.HasResponded)
                {
                    await command.ModifyOriginalResponseAsync(response => response.Content = error);
                }
                else
                {
                    await command.RespondAsync(error, ephemeral: true);
                }
            }
            catch (Exception responseException)
            {
                _logger.LogWarning(responseException, "Could not send the command error response");
            }
        }
    }

    private async Task HandleSlashCommand(SocketSlashCommand command)
    {
        if (command.GuildId is not { } guildId || _client.GetGuild(guildId) is not { } guild)
        {
            await command.RespondAsync(
                "This command must be executed in a server.",
                ephemeral: true
            );
            return;
        }

        var guildUser = command.User as SocketGuildUser ?? guild.GetUser(command.User.Id);
        switch (command.Data.Name)
        {
            case "ilovechinesewomen":
                if (guildUser is null)
                {
                    await command.RespondAsync(
                        "Could not find your server membership.",
                        ephemeral: true
                    );
                    return;
                }

                var role = guild.GetRole(_options.ToggleRoleId);
                if (role is null)
                {
                    await command.RespondAsync(
                        "The configured role could not be found.",
                        ephemeral: true
                    );
                    return;
                }

                await command.DeferAsync();
                var hasRole = guildUser.Roles.Any(currentRole => currentRole.Id == role.Id);
                if (hasRole)
                {
                    await guildUser.RemoveRoleAsync(role);
                }
                else
                {
                    await guildUser.AddRoleAsync(role);
                }

                _logger.LogInformation(
                    "Role {RoleId} {Action} for user {UserId}",
                    role.Id,
                    hasRole ? "removed" : "added",
                    guildUser.Id
                );
                await command.ModifyOriginalResponseAsync(response =>
                    response.Content = hasRole
                        ? "Successfully yoinked access"
                        : "Successfully awarded you access"
                );
                break;

            case "yoink":
                if (guildUser?.VoiceChannel is not { } sourceChannel)
                {
                    await command.RespondAsync(
                        "You need to be in a voice channel first.",
                        ephemeral: true
                    );
                    return;
                }

                var destinationId = (
                    command.Data.Options.FirstOrDefault(option => option.Name == "channel")?.Value
                    as IChannel
                )?.Id;
                var voiceChannel = destinationId is { } channelId
                    ? guild.GetVoiceChannel(channelId)
                    : null;
                if (voiceChannel is null)
                {
                    await command.RespondAsync(
                        "Please select a voice channel in this server.",
                        ephemeral: true
                    );
                    return;
                }

                if (
                    !guildUser.GetPermissions(sourceChannel).MoveMembers
                    || !guildUser.GetPermissions(voiceChannel).MoveMembers
                )
                {
                    await command.RespondAsync(
                        "You need Move Members permission in both voice channels.",
                        ephemeral: true
                    );
                    return;
                }

                if (sourceChannel.Id == voiceChannel.Id)
                {
                    await command.RespondAsync(
                        "You are already in that voice channel.",
                        ephemeral: true
                    );
                    return;
                }

                await command.DeferAsync(ephemeral: true);
                var movedCount = 0;
                var failedMoves = 0;
                foreach (var user in sourceChannel.ConnectedUsers.ToArray())
                {
                    _stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        await user.ModifyAsync(
                            properties => properties.Channel = voiceChannel,
                            new RequestOptions { CancelToken = _stoppingToken }
                        );
                        movedCount++;
                    }
                    catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Failed to move user {UserId}", user.Id);
                        failedMoves++;
                    }
                }

                await command.ModifyOriginalResponseAsync(response =>
                {
                    response.Content =
                        $"Moved {movedCount} users to {voiceChannel.Name}."
                        + (
                            failedMoves > 0 ? $" Failed to move {failedMoves} users." : string.Empty
                        );
                    response.AllowedMentions = AllowedMentions.None;
                });
                break;

            case "askjaina":
                if (guildUser?.VoiceChannel is not { } askJainaChannel)
                {
                    await command.RespondAsync(
                        "You need to be in a voice channel first.",
                        ephemeral: true
                    );
                    return;
                }

                var question =
                    command.Data.Options.FirstOrDefault(option => option.Name == "question")?.Value
                    as string
                    ?? string.Empty;

                if (string.IsNullOrWhiteSpace(_options.GeminiApiKey))
                {
                    await command.RespondAsync(
                        "Ask Jaina isn't configured on this server yet.",
                        ephemeral: true
                    );
                    return;
                }

                await command.DeferAsync(ephemeral: true);
                _ = Task.Run(
                    () => RunAskJainaAsync(command, askJainaChannel, question),
                    _stoppingToken
                );
                break;

            default:
                await command.RespondAsync("Unknown command.", ephemeral: true);
                break;
        }
    }

    private async Task RunAskJainaAsync(
        SocketSlashCommand command,
        IVoiceChannel voiceChannel,
        string question
    )
    {
        try
        {
            var http = _httpClientFactory.CreateClient("gemini");
            var gemini = new GeminiClient(http);

            var answer = await gemini.GenerateAnswerAsync(
                _options.GeminiTextModel,
                _options.GeminiPersonaPrompt,
                question,
                _stoppingToken
            );
            var capped = AnswerFormatting.CapSentences(answer, 3);

            var wav = await gemini.SynthesizeSpeechAsync(
                _options.GeminiTtsModel,
                _options.GeminiVoice,
                capped,
                _stoppingToken
            );

            await new VoicePlayback(_logger).PlayWavAsync(voiceChannel, wav, _stoppingToken);

            await command.ModifyOriginalResponseAsync(response => response.Content = capped);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "askjaina failed for user {UserId}",
                command.User.Id
            );
            try
            {
                await command.ModifyOriginalResponseAsync(response =>
                    response.Content = "Jaina couldn't answer that right now. Try again later."
                );
            }
            catch (Exception responseException)
            {
                _logger.LogWarning(
                    responseException,
                    "Could not send the askjaina error response"
                );
            }
        }
    }

    private async Task ClientReady()
    {
        var commands = new[]
        {
            new SlashCommandBuilder()
                .WithName("ilovechinesewomen")
                .WithDescription("Toggle access to degen channel")
                .Build(),
            new SlashCommandBuilder()
                .WithName("yoink")
                .WithDescription("Yoink everyone in your voice channel to another")
                .WithDefaultMemberPermissions(GuildPermission.MoveMembers)
                .AddOption(
                    new SlashCommandOptionBuilder()
                        .WithName("channel")
                        .WithDescription("The channel to yoink to")
                        .WithType(ApplicationCommandOptionType.Channel)
                        .WithRequired(true)
                        .AddChannelType(ChannelType.Voice)
                )
                .Build(),
            new SlashCommandBuilder()
                .WithName("askjaina")
                .WithDescription(
                    "Ask Jaina a question; she answers out loud in your voice channel"
                )
                .AddOption(
                    new SlashCommandOptionBuilder()
                        .WithName("question")
                        .WithDescription("What you want to ask Jaina")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)
                )
                .Build(),
        };

        try
        {
            foreach (var command in commands)
            {
                await _client.CreateGlobalApplicationCommandAsync(
                    command,
                    new RequestOptions { CancelToken = _stoppingToken }
                );
            }

            _logger.LogInformation("Registered commands globally");
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not register global commands");
        }
    }

    private Task ClientLog(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
        _logger.Log(
            level,
            message.Exception,
            "Discord {Source}: {Message}",
            message.Source,
            message.Message
        );
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        try
        {
            await _client.LoginAsync(TokenType.Bot, _options.Token);
            await _client.StartAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await _client.StopAsync();
            await _client.LogoutAsync();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _client.Dispose();
    }
}
