using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Flux.Gateway.Auth;

/// <summary>
/// The API-key format from spec §7.2 and ADR-0003: "flux_" + 32 random bytes as 43 base62
/// characters. 32 bytes is 256 bits of entropy, which is why a fast hash (SHA-256) is enough.
/// </summary>
public static class ApiKey
{
    public const string Scheme = "flux_";
    public const int SecretLength = 43;      // 62^43 > 2^256, so 43 characters always fit 32 bytes
    public const int Length = 48;            // Scheme.Length + SecretLength
    public const int PrefixLength = 12;      // "flux_" + 7 characters: enough to tell keys apart

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly SearchValues<char> Base62 = SearchValues.Create(Alphabet);

    public static string Generate()
    {
        Span<byte> secret = stackalloc byte[32];
        RandomNumberGenerator.Fill(secret);
        return Scheme + ToBase62(secret);
    }

    /// <summary>Shape check only. It never touches the cache or the database, so junk is rejected for free.</summary>
    public static bool IsWellFormed(ReadOnlySpan<char> key) =>
        key.Length == Length
        && key.StartsWith(Scheme, StringComparison.Ordinal)
        && !key[Scheme.Length..].ContainsAnyExcept(Base62);

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.ASCII.GetBytes(key));

    public static string PrefixOf(string key) => key[..PrefixLength];

    // Fixed width (left-padded with '0') so every key has the same length and the format check
    // can be exact; otherwise ~1 key in 62 would come out one character shorter.
    private static string ToBase62(ReadOnlySpan<byte> bytes)
    {
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[SecretLength];
        for (var i = SecretLength - 1; i >= 0; i--)
        {
            value = BigInteger.DivRem(value, 62, out var digit);
            chars[i] = Alphabet[(int)digit];
        }
        return new string(chars);
    }
}
