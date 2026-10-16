-- Token bucket, shared by every gateway instance (spec §9.4, ADR-0004).
--
-- Redis runs a script with nothing interleaved, so read → refill → decide → write is atomic:
-- two gateways can never both read "1 token left" and both spend it.
--
-- KEYS[1]  flux:rl:{<merchantId>}:<routeGroup>   (a hash: tokens, ts)
-- ARGV[1]  burst capacity B
-- ARGV[2]  refill rate r, tokens per second (may be fractional)
--
-- Returns integers only, because Redis truncates Lua numbers in replies:
--   { allowed (0|1), remaining (whole tokens), retry_after_ms, reset_after_ms }

local capacity = tonumber(ARGV[1])
local rate = tonumber(ARGV[2])

-- Redis's clock is the only clock. If each gateway sent its own time, a gateway whose clock ran
-- fast would mint tokens. Microseconds, so refill isn't quantised to whole milliseconds.
local time = redis.call('TIME')
local now_us = tonumber(time[1]) * 1000000 + tonumber(time[2])

local state = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
local tokens = tonumber(state[1])
local last_us = tonumber(state[2])
if tokens == nil or last_us == nil then
  tokens = capacity            -- a new (or expired) bucket starts full
  last_us = now_us
end

-- Lazy refill. max(0, …) guards against Redis's clock stepping backwards after a failover.
local elapsed_s = math.max(0, now_us - last_us) / 1000000
tokens = math.min(capacity, tokens + elapsed_s * rate)
tokens = math.floor(tokens * 1e9 + 0.5) / 1e9   -- same rounding as the in-memory bucket

local allowed = 0
if tokens >= 1 then
  tokens = tokens - 1
  allowed = 1
end

-- %.9f keeps the fraction exactly as computed; a bare number could be stringified with less precision.
redis.call('HSET', KEYS[1], 'tokens', string.format('%.9f', tokens), 'ts', string.format('%d', now_us))
-- After B / r seconds an untouched bucket would be full anyway, so deleting it then changes nothing.
redis.call('PEXPIRE', KEYS[1], math.ceil(capacity / rate * 1000) + 1000)

local retry_after_ms = 0
if allowed == 0 then
  retry_after_ms = math.ceil((1 - tokens) / rate * 1000)
end
local reset_after_ms = math.ceil((capacity - tokens) / rate * 1000)

return { allowed, math.floor(tokens), retry_after_ms, reset_after_ms }
