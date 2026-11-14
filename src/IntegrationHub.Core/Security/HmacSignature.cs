using System.Security.Cryptography;
using System.Text;

namespace IntegrationHub.Core.Security;

/// <summary>Webhook signature in the common <c>sha256=&lt;hex&gt;</c> format (HMAC-SHA256 over the raw body).</summary>
public static class HmacSignature
{
    public const string Prefix = "sha256=";

    public static string Compute(ReadOnlySpan<byte> body, string secret) =>
        Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    public static bool IsValid(ReadOnlySpan<byte> body, string? header, string secret)
    {
        if (header is null || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(header.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);

        // Constant-time compare: a plain SequenceEqual leaks how many leading bytes matched.
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
