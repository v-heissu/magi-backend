using Magi.Api.Auth;
using Magi.Api.Configuration;
using Magi.Api.Cores;
using Magi.Api.Data;
using Magi.Api.Deliberations;
using Magi.Api.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var settings = MagiSettings.FromConfiguration(builder.Configuration);
builder.WebHost.UseUrls($"http://+:{settings.Port}");
builder.Services.AddSingleton(settings);

builder.Services.AddDbContext<MagiDbContext>(options =>
    options.UseMySql(settings.ConnectionString, DbConfig.ServerVersion,
        mysql => mysql.EnableRetryOnFailure(maxRetryCount: 5)));

builder.Services.AddSingleton<JwtTokenService>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep "sub"/"email" as-is instead of remapping them to long WS-* claim URIs.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = MagiSettings.JwtIssuer,
            ValidateAudience = true,
            ValidAudience = MagiSettings.JwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtTokenService.SigningKey(settings),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorization();

// The browser never calls the backend directly (the SvelteKit server proxies every request),
// so CORS stays closed unless explicit origins are configured.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (settings.CorsOrigins.Length > 0)
    {
        policy.WithOrigins(settings.CorsOrigins).AllowAnyHeader().WithMethods("GET", "POST", "DELETE");
    }
}));

builder.Services.AddMagiRateLimits(settings);
builder.Services.AddProblemDetails();

// The three cores. Plain REST providers get named HttpClients with a generous timeout:
// reasoning models can think for minutes. The engine applies its own per-core timeout on top.
builder.Services.AddHttpClient(OpenAiCore.HttpClientName, c =>
{
    c.BaseAddress = new Uri(WithTrailingSlash(settings.OpenAi.BaseUrl));
    c.Timeout = TimeSpan.FromSeconds(settings.CoreTimeoutSeconds + 30);
});
builder.Services.AddHttpClient(GeminiCore.HttpClientName, c =>
{
    c.BaseAddress = new Uri(WithTrailingSlash(settings.Gemini.BaseUrl));
    c.Timeout = TimeSpan.FromSeconds(settings.CoreTimeoutSeconds + 30);
});
builder.Services.AddSingleton<IMagiCore, AnthropicCore>();
builder.Services.AddSingleton<IMagiCore>(sp => new OpenAiCore(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAiCore.HttpClientName), settings));
builder.Services.AddSingleton<IMagiCore>(sp => new GeminiCore(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(GeminiCore.HttpClientName), settings));

builder.Services.AddSingleton<DeliberationEventHub>();
builder.Services.AddSingleton<DeliberationQueue>();
builder.Services.AddSingleton<DeliberationEngine>();
builder.Services.AddHostedService<DeliberationWorker>();

var app = builder.Build();

await InitializeDatabaseAsync(app);

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapSystemEndpoints();
app.MapAuthEndpoints();
app.MapDeliberationEndpoints();

app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<MagiDbContext>();

    // MySQL can still be warming up even after the compose healthcheck passes: retry for ~1 minute.
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            break;
        }
        catch (Exception ex) when (attempt < 12 && ex is MySqlConnector.MySqlException or InvalidOperationException)
        {
            logger.LogWarning("Database not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    await AdminSeeder.SeedAsync(db, app.Services.GetRequiredService<MagiSettings>(), logger);
}

static string WithTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
