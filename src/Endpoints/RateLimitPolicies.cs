using System.Threading.RateLimiting;
using Magi.Api.Auth;
using Magi.Api.Configuration;
using Microsoft.AspNetCore.RateLimiting;

namespace Magi.Api.Endpoints;

public static class RateLimitPolicies
{
    public const string Login = "login";
    public const string Deliberations = "deliberations";

    public static IServiceCollection AddMagiRateLimits(this IServiceCollection services, MagiSettings settings)
    {
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Brute-force protection. The backend is reachable only from the frontend container,
            // which forwards the real client address in X-Forwarded-For.
            options.AddPolicy(Login, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));

            // Cost protection: each deliberation fires up to six paid model calls.
            options.AddPolicy(Deliberations, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.Identity?.IsAuthenticated == true ? http.User.UserId().ToString() : ClientAddress(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.DeliberationsPerHour,
                    Window = TimeSpan.FromHours(1),
                }));
        });
    }

    private static string ClientAddress(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            return forwarded.Split(',')[0].Trim();
        }

        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
