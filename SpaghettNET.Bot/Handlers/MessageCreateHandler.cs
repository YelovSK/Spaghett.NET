using SpaghettNET.Bot.Extensions;
using SpaghettNET.Bot.Handlers.MessageResponders;
using SpaghettNET.Bot.Services;
using SpaghettNET.Persistence;
using Microsoft.EntityFrameworkCore;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using SpaghettNET.Bot.Services.Memory;

namespace SpaghettNET.Bot.Handlers;

public class MessageCreateHandler(
    IDbContextFactory<BotContext> dbContextFactory,
    GatewayClient gatewayClient,
    DiscordMemoryRunBuffer memoryRunBuffer,
    DiscordMessageSender messageSender,
    IEnumerable<IMessageCreateResponder> responders) : IMessageCreateGatewayHandler
{
    private static readonly TimeSpan TypingRefreshInterval = TimeSpan.FromSeconds(8);

    public async ValueTask HandleAsync(Message message)
    {
        await IncrementMessageCountAsync(message.Author);
        memoryRunBuffer.Observe(message);

        // Ignore this bot's messages
        if (message.Author.Id == gatewayClient.Id)
        {
            return;
        }

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

    private async Task IncrementMessageCountAsync(NetCord.User user)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var dbUser = await dbContext.Users.TryAddAsync(user.Id, user.Username);
        dbUser.MessagesSent++;
        await dbContext.SaveChangesAsync();
    }

    private Task SendResponseAsync(Message sourceMessage, MessageResponseType responseType, string content) =>
        responseType switch
        {
            MessageResponseType.Reply => messageSender.SendAsync(sourceMessage.ChannelId, content, replyTo: sourceMessage),
            MessageResponseType.ChannelMessage => messageSender.SendAsync(sourceMessage.ChannelId, content),
            _ => throw new ArgumentOutOfRangeException(nameof(responseType), responseType, "Unknown message response type."),
        };

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
