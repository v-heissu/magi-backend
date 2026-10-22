using System.Text.Json.Nodes;
using Magi.Api.Configuration;

namespace Magi.Api.Cores;

/// <summary>CASPER-3, backed by Google Gemini through generateContent with a JSON response schema.</summary>
public sealed class GeminiCore(HttpClient http, MagiSettings settings) : IMagiCore
{
    public const string HttpClientName = "gemini";
    private readonly ProviderSettings _settings = settings.Gemini;

    public CoreDefinition Definition => CoreDefinitions.Casper;
    public string Provider => "Google";
    public string Model => _settings.Model;
    public bool IsConfigured => _settings.IsConfigured;

    public async Task<CoreAnswer> DeliberateAsync(CoreRequest request, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new CoreFailureException("GEMINI_API_KEY is not configured");
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = CorePrompts.BuildSystemPrompt(Definition) }),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = CorePrompts.BuildUserPrompt(request) }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseJsonSchema"] = CorePrompts.VoteSchema(),
                ["maxOutputTokens"] = 16000,
                ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = _settings.Effort },
            },
        };

        var path = $"v1beta/models/{Uri.EscapeDataString(_settings.Model)}:generateContent";
        using var message = new HttpRequestMessage(HttpMethod.Post, path);
        // Key in a header rather than the query string, so it never ends up in URL logs.
        message.Headers.Add("x-goog-api-key", _settings.ApiKey);

        var json = await HttpCoreSupport.PostJsonAsync(http, message, body, Provider, Model, ct);

        if (json["promptFeedback"]?["blockReason"]?.GetValue<string>() is { } blockReason)
        {
            throw new CoreFailureException($"Gemini blocked the proposition ({blockReason})");
        }

        var candidate = json["candidates"]?.AsArray().FirstOrDefault()
            ?? throw new CoreFailureException("Gemini returned no candidates");

        var finishReason = candidate["finishReason"]?.GetValue<string>();
        if (finishReason is not null and not "STOP")
        {
            throw new CoreFailureException(finishReason == "MAX_TOKENS"
                ? "The answer was cut off (max tokens reached)"
                : $"Gemini stopped early ({finishReason})");
        }

        // Thought-summary parts are flagged with thought=true; only the plain text parts hold the vote.
        var text = string.Concat((candidate["content"]?["parts"]?.AsArray() ?? [])
            .Where(p => p?["thought"]?.GetValue<bool>() != true)
            .Select(p => p?["text"]?.GetValue<string>() ?? string.Empty));

        var usage = json["usageMetadata"];
        var output = (usage?["candidatesTokenCount"]?.GetValue<int>() ?? 0)
                     + (usage?["thoughtsTokenCount"]?.GetValue<int>() ?? 0);

        return CorePrompts.ParseAnswer(text, usage?["promptTokenCount"]?.GetValue<int>() ?? 0, output);
    }
}
