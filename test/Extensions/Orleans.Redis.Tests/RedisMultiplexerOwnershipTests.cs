using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Redis;
using Orleans.Configuration;
using Orleans.GrainDirectory.Redis;
using Orleans.Persistence;
using Orleans.Reminders.Redis;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis;

/// <summary>
/// Verifies that the non-streaming Redis providers dispose multiplexers they own while leaving shared
/// (for example, DI-provided) multiplexers untouched. Each provider implements <see cref="System.IDisposable"/>
/// and <see cref="System.IAsyncDisposable"/> independently, so both disposal paths are exercised.
/// </summary>
[TestCategory("Redis"), TestCategory("Functional")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public sealed class RedisMultiplexerOwnershipTests
{
    private readonly CommonFixture _fixture;

    public RedisMultiplexerOwnershipTests(CommonFixture fixture)
    {
        _fixture = fixture;
    }

    private static ConfigurationOptions GetConfigurationOptions()
    {
        var connectionString = TestDefaultConfiguration.RedisConnectionString;

        // The dispose-before-initialize tests do not require a live Redis and never connect, so fall back to an
        // empty configuration when no connection string is configured rather than failing to parse a null value.
        return string.IsNullOrWhiteSpace(connectionString)
            ? new ConfigurationOptions()
            : ConfigurationOptions.Parse(connectionString);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisGrainStorage_Dispose_DoesNotDisposeSharedMultiplexer(bool useAsyncDispose)
    {
        TestUtils.CheckForRedis();

        using var connection = await ConnectionMultiplexer.ConnectAsync(GetConfigurationOptions());
        var (provider, initialize) = CreateStorage(
            _ => Task.FromResult((Multiplexer: (IConnectionMultiplexer)connection, IsShared: true)));

        await AssertSharedMultiplexerNotDisposed(connection, initialize, GetDispose(provider, useAsyncDispose));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisGrainStorage_Dispose_DisposesExclusiveMultiplexer(bool useAsyncDispose)
    {
        TestUtils.CheckForRedis();

        ConnectionMultiplexer connection = null;
        var (provider, initialize) = CreateStorage(async _ =>
        {
            connection = await ConnectionMultiplexer.ConnectAsync(GetConfigurationOptions());
            return ((IConnectionMultiplexer)connection, false);
        });

        await AssertExclusiveMultiplexerDisposed(() => connection, initialize, GetDispose(provider, useAsyncDispose));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisMembershipTable_Dispose_DoesNotDisposeSharedMultiplexer(bool useAsyncDispose)
    {
        TestUtils.CheckForRedis();

        using var connection = await ConnectionMultiplexer.ConnectAsync(GetConfigurationOptions());
        var (provider, initialize) = CreateMembershipTable(
            _ => Task.FromResult((Multiplexer: (IConnectionMultiplexer)connection, IsShared: true)));

        await AssertSharedMultiplexerNotDisposed(connection, initialize, GetDispose(provider, useAsyncDispose));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisMembershipTable_Dispose_DisposesExclusiveMultiplexer(bool useAsyncDispose)
    {
        TestUtils.CheckForRedis();

        ConnectionMultiplexer connection = null;
        var (provider, initialize) = CreateMembershipTable(async _ =>
        {
            connection = await ConnectionMultiplexer.ConnectAsync(GetConfigurationOptions());
            return ((IConnectionMultiplexer)connection, false);
        });

        await AssertExclusiveMultiplexerDisposed(() => connection, initialize, GetDispose(provider, useAsyncDispose));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisReminderTable_Dispose_DoesNotDisposeSharedMultiplexer(bool useAsyncDispose)
    {
        TestUtils.CheckForRedis();

        using var connection = await ConnectionMultiplexer.ConnectAsync(GetConfigurationOptions());
        var (provider, initialize) = CreateReminderTable(
            _ => Task.FromResult((Multiplexer: (IConnectionMultiplexer)connection, IsShared: true)));

        await AssertSharedMultiplexerNotDisposed(connection, initialize, GetDispose(provider, useAsyncDispose));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisReminderTable_Dispose_DisposesExclusiveMultiplexer(bool useAsyncDispose)
    {
        TestUtils.CheckForRedis();

        ConnectionMultiplexer connection = null;
        var (provider, initialize) = CreateReminderTable(async _ =>
        {
            connection = await ConnectionMultiplexer.ConnectAsync(GetConfigurationOptions());
            return ((IConnectionMultiplexer)connection, false);
        });

        await AssertExclusiveMultiplexerDisposed(() => connection, initialize, GetDispose(provider, useAsyncDispose));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisGrainDirectory_Dispose_DoesNotDisposeSharedMultiplexer(bool useAsyncDispose)
    {
        TestUtils.CheckForRedis();

        using var connection = await ConnectionMultiplexer.ConnectAsync(GetConfigurationOptions());
        var (provider, initialize) = CreateDirectory(
            _ => Task.FromResult((Multiplexer: (IConnectionMultiplexer)connection, IsShared: true)));

        await AssertSharedMultiplexerNotDisposed(connection, initialize, GetDispose(provider, useAsyncDispose));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisGrainDirectory_Dispose_DisposesExclusiveMultiplexer(bool useAsyncDispose)
    {
        TestUtils.CheckForRedis();

        ConnectionMultiplexer connection = null;
        var (provider, initialize) = CreateDirectory(async _ =>
        {
            connection = await ConnectionMultiplexer.ConnectAsync(GetConfigurationOptions());
            return ((IConnectionMultiplexer)connection, false);
        });

        await AssertExclusiveMultiplexerDisposed(() => connection, initialize, GetDispose(provider, useAsyncDispose));
    }

    [Fact]
    public async Task RedisGrainDirectory_DisposeBeforeInitialize_BehavesAsDisposed()
    {
        var (provider, _) = CreateDirectory(_ => Task.FromResult((Multiplexer: (IConnectionMultiplexer)null, IsShared: false)));

        provider.Dispose();

        // Disposing before Initialize should leave the directory in a disposed state rather than throwing.
        Assert.Null(await provider.Lookup(GrainId.Create("test", "key")));
    }

    [Fact]
    public async Task RedisGrainDirectory_DisposeAsyncBeforeInitialize_BehavesAsDisposed()
    {
        var (provider, _) = CreateDirectory(_ => Task.FromResult((Multiplexer: (IConnectionMultiplexer)null, IsShared: false)));

        await provider.DisposeAsync();

        Assert.Null(await provider.Lookup(GrainId.Create("test", "key")));
    }

    private static async Task AssertSharedMultiplexerNotDisposed(
        IConnectionMultiplexer connection,
        Func<Task> initialize,
        Func<Task> dispose)
    {
        await initialize();

        Assert.True(connection.IsConnected);

        await dispose();

        // The shared multiplexer is owned by the caller, so it must remain usable after the provider is disposed.
        Assert.True(connection.IsConnected);
        await connection.GetDatabase().PingAsync();
    }

    private static async Task AssertExclusiveMultiplexerDisposed(
        Func<ConnectionMultiplexer> getConnection,
        Func<Task> initialize,
        Func<Task> dispose)
    {
        try
        {
            await initialize();

            var connection = getConnection();
            Assert.NotNull(connection);
            Assert.True(connection.IsConnected);

            await dispose();

            // The provider created and therefore owns this multiplexer, so disposal must tear it down.
            Assert.False(connection.IsConnected);
        }
        finally
        {
            getConnection()?.Dispose();
        }
    }

    private static Func<Task> GetDispose<TProvider>(TProvider provider, bool useAsyncDispose)
        where TProvider : IDisposable, IAsyncDisposable
        => useAsyncDispose
            ? () => provider.DisposeAsync().AsTask()
            : () =>
            {
                provider.Dispose();
                return Task.CompletedTask;
            };

    private (RedisGrainStorage Provider, Func<Task> Initialize) CreateStorage(
        Func<RedisStorageOptions, Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>> createMultiplexer)
    {
        var services = _fixture.Services;
        var serializer = new JsonGrainStorageSerializer(services.GetService<OrleansJsonSerializer>());
        var options = new RedisStorageOptions
        {
            ConfigurationOptions = GetConfigurationOptions(),
            GrainStorageSerializer = serializer,
            CreateMultiplexer = createMultiplexer,
        };

        var clusterOptions = Options.Create(new ClusterOptions { ServiceId = Guid.NewGuid().ToString("N") });
        var storage = new RedisGrainStorage(
            "ownership-tests",
            options,
            serializer,
            clusterOptions,
            services.GetRequiredService<IActivatorProvider>(),
            services.GetRequiredService<ILogger<RedisGrainStorage>>());

        return (storage, async () =>
        {
            ISiloLifecycleSubject lifecycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
            storage.Participate(lifecycle);
            await lifecycle.OnStart(CancellationToken.None);
        });
    }

    private static (RedisMembershipTable Provider, Func<Task> Initialize) CreateMembershipTable(
        Func<RedisClusteringOptions, Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>> createMultiplexer)
    {
        var options = Options.Create(new RedisClusteringOptions
        {
            ConfigurationOptions = GetConfigurationOptions(),
            CreateMultiplexer = createMultiplexer,
        });

        var clusterOptions = Options.Create(new ClusterOptions
        {
            ServiceId = Guid.NewGuid().ToString("N"),
            ClusterId = Guid.NewGuid().ToString("N"),
        });

        var table = new RedisMembershipTable(options, clusterOptions);
        return (table, () => table.InitializeMembershipTable(tryInitTableVersion: true));
    }

    private static (RedisReminderTable Provider, Func<Task> Initialize) CreateReminderTable(
        Func<RedisReminderTableOptions, Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>> createMultiplexer)
    {
        var options = Options.Create(new RedisReminderTableOptions
        {
            ConfigurationOptions = GetConfigurationOptions(),
            CreateMultiplexer = createMultiplexer,
        });

        var clusterOptions = Options.Create(new ClusterOptions
        {
            ServiceId = Guid.NewGuid().ToString("N"),
            ClusterId = Guid.NewGuid().ToString("N"),
        });

        var table = new RedisReminderTable(NullLogger<RedisReminderTable>.Instance, clusterOptions, options);
        return (table, () => table.Init());
    }

    private static (RedisGrainDirectory Provider, Func<Task> Initialize) CreateDirectory(
        Func<RedisGrainDirectoryOptions, Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>> createMultiplexer)
    {
        var options = new RedisGrainDirectoryOptions
        {
            ConfigurationOptions = GetConfigurationOptions(),
            CreateMultiplexer = createMultiplexer,
        };

        var clusterOptions = Options.Create(new ClusterOptions
        {
            ServiceId = Guid.NewGuid().ToString("N"),
            ClusterId = Guid.NewGuid().ToString("N"),
        });

        var directory = new RedisGrainDirectory(options, clusterOptions, NullLogger<RedisGrainDirectory>.Instance);
        return (directory, () => directory.Initialize());
    }
}
