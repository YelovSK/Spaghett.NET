using Bot.API.Extensions;
using Bot.API.Handlers.MessageResponders;
using Bot.Persistence;
using Microsoft.EntityFrameworkCore;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

namespace Bot.API.Handlers;

public class MessageCreateHandler(
    IDbContextFactory<BotContext> dbContextFactory,
    GatewayClient gatewayClient,
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

                    if (response is not null)
                    {
                        await SendResponseAsync(message, response);
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

    private async Task SendResponseAsync(Message sourceMessage, MessageCreateResponse response)
    {
        var trimmedContent = TrimToDiscordMessageLimit(response.Content);

        switch (response.ResponseType)
        {
            case MessageResponseType.Reply:
                await sourceMessage.ReplyAsync(trimmedContent);
                break;

            case MessageResponseType.ChannelMessage:
                await gatewayClient.Rest.SendMessageAsync(sourceMessage.ChannelId, new MessageProperties
                {
                    Content = trimmedContent,
                });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(response), response.ResponseType, "Unknown message response type.");
        }
    }

    private static string TrimToDiscordMessageLimit(string response) =>
        response.Length <= 2000
            ? response
            : response[..1997] + "...";

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
