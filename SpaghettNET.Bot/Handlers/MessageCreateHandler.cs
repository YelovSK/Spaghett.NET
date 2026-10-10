using SpaghettNET.Bot.Extensions;
using SpaghettNET.Bot.Handlers.MessageResponders;
using SpaghettNET.Bot.Services;
using SpaghettNET.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

namespace SpaghettNET.Bot.Handlers;

public class MessageCreateHandler(
    IDbContextFactory<BotContext> dbContextFactory,
    GatewayClient gatewayClient,
    DiscordMemoryService memoryService,
    ILogger<MessageCreateHandler> logger,
    IEnumerable<IMessageCreateResponder> responders) : IMessageCreateGatewayHandler
{
    private static readonly TimeSpan TypingRefreshInterval = TimeSpan.FromSeconds(8);

    public async ValueTask HandleAsync(Message message)
    {
        await IncrementMessageCountAsync(message.Author);

        // Ignore this bot's messages
        if (message.Author.Id == gatewayClient.Id)
        {
            return;
        }

        var memoryTask = memoryService.ObserveAsync(message);
        try
        {
            foreach (var responder in responders)
            {
                if (!await responder.ShouldRespondAsync(message))
                {
                    continue;
                }

                MessageCreateResponse? response = null;
                await RunWithTypingIndicatorAsync(
                    message.ChannelId,
                    async () =>
                    {
                        response = await responder.GetResponseAsync(message);

                        if (response?.Content is { } content)
                        {
                            await SendResponseAsync(message, response.ResponseType, content);
                        }
                    });

                if (response is null)
                {
                    continue;
                }

                if (response.StopProcessing)
                {
                    return;
                }
            }
        }
        finally
        {
            try
            {
                await memoryTask;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discord memory observation failed.");
            }
        }
    }

    private async Task IncrementMessageCountAsync(NetCord.User user)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var dbUser = await dbContext.Users.TryAddAsync(user.Id, user.Username);
        dbUser.MessagesSent++;
        await dbContext.SaveChangesAsync();
    }

    private async Task SendResponseAsync(Message sourceMessage, MessageResponseType responseType, string content)
    {
        var chunks = SplitToDiscordMessageLimit(content);

        switch (responseType)
        {
            case MessageResponseType.Reply:
                await sourceMessage.ReplyAsync(chunks[0]);
                break;

            case MessageResponseType.ChannelMessage:
                await gatewayClient.Rest.SendMessageAsync(sourceMessage.ChannelId, chunks[0]);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(responseType), responseType, "Unknown message response type.");
        }

        // Only the first chunk is a reply, so the rest don't each ping the author.
        foreach (var chunk in chunks.Skip(1))
        {
            await gatewayClient.Rest.SendMessageAsync(sourceMessage.ChannelId, chunk);
        }
    }

    internal static List<string> SplitToDiscordMessageLimit(string content, int limit = 2000)
    {
        var chunks = new List<string>();
        var remaining = content.AsSpan().Trim();

        while (remaining.Length > limit)
        {
            var window = remaining[..limit];
            var cut = window.LastIndexOf('\n');
            if (cut <= 0)
            {
                cut = window.LastIndexOf(' ');
            }

            if (cut <= 0)
            {
                cut = limit;
            }

            chunks.Add(remaining[..cut].TrimEnd().ToString());
            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0 || chunks.Count == 0)
        {
            chunks.Add(remaining.ToString());
        }

        return chunks;
    }

    private async Task RunWithTypingIndicatorAsync(ulong channelId, Func<Task> action)
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var typingTask = RefreshTypingIndicatorAsync(channelId, cancellationTokenSource.Token);

        try
        {
            await action();
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();

            try
            {
                await typingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when the guarded action completes before the next refresh.
            }
        }
    }

    private async Task RefreshTypingIndicatorAsync(ulong channelId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await gatewayClient.Rest.TriggerTypingStateAsync(channelId);
            await Task.Delay(TypingRefreshInterval, cancellationToken);
        }
    }
}
