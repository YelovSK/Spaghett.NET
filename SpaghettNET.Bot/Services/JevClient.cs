using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SpaghettNET.Bot.Services;

public sealed class JevClient(
    HttpClient httpClient,
    OpenAIOptions options,
    ILogger<JevClient> logger)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Asks yes/no questions, returning each answer's probability that the answer is yes.</summary>
    internal async Task<IReadOnlyDictionary<string, double>?> EvaluateAsync(
        object state,
        IReadOnlyDictionary<string, JevNoulQuestion> questions,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var answers = await SendAsync(state, questions, sessionId, cancellationToken);
        if (answers is null)
        {
            return null;
        }

        if (questions.Keys.Any(key =>
                !answers.TryGetValue(key, out var answer) ||
                answer.Type != "noul" || answer.Noul is not { } value ||
                !double.IsFinite(value) || value is < 0 or > 1))
        {
            logger.LogWarning("Jev returned an incomplete or invalid noul answer set.");
            return null;
        }

        return questions.Keys.ToDictionary(key => key, key => answers[key].Noul!.Value);
    }

    /// <summary>
    /// Asks each question to pick one option, returning the probability spread across that
    /// question's options. Probabilities are normalised per question, so they rank options
    /// against each other rather than measuring them on an absolute scale.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, JevChoiceResult>?> ChooseAsync(
        object state,
        IReadOnlyDictionary<string, JevChoiceQuestion> questions,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var answers = await SendAsync(state, questions, sessionId, cancellationToken);
        if (answers is null)
        {
            return null;
        }

        var results = new Dictionary<string, JevChoiceResult>(questions.Count);
        foreach (var key in questions.Keys)
        {
            if (!answers.TryGetValue(key, out var answer) ||
                answer.Type != "choice" ||
                answer.Probabilities is not { Count: > 0 } probabilities ||
                answer.Confidence is not { } confidence ||
                !double.IsFinite(confidence))
            {
                logger.LogWarning("Jev returned an incomplete or invalid choice answer set.");
                return null;
            }

            results[key] = new JevChoiceResult(confidence, probabilities);
        }

        return results;
    }

    private async Task<IReadOnlyDictionary<string, JevAnswer>?> SendAsync(
        object state,
        object questions,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.MemoryModel) ||
            !TryGetDecisionsUri(options.BaseUrl, out var decisionsUri))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, decisionsUri)
        {
            Content = JsonContent.Create(
                new JevDecisionRequest(options.MemoryModel, state, questions, sessionId),
                options: JsonSerializerOptions),
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Jev request failed with status code {StatusCode}.",
                    response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<JevDecisionResponse>(
                JsonSerializerOptions,
                cancellationToken);
            return result?.Answers;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Jev request failed.");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogWarning(ex, "Jev request timed out.");
            return null;
        }
    }

    private static bool TryGetDecisionsUri(string? baseUrl, out Uri? decisionsUri)
    {
        decisionsUri = null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("openrouter.ai", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        decisionsUri = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/api/alpha/decisions");
        return true;
    }
}

internal sealed record JevDecisionRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("state")] object State,
    [property: JsonPropertyName("questions")] object Questions,
    [property: JsonPropertyName("session_id")] string SessionId);

internal sealed record JevNoulQuestion(
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("criteria")] JevNoulCriteria Criteria,
    [property: JsonPropertyName("type")] string Type = "noul");

internal sealed record JevNoulCriteria(
    [property: JsonPropertyName("true")] string True,
    [property: JsonPropertyName("false")] string False);

/// <summary>Jev accepts at most <see cref="MaxOptions"/> options per choice question.</summary>
internal sealed record JevChoiceQuestion(
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("criteria")] IReadOnlyDictionary<string, string> Criteria,
    [property: JsonPropertyName("type")] string Type = "choice")
{
    public const int MaxOptions = 255;
}

/// <param name="Confidence">How peaked <paramref name="Probabilities"/> is; a flat spread means no option stood out.</param>
internal sealed record JevChoiceResult(
    double Confidence,
    IReadOnlyDictionary<string, double> Probabilities);

internal sealed record JevDecisionResponse(
    [property: JsonPropertyName("answers")] IReadOnlyDictionary<string, JevAnswer>? Answers);

internal sealed record JevAnswer(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("noul")] double? Noul,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("probabilities")] IReadOnlyDictionary<string, double>? Probabilities);
