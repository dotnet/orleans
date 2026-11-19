#nullable enable
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Journaling;
using Orleans.Journaling.Tests;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Journaling;

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestCategory("BVT")]
public sealed class RedisJournalStorageConformanceTests : JournalStorageConformanceTests
{
    private readonly string _keyPrefix = $"orleans-tests/journaling-conformance/{Guid.NewGuid():N}";
    private readonly List<SiloLifecycleSubject> _lifecycles = [];
    private ConnectionMultiplexer? _multiplexer;

    protected override async ValueTask<IJournalStorageProvider> CreateProviderAsync(JournalStorageFaultInjector? faults, CancellationToken cancellationToken)
    {
        TestUtils.CheckForRedis();
        var connectionString = TestDefaultConfiguration.RedisConnectionString
            ?? throw new InvalidOperationException("Redis connection string is not configured.");
        _multiplexer ??= await ConnectionMultiplexer.ConnectAsync(connectionString);
        var multiplexer = _multiplexer;
        var provider = new RedisJournalStorageProvider(
            Options.Create(new RedisJournalStorageOptions
            {
                ConfigurationOptions = ConfigurationOptions.Parse(connectionString),
                CreateMultiplexer = _ => Task.FromResult(((IConnectionMultiplexer)multiplexer, true)),
                KeyPrefix = _keyPrefix,
                ReadChunkSize = 4096,
            }),
            Options.Create(new ClusterOptions { ServiceId = $"redis-journaling-conformance-{Guid.NewGuid():N}" }),
            Options.Create(new JournaledStateManagerOptions()));
        var lifecycle = new SiloLifecycleSubject(Microsoft.Extensions.Logging.Abstractions.NullLogger<SiloLifecycleSubject>.Instance);
        provider.Participate(lifecycle);
        await lifecycle.OnStart(cancellationToken);
        _lifecycles.Add(lifecycle);
        return provider;
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var lifecycle in _lifecycles)
        {
            await lifecycle.OnStop(CancellationToken.None);
        }

        if (_multiplexer is null)
        {
            return;
        }

        var database = _multiplexer.GetDatabase();
        foreach (var server in _multiplexer.GetServers())
        {
            await foreach (var key in server.KeysAsync(pattern: $"{_keyPrefix}:*"))
            {
                await database.KeyDeleteAsync(key);
            }
        }

        await _multiplexer.CloseAsync();
        _multiplexer.Dispose();
    }
}
