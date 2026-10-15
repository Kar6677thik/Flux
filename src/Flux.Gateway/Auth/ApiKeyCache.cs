using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Flux.Gateway.Auth;

/// <summary>
/// In-memory cache in front of the api_keys table (spec §8), keyed by the key's SHA-256 hash so the
/// plaintext key is never held longer than one request. Found keys are kept for 60 s, unknown keys
/// for 10 s. Time comes from TimeProvider so tests can jump past a TTL instead of sleeping.
/// </summary>
public sealed class ApiKeyCache(TimeProvider time, IOptions<ApiKeyOptions> options)
{
    private readonly ApiKeyOptions _options = options.Value;

    // Two maps because only the negative one can be grown by an attacker: positive entries are
    // bounded by the number of real keys, negative entries by the number of keys anyone can invent.
    private readonly ConcurrentDictionary<string, Entry> _found = new();
    private readonly ConcurrentDictionary<string, Entry> _notFound = new();

    private sealed record Entry(AuthenticatedMerchant? Merchant, DateTimeOffset ExpiresAt);

    public bool TryGet(string keyHash, out AuthenticatedMerchant? merchant)
    {
        var now = time.GetUtcNow();
        if (TryGetFresh(_found, keyHash, now, out var entry) || TryGetFresh(_notFound, keyHash, now, out entry))
        {
            merchant = entry.Merchant;
            return true;
        }

        merchant = null;
        return false;
    }

    public void SetFound(string keyHash, AuthenticatedMerchant merchant)
    {
        _notFound.TryRemove(keyHash, out _);
        _found[keyHash] = new Entry(merchant, time.GetUtcNow().AddSeconds(_options.CacheTtlSeconds));
    }

    public void SetNotFound(string keyHash)
    {
        var now = time.GetUtcNow();
        if (_notFound.Count >= _options.MaxCacheEntries)
        {
            foreach (var (key, entry) in _notFound)
                if (entry.ExpiresAt <= now)
                    _notFound.TryRemove(new KeyValuePair<string, Entry>(key, entry));

            // Still full: skip caching rather than grow without bound. Those requests go to the
            // database, which is what they would cost with no negative cache at all.
            if (_notFound.Count >= _options.MaxCacheEntries)
                return;
        }

        _found.TryRemove(keyHash, out _);
        _notFound[keyHash] = new Entry(null, now.AddSeconds(_options.NegativeCacheTtlSeconds));
    }

    private static bool TryGetFresh(ConcurrentDictionary<string, Entry> map, string keyHash, DateTimeOffset now, out Entry entry)
    {
        if (!map.TryGetValue(keyHash, out entry!))
            return false;
        if (entry.ExpiresAt > now)
            return true;

        // Remove only the exact entry we saw: another request may have refreshed it meanwhile.
        map.TryRemove(new KeyValuePair<string, Entry>(keyHash, entry));
        return false;
    }
}
