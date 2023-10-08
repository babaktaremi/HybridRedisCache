﻿using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;
using System.Runtime.CompilerServices;

namespace HybridRedisCache;

/// <summary>
/// The HybridCache class provides a hybrid caching solution that stores cached items in both
/// an in-memory cache and a Redis cache. 
/// </summary>
public class HybridCache : IHybridCache, IDisposable
{
    private const string FlushDb = "FLUSHDB";
    private readonly IDatabase _redisDb;
    private readonly string _instanceId;
    private readonly HybridCachingOptions _options;
    private readonly ISubscriber _redisSubscriber;
    private readonly ILogger _logger;

    private IMemoryCache _memoryCache;
    private string InvalidationChannel => _options.InstancesSharedName + ":invalidate";
    private int retryPublishCounter = 0;
    private int exponentialRetryMilliseconds = 100;
    private string ClearAllKey => GetCacheKey($"*{FlushDb}*");

    /// <summary>
    /// This method initializes the HybridCache instance and subscribes to Redis key-space events 
    /// to invalidate cache entries on all instances. 
    /// </summary>
    /// <param name="redisConnectionString">Redis connection string</param>
    /// <param name="instanceName">Application unique name for redis indexes</param>
    /// <param name="defaultExpiryTime">default caching expiry time</param>
    public HybridCache(HybridCachingOptions option, ILoggerFactory loggerFactory = null)
    {
        option.NotNull(nameof(option));

        _instanceId = Guid.NewGuid().ToString("N");
        _options = option;
        CreateLocalCache();
        var redisConfig = ConfigurationOptions.Parse(option.RedisConnectString, true);
        redisConfig.AbortOnConnectFail = option.AbortOnConnectFail;
        redisConfig.ConnectRetry = option.ConnectRetry;
        redisConfig.ClientName = option.InstancesSharedName + ":" + _instanceId;
        var redis = ConnectionMultiplexer.Connect(redisConfig);

        _redisDb = redis.GetDatabase();
        _redisSubscriber = redis.GetSubscriber();
        _logger = loggerFactory?.CreateLogger(nameof(HybridCache));

        // Subscribe to Redis key-space events to invalidate cache entries on all instances
        _redisSubscriber.Subscribe(new RedisChannel(InvalidationChannel, RedisChannel.PatternMode.Literal), OnInvalidationMessage, CommandFlags.FireAndForget);
        _redisSubscriber.Subscribe(new RedisChannel(_options.RedisBackChannelName, RedisChannel.PatternMode.Literal), OnCacheUpdate, CommandFlags.FireAndForget);
        redis.ConnectionRestored += OnReconnect;
    }

    private void OnCacheUpdate(RedisChannel channel, RedisValue value)
    {
        lock (_memoryCache)
        {
            var message = value.ToString().Deserialize<SyncCacheEventModel>();

            if (message.EventCreatorIdentifier.Equals(_instanceId,
                    StringComparison.InvariantCultureIgnoreCase))
                return;


            _memoryCache.Set(message.Key, message.Value, message.ExpiryDate);
        }
    }

    private void CreateLocalCache()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
    }

    private void OnInvalidationMessage(RedisChannel channel, RedisValue value)
    {
        // With this implementation, when a key is updated or removed in Redis,
        // all instances of HybridCache that are subscribed to the pub/sub channel will receive a message
        // and invalidate the corresponding key in their local MemoryCache.

        var message = value.ToString().Deserialize<CacheInvalidationMessage>();
        if (message.InstanceId != _instanceId) // filter out messages from the current instance
        {
            if (message.CacheKeys.FirstOrDefault().Equals(ClearAllKey))
            {
                ClearLocalMemory();
                return;
            }

            foreach (var key in message.CacheKeys)
            {
                _memoryCache.Remove(key);
                LogMessage($"remove local cache that cache key is {key}");
            }
        }
    }

    /// <summary>
    /// On reconnect (flushes local memory as it could be stale).
    /// </summary>
    private void OnReconnect(object sender, ConnectionFailedEventArgs e)
    {
        if (_options.FlushLocalCacheOnBusReconnection)
        {
            LogMessage("Flushing local cache due to bus reconnection");
            ClearLocalMemory();
        }
    }

    public bool Exists(string key)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var cacheKey = GetCacheKey(key);

        // Circuit Breaker may be more better
        try
        {
            if (_redisDb.KeyExists(cacheKey))
                return true;
        }
        catch (Exception ex)
        {
            LogMessage($"Check cache key exists error [{key}] ", ex);
            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        return _memoryCache.TryGetValue(cacheKey, out var _);
    }

    public async Task<bool> ExistsAsync(string key)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var cacheKey = GetCacheKey(key);

        // Circuit Breaker may be more better
        try
        {
            if (await _redisDb.KeyExistsAsync(cacheKey))
                return true;
        }
        catch (Exception ex)
        {
            LogMessage($"Check cache key [{key}] exists error", ex);
            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        return _memoryCache.TryGetValue(cacheKey, out var _);
    }

    public void Set<T>(string key, T value, TimeSpan? localExpiry = null, TimeSpan? redisExpiry = null, bool fireAndForget = true)
    {
        Set(key, value, localExpiry, redisExpiry, fireAndForget, true, true);
    }

    public void Set<T>(string key, T value, HybridCacheEntry cacheEntry)
    {
        Set(key, value, cacheEntry.LocalExpiry, cacheEntry.RedisExpiry, cacheEntry.FireAndForget, cacheEntry.LocalCacheEnable, cacheEntry.RedisCacheEnable);
    }

    private void Set<T>(string key, T value, TimeSpan? localExpiry, TimeSpan? redisExpiry, bool fireAndForget, bool localCacheEnable, bool redisCacheEnable)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        SetExpiryTimes(ref localExpiry, ref redisExpiry);
        var cacheKey = GetCacheKey(key);
        if (localCacheEnable)
            _memoryCache.Set(cacheKey, value, localExpiry.Value);

        try
        {
            if (redisCacheEnable)
                _redisDb.StringSet(cacheKey, value.Serialize(), redisExpiry.Value, flags: GetCommandFlags(fireAndForget));
        }
        catch (Exception ex)
        {
            LogMessage($"set cache key [{key}] error", ex);

            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        // When create/update cache, send message to bus so that other clients can update it.
        PublishBus(new SyncCacheEventModel() { ExpiryDate = localExpiry.Value, Key = cacheKey, Value = value.Serialize(), EventCreatorIdentifier = _instanceId });
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? localExpiry = null, TimeSpan? redisExpiry = null, bool fireAndForget = true)
    {
        return SetAsync(key, value, localExpiry, redisExpiry, fireAndForget, true, true);
    }

    public Task SetAsync<T>(string key, T value, HybridCacheEntry cacheEntry)
    {
        return SetAsync(key, value, cacheEntry.LocalExpiry, cacheEntry.RedisExpiry, cacheEntry.FireAndForget, cacheEntry.LocalCacheEnable, cacheEntry.RedisCacheEnable);
    }

    private async Task SetAsync<T>(string key, T value, TimeSpan? localExpiry, TimeSpan? redisExpiry, bool fireAndForget, bool localCacheEnable, bool redisCacheEnable)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        SetExpiryTimes(ref localExpiry, ref redisExpiry);
        var cacheKey = GetCacheKey(key);
        if (localCacheEnable)
            _memoryCache.Set(cacheKey, value, localExpiry.Value);

        try
        {
            if (redisCacheEnable)
                await _redisDb.StringSetAsync(cacheKey, value.Serialize(), redisExpiry.Value,
                        flags: GetCommandFlags(fireAndForget)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMessage($"set cache key [{key}] error", ex);

            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        // When create/update cache, send message to bus so that other clients can update it.
        await PublishBusAsync(new SyncCacheEventModel() { ExpiryDate = localExpiry.Value, Key = cacheKey, Value = value.Serialize(), EventCreatorIdentifier = _instanceId }).ConfigureAwait(false);
    }

    public void SetAll<T>(IDictionary<string, T> value, TimeSpan? localExpiry = null, TimeSpan? redisExpiry = null, bool fireAndForget = true)
    {
        SetAll(value, localExpiry, redisExpiry, fireAndForget, true, true);
    }

    public void SetAll<T>(IDictionary<string, T> value, HybridCacheEntry cacheEntry)
    {
        SetAll(value, cacheEntry.LocalExpiry, cacheEntry.RedisExpiry, cacheEntry.FireAndForget, cacheEntry.LocalCacheEnable, cacheEntry.RedisCacheEnable);
    }

    private void SetAll<T>(IDictionary<string, T> value, TimeSpan? localExpiry, TimeSpan? redisExpiry, bool fireAndForget, bool localCacheEnable, bool redisCacheEnable)
    {
        value.NotNullAndCountGTZero(nameof(value));
        SetExpiryTimes(ref localExpiry, ref redisExpiry);

        foreach (var kvp in value)
        {
            var cacheKey = GetCacheKey(kvp.Key);
            if (localCacheEnable)
                _memoryCache.Set(cacheKey, kvp.Value, localExpiry.Value);

            try
            {
                if (redisCacheEnable)
                    _redisDb.StringSet(cacheKey, kvp.Value.Serialize(), redisExpiry.Value,
                         flags: GetCommandFlags(fireAndForget));

                PublishBus(new SyncCacheEventModel()
                {
                    ExpiryDate = localExpiry.Value
                    ,
                    Key = cacheKey
                    ,
                    Value = kvp.Value.Serialize()
                    ,
                    EventCreatorIdentifier = _instanceId
                });
            }
            catch (Exception ex)
            {
                LogMessage($"set cache key [{kvp.Key}] error", ex);

                if (_options.ThrowIfDistributedCacheError)
                {
                    throw;
                }
            }
        }

        // send message to bus 
        // PublishBus(value.Keys.ToArray());
    }

    public Task SetAllAsync<T>(IDictionary<string, T> value, TimeSpan? localExpiry = null, TimeSpan? redisExpiry = null, bool fireAndForget = true)
    {
        return SetAllAsync(value, localExpiry, redisExpiry, fireAndForget, true, true);
    }

    public Task SetAllAsync<T>(IDictionary<string, T> value, HybridCacheEntry cacheEntry)
    {
        return SetAllAsync(value, cacheEntry.LocalExpiry, cacheEntry.RedisExpiry, cacheEntry.FireAndForget, cacheEntry.LocalCacheEnable, cacheEntry.RedisCacheEnable);
    }

    private async Task SetAllAsync<T>(IDictionary<string, T> value, TimeSpan? localExpiry, TimeSpan? redisExpiry, bool fireAndForget, bool localCacheEnable, bool redisCacheEnable)
    {
        value.NotNullAndCountGTZero(nameof(value));
        SetExpiryTimes(ref localExpiry, ref redisExpiry);

        foreach (var kvp in value)
        {
            var cacheKey = GetCacheKey(kvp.Key);
            _memoryCache.Set(cacheKey, kvp.Value, localExpiry.Value);

            try
            {
                await _redisDb.StringSetAsync(cacheKey, kvp.Value.Serialize(), redisExpiry.Value,
                     flags: GetCommandFlags(fireAndForget)).ConfigureAwait(false);

                await PublishBusAsync(new SyncCacheEventModel() { ExpiryDate = localExpiry.Value, Key = cacheKey, Value = kvp.Value.Serialize(), EventCreatorIdentifier = _instanceId }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogMessage($"set cache key [{kvp.Key}] error", ex);

                if (_options.ThrowIfDistributedCacheError)
                {
                    throw;
                }
            }
        }

        // send message to bus 
        //await PublishBusAsync(value.Keys.ToArray());


    }

    public T Get<T>(string key)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var cacheKey = GetCacheKey(key);

        if (TryGetStringValueFromCache(cacheKey, out T cacheValue))
            return cacheValue;

        if (_memoryCache.TryGetValue(cacheKey, out T value))
        {
            return value;
        }

        try
        {
            var redisValue = _redisDb.StringGet(cacheKey);
            if (redisValue.HasValue)
            {
                value = redisValue.ToString().Deserialize<T>();
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Redis cache get error, [{key}]", ex);
            if (_options.ThrowIfDistributedCacheError)
                throw;
        }

        if (value != null)
        {
            var expiry = GetExpiration(key);
            _memoryCache.Set(cacheKey, value, expiry);
            return value;
        }

        LogMessage($"distributed cache can not get the value of `{key}` key");
        return value;
    }

    public T Get<T>(string key, Func<string, T> dataRetriever, TimeSpan? localExpiry = null, TimeSpan? redisExpiry = null, bool fireAndForget = true)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        SetExpiryTimes(ref localExpiry, ref redisExpiry);
        var cacheKey = GetCacheKey(key);


        if (TryGetStringValueFromCache(cacheKey, out T cacheValue))
            return cacheValue;

        if (_memoryCache.TryGetValue(cacheKey, out T value))
        {
            return value;
        }

      

        try
        {
            var redisValue = _redisDb.StringGet(cacheKey);
            if (redisValue.HasValue)
            {
                value = redisValue.ToString().Deserialize<T>();
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Redis cache get error, [{key}]", ex);
            if (_options.ThrowIfDistributedCacheError)
                throw;
        }

        if (value is not null)
        {
            _memoryCache.Set(cacheKey, value, localExpiry.Value);
            return value;
        }

        try
        {
            value = dataRetriever(key);
        }
        catch (Exception ex)
        {
            LogMessage($"get with data retriever error [{key}]", ex);
            if (_options.ThrowIfDistributedCacheError)
                throw;
        }

        if (value is not null)
        {
            Set(key, value, localExpiry, redisExpiry, fireAndForget);
            return value;
        }

        LogMessage($"distributed cache can not get the value of `{key}` key. Data retriver also had problem.");
        return value;
    }

    public async Task<T> GetAsync<T>(string key)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var cacheKey = GetCacheKey(key);

        if (TryGetStringValueFromCache(cacheKey, out T cacheValue))
            return cacheValue;

        if (_memoryCache.TryGetValue(cacheKey, out T value))
        {
            return value;
        }

       

        try
        {
            var redisValue = await _redisDb.StringGetAsync(cacheKey).ConfigureAwait(false);
            if (redisValue.HasValue)
            {
                value = redisValue.ToString().Deserialize<T>();
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Redis cache get error, [{key}]", ex);
            if (_options.ThrowIfDistributedCacheError)
                throw;
        }

        if (value != null)
        {
            var expiry = await GetExpirationAsync(key);
            _memoryCache.Set(cacheKey, value, expiry);
            return value;
        }

        LogMessage($"distributed cache can not get the value of `{key}` key");
        return value;
    }

    public async Task<T> GetAsync<T>(string key, Func<string, Task<T>> dataRetriever,
        TimeSpan? localExpiry = null, TimeSpan? redisExpiry = null, bool fireAndForget = true)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        SetExpiryTimes(ref localExpiry, ref redisExpiry);
        var cacheKey = GetCacheKey(key);

        if (TryGetStringValueFromCache(cacheKey, out T cacheValue))
            return cacheValue;

        if (_memoryCache.TryGetValue(cacheKey, out T value))
        {
            return value;
        }

       

        try
        {
            var redisValue = await _redisDb.StringGetAsync(cacheKey).ConfigureAwait(false);
            if (redisValue.HasValue)
            {
                value = redisValue.ToString().Deserialize<T>();
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Redis cache get error, [{key}]", ex);
            if (_options.ThrowIfDistributedCacheError)
                throw;
        }

        if (value is not null)
        {
            _memoryCache.Set(cacheKey, value, localExpiry.Value);
            return value;
        }

        try
        {
            value = await dataRetriever(key);
        }
        catch (Exception ex)
        {
            LogMessage($"get with data retriever error [{key}]", ex);
            if (_options.ThrowIfDistributedCacheError)
                throw;
        }

        if (value is not null)
        {
            Set(key, value, localExpiry, redisExpiry, fireAndForget);
            return value;
        }

        LogMessage($"distributed cache can not get the value of `{key}` key. Data retriver also had a problem.");
        return value;
    }

    public bool TryGetValue<T>(string key, out T value)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var cacheKey = GetCacheKey(key);

        if (TryGetStringValueFromCache(cacheKey, out value))
            return true;

        if (_memoryCache.TryGetValue(cacheKey, out value))
        {
            return true;
        }

       

        try
        {
            var redisValue = _redisDb.StringGet(cacheKey);
            if (redisValue.HasValue)
            {
                value = redisValue.ToString().Deserialize<T>();
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Redis cache get error, [{key}]", ex);
            if (_options.ThrowIfDistributedCacheError)
                throw;
        }

        if (value != null)
        {
            var expiry = GetExpiration(key);
            _memoryCache.Set(cacheKey, value, expiry);
            return true;
        }

        LogMessage($"distributed cache can not get the value of `{key}` key");
        return false;
    }

    public void Remove(string key, bool fireAndForget = false)
    {
        Remove(new[] { key }, fireAndForget);
    }

    public void Remove(string[] keys, bool fireAndForget = false)
    {
        keys.NotNullAndCountGTZero(nameof(keys));
        var cacheKeys = Array.ConvertAll(keys, GetCacheKey);
        try
        {
            // distributed cache at first
            _redisDb.KeyDelete(Array.ConvertAll(cacheKeys, x => (RedisKey)x),
                flags: GetCommandFlags(fireAndForget));
        }
        catch (Exception ex)
        {
            LogMessage($"remove cache key [{string.Join(" | ", keys)}] error", ex);

            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        Array.ForEach(cacheKeys, _memoryCache.Remove);

        // send message to bus 
        PublishBus(cacheKeys);
    }

    public Task RemoveAsync(string key, bool fireAndForget = false)
    {
        return RemoveAsync(new[] { key }, fireAndForget);
    }

    public async Task RemoveAsync(string[] keys, bool fireAndForget = false)
    {
        keys.NotNullAndCountGTZero(nameof(keys));
        var cacheKeys = Array.ConvertAll(keys, GetCacheKey);
        try
        {
            // distributed cache at first
            await _redisDb.KeyDeleteAsync(Array.ConvertAll(cacheKeys, x => (RedisKey)x),
                flags: GetCommandFlags(fireAndForget)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMessage($"remove cache key [{string.Join(" | ", keys)}] error", ex);

            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        Array.ForEach(cacheKeys, _memoryCache.Remove);

        // send message to bus 
        await PublishBusAsync(cacheKeys).ConfigureAwait(false);
    }

    public async Task<string[]> RemoveWithPatternAsync(string pattern, bool fireAndForget = false, CancellationToken token = default)
    {
        pattern.NotNullAndCountGTZero(nameof(pattern));
        var removedKeys = new List<string>();
        var keyPattern = "*" + GetCacheKey(pattern);
        if (keyPattern.EndsWith("*") == false)
            keyPattern += "*";

        try
        {
            await foreach (var key in GetKeysAsync(keyPattern, token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested)
                    break;

                if (await _redisDb.KeyDeleteAsync(key).ConfigureAwait(false))
                {
                    removedKeys.Add(key);
                }
            }
            LogMessage($"{removedKeys.Count} matching keys found and removed with `{keyPattern}` pattern");
        }
        catch (Exception ex)
        {
            LogMessage($"remove cache key [{string.Join(" | ", removedKeys)}] error", ex);

            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        var keys = removedKeys.ToArray();
        Array.ForEach(keys, _memoryCache.Remove);

        // send message to bus 
        await PublishBusAsync(keys).ConfigureAwait(false);
        return keys;
    }

    public void ClearAll()
    {
        _redisDb.Execute(FlushDb);
        FlushLocalCaches();
    }

    public async Task ClearAllAsync()
    {
        await _redisDb.ExecuteAsync(FlushDb);
        await FlushLocalCachesAsync();
    }

    public void FlushLocalCaches()
    {
        ClearLocalMemory();
        PublishBus(ClearAllKey);
    }

    public async Task FlushLocalCachesAsync()
    {
        ClearLocalMemory();
        await PublishBusAsync(ClearAllKey);
    }

    private void ClearLocalMemory()
    {
        lock (_memoryCache)
        {
            _memoryCache.Dispose();
            CreateLocalCache();
            LogMessage($"clear all local cache");
        }
    }

    private string GetCacheKey(string key) => $"{_options.InstancesSharedName}:{key}";

    private async Task PublishBusAsync(params string[] cacheKeys)
    {
        cacheKeys.NotNullAndCountGTZero(nameof(cacheKeys));

        try
        {
            // include the instance ID in the pub/sub message payload to update another instances
            var message = new CacheInvalidationMessage(_instanceId, cacheKeys);
            await _redisDb.PublishAsync(InvalidationChannel, message.Serialize(), CommandFlags.FireAndForget).ConfigureAwait(false);
        }
        catch
        {
            // Retry to publish message
            if (retryPublishCounter++ < _options.ConnectRetry)
            {
                await Task.Delay(exponentialRetryMilliseconds * retryPublishCounter).ConfigureAwait(false);
                await PublishBusAsync(cacheKeys).ConfigureAwait(false);
            }
        }
    }

    private async Task PublishBusAsync(SyncCacheEventModel cacheEventModel)
    {

        try
        {
            // include the instance ID in the pub/sub message payload to update another instances
            await _redisDb.PublishAsync(_options.RedisBackChannelName, cacheEventModel.Serialize(), CommandFlags.FireAndForget).ConfigureAwait(false);
        }
        catch
        {
            // Retry to publish message
            if (retryPublishCounter++ < _options.ConnectRetry)
            {
                await Task.Delay(exponentialRetryMilliseconds * retryPublishCounter).ConfigureAwait(false);
                await PublishBusAsync(cacheEventModel).ConfigureAwait(false);
            }
        }
    }

    private void PublishBus(params string[] cacheKeys)
    {
        cacheKeys.NotNullAndCountGTZero(nameof(cacheKeys));

        try
        {
            // include the instance ID in the pub/sub message payload to update another instances
            var message = new CacheInvalidationMessage(_instanceId, cacheKeys);
            _redisDb.Publish(InvalidationChannel, message.Serialize(), CommandFlags.FireAndForget);
        }
        catch
        {
            // Retry to publish message
            if (retryPublishCounter++ < _options.ConnectRetry)
            {
                Thread.Sleep(exponentialRetryMilliseconds * retryPublishCounter);
                PublishBus(cacheKeys);
            }
        }
    }

    private void PublishBus(SyncCacheEventModel cacheEventModel)
    {


        try
        {
            // include the instance ID in the pub/sub message payload to update another instances

            _redisDb.Publish(_options.RedisBackChannelName, cacheEventModel.Serialize(), CommandFlags.FireAndForget);
        }
        catch
        {
            // Retry to publish message
            if (retryPublishCounter++ < _options.ConnectRetry)
            {
                Thread.Sleep(exponentialRetryMilliseconds * retryPublishCounter);
                PublishBus(cacheEventModel);
            }
        }
    }

    public TimeSpan GetExpiration(string cacheKey)
    {
        cacheKey.NotNullOrWhiteSpace(nameof(cacheKey));

        try
        {
            var time = _redisDb.KeyExpireTime(GetCacheKey(cacheKey));
            return time.ToTimeSpan();
        }
        catch
        {
            return _options.DefaultDistributedExpirationTime;
        }
    }

    public async Task<TimeSpan> GetExpirationAsync(string cacheKey)
    {
        cacheKey.NotNullOrWhiteSpace(nameof(cacheKey));

        try
        {
            var time = await _redisDb.KeyExpireTimeAsync(GetCacheKey(cacheKey));
            return time.ToTimeSpan();
        }
        catch
        {
            return _options.DefaultDistributedExpirationTime;
        }
    }

    public async IAsyncEnumerable<string> KeysAsync(string pattern, [EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var key in GetKeysAsync(pattern, token).ConfigureAwait(false))
        {
            yield return key;
        }
    }

    private async IAsyncEnumerable<RedisKey> GetKeysAsync(string pattern, [EnumeratorCancellation] CancellationToken token = default)
    {
        var servers = GetServers();
        foreach (var server in servers)
        {
            // it would be *better* to try and find a single replica per
            // primary and run the SCAN on the replica, but... let's
            // keep it relatively simple
            if (server.IsConnected && !server.IsReplica)
            {
                if (token.IsCancellationRequested)
                    break;

                await foreach (var key in server.KeysAsync(pattern: pattern).ConfigureAwait(false))
                {
                    if (token.IsCancellationRequested)
                        break;

                    yield return key;
                }
            }
        }
    }

    private void SetExpiryTimes(ref TimeSpan? localExpiry, ref TimeSpan? redisExpiry)
    {
        localExpiry ??= _options.DefaultLocalExpirationTime;
        redisExpiry ??= _options.DefaultDistributedExpirationTime;
    }

    private IServer[] GetServers()
    {
        // there may be multiple endpoints behind a multiplexer
        var endpoints = _redisDb.Multiplexer.GetEndPoints();

        // SCAN is on the server API per endpoint
        return endpoints.Select(ep => _redisDb.Multiplexer.GetServer(ep)).ToArray();
    }

    private bool TryGetStringValueFromCache<TValue>(string cacheKey, out TValue value)
    {
        if (_memoryCache.TryGetValue(cacheKey, out string stringValue))
        {
            try
            {

                value = stringValue.Deserialize<TValue>();
                return true;
            }
            catch
            {
                _logger.LogWarning("Invalid String Cache Value for cache key {cacheKey}", cacheKey);
            }
        }

        value = default(TValue);
        return false;
    }

    private void LogMessage(string message, Exception ex = null)
    {
        if (_options.EnableLogging && _logger is not null)
        {
            if (ex is null)
            {
                _logger.LogInformation(message);
            }
            else
            {
                _logger.LogError(ex, message);
            }
        }
    }

    private CommandFlags GetCommandFlags(bool fireAndForget)
    {
        return fireAndForget ? CommandFlags.FireAndForget : CommandFlags.None;
    }

    public void Dispose()
    {
        _redisSubscriber?.UnsubscribeAll();
        _redisDb?.Multiplexer?.Dispose();
        _memoryCache?.Dispose();
    }
}