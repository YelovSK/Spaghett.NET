using NetCord.Rest;

namespace SpaghettNET.Bot.Services.Memory;

/// <summary>
/// Consecutive messages from one author in one channel. People often split one thought across
/// several quick messages, so a run is judged and remembered as a single message.
/// </summary>
internal sealed class MessageRun
{
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(60);
    private const int MaxMessages = 10;
    private const int MaxCharacters = 2000;

    private readonly List<RestMessage> _messages;

    private MessageRun(RestMessage first) => _messages = [first];

    public IReadOnlyList<RestMessage> Messages => _messages;
    public RestMessage First => _messages[0];
    public RestMessage Last => _messages[^1];
    public string Content => string.Join('\n', _messages.Select(message => message.Content.Trim()));

    private static bool IsCandidate(RestMessage message) =>
        !message.Author.IsBot &&
        !message.WebhookId.HasValue &&
        !string.IsNullOrWhiteSpace(message.Content);

    /// <summary>
    /// Feeds the next message in a channel to the run that is open there. Returns that run when the
    /// message joins it or is skipped, a new run when the message starts one, or
    /// <see langword="null"/> when the open run ended and nothing replaced it. Anyone else speaking
    /// ends a run; the author's own unrememberable messages (an image, an embed) are skipped.
    /// </summary>
    public static MessageRun? Advance(MessageRun? open, RestMessage message)
    {
        if (open is not null && message.Author.Id == open.First.Author.Id)
        {
            if (!IsCandidate(message))
            {
                return open;
            }

            if (open.CanAppend(message))
            {
                open._messages.Add(message);
                return open;
            }
        }

        return IsCandidate(message) ? new MessageRun(message) : null;
    }

    /// <summary>Splits one channel's messages, oldest first, into runs.</summary>
    public static List<MessageRun> Group(IEnumerable<RestMessage> messages)
    {
        var runs = new List<MessageRun>();
        MessageRun? open = null;
        foreach (var message in messages)
        {
            var next = Advance(open, message);
            if (next is not null && next != open)
            {
                runs.Add(next);
            }

            open = next;
        }

        return runs;
    }

    private bool CanAppend(RestMessage message) =>
        message.CreatedAt - Last.CreatedAt < MaxGap &&
        _messages.Count < MaxMessages &&
        Content.Length + 1 + message.Content.Trim().Length <= MaxCharacters;
}
