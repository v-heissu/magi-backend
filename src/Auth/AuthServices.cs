using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Magi.Api.Configuration;
using Magi.Api.Data;
using Magi.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Magi.Api.Auth;

public sealed class JwtTokenService(MagiSettings settings)
{
    public static SymmetricSecurityKey SigningKey(MagiSettings settings) =>
        new(Encoding.UTF8.GetBytes(settings.JwtSecret));

    public (string Token, DateTime ExpiresAt) Create(User user)
    {
        var expiresAt = DateTime.UtcNow.AddSeconds(settings.JwtExpirationSeconds);
        var token = new JwtSecurityToken(
            issuer: MagiSettings.JwtIssuer,
            audience: MagiSettings.JwtAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ],
            expires: expiresAt,
            signingCredentials: new SigningCredentials(SigningKey(settings), SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw new UnauthorizedAccessException("Token without a valid subject");
}

public static class PasswordHasher
{
    private const int WorkFactor = 12;

    // Used when the e-mail does not exist, so a login attempt takes the same time either way
    // and response timing does not reveal which accounts exist.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("magi-dummy-password", WorkFactor);

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public static bool Verify(string password, string? hash)
    {
        if (hash is null)
        {
            BCrypt.Net.BCrypt.Verify(password, DummyHash);
            return false;
        }

        return BCrypt.Net.BCrypt.Verify(password, hash);
    }
}

/// <summary>
/// MAGI has no public sign-up (every deliberation costs API credits): the operator account comes
/// from ADMIN_EMAIL / ADMIN_PASSWORD. Changing ADMIN_PASSWORD and restarting resets the password.
/// </summary>
public static class AdminSeeder
{
    public static async Task SeedAsync(MagiDbContext db, MagiSettings settings, ILogger logger)
    {
        if (settings.AdminEmail is null || settings.AdminPassword is null)
        {
            logger.LogWarning("ADMIN_EMAIL / ADMIN_PASSWORD not set: no operator account was created or updated");
            return;
        }

        if (settings.AdminPassword.Length < 12)
        {
            throw new InvalidOperationException("ADMIN_PASSWORD must be at least 12 characters long");
        }

        var email = settings.AdminEmail.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user is null)
        {
            db.Users.Add(new User { Email = email, PasswordHash = PasswordHasher.Hash(settings.AdminPassword) });
            logger.LogInformation("Operator account {Email} created", email);
        }
        else if (!BCrypt.Net.BCrypt.Verify(settings.AdminPassword, user.PasswordHash))
        {
            user.PasswordHash = PasswordHasher.Hash(settings.AdminPassword);
            logger.LogInformation("Operator account {Email} password updated from environment", email);
        }

        await db.SaveChangesAsync();
    }
}
