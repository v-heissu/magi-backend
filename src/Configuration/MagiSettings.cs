namespace Magi.Api.Configuration;

/// <summary>
/// All runtime configuration, read once at startup from flat environment variables
/// (see .env.example). Nothing here has a usable default for secrets: missing
/// required values fail fast at boot instead of surfacing later as odd runtime errors.
/// </summary>
public sealed class MagiSettings
{
    public required int Port { get; init; }
    public required string ConnectionString { get; init; }

    public required string JwtSecret { get; init; }
    public required int JwtExpirationSeconds { get; init; }
    public const string JwtIssuer = "magi-backend";
    public const string JwtAudience = "magi-frontend";

    /// <summary>Encrypts API keys saved from the settings page. Falls back to JWT_SECRET when unset.</summary>
    public required string SettingsEncryptionKey { get; init; }

    public required string? AdminEmail { get; init; }
    public required string? AdminPassword { get; init; }

    public required string[] CorsOrigins { get; init; }

    public required ProviderSettings Anthropic { get; init; }
    public required ProviderSettings OpenAi { get; init; }
    public required ProviderSettings Gemini { get; init; }

    public required int DeliberationsPerHour { get; init; }
    public required int MaxConcurrentDeliberations { get; init; }
    public required int CoreTimeoutSeconds { get; init; }

    public static MagiSettings FromConfiguration(IConfiguration config)
    {
        string Required(string key) =>
            string.IsNullOrWhiteSpace(config[key])
                ? throw new InvalidOperationException($"Missing required environment variable {key}")
                : config[key]!;

        string? Optional(string key) => string.IsNullOrWhiteSpace(config[key]) ? null : config[key];

        int Int(string key, int fallback) =>
            int.TryParse(config[key], out var value) && value > 0 ? value : fallback;

        var jwtSecret = Required("JWT_SECRET");
        if (jwtSecret.Length < 32)
        {
            throw new InvalidOperationException("JWT_SECRET must be at least 32 characters long");
        }

        var connectionString =
            $"Server={Required("DB_HOST")};Port={Int("DB_PORT", 3306)};Database={Required("DB_NAME")};" +
            $"User={Required("DB_USER")};Password={Required("DB_PASSWORD")};";

        return new MagiSettings
        {
            Port = Int("PORT", 8080),
            ConnectionString = connectionString,
            JwtSecret = jwtSecret,
            JwtExpirationSeconds = Int("JWT_EXPIRATION", 43200),
            SettingsEncryptionKey = Optional("SETTINGS_ENCRYPTION_KEY") ?? jwtSecret,
            AdminEmail = Optional("ADMIN_EMAIL"),
            AdminPassword = Optional("ADMIN_PASSWORD"),
            CorsOrigins = (Optional("CORS_ORIGINS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Anthropic = new ProviderSettings(
                Optional("ANTHROPIC_API_KEY"),
                Optional("ANTHROPIC_MODEL") ?? "claude-opus-5-5",
                Optional("ANTHROPIC_EFFORT") ?? "medium",
                Optional("ANTHROPIC_BASE_URL") ?? "https://api.anthropic.com"),
            OpenAi = new ProviderSettings(
                Optional("OPENAI_API_KEY"),
                Optional("OPENAI_MODEL") ?? "gpt-6.1-sol",
                Optional("OPENAI_REASONING_EFFORT") ?? "medium",
                Optional("OPENAI_BASE_URL") ?? "https://api.openai.com/"),
            Gemini = new ProviderSettings(
                Optional("GEMINI_API_KEY"),
                Optional("GEMINI_MODEL") ?? "gemini-3.1-pro-preview",
                Optional("GEMINI_THINKING_LEVEL") ?? "high",
                Optional("GEMINI_BASE_URL") ?? "https://generativelanguage.googleapis.com/"),
            DeliberationsPerHour = Int("DELIBERATIONS_PER_HOUR", 30),
            MaxConcurrentDeliberations = Int("MAX_CONCURRENT_DELIBERATIONS", 3),
            CoreTimeoutSeconds = Int("CORE_TIMEOUT_SECONDS", 240),
        };
    }
}

/// <param name="ApiKey">Null when the provider is not configured: its core is reported OFFLINE.</param>
/// <param name="Effort">Provider-specific reasoning depth knob (low / medium / high).</param>
/// <param name="BaseUrl">Provider endpoint; overridable to point at a proxy or a local mock in tests.</param>
public sealed record ProviderSettings(string? ApiKey, string Model, string Effort, string BaseUrl)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
