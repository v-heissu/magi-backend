using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magi.Api.Models;

namespace Magi.Api.Cores;

/// <summary>Prompt text and the JSON schema shared by all three providers.</summary>
public static class CorePrompts
{
    public static string BuildSystemPrompt(CoreDefinition core) =>
        $"""
        You are {core.Designation}, one of the three units of the MAGI decision system.
        The MAGI resolve a proposition by vote: three independent units, each carrying a different facet of
        the same personality, each casting APPROVE or REJECT. The final decision is taken by the system, not by you.

        {core.Persona.Trim()}

        Rules:
        - Judge the proposition strictly from your own facet. Do not try to be balanced on behalf of the other
          units: they exist to cover the viewpoints you do not represent.
        - You must cast a vote. If the proposition is not phrased as a yes/no question, read it as
          "should this be done / accepted?". If information is missing, state the assumption you made and vote anyway.
        - confidence is an integer from 0 to 100 describing how sure you are of your own vote.
        - summary: one sharp sentence that states your position.
        - reasoning: at most 150 words, concrete, in your own voice. No headings, no bullet lists.
        - conditions: things that must hold for your vote to stand (or that would change it). Empty list if none.
        - Write summary, reasoning and conditions in the same language as the proposition.
        - Answer only with the JSON object required by the response schema.
        """;

    public static string BuildUserPrompt(CoreRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<proposition>");
        sb.AppendLine(request.Proposition.Trim());
        sb.AppendLine("</proposition>");

        if (!string.IsNullOrWhiteSpace(request.Context))
        {
            sb.AppendLine();
            sb.AppendLine("<context>");
            sb.AppendLine(request.Context.Trim());
            sb.AppendLine("</context>");
        }

        if (request.Round > 1)
        {
            // Round 2+: each unit reads the others' arguments and may revise.
            // The instruction explicitly discourages conformity so that a 2-1 split survives
            // when the minority unit still has the stronger argument for its own facet.
            sb.AppendLine();
            sb.AppendLine($"This is deliberation round {request.Round}. The units did not reach unanimity.");
            if (request.OwnPrevious is { } own)
            {
                sb.AppendLine();
                sb.AppendLine("<your_previous_position>");
                AppendPosition(sb, own);
                sb.AppendLine("</your_previous_position>");
            }

            foreach (var peer in request.Peers)
            {
                sb.AppendLine();
                sb.AppendLine($"<position unit=\"{peer.Designation}\">");
                AppendPosition(sb, peer);
                sb.AppendLine("</position>");
            }

            sb.AppendLine();
            sb.AppendLine(
                "Reconsider your vote. Keep it if your facet still leads you there; change it only if the other " +
                "units showed you something your own facet cares about and you had missed. Never change just to agree. " +
                "If you keep your vote, answer their strongest objection in your reasoning.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Cast your independent vote.");
        }

        return sb.ToString();
    }

    private static void AppendPosition(StringBuilder sb, PeerPosition p)
    {
        sb.AppendLine($"vote: {p.Verdict.ToString().ToUpperInvariant()} (confidence {p.Confidence})");
        sb.AppendLine($"summary: {p.Summary}");
        sb.AppendLine($"reasoning: {p.Reasoning}");
        if (p.Conditions.Count > 0)
        {
            sb.AppendLine($"conditions: {string.Join("; ", p.Conditions)}");
        }
    }

    /// <summary>
    /// Vote schema. Kept to the subset every provider accepts in strict mode:
    /// all properties required, no additional properties, no numeric bounds (clamped in code instead).
    /// </summary>
    public static JsonObject VoteSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["verdict"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("APPROVE", "REJECT") },
            ["confidence"] = new JsonObject { ["type"] = "integer" },
            ["summary"] = new JsonObject { ["type"] = "string" },
            ["reasoning"] = new JsonObject { ["type"] = "string" },
            ["conditions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
        },
        ["required"] = new JsonArray("verdict", "confidence", "summary", "reasoning", "conditions"),
        ["additionalProperties"] = false,
    };

    /// <summary>Parses and validates the JSON text returned by a provider.</summary>
    public static CoreAnswer ParseAnswer(string? json, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new CoreFailureException("Empty answer from the provider");
        }

        try
        {
            using var doc = JsonDocument.Parse(StripCodeFence(json));
            var root = doc.RootElement;

            var verdictText = root.GetProperty("verdict").GetString()?.Trim().ToUpperInvariant();
            Verdict verdict = verdictText switch
            {
                "APPROVE" => Verdict.Approve,
                "REJECT" => Verdict.Reject,
                _ => throw new CoreFailureException($"Invalid verdict '{verdictText}'"),
            };

            var confidence = root.GetProperty("confidence").ValueKind == JsonValueKind.Number
                ? (int)Math.Round(root.GetProperty("confidence").GetDouble())
                : 50;

            var conditions = root.TryGetProperty("conditions", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray()
                    .Select(x => x.GetString()?.Trim())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Select(x => Truncate(x!, 300))
                    .Take(8)
                    .ToList()
                : [];

            return new CoreAnswer(
                verdict,
                Math.Clamp(confidence, 0, 100),
                Truncate(root.GetProperty("summary").GetString() ?? string.Empty, 1000),
                root.GetProperty("reasoning").GetString() ?? string.Empty,
                conditions,
                inputTokens,
                outputTokens);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CoreFailureException("Malformed answer from the provider", ex);
        }
    }

    // Defensive: some models wrap JSON in ```json fences even in JSON mode.
    private static string StripCodeFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
        {
            return t;
        }

        var firstNewline = t.IndexOf('\n');
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline ? t[(firstNewline + 1)..lastFence] : t;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
