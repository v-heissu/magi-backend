using System.Text.Json.Nodes;
using Magi.Api.Models;
using Magi.Api.Providers;

namespace Magi.Api.Cores;

/// <summary>BALTHASAR-2, backed by OpenAI through the Responses API with a strict JSON schema.</summary>
public sealed class OpenAiCore(HttpClient http, ProviderConfigStore store) : IMagiCore
{
    public const string HttpClientName = "openai";

    public CoreDefinition Definition => CoreDefinitions.Balthasar;
    public string Provider => "OpenAI";
    public string Model => store.Get(CoreId.Balthasar).Settings.Model;
    public bool IsConfigured => store.Get(CoreId.Balthasar).Settings.IsConfigured;

    public async Task<CoreAnswer> DeliberateAsync(CoreRequest request, CancellationToken ct)
    {
        // One snapshot per call, so a settings change mid-call cannot mix models or keys.
        var provider = store.Get(CoreId.Balthasar).Settings;
        if (!provider.IsConfigured)
        {
            throw new CoreFailureException("OpenAI API key is not configured");
        }

        var body = new JsonObject
        {
            ["model"] = provider.Model,
            ["instructions"] = CorePrompts.BuildSystemPrompt(Definition),
            ["input"] = CorePrompts.BuildUserPrompt(request),
            ["max_output_tokens"] = 16000,
            ["reasoning"] = new JsonObject { ["effort"] = provider.Effort },
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "magi_vote",
                    ["strict"] = true,
                    ["schema"] = CorePrompts.VoteSchema(),
                },
            },
            // Votes are persisted by MAGI itself; no need for OpenAI to keep a copy.
            ["store"] = false,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/responses");
        message.Headers.Authorization = new("Bearer", provider.ApiKey);

        var json = await HttpCoreSupport.PostJsonAsync(http, message, body, Provider, provider.Model, ct);

        if (json["status"]?.GetValue<string>() == "incomplete")
        {
            var reason = json["incomplete_details"]?["reason"]?.GetValue<string>() ?? "unknown";
            throw new CoreFailureException($"OpenAI returned an incomplete answer ({reason})");
        }

        // output[] mixes reasoning items and message items; the vote is in the message's output_text.
        string? text = null;
        foreach (var item in json["output"]?.AsArray() ?? [])
        {
            if (item?["type"]?.GetValue<string>() != "message")
            {
                continue;
            }

            foreach (var part in item["content"]?.AsArray() ?? [])
            {
                switch (part?["type"]?.GetValue<string>())
                {
                    case "refusal":
                        throw new CoreFailureException("The model declined to vote on this proposition");
                    case "output_text":
                        text += part["text"]?.GetValue<string>();
                        break;
                }
            }
        }

        var usage = json["usage"];
        return CorePrompts.ParseAnswer(
            text,
            usage?["input_tokens"]?.GetValue<int>() ?? 0,
            usage?["output_tokens"]?.GetValue<int>() ?? 0);
    }
}
