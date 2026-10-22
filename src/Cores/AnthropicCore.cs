using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Magi.Api.Configuration;

namespace Magi.Api.Cores;

/// <summary>MELCHIOR-1, backed by Claude through the official Anthropic SDK.</summary>
public sealed class AnthropicCore : IMagiCore
{
    private readonly ProviderSettings _settings;
    private readonly AnthropicClient? _client;

    public AnthropicCore(MagiSettings settings)
    {
        _settings = settings.Anthropic;
        if (_settings.IsConfigured)
        {
            _client = new AnthropicClient { ApiKey = _settings.ApiKey!, BaseUrl = _settings.BaseUrl };
        }
    }

    public CoreDefinition Definition => CoreDefinitions.Melchior;
    public string Provider => "Anthropic";
    public string Model => _settings.Model;
    public bool IsConfigured => _client is not null;

    public async Task<CoreAnswer> DeliberateAsync(CoreRequest request, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new CoreFailureException("ANTHROPIC_API_KEY is not configured");
        }

        var schema = CorePrompts.VoteSchema()
            .ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));

        var parameters = new MessageCreateParams
        {
            Model = _settings.Model,
            MaxTokens = 16000,
            System = CorePrompts.BuildSystemPrompt(Definition),
            Messages = [new BetaUserMessageParam { Content = CorePrompts.BuildUserPrompt(request) }],
            // Thinking is always on for current Opus models; effort is the depth control.
            OutputConfig = new BetaOutputConfig
            {
                Effort = ParseEffort(_settings.Effort),
                Format = new BetaJsonOutputFormat { Schema = schema },
            },
            // If a safety classifier declines, the API re-runs the request on Anthropic's
            // recommended fallback model instead of returning a refusal.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
        };

        try
        {
            var response = await _client.Beta.Messages.Create(parameters, ct);

            if (response.StopReason == "refusal")
            {
                throw new CoreFailureException("The model declined to vote on this proposition");
            }

            if (response.StopReason == "max_tokens")
            {
                throw new CoreFailureException("The answer was cut off (max_tokens reached)");
            }

            var text = string.Concat(response.Content
                .Select(b => b.Value)
                .OfType<BetaTextBlock>()
                .Select(t => t.Text));

            return CorePrompts.ParseAnswer(
                text,
                (int)response.Usage.InputTokens,
                (int)response.Usage.OutputTokens);
        }
        catch (AnthropicNotFoundException ex)
        {
            throw new CoreFailureException($"Model '{_settings.Model}' not found", ex);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new CoreFailureException("Invalid Anthropic API key", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new CoreFailureException("Anthropic rate limit reached, try again later", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new CoreFailureException("Anthropic service error", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new CoreFailureException($"Anthropic API error: {Short(ex.Message)}", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new CoreFailureException("Network error while contacting Anthropic", ex);
        }
    }

    private static Effort ParseEffort(string value) => value.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };

    private static string Short(string message) => message.Length <= 300 ? message : message[..300];
}
