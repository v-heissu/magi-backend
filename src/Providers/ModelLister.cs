using System.Net;
using System.Text.Json.Nodes;
using Magi.Api.Configuration;
using Magi.Api.Cores;
using Magi.Api.Models;

namespace Magi.Api.Providers;

public sealed record ModelOption(string Id, string Label);

public sealed record ModelListResult(IReadOnlyList<ModelOption> Models, bool Live, string? Error, bool KeyRejected);

/// <summary>
/// Asks each provider for the models the given key can use, so the dropdown always shows the
/// latest releases. Without a key, or if the call fails, it returns the static fallback list.
/// </summary>
public sealed class ModelLister(IHttpClientFactory httpFactory)
{
    public const string AnthropicHttpClientName = "anthropic-models";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // Ids containing these are not chat/text models usable for a vote.
    private static readonly string[] OpenAiExcluded =
        ["audio", "realtime", "tts", "transcribe", "image", "embedding", "moderation", "search", "dall-e", "whisper", "instruct", "codex"];

    private static readonly string[] GeminiExcluded =
        ["embedding", "live", "tts", "image", "audio", "aqa", "robotics", "computer-use"];

    public async Task<ModelListResult> ListAsync(CoreId core, ProviderSettings provider, CancellationToken ct)
    {
        var fallback = ProviderCatalog.Get(core).FallbackModels.Select(id => new ModelOption(id, id)).ToList();
        if (!provider.IsConfigured)
        {
            return new ModelListResult(fallback, false, null, false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            var models = core switch
            {
                CoreId.Melchior => await ListAnthropicAsync(provider, timeout.Token),
                CoreId.Balthasar => await ListOpenAiAsync(provider, timeout.Token),
                _ => await ListGeminiAsync(provider, timeout.Token),
            };
            return models.Count > 0
                ? new ModelListResult(models, true, null, false)
                : new ModelListResult(fallback, false, "The provider returned no usable models", false);
        }
        catch (CoreFailureException ex)
        {
            var rejected = ex.Data["status"] is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.BadRequest;
            return new ModelListResult(fallback, false, ex.Message, rejected);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ModelListResult(fallback, false, "The provider did not answer in time", false);
        }
    }

    private async Task<List<ModelOption>> ListAnthropicAsync(ProviderSettings p, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models?limit=100");
        request.Headers.Add("x-api-key", p.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        var json = await SendAsync(AnthropicHttpClientName, request, "Anthropic", ct);

        // Already sorted newest first by the API.
        return (json["data"]?.AsArray() ?? [])
            .Select(m => (Id: m?["id"]?.GetValue<string>(), Name: m?["display_name"]?.GetValue<string>()))
            .Where(m => !string.IsNullOrEmpty(m.Id))
            .Select(m => new ModelOption(m.Id!, m.Name is null ? m.Id! : $"{m.Name} · {m.Id}"))
            .ToList();
    }

    private async Task<List<ModelOption>> ListOpenAiAsync(ProviderSettings p, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        request.Headers.Authorization = new("Bearer", p.ApiKey);
        var json = await SendAsync(OpenAiCore.HttpClientName, request, "OpenAI", ct);

        return (json["data"]?.AsArray() ?? [])
            .Select(m => (Id: m?["id"]?.GetValue<string>(), Created: m?["created"]?.GetValue<long>() ?? 0))
            .Where(m => m.Id is not null
                        && (m.Id.StartsWith("gpt-", StringComparison.Ordinal) || IsOSeries(m.Id))
                        && !OpenAiExcluded.Any(x => m.Id.Contains(x, StringComparison.Ordinal)))
            .OrderByDescending(m => m.Created)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => new ModelOption(m.Id!, m.Id!))
            .ToList();
    }

    private async Task<List<ModelOption>> ListGeminiAsync(ProviderSettings p, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1beta/models?pageSize=1000");
        request.Headers.Add("x-goog-api-key", p.ApiKey);
        var json = await SendAsync(GeminiCore.HttpClientName, request, "Google", ct);

        // No release date in the payload: order by version number, newest first.
        return (json["models"]?.AsArray() ?? [])
            .Where(m => (m?["supportedGenerationMethods"]?.AsArray() ?? [])
                .Any(x => x?.GetValue<string>() == "generateContent"))
            .Select(m => (Id: m?["name"]?.GetValue<string>()?.Replace("models/", string.Empty),
                Name: m?["displayName"]?.GetValue<string>()))
            .Where(m => m.Id is not null
                        && m.Id.StartsWith("gemini-", StringComparison.Ordinal)
                        && !GeminiExcluded.Any(x => m.Id.Contains(x, StringComparison.Ordinal)))
            .OrderByDescending(m => GeminiVersion(m.Id!))
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => new ModelOption(m.Id!, m.Name is null ? m.Id! : $"{m.Name} · {m.Id}"))
            .ToList();
    }

    private async Task<JsonNode> SendAsync(string clientName, HttpRequestMessage request, string provider, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(clientName);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CoreFailureException($"Network error while contacting {provider}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? $"{provider} rejected the API key"
                    : $"{provider} model list failed ({(int)response.StatusCode})";
                var error = new CoreFailureException(message);
                error.Data["status"] = response.StatusCode;
                throw error;
            }

            var raw = await response.Content.ReadAsStringAsync(ct);
            try
            {
                return JsonNode.Parse(raw) ?? new JsonObject();
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new CoreFailureException($"Unreadable model list from {provider}", ex);
            }
        }
    }

    private static bool IsOSeries(string id) => id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1]);

    private static double GeminiVersion(string id)
    {
        // "gemini-3.8-flash" -> 3.8 ; anything unparsable sorts last.
        var part = id["gemini-".Length..].Split('-')[0];
        return double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
