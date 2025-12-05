using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Orleans.DurableJobs.Redis;

/// <summary>
/// Centralizes Redis operations for DurableJobs Redis implementation.
/// </summary>
internal sealed class RedisOperationsManager
{
    private readonly IDatabase _db;

    // Lua scripts
    private const string CreateShardLua = @"
            -- KEYS[1] = metaKey
            -- KEYS[2] = shardsSetKey
            -- ARGV[1] = shardId
            -- ARGV[2] = metadataJson (JSON object with all metadata fields)
            if redis.call('EXISTS', KEYS[1]) == 1 then
                return 0
            end
            local metadata = cjson.decode(ARGV[2])
            for k, v in pairs(metadata) do
                redis.call('HSET', KEYS[1], k, v)
            end
            redis.call('SADD', KEYS[2], ARGV[1])
            return 1
        ";

    private const string TryTakeOwnershipLua = @"
            -- KEYS[1] = metaKey
            -- ARGV[1] = expectedVersion
            -- ARGV[2] = newOwner
            -- ARGV[3] = newMembershipVersion
            local curr = redis.call('HGET', KEYS[1], 'version')
            if curr == false then curr = '0' end
            if curr == ARGV[1] then
                local next = tostring(tonumber(curr) + 1)
                redis.call('HSET', KEYS[1], 'Owner', ARGV[2], 'MembershipVersion', ARGV[3], 'version', next)
                return 1
            end
            return 0
        ";

    private const string ReleaseOwnershipLua = @"
            -- KEYS[1] = metaKey
            -- ARGV[1] = expectedVersion
            local curr = redis.call('HGET', KEYS[1], 'version')
            if curr == false then curr = '0' end
            if curr == ARGV[1] then
                local next = tostring(tonumber(curr) + 1)
                redis.call('HDEL', KEYS[1], 'Owner')
                redis.call('HSET', KEYS[1], 'version', next)
                return 1
            end
            return 0
        ";

    private const string MultiXAddLua = @"
            local streamKey = KEYS[1]
            local n = tonumber(ARGV[1])
            local ids = {}
            local idx = 2
            for i=1,n do
                local payload = ARGV[idx]
                idx = idx + 1
                local id = redis.call('XADD', streamKey, '*', 'payload', payload)
                table.insert(ids, id)
            end
            return ids
        ";

    private const string UpdateMetaLua = @"
            local metaKey = KEYS[1]
            local expectedVersion = ARGV[1]
            local newVersion = ARGV[2]
            local fieldsJson = ARGV[3]
            local curr = redis.call('HGET', metaKey, 'version')
            if curr == false then curr = '' end
            if curr == expectedVersion then
                local obj = cjson.decode(fieldsJson)
                for k,v in pairs(obj) do
                    redis.call('HSET', metaKey, k, v)
                end
                redis.call('HSET', metaKey, 'version', newVersion)
                return 1
            else
                return 0
            end
        ";

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisOperationsManager"/> class.
    /// </summary>
    /// <param name="db">The Redis database instance.</param>
    public RedisOperationsManager(IDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Gets all member values from a Redis set.
    /// </summary>
    /// <param name="setKey">The key of the set.</param>
    /// <returns>An array of string values representing the set members.</returns>
    public async Task<string[]> GetSetMembersAsync(RedisKey setKey)
    {
        var members = await _db.SetMembersAsync(setKey).ConfigureAwait(false);
        return members.Select(rv => rv.ToString()).ToArray();
    }

    /// <summary>
    /// Gets all hash entries and converts them to a dictionary.
    /// </summary>
    /// <param name="hashKey">The key of the hash.</param>
    /// <returns>A dictionary containing the hash entries.</returns>
    public async Task<IDictionary<string, string>> GetHashAllAsync(RedisKey hashKey)
    {
        var entries = await _db.HashGetAllAsync(hashKey).ConfigureAwait(false);
        return HashEntriesToDictionary(entries);
    }

    /// <summary>
    /// Creates a new shard using the CreateShardLua script.
    /// </summary>
    /// <param name="metaKey">The metadata key for the shard.</param>
    /// <param name="shardsSetKey">The key of the set containing all shard IDs.</param>
    /// <param name="shardId">The unique identifier for the shard.</param>
    /// <param name="metadata">The shard metadata.</param>
    /// <returns>True if the shard was created successfully, false if it already exists.</returns>
    public async Task<bool> CreateShardAsync(RedisKey metaKey, RedisKey shardsSetKey, string shardId, IDictionary<string, string> metadata)
    {
        var metadataJson = JsonSerializer.Serialize(metadata);
        var res = (int)await _db.ScriptEvaluateAsync(CreateShardLua,
            new RedisKey[] { metaKey, shardsSetKey },
            new RedisValue[] { shardId, metadataJson }).ConfigureAwait(false);
        return res == 1;
    }

    /// <summary>
    /// Attempts to take ownership of a shard.
    /// </summary>
    /// <param name="metaKey">The metadata key for the shard.</param>
    /// <param name="expectedVersion">The expected version for optimistic concurrency control.</param>
    /// <param name="newOwner">The new owner's address.</param>
    /// <param name="newMembershipVersion">The new membership version.</param>
    /// <returns>True if ownership was successfully taken, false otherwise.</returns>
    public async Task<bool> TryTakeOwnershipAsync(RedisKey metaKey, string expectedVersion, string newOwner, string newMembershipVersion)
    {
        var res = (int)await _db.ScriptEvaluateAsync(TryTakeOwnershipLua,
            new RedisKey[] { metaKey },
            new RedisValue[] { expectedVersion ?? "0", newOwner, newMembershipVersion }).ConfigureAwait(false);
        return res == 1;
    }

    /// <summary>
    /// Releases ownership of a shard.
    /// </summary>
    /// <param name="metaKey">The metadata key for the shard.</param>
    /// <param name="expectedVersion">The expected version for optimistic concurrency control.</param>
    /// <returns>True if ownership was successfully released, false otherwise.</returns>
    public async Task<bool> ReleaseOwnershipAsync(RedisKey metaKey, string expectedVersion)
    {
        var res = (int)await _db.ScriptEvaluateAsync(ReleaseOwnershipLua,
            new RedisKey[] { metaKey },
            new RedisValue[] { expectedVersion }).ConfigureAwait(false);
        return res == 1;
    }

    /// <summary>
    /// Deletes multiple Redis keys.
    /// </summary>
    /// <param name="keys">The keys to delete.</param>
    /// <returns>The number of keys that were deleted.</returns>
    public async Task<long> DeleteKeysAsync(RedisKey[] keys)
    {
        return await _db.KeyDeleteAsync(keys).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a value from a Redis set.
    /// </summary>
    /// <param name="setKey">The key of the set.</param>
    /// <param name="value">The value to remove.</param>
    /// <returns>True if the value was removed, false if it didn't exist.</returns>
    public async Task<bool> SetRemoveAsync(RedisKey setKey, RedisValue value)
    {
        return await _db.SetRemoveAsync(setKey, value).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a range of entries from a Redis stream.
    /// Note: This method is synchronous because StackExchange.Redis's StreamRange is synchronous.
    /// </summary>
    /// <param name="streamKey">The key of the stream.</param>
    /// <param name="minId">The minimum stream entry ID (use "-" for start).</param>
    /// <param name="maxId">The maximum stream entry ID (use "+" for end).</param>
    /// <returns>An array of stream entries.</returns>
    public StreamEntry[] StreamRange(RedisKey streamKey, RedisValue minId = default, RedisValue maxId = default)
    {
        return _db.StreamRange(streamKey, minId, maxId);
    }

    /// <summary>
    /// Appends multiple job operations to a Redis stream in a single batch.
    /// </summary>
    /// <param name="streamKey">The key of the stream.</param>
    /// <param name="payloads">The payloads to append to the stream.</param>
    /// <returns>A Redis result containing the stream entry IDs.</returns>
    public async Task<RedisResult> AppendJobOperationBatchAsync(RedisKey streamKey, RedisValue[] payloads)
    {
        var args = new RedisValue[1 + payloads.Length];
        args[0] = payloads.Length;
        for (int i = 0; i < payloads.Length; i++)
        {
            args[i + 1] = payloads[i];
        }

        return await _db.ScriptEvaluateAsync(MultiXAddLua, new RedisKey[] { streamKey }, args).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates shard metadata using optimistic concurrency control.
    /// </summary>
    /// <param name="metaKey">The metadata key for the shard.</param>
    /// <param name="metadata">The new metadata values.</param>
    /// <param name="expectedVersion">The expected version for optimistic concurrency control.</param>
    /// <returns>True if the metadata was successfully updated, false if version mismatch occurred.</returns>
    public async Task<bool> UpdateMetadataAsync(RedisKey metaKey, IDictionary<string, string> metadata, long expectedVersion)
    {
        var newVersion = (expectedVersion + 1).ToString();
        var fieldsJson = JsonSerializer.Serialize(metadata);
        var res = await _db.ScriptEvaluateAsync(UpdateMetaLua,
            new RedisKey[] { metaKey },
            new RedisValue[] { expectedVersion.ToString(), newVersion, fieldsJson }).ConfigureAwait(false);
        return (int)res == 1;
    }

    private static IDictionary<string, string> HashEntriesToDictionary(HashEntry[] entries)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            dict[e.Name.ToString()] = e.Value.ToString();
        }
        return dict;
    }
}
