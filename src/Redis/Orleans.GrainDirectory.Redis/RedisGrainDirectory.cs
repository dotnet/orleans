#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.GrainDirectory.Redis
{
    public partial class RedisGrainDirectory : IGrainDirectory, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly RedisGrainDirectoryOptions _directoryOptions;
        private readonly ClusterOptions _clusterOptions;
        private readonly ILogger<RedisGrainDirectory> _logger;
        private readonly RedisKey _keyPrefix;
        private readonly string _ttl;

        // Both are initialized in the Initialize method.
        private IConnectionMultiplexer _redis = null!;
        private IDatabase _database = null!;

        private bool _disposed;

        public RedisGrainDirectory(
            RedisGrainDirectoryOptions directoryOptions,
            IOptions<ClusterOptions> clusterOptions,
            ILogger<RedisGrainDirectory> logger)
        {
            _directoryOptions = directoryOptions;
            _logger = logger;
            _clusterOptions = clusterOptions.Value;
            _keyPrefix = Encoding.UTF8.GetBytes($"{_clusterOptions.ClusterId}/directory/");
            _ttl = directoryOptions.EntryExpiry is { } ts ? ts.TotalSeconds.ToString(CultureInfo.InvariantCulture) : "-1";
        }

        public async Task<GrainAddress?> Lookup(GrainId grainId)
        {
            try
            {
                var result = _disposed ? null : (string?)await _database.StringGetAsync(GetKey(grainId));

                LogDebugLookup(grainId, string.IsNullOrWhiteSpace(result) ? "null" : result);

                if (string.IsNullOrWhiteSpace(result))
                    return default;

                return JsonSerializer.Deserialize<GrainAddress>(result);
            }
            catch (Exception ex)
            {
                LogErrorLookupFailed(ex, grainId);

                if (IsRedisException(ex))
                    throw new OrleansException($"Lookup failed for {grainId} : {ex}");
                else
                    throw;
            }
        }

        public Task<GrainAddress?> Register(GrainAddress address) => Register(address, null);

        public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress)
        {
            const string RegisterScript =
                """
                local cur = redis.call('GET', KEYS[1])
                local success = true
                if cur ~= false then
                    local typedCur = cjson.decode(cur)
                    if typedCur.ActivationId ~= ARGV[2] then
                       success = false
                    end
                end

                if (success) then
                    redis.call('SET', KEYS[1], ARGV[1])
                    if ARGV[3] ~= '-1' then
                        redis.call('EXPIRE', KEYS[1], ARGV[3])
                    end
                    return nil
                end

                return cur
                """;

            var value = JsonSerializer.Serialize(address);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, _database);

                var previousActivationId = previousAddress is { } ? previousAddress.ActivationId.ToString() : "";
                var key = GetKey(address.GrainId);
                var entryString = (string?)await _database.ScriptEvaluateAsync(
                    RegisterScript,
                    keys: new RedisKey[] { key },
                    values: new RedisValue[] { value, previousActivationId, _ttl })!;

                if (entryString is null)
                {
                    LogDebugRegistered(address.GrainId, value);

                    return address;
                }

                LogDebugRegisterFailed(address.GrainId, value, entryString);

                return JsonSerializer.Deserialize<GrainAddress>(entryString);
            }
            catch (Exception ex)
            {
                LogErrorRegisterFailed(ex, address.GrainId, value);

                if (IsRedisException(ex))
                {
                    throw new OrleansException($"Register failed for {address.GrainId} ({value}) : {ex}");
                }
                else
                {
                    throw;
                }
            }
        }

        public async Task Unregister(GrainAddress address)
        {
            const string DeleteScript =
                """
                local cur = redis.call('GET', KEYS[1])
                if cur ~= false then
                    local typedCur = cjson.decode(cur)
                    if typedCur.ActivationId == ARGV[1] then
                        return redis.call('DEL', KEYS[1])
                    end
                end
                return 0
                """;

            try
            {
                ObjectDisposedException.ThrowIf(_disposed, _database);

                var value = JsonSerializer.Serialize(address);
                var result = (int)await _database.ScriptEvaluateAsync(
                    DeleteScript,
                    keys: new RedisKey[] { GetKey(address.GrainId) },
                    values: new RedisValue[] { address.ActivationId.ToString() });

                LogDebugUnregister(address.GrainId, new(address), (result != 0) ? "OK" : "Conflict");
            }
            catch (Exception ex)
            {
                LogErrorUnregisterFailed(ex, address.GrainId, new(address));

                if (IsRedisException(ex))
                    throw new OrleansException($"Unregister failed for {address.GrainId} ({JsonSerializer.Serialize(address)}) : {ex}");
                else
                    throw;
            }
        }

        public Task UnregisterSilos(List<SiloAddress> siloAddresses)
        {
            return Task.CompletedTask;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(nameof(RedisGrainDirectory), ServiceLifecycleStage.RuntimeInitialize, Initialize, Uninitialize);
        }

        public async Task Initialize(CancellationToken ct = default)
        {
            _redis = await _directoryOptions.CreateMultiplexer(_directoryOptions);

            // Configure logging
            _redis.ConnectionRestored += LogConnectionRestored;
            _redis.ConnectionFailed += LogConnectionFailed;
            _redis.ErrorMessage += LogErrorMessage;
            _redis.InternalError += LogInternalError;

            _database = _redis.GetDatabase();
        }

        private async Task Uninitialize(CancellationToken arg)
        {
            if (_redis != null && _redis.IsConnected)
            {
                _disposed = true;

                await _redis.CloseAsync();
                _redis.Dispose();
                _redis = null!;
                _database = null!;
            }
        }

        private RedisKey GetKey(GrainId grainId) => _keyPrefix.Append(grainId.ToString());

        #region Logging
        private void LogConnectionRestored(object? sender, ConnectionFailedEventArgs e)
            => LogInfoConnectionRestored(e.Exception, e.EndPoint, e.FailureType);

        private void LogConnectionFailed(object? sender, ConnectionFailedEventArgs e)
            => LogErrorConnectionFailed(e.Exception, e.EndPoint, e.FailureType);

        private void LogErrorMessage(object? sender, RedisErrorEventArgs e)
            => LogErrorRedisMessage(e.Message);

        private void LogInternalError(object? sender, InternalErrorEventArgs e)
            => LogErrorInternalError(e.Exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Lookup {GrainId}: {Result}"
        )]
        private partial void LogDebugLookup(GrainId grainId, string result);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Lookup failed for {GrainId}"
        )]
        private partial void LogErrorLookupFailed(Exception exception, GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Registered {GrainId} ({Address})"
        )]
        private partial void LogDebugRegistered(GrainId grainId, string address);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Failed to register {GrainId} ({Address}) in directory: Conflicted with existing value, {Result}"
        )]
        private partial void LogDebugRegisterFailed(GrainId grainId, string address, string result);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to register {GrainId} ({Address}) in directory"
        )]
        private partial void LogErrorRegisterFailed(Exception exception, GrainId grainId, string address);

        private readonly struct GrainAddressLogRecord(GrainAddress address)
        {
            public override string ToString() => JsonSerializer.Serialize(address);
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Unregister {GrainId} ({Address}): {Result}"
        )]
        private partial void LogDebugUnregister(GrainId grainId, GrainAddressLogRecord address, string result);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Unregister failed for {GrainId} ({Address})"
        )]
        private partial void LogErrorUnregisterFailed(Exception exception, GrainId grainId, GrainAddressLogRecord address);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Connection to {EndPoint} restored: {FailureType}"
        )]
        private partial void LogInfoConnectionRestored(Exception? exception, EndPoint? endPoint, ConnectionFailureType failureType);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Connection to {EndPoint} failed: {FailureType}"
        )]
        private partial void LogErrorConnectionFailed(Exception? exception, EndPoint? endPoint, ConnectionFailureType failureType);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "{Message}"
        )]
        private partial void LogErrorRedisMessage(string message);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Internal error"
        )]
        private partial void LogErrorInternalError(Exception? exception);
        #endregion

        // These exceptions are not serializable by the client
        private static bool IsRedisException(Exception ex) => ex is RedisException || ex is RedisTimeoutException || ex is RedisCommandException;
    }
}
