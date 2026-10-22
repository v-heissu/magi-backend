using Magi.Api.Auth;
using Magi.Api.Contracts;
using Magi.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Magi.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", async (LoginRequest body, MagiDbContext db, JwtTokenService tokens) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrEmpty(body.Password) || body.Password.Length > 200)
            {
                return Results.Problem("Invalid credentials", statusCode: StatusCodes.Status401Unauthorized);
            }

            var email = body.Email.Trim().ToLowerInvariant();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);

            if (!PasswordHasher.Verify(body.Password, user?.PasswordHash) || user is null)
            {
                return Results.Problem("Invalid credentials", statusCode: StatusCodes.Status401Unauthorized);
            }

            var (token, expiresAt) = tokens.Create(user);
            return Results.Ok(new LoginResponse(token, expiresAt, user.Email));
        }).RequireRateLimiting(RateLimitPolicies.Login);

        group.MapGet("/me", async (HttpContext http, MagiDbContext db) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == http.User.UserId());
            return user is null ? Results.Unauthorized() : Results.Ok(new MeResponse(user.Id, user.Email));
        }).RequireAuthorization();
    }
}
