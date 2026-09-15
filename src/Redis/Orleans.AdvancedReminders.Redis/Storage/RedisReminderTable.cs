using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Orleans.Configuration;

using StackExchange.Redis;
using static System.FormattableString;

namespace Orleans.AdvancedReminders.Redis;


internal partial class RedisReminderTable : IReminderTable, IDisposable, IAsyncDisposable
{
    private readonly RedisKey _hashSetKey;
    private readonly RedisReminderTableOptions _redisOptions;
    private readonly ClusterOptions _clusterOptions;
    private readonly ILogger _logger;
    private IConnectionMultiplexer _muxer = default!;
    private IDatabase _db = default!;
    private bool _muxerIsShared;

    public RedisReminderTable(
        ILogger<RedisReminderTable> logger,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<RedisReminderTableOptions> redisOptions)
    {
        _redisOptions = redisOptions.Value;
        _clusterOptions = clusterOptions.Value;
        _logger = logger;

        _hashSetKey = Encoding.UTF8.GetBytes($"{_clusterOptions.ServiceId}/advanced-reminders");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>? creationTask = null;
        try
        {
            creationTask = _redisOptions.CreateMultiplexer(_redisOptions);
            (_muxer, _muxerIsShared) = await creationTask.WaitAsync(cancellationToken);
            _db = _muxer.GetDatabase();

            if (_redisOptions.EntryExpiry is { } expiry)
            {
                await _db.KeyExpireAsync(_hashSetKey, expiry).WaitAsync(cancellationToken);
            }
            else
            {
                await _db.KeyPersistAsync(_hashSetKey).WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_muxer is not null)
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            else if (creationTask is not null)
            {
                _ = DisposeMultiplexerWhenCreatedAsync(creationTask);
            }

            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeException)
            {
                _logger.LogWarning(
                    disposeException,
                    "Error disposing the Redis connection after advanced reminder table initialization failed.");
            }

            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
        => await DisposeAsync().AsTask().WaitAsync(cancellationToken);

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        try
        {
            var (from, to) = GetFilter(grainId, reminderName, eTag);
            long removed = await _db.SortedSetRemoveRangeByValueAsync(_hashSetKey, from, to);
            return removed > 0;
        }
        catch (Exception exception)
        {
            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public async Task TestOnlyClearTable()
    {
        try
        {
            await _db.KeyDeleteAsync(_hashSetKey);
        }
        catch (Exception exception)
        {
            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public async Task<string> UpsertRow(ReminderEntry entry)
    {
        const string UpsertScript =
            """
            local key = KEYS[1]
            local expectedFrom = '[' .. ARGV[1]
            local expectedTo = '[' .. ARGV[2]
            local allFrom = '[' .. ARGV[3]
            local allTo = '[' .. ARGV[4]
            local value = ARGV[5]
            local expectedETag = ARGV[6]
            local expiryMilliseconds = tonumber(ARGV[7])

            if expectedETag == '' then
                local existing = redis.call('ZRANGEBYLEX', key, allFrom, allTo, 'LIMIT', 0, 1)
                if #existing ~= 0 then
                    return 0
                end
            elseif redis.call('ZREMRANGEBYLEX', key, expectedFrom, expectedTo) ~= 1 then
                return 0
            end

            redis.call('ZADD', key, 0, value)
            if expiryMilliseconds > 0 then
                redis.call('PEXPIRE', key, expiryMilliseconds)
            else
                redis.call('PERSIST', key)
            end
            return 1
            """;

        try
        {
            LogDebugUpsertRow(new(entry), entry.ETag);

            var (newETag, value) = ConvertFromEntry(entry);
            var (expectedFrom, expectedTo) = GetFilter(entry.GrainId, entry.ReminderName, entry.ETag);
            var (allFrom, allTo) = GetFilter(entry.GrainId, entry.ReminderName);
            var expiryMilliseconds = _redisOptions.EntryExpiry is { } expiry
                ? Math.Max(1, (long)Math.Ceiling(expiry.TotalMilliseconds))
                : -1;
            var result = await _db.ScriptEvaluateAsync(
                UpsertScript,
                keys: new[] { _hashSetKey },
                values: new RedisValue[] { expectedFrom, expectedTo, allFrom, allTo, value, entry.ETag, expiryMilliseconds });
            if ((long)result != 1)
            {
                throw new Runtime.ReminderException(
                    $"Could not update reminder '{entry.ReminderName}' for grain '{entry.GrainId}' due to ETag mismatch.");
            }

            return newETag.ToString();
        }
        catch (Exception exception) when (exception is not Runtime.ReminderException)
        {
            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public void Dispose()
    {
        var muxer = _muxer;
        if (muxer is null)
        {
            return;
        }

        var muxerIsShared = _muxerIsShared;
        _muxer = null!;
        _db = null!;
        _muxerIsShared = false;

        if (!muxerIsShared)
        {
            muxer.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var muxer = _muxer;
        if (muxer is null)
        {
            return;
        }

        var muxerIsShared = _muxerIsShared;
        _muxer = null!;
        _db = null!;
        _muxerIsShared = false;

        if (!muxerIsShared)
        {
            await muxer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task DisposeMultiplexerWhenCreatedAsync(
        Task<(IConnectionMultiplexer Multiplexer, bool IsShared)> creationTask)
    {
        try
        {
            var (multiplexer, isShared) = await creationTask.ConfigureAwait(false);
            if (!isShared)
            {
                await multiplexer.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Observe a late connection failure after initialization was canceled.
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "UpsertRow entry = {Entry}, ETag = {ETag}"
    )]
    private partial void LogDebugUpsertRow(ReminderEntryLogValue entry, string eTag);
}
