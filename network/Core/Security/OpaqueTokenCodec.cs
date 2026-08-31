using System.Security.Cryptography;
using System.Text;

namespace network.core.security;

/// <summary>
///     Creates, validates, and fingerprints fixed-entropy opaque authentication tokens without depending on an
///     application or persistence layer.
/// </summary>
internal static class OpaqueTokenCodec
{
    private const int RandomByteCount = 32;
    private const int EncodedRandomLength = 43;

    public static string Create(string prefix)
    {
        byte[] randomBytes = RandomNumberGenerator.GetBytes(RandomByteCount);
        return CreateFromBytes(prefix, randomBytes);
    }

    public static string CreateFromBytes(string prefix, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RandomByteCount)
            throw new ArgumentException($"Opaque token entropy must be {RandomByteCount} bytes.", nameof(bytes));

        string encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return prefix + encoded;
    }

    public static bool IsValid(string token, string prefix)
    {
        if (!token.StartsWith(prefix, StringComparison.Ordinal) ||
            token.Length != prefix.Length + EncodedRandomLength)
        {
            return false;
        }

        return token.AsSpan(prefix.Length).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) < 0;
    }

    public static string Fingerprint(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
