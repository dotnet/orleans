using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.DurableJobs;
using Orleans.DurableJobs.Redis;
using StackExchange.Redis;
using Tester.DurableJobs;
using TestExtensions;

namespace Tester.Redis.DurableJobs;

/// <summary>
/// Redis implementation of <see cref="IJobShardManagerTestFixture"/>.
/// Provides the infrastructure needed to run shared job shard manager tests against Redis.
/// </summary>
internal sealed class RedisJobShardManagerTestFixture : IJobShardManagerTestFixture
{
    private readonly IOptions<RedisJobShardOptions> _options;
    private IConnectionMultiplexer _multiplexer;
    private readonly string _shardPrefix;

    public RedisJobShardManagerTestFixture()
    {
        _shardPrefix = $"test-{Guid.NewGuid():N}";

        // Create a custom CreateMultiplexer that caches the multiplexer for cleanup
        _options = Options.Create(new RedisJobShardOptions
        {
            ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString),
            CreateMultiplexer = CreateMultiplexerAsync,
            ShardPrefix = _shardPrefix,
            MaxShardCreationRetries = 5,
            MaxBatchSize = 128,
            MinBatchSize = 1,
            BatchFlushInterval = TimeSpan.FromMilliseconds(100)
        });
    }

    private async Task<IConnectionMultiplexer> CreateMultiplexerAsync(RedisJobShardOptions options)
    {
        if (_multiplexer == null)
        {
            _multiplexer = await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions);
        }
        return _multiplexer;
    }

    public JobShardManager CreateManager(ILocalSiloDetails localSiloDetails, IClusterMembershipService membershipService)
    {
        return new RedisJobShardManager(
            localSiloDetails,
            _options,
            membershipService,
            NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer != null)
        {
            // Clean up test data from Redis
            var db = _multiplexer.GetDatabase();
            var server = _multiplexer.GetServer(_multiplexer.GetEndPoints()[0]);

            // Delete all keys with our test prefix
            await foreach (var key in server.KeysAsync(pattern: $"durablejobs:shard:{_shardPrefix}*"))
            {
                await db.KeyDeleteAsync(key);
            }

            // Delete the shard set key
            await db.KeyDeleteAsync($"durablejobs:shards:{_shardPrefix}");

            await _multiplexer.CloseAsync();
            _multiplexer.Dispose();
        }
    }
}
