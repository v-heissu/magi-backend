using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Magi.Api.Cores;

/// <summary>Shared plumbing for the providers called over plain REST (OpenAI, Gemini).</summary>
internal static class HttpCoreSupport
{
    public static async Task<JsonNode> PostJsonAsync(
        HttpClient http,
        HttpRequestMessage request,
        JsonObject body,
        string providerName,
        string model,
        CancellationToken ct)
    {
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CoreFailureException($"Network error while contacting {providerName}", ex);
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new CoreFailureException(DescribeHttpError(response.StatusCode, raw, providerName, model));
            }

            try
            {
                return JsonNode.Parse(raw) ?? throw new CoreFailureException($"Empty response from {providerName}");
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new CoreFailureException($"Unreadable response from {providerName}", ex);
            }
        }
    }

    private static string DescribeHttpError(HttpStatusCode status, string raw, string provider, string model)
    {
        // Both providers return {"error": {"message": "..."}}; show it, trimmed, so the user can act on it.
        string? detail = null;
        try
        {
            detail = JsonNode.Parse(raw)?["error"]?["message"]?.GetValue<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            // Body was not JSON (e.g. a proxy error page): fall back to the status code alone.
        }

        var prefix = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"Invalid or unauthorized {provider} API key",
            HttpStatusCode.NotFound => $"{provider} model '{model}' not found",
            HttpStatusCode.TooManyRequests => $"{provider} rate limit or quota reached",
            >= HttpStatusCode.InternalServerError => $"{provider} service error",
            _ => $"{provider} API error ({(int)status})",
        };

        if (string.IsNullOrWhiteSpace(detail))
        {
            return prefix;
        }

        var trimmed = detail.Length <= 300 ? detail : detail[..300];
        return $"{prefix}: {trimmed}";
    }
}
