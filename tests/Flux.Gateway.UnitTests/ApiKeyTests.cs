using System.Security.Cryptography;
using System.Text;
using Flux.Gateway.Auth;

namespace Flux.Gateway.UnitTests;

public sealed class ApiKeyTests
{
    [Fact]
    public void Generated_keys_have_the_documented_shape()
    {
        for (var i = 0; i < 1_000; i++)   // enough draws to hit keys whose number is "short" in base62
        {
            var key = ApiKey.Generate();

            Assert.Equal(48, key.Length);
            Assert.Matches("^flux_[0-9A-Za-z]{43}$", key);
            Assert.True(ApiKey.IsWellFormed(key));
        }
    }

    [Fact]
    public void Generated_keys_do_not_repeat()
    {
        var keys = Enumerable.Range(0, 10_000).Select(_ => ApiKey.Generate()).ToHashSet();

        Assert.Equal(10_000, keys.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("flux_")]
    [InlineData("flux_tooShort")]
    [InlineData("FLUX_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT")]    // scheme is case-sensitive
    [InlineData("flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVH!")]    // not base62
    [InlineData("flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHTX")]   // one character too long
    [InlineData("acme_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT")]   // right length, another vendor's scheme
    public void Malformed_keys_are_rejected_without_any_lookup(string key)
    {
        Assert.False(ApiKey.IsWellFormed(key));
    }

    [Fact]
    public void Hash_is_sha256_of_the_full_key()
    {
        const string key = "flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT";

        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(key)), ApiKey.Hash(key));
        Assert.Equal(32, ApiKey.Hash(key).Length);
    }

    [Fact]
    public void Prefix_is_the_first_twelve_characters()
    {
        Assert.Equal("flux_devQuie", ApiKey.PrefixOf("flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT"));
    }
}
