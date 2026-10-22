using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Magi.Api.Cores;
using Magi.Api.Models;
using Magi.Api.Providers;

namespace Magi.Api.Deliberations;

public sealed record DebateSummary(string Text, string Model);

/// <summary>
/// Writes a short recap of a finished deliberation with a low-cost model: Claude Haiku first,
/// then the cheap OpenAI or Gemini model, depending on which keys are configured.
/// The recap is a convenience: when it fails, the deliberation itself is unaffected.
/// </summary>
public sealed class DebateSummarizer(ProviderConfigStore store, IHttpClientFactory httpFactory)
{
    private const int MaxOutputTokens = 4000;

    private const string SystemPrompt =
        """
        You are the secretary of the MAGI system: three AI units (MELCHIOR-1 the scientist,
        BALTHASAR-2 the mother, CASPER-3 the woman) that voted on a proposition, possibly over two rounds.
        Write a recap of the debate for the person who asked.

        Rules:
        - Write in the same language as the proposition.
        - 120-200 words, plain text, 2-4 short paragraphs. No markdown, no headings, no bullet lists.
        - Open with the outcome in one sentence and why, then the decisive argument of each unit.
        - If a unit changed its vote in round 2, say what convinced it. Mention conditions only if they matter.
        - Report only what the units said; add no new advice or opinions of your own.
        - Units that failed to answer are mentioned in a few words, without technical details.
        """;

    public async Task<DebateSummary> SummarizeAsync(Deliberation deliberation, CancellationToken ct)
    {
        var info = ProviderCatalog.All.FirstOrDefault(p => store.Get(p.Core).Settings.IsConfigured)
            ?? throw new CoreFailureException("No API key configured for the recap");
        var provider = store.Get(info.Core).Settings;
        var prompt = BuildPrompt(deliberation);

        var text = info.Core switch
        {
            CoreId.Melchior => await AnthropicAsync(provider.ApiKey!, provider.BaseUrl, info.SummaryModel, prompt, ct),
            CoreId.Balthasar => await OpenAiAsync(provider.ApiKey!, info.SummaryModel, prompt, ct),
            _ => await GeminiAsync(provider.ApiKey!, info.SummaryModel, prompt, ct),
        };

        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new CoreFailureException("The recap model returned an empty answer");
        }

        return new DebateSummary(text.Length <= 6000 ? text : text[..6000], info.SummaryModel);
    }

    private static string BuildPrompt(Deliberation d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Proposition: {d.Proposition}");
        if (!string.IsNullOrWhiteSpace(d.Context))
        {
            sb.AppendLine($"Context: {d.Context}");
        }

        sb.AppendLine($"Decision rule: {(d.Mode == ResolutionMode.Unanimous ? "unanimity (3/3)" : "majority (2/3)")}");
        sb.AppendLine($"Outcome: {d.Resolution?.ToString().ToUpperInvariant() ?? "NONE"} " +
                      $"({d.ApproveCount} approve, {d.RejectCount} reject)");

        foreach (var round in d.Votes.GroupBy(v => v.Round).OrderBy(g => g.Key))
        {
            sb.AppendLine();
            sb.AppendLine($"== Round {round.Key} ==");
            foreach (var v in round.OrderBy(v => v.Core))
            {
                var name = CoreDefinitions.Get(v.Core).Designation;
                if (v.Verdict is null)
                {
                    sb.AppendLine($"{name}: no answer (technical failure)");
                    continue;
                }

                var conditions = JsonSerializer.Deserialize<List<string>>(v.ConditionsJson) ?? [];
                sb.AppendLine($"{name}: {v.Verdict.Value.ToString().ToUpperInvariant()}, confidence {v.Confidence}%" +
                              (v.ChangedVerdict ? " (CHANGED its vote)" : string.Empty));
                sb.AppendLine($"  Summary: {v.Summary}");
                sb.AppendLine($"  Reasoning: {v.Reasoning}");
                if (conditions.Count > 0)
                {
                    sb.AppendLine($"  Conditions: {string.Join("; ", conditions)}");
                }
            }
        }

        return sb.ToString();
    }

    private static async Task<string?> AnthropicAsync(string apiKey, string baseUrl, string model, string prompt, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl };
        try
        {
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = MaxOutputTokens,
                System = SystemPrompt,
                Messages = [new UserMessageParam { Content = prompt }],
                // Thinking is adaptive by default on Haiku 5.5; low effort keeps the recap cheap.
                OutputConfig = new OutputConfig { Effort = Effort.Low },
            }, ct);

            if (response.StopReason == "refusal")
            {
                throw new CoreFailureException("The recap model declined to answer");
            }

            return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        }
        catch (AnthropicApiException ex)
        {
            throw new CoreFailureException($"Anthropic error while writing the recap ({ex.GetType().Name})", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new CoreFailureException("Network error while contacting Anthropic", ex);
        }
    }

    private async Task<string?> OpenAiAsync(string apiKey, string model, string prompt, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = SystemPrompt,
            ["input"] = prompt,
            ["max_output_tokens"] = MaxOutputTokens,
            ["reasoning"] = new JsonObject { ["effort"] = "low" },
            ["store"] = false,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/responses");
        message.Headers.Authorization = new("Bearer", apiKey);
        var json = await HttpCoreSupport.PostJsonAsync(
            httpFactory.CreateClient(OpenAiCore.HttpClientName), message, body, "OpenAI", model, ct);

        return string.Concat((json["output"]?.AsArray() ?? [])
            .Where(item => item?["type"]?.GetValue<string>() == "message")
            .SelectMany(item => item!["content"]?.AsArray() ?? [])
            .Where(part => part?["type"]?.GetValue<string>() == "output_text")
            .Select(part => part!["text"]?.GetValue<string>()));
    }

    private async Task<string?> GeminiAsync(string apiKey, string model, string prompt, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = SystemPrompt }),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }),
            }),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = MaxOutputTokens },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
        message.Headers.Add("x-goog-api-key", apiKey);
        var json = await HttpCoreSupport.PostJsonAsync(
            httpFactory.CreateClient(GeminiCore.HttpClientName), message, body, "Google", model, ct);

        var parts = json["candidates"]?.AsArray().FirstOrDefault()?["content"]?["parts"]?.AsArray() ?? [];
        return string.Concat(parts
            .Where(p => p?["thought"]?.GetValue<bool>() != true)
            .Select(p => p?["text"]?.GetValue<string>()));
    }
}
