using System;
using System.Threading.Tasks;
using StackExchange.Redis;
using static System.FormattableString;

namespace Orleans.Streaming.Redis;

internal sealed class RedisStreamCheckpointer
{
    private readonly IDatabase _database;
    private readonly RedisKey _checkpointKey;
    private readonly TimeSpan _persistInterval;
    private readonly TimeSpan? _entryExpiry;
    private Task _inProgressSave = Task.CompletedTask;
    private DateTime? _throttleSavesUntilUtc;
    private string? _persistedOffset;

    public RedisStreamCheckpointer(IDatabase database, RedisKey checkpointKey, TimeSpan persistInterval, TimeSpan? entryExpiry)
    {
        _database = database;
        _checkpointKey = checkpointKey;
        _persistInterval = persistInterval;
        _entryExpiry = entryExpiry;
    }

    public string? Offset { get; private set; }

    public bool CheckpointExists => !string.IsNullOrEmpty(Offset);

    public async Task LoadAsync()
    {
        try
        {
            var value = await _database.StringGetAsync(_checkpointKey);
            Offset = value.HasValue ? value.ToString() : null;
            _persistedOffset = Offset;
        }
        catch (Exception ex)
        {
            throw new RedisStreamingException(Invariant($"{ex.GetType()}: {ex.Message}"), ex);
        }
    }

    public void Update(string offset, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(offset) || string.Equals(offset, Offset, StringComparison.Ordinal))
        {
            return;
        }

        Offset = offset;
        if (_inProgressSave.IsCompleted && (!_throttleSavesUntilUtc.HasValue || _throttleSavesUntilUtc.Value <= utcNow))
        {
            StartSave(offset, utcNow);
        }
    }

    public async Task FlushAsync()
    {
        while (true)
        {
            await _inProgressSave;
            if (string.Equals(_persistedOffset, Offset, StringComparison.Ordinal))
            {
                return;
            }

            StartSave(Offset!, DateTime.UtcNow);
        }
    }

    private void StartSave(string offset, DateTime utcNow)
    {
        _throttleSavesUntilUtc = utcNow + _persistInterval;
        _inProgressSave = SaveAsync(offset);
    }

    private async Task SaveAsync(string offset)
    {
        try
        {
            await _database.StringSetAsync(_checkpointKey, offset);
            if (_entryExpiry is { } expiry)
            {
                await _database.KeyExpireAsync(_checkpointKey, expiry);
            }

            _persistedOffset = offset;
        }
        catch (Exception ex)
        {
            throw new RedisStreamingException(Invariant($"{ex.GetType()}: {ex.Message}"), ex);
        }
    }
}
