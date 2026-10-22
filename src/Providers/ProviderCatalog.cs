using Magi.Api.Models;

namespace Magi.Api.Providers;

/// <summary>Static facts about each provider: env variable names, effort values, fallback model list.</summary>
public sealed record ProviderInfo(
    CoreId Core,
    string Provider,
    string KeyEnvVar,
    IReadOnlyList<string> EffortOptions,
    IReadOnlyList<string> FallbackModels,
    string SummaryModel);

public static class ProviderCatalog
{
    // Fallback lists, newest first (checked against the provider docs on 2026-10-22). The settings
    // page prefers the live list from each provider's /models endpoint; these show only without a key.
    public static readonly ProviderInfo Anthropic = new(
        CoreId.Melchior,
        "Anthropic",
        "ANTHROPIC_API_KEY",
        ["low", "medium", "high", "max"],
        ["claude-fable-5-1", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"],
        "claude-haiku-5-5");

    public static readonly ProviderInfo OpenAi = new(
        CoreId.Balthasar,
        "OpenAI",
        "OPENAI_API_KEY",
        ["low", "medium", "high", "xhigh", "max"],
        ["gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-luna"],
        "gpt-6-luna");

    public static readonly ProviderInfo Gemini = new(
        CoreId.Casper,
        "Google",
        "GEMINI_API_KEY",
        ["low", "medium", "high"],
        ["gemini-4-argon", "gemini-3.8-flash", "gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-3.1-pro-preview"],
        "gemini-3.5-flash-lite");

    // SummaryModel: the low-cost model that writes the debate recap. The recap uses the first
    // provider in this order that has a key (Haiku is the cheapest of the three).
    public static readonly IReadOnlyList<ProviderInfo> All = [Anthropic, OpenAi, Gemini];

    public static ProviderInfo Get(CoreId core) => All.Single(p => p.Core == core);
}
