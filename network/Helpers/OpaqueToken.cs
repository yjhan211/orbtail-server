using System.Security.Cryptography;
using System.Text;

namespace network.helpers;

/// <summary>
///     클라이언트가 내용을 해석하지 않는 고정 길이 난수 토큰을 생성한다.
///     접두사와 문자열 형식을 검사하고, 토큰 원문 대신 저장·조회할 SHA-256 fingerprint를 계산한다.
///     토큰과 계정·매치 정보를 연결하거나 만료와 일회성 소비를 관리하는 책임은 각 서비스와 저장소가 담당한다.
/// </summary>
public static class OpaqueToken
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
