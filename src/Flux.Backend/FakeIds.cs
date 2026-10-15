using System.Security.Cryptography;

namespace Flux.Backend;

/// <summary>Razorpay-style ids: a type prefix plus random base62 characters, e.g. "pay_8fK2mQ9xLp3Za1".</summary>
public static class FakeIds
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string New(string prefix) =>
        prefix + "_" + RandomNumberGenerator.GetString(Alphabet, 14);
}
