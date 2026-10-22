using System.Security.Cryptography;
using System.Text;
using Magi.Api.Configuration;

namespace Magi.Api.Providers;

/// <summary>
/// AES-256-GCM for the API keys saved in the database, so a DB dump alone does not leak them.
/// The key comes from SETTINGS_ENCRYPTION_KEY; changing it makes saved keys unreadable
/// (they then fall back to the .env values until re-entered).
/// </summary>
public sealed class SecretProtector
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretProtector(MagiSettings settings)
    {
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("magi-settings:" + settings.SettingsEncryptionKey));
    }

    public string Protect(string plain)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var payload = new byte[NonceSize + TagSize + plainBytes.Length];
        var nonce = payload.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, payload.AsSpan(NonceSize + TagSize), payload.AsSpan(NonceSize, TagSize));
        return Prefix + Convert.ToBase64String(payload);
    }

    /// <summary>Returns null when the value cannot be decrypted (wrong key, tampered data).</summary>
    public string? Unprotect(string cipher)
    {
        if (!cipher.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var payload = Convert.FromBase64String(cipher[Prefix.Length..]);
            if (payload.Length < NonceSize + TagSize)
            {
                return null;
            }

            var plain = new byte[payload.Length - NonceSize - TagSize];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(payload.AsSpan(0, NonceSize), payload.AsSpan(NonceSize + TagSize),
                payload.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
