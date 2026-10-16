using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Journaling.Ranges;
using Orleans.TestingHost;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Compares append latency and throughput through <see cref="IJournalStorage"/> across journal storage providers:
/// Garnet on local disk, Garnet on page blobs, the Azure Blob provider, and the Redis provider, with the same workload.
/// </summary>
/// <remarks>
/// <para>
/// Each case appends 128-byte entries to one journal per writer, after one warm-up append per journal, and reports
/// latency percentiles and throughput. The Garnet page blob case also reports the bytes and page uploads its devices
/// completed per append, which is the partition's AOF traffic.
/// </para>
/// <para>
/// Services: Azure cases use the blob test settings, as <c>GarnetBlobTestStorage</c> does (for example Azurite, with
/// <c>DataConnectionString=UseDevelopmentStorage=true</c>), and Redis cases, which require <c>appendonly yes</c>, use <c>RedisConnectionString</c>, with an optional second
/// instance in <c>GARNET_BENCHMARK_REDIS_EVERYSEC</c> configured with <c>appendfsync everysec</c>. Results are appended to
/// <c>garnet-provider-comparison.txt</c> in the temporary directory.
/// </para>
/// </remarks>
[TestCategory("Performance")]
public sealed class GarnetJournalProviderComparison
{
    private const int PayloadSize = 128;
    private static readonly (int Writers, int Appends)[] Workloads = [(1, 1000), (16, 250), (64, 100)];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GarnetLocalDisk()
    {
        var directory = GarnetPartitionFixture.CreateTemporaryDirectory();
        var provider = GarnetPartitionFixture.CreateProvider(directory, new InMemoryRangeMetaStore(), options => options.LogMemorySize = "1g");
        try
        {
            await provider.StartAsync(CancellationToken);
            foreach (var (writers, appends) in Workloads)
            {
                Report("garnet-local-disk", writers, await RunAsync(provider.CreateStorage, writers, appends), string.Empty);
            }
        }
        finally
        {
            await provider.DisposeAsync();
            GarnetPartitionFixture.TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task GarnetPageBlobs()
    {
        GarnetBlobTestStorage.SkipIfUnavailable();
        var container = GarnetBlobTestStorage.CreateContainer();
        await container.CreateIfNotExistsAsync(cancellationToken: CancellationToken);
        var (storage, host) = await GarnetBlobTestStorage.StartAsync(container, "p0", 1, CancellationToken);
        try
        {
            var map = GarnetBlobTestStorage.CreateMap(4096);
            await using var router = await GarnetBlobTestStorage.CreateRouterAsync(new InMemoryRangeMetaStore(), map, host.EndPoint, TimeSpan.FromSeconds(30), CancellationToken);
            foreach (var (writers, appends) in Workloads)
            {
                var (bytes, writes) = (storage.BytesWritten, storage.PageWrites);
                var result = await RunAsync(id => new GarnetJournalStorage(router, id, "json", compactionThresholdBytes: 0), writers, appends);
                Report(
                    "garnet-page-blobs",
                    writers,
                    result,
                    $"bytes/append={(storage.BytesWritten - bytes) / (double)result.Appends:F0} page-writes/append={(storage.PageWrites - writes) / (double)result.Appends:F3}");
            }
        }
        finally
        {
            host.Dispose();
            await container.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
        }
    }

    [Fact]
    public async Task AzureBlobProvider()
    {
        GarnetBlobTestStorage.SkipIfUnavailable();
        var containerName = $"journal-comparison-{Guid.NewGuid():N}";
        await using var cluster = await StartSiloAsync(silo => silo.AddAzureBlobJournalStorage(options =>
        {
            // Authenticate as GarnetBlobTestStorage does: with a token credential when configured, else a connection string.
            if (TestExtensions.TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureBlobServiceClient(TestExtensions.TestDefaultConfiguration.DataBlobUri!, TestExtensions.TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureBlobServiceClient(TestExtensions.TestDefaultConfiguration.DataConnectionString!);
            }

            options.ContainerName = containerName;
        }));
        var provider = cluster.Silos[0].ServiceProvider.GetRequiredService<IJournalStorageProvider>();
        foreach (var (writers, appends) in Workloads)
        {
            Report("azure-blob-provider", writers, await RunAsync(provider.CreateStorage, writers, appends), string.Empty);
        }
    }

    [Theory]
    [InlineData("always")]
    [InlineData("everysec")]
    public async Task RedisProvider(string appendFsync)
    {
        var connectionString = appendFsync == "always"
            ? TestExtensions.TestDefaultConfiguration.RedisConnectionString
            : Environment.GetEnvironmentVariable("GARNET_BENCHMARK_REDIS_EVERYSEC");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), "No Redis instance is configured for this case.");
        await using (var admin = await ConnectionMultiplexer.ConnectAsync(connectionString + ",allowAdmin=true"))
        {
            var server = admin.GetServer(admin.GetEndPoints()[0]);
            // appendfsync only takes effect with the append-only file enabled.
            Assert.Equal("yes", (await server.ConfigGetAsync("appendonly")).Single().Value);
            Assert.Equal(appendFsync, (await server.ConfigGetAsync("appendfsync")).Single().Value);

            // The round trip to the server, which a container adds to (here, Docker's port forwarding).
            var database = admin.GetDatabase();
            var pings = new double[1000];
            for (var i = 0; i < pings.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await database.PingAsync();
                pings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            Array.Sort(pings);
            Report($"redis-appendfsync-{appendFsync} PING", 1, new RunResult(pings, TimeSpan.FromMilliseconds(pings.Sum())), string.Empty);
        }

        await using var cluster = await StartSiloAsync(silo => silo.AddRedisJournalStorage(options =>
        {
            options.ConfigurationOptions = ConfigurationOptions.Parse(connectionString!);
            options.KeyPrefix = $"comparison-{Guid.NewGuid():N}";
        }));
        var provider = cluster.Silos[0].ServiceProvider.GetRequiredService<IJournalStorageProvider>();
        foreach (var (writers, appends) in Workloads)
        {
            Report($"redis-provider-appendfsync-{appendFsync}", writers, await RunAsync(provider.CreateStorage, writers, appends), string.Empty);
        }
    }

    [Fact]
    public async Task GarnetRoundTripCosts()
    {
        // The share of an operation's latency which in-process sessions would remove: a loopback RESP round trip (PING),
        // against an unlogged procedure and a journal read, which waits for an AOF commit (D32).
        var directory = GarnetPartitionFixture.CreateTemporaryDirectory();
        var provider = GarnetPartitionFixture.CreateProvider(directory, new InMemoryRangeMetaStore());
        try
        {
            await provider.StartAsync(CancellationToken);
            var router = provider.Router;
            var storage = provider.CreateStorage(JournalId.Create("roundtrip", "1"));
            await storage.AppendAsync(new ReadOnlySequence<byte>(new byte[PayloadSize]), CancellationToken);
            await using var connection = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions { EndPoints = { provider.HostedPartitions.Values.Single().EndPoint }, Protocol = RedisProtocol.Resp2 });
            var database = connection.GetDatabase();
            var bucketArgs = GarnetJournalKeySet.GetBucketArgs(router.MapId, 0);
            var ping = await TimeAsync(() => database.PingAsync());
            var info = await TimeAsync(() => database.ExecuteAsync(GarnetJournalProtocol.BucketInfo, bucketArgs));
            var read = await TimeAsync(async () => await storage.ReadAsync(new CapturingConsumer(), CancellationToken));
            var message = $"garnet round trips (2000 each, sequential): PING p50={ping[1000]:F3}ms; unlogged procedure p50={info[1000]:F3}ms; journal read (logged) p50={read[1000]:F3}ms";
            TestContext.Current.TestOutputHelper?.WriteLine(message);
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "garnet-provider-comparison.txt"), message + Environment.NewLine);
        }
        finally
        {
            await provider.DisposeAsync();
            GarnetPartitionFixture.TryDeleteDirectory(directory);
        }

        static async Task<double[]> TimeAsync(Func<Task> operation)
        {
            var latencies = new double[2000];
            for (var i = 0; i < latencies.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await operation();
                latencies[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            Array.Sort(latencies);
            return latencies;
        }
    }

    private static async Task<InProcessTestCluster> StartSiloAsync(Action<ISiloBuilder> configure)
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.RemoveAll<IJournalStorageProvider>();
            configure(silo);
        });
        var cluster = builder.Build();
        await cluster.DeployAsync(CancellationToken);
        return cluster;
    }

    private static async Task<RunResult> RunAsync(Func<JournalId, IJournalStorage> create, int writers, int appendsPerWriter)
    {
        var payload = new ReadOnlySequence<byte>(new byte[PayloadSize]);
        var storages = Enumerable.Range(0, writers).Select(_ => create(JournalId.Create("comparison", Guid.NewGuid().ToString("N")))).ToArray();
        await Task.WhenAll(storages.Select(storage => storage.AppendAsync(payload, CancellationToken).AsTask()));

        var latencies = new double[writers * appendsPerWriter];
        var total = Stopwatch.StartNew();
        await Task.WhenAll(storages.Select((storage, writer) => Task.Run(async () =>
        {
            for (var i = 0; i < appendsPerWriter; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await storage.AppendAsync(payload, CancellationToken);
                latencies[writer * appendsPerWriter + i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        }, CancellationToken)));
        total.Stop();
        Array.Sort(latencies);
        return new RunResult(latencies, total.Elapsed);
    }

    private static void Report(string provider, int writers, RunResult result, string extra)
    {
        var message =
            $"{provider} writers={writers} appends={result.Appends} payload={PayloadSize}B: "
            + $"p50={result.Percentile(0.50):F2}ms p99={result.Percentile(0.99):F2}ms p99.9={result.Percentile(0.999):F2}ms "
            + $"throughput={result.Appends / result.Elapsed.TotalSeconds:F0}/s {extra}".TrimEnd();
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        TestContext.Current.SendDiagnosticMessage(message);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "garnet-provider-comparison.txt"), message + Environment.NewLine);
    }

    private sealed record RunResult(double[] Latencies, TimeSpan Elapsed)
    {
        public int Appends => Latencies.Length;

        public double Percentile(double percentile) => Latencies[Math.Min(Latencies.Length - 1, (int)Math.Ceiling(percentile * Latencies.Length) - 1)];
    }
}
