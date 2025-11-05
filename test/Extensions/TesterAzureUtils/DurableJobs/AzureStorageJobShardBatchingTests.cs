using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.DurableJobs;
using Orleans.DurableJobs.AzureStorage;
using Tester.AzureUtils;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

/// <summary>
/// Azure Storage-specific tests for job shard batching functionality.
/// These tests verify Azure-specific batching behaviors that don't apply to all providers.
/// </summary>
[TestCategory("DurableJobs")]
public class AzureStorageJobShardBatchingTests : AzureStorageBasicTests, IAsyncDisposable
{
    private readonly IDictionary<string, string> _metadata = new Dictionary<string, string>
    {
        { "CreatedBy", "UnitTest" },
        { "Purpose", "Testing" }
    };

    internal InMemoryClusterMembershipService MembershipService { get; }

    internal IOptions<AzureStorageJobShardOptions> StorageOptions { get; }

    public AzureStorageJobShardBatchingTests()
    {
        MembershipService = new InMemoryClusterMembershipService();
        StorageOptions = Options.Create(new AzureStorageJobShardOptions());
        StorageOptions.Value.ConfigureTestDefaults();
        StorageOptions.Value.ContainerName = "test-batch-container-" + Guid.NewGuid().ToString("N");
    }

    public async ValueTask DisposeAsync()
    {
        // Cleanup storage container
        var client = StorageOptions.Value.BlobServiceClient;
        var container = client.GetBlobContainerClient(StorageOptions.Value.ContainerName);
        await container.DeleteIfExistsAsync();
    }

    public class TestLocalSiloDetails : ILocalSiloDetails
    {
        public TestLocalSiloDetails(SiloAddress siloAddress)
        {
            SiloAddress = siloAddress;
        }

        public string Name => SiloAddress.ToString();

        public string ClusterId => "TestCluster";

        public string DnsHostName => SiloAddress.ToString();

        public SiloAddress SiloAddress { get; }

        public SiloAddress GatewayAddress => SiloAddress;
    }

    internal AzureStorageJobShardManager CreateManager(SiloAddress siloAddress)
    {
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        return new AzureStorageJobShardManager(localSiloDetails, StorageOptions, MembershipService, NullLoggerFactory.Instance);
    }

    internal void SetSiloStatus(SiloAddress siloAddress, SiloStatus status)
    {
        MembershipService.SetSiloStatus(siloAddress, status);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShard_MultipleOperationsBatched()
    {
        // Configure batching options to batch multiple operations
        StorageOptions.Value.MinBatchSize = 5;
        StorageOptions.Value.MaxBatchSize = 50;
        StorageOptions.Value.BatchFlushInterval = TimeSpan.FromMilliseconds(100);

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        SetSiloStatus(localAddress, SiloStatus.Active);
        var manager = CreateManager(localAddress);

        var date = DateTime.UtcNow;
        var shard = await manager.CreateShardAsync(date, date.AddHours(1), _metadata, CancellationToken.None);

        // Schedule 10 jobs rapidly to trigger batching
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(shard.TryScheduleJobAsync(GrainId.Create("type", $"target{i}"), $"job{i}", date.AddMilliseconds(i*10), null, CancellationToken.None));
        }

        await Task.WhenAll(tasks);

        // Wait for batches to flush
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // Verify batching occurred - should have fewer committed blocks than individual operations
        var azureShard = (AzureStorageJobShard)shard;
        Assert.True(azureShard.CommitedBlockCount < 10, $"Expected batching to reduce block count, but got {azureShard.CommitedBlockCount}");

        // Verify all jobs were persisted by marking silo as dead and reassigning
        SetSiloStatus(localAddress, SiloStatus.Dead);
        var newSiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        SetSiloStatus(newSiloAddress, SiloStatus.Active);

        var newManager = CreateManager(newSiloAddress);
        var shards = await newManager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Single(shards);

        var consumedJobs = new List<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var jobCtx in shards[0].ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            consumedJobs.Add(jobCtx.Job.Name);
            await shards[0].RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
        }

        Assert.Equal(10, consumedJobs.Count);
        await newManager.UnregisterShardAsync(shards[0], CancellationToken.None);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShard_PartialBatchFlushesOnTimeout()
    {
        // Configure batching to require 10 operations but with a short timeout
        StorageOptions.Value.MinBatchSize = 10;
        StorageOptions.Value.MaxBatchSize = 100;
        StorageOptions.Value.BatchFlushInterval = TimeSpan.FromMilliseconds(200);

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        SetSiloStatus(localAddress, SiloStatus.Active);
        var manager = CreateManager(localAddress);

        var date = DateTime.UtcNow;
        var shard = await manager.CreateShardAsync(date, date.AddHours(1), _metadata, CancellationToken.None);

        // Schedule only 3 jobs (less than MinBatchSize of 10)
        var tasks = new Task[3];
        tasks[0] = shard.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", date.AddSeconds(1), null, CancellationToken.None);
        tasks[1] = shard.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", date.AddSeconds(2), null, CancellationToken.None);
        tasks[2] = shard.TryScheduleJobAsync(GrainId.Create("type", "target3"), "job3", date.AddSeconds(3), null, CancellationToken.None);

        await Task.WhenAll(tasks);

        // Verify that the partial batch was flushed - should have 1 committed block
        var azureShard = (AzureStorageJobShard)shard;
        Assert.Equal(1, azureShard.CommitedBlockCount);

        // Verify jobs were persisted despite not reaching MinBatchSize
        SetSiloStatus(localAddress, SiloStatus.Dead);
        var newSiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        SetSiloStatus(newSiloAddress, SiloStatus.Active);

        var newManager = CreateManager(newSiloAddress);
        var shards = await newManager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Single(shards);

        var consumedJobs = new List<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var jobCtx in shards[0].ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            consumedJobs.Add(jobCtx.Job.Name);
            await shards[0].RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
        }

        Assert.Equal(3, consumedJobs.Count);
        await newManager.UnregisterShardAsync(shards[0], CancellationToken.None);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShard_MaxBatchSizeEnforced()
    {
        // Configure batching with a small max batch size
        StorageOptions.Value.MinBatchSize = 1;
        StorageOptions.Value.MaxBatchSize = 20;
        StorageOptions.Value.BatchFlushInterval = TimeSpan.FromMilliseconds(50);

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        SetSiloStatus(localAddress, SiloStatus.Active);
        var manager = CreateManager(localAddress);

        var date = DateTime.UtcNow;
        var shard = await manager.CreateShardAsync(date, date.AddHours(1), _metadata, CancellationToken.None);

        // Schedule 50 jobs rapidly (exceeds MaxBatchSize of 20)
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(shard.TryScheduleJobAsync(GrainId.Create("type", $"target{i}"), $"job{i}", date.AddMilliseconds(i), null, CancellationToken.None));
        }

        await Task.WhenAll(tasks);

        // Wait for all batches to flush
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Verify multiple batches were created due to MaxBatchSize limit
        // With 50 jobs and MaxBatchSize=20, expect at least 3 blocks (50/20 = 2.5, rounded up)
        var azureShard = (AzureStorageJobShard)shard;
        Assert.True(azureShard.CommitedBlockCount >= 3, $"Expected at least 3 blocks for 50 jobs with MaxBatchSize=20, but got {azureShard.CommitedBlockCount}");

        // Verify all jobs were persisted (should be split into multiple batches)
        SetSiloStatus(localAddress, SiloStatus.Dead);
        var newSiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        SetSiloStatus(newSiloAddress, SiloStatus.Active);

        var newManager = CreateManager(newSiloAddress);
        var shards = await newManager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Single(shards);

        var consumedJobs = new List<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var jobCtx in shards[0].ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            consumedJobs.Add(jobCtx.Job.Name);
            await shards[0].RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
        }

        Assert.Equal(50, consumedJobs.Count);
        await newManager.UnregisterShardAsync(shards[0], CancellationToken.None);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShard_MetadataOperationsBreakBatches()
    {
        // Configure batching to require large batch
        StorageOptions.Value.MinBatchSize = 10;
        StorageOptions.Value.MaxBatchSize = 100;
        StorageOptions.Value.BatchFlushInterval = TimeSpan.FromSeconds(5);

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        SetSiloStatus(localAddress, SiloStatus.Active);
        var manager = CreateManager(localAddress);

        var date = DateTime.UtcNow;
        var shard = await manager.CreateShardAsync(date, date.AddHours(1), _metadata, CancellationToken.None);

        // Schedule 5 jobs (less than MinBatchSize)
        var tasks = new List<Task>();
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(shard.TryScheduleJobAsync(GrainId.Create("type", $"target{i}"), $"job{i}", date.AddMilliseconds(i), null, CancellationToken.None));
        }

        // Give operations time to queue
        await Task.Delay(50);

        // Verify no blocks committed yet (batch still pending)
        var azureShard = (AzureStorageJobShard)shard;
        var blockCountBefore = azureShard.CommitedBlockCount;

        // Update metadata (should flush pending batch and process immediately)
        var newMetadata = new Dictionary<string, string>(shard.Metadata) { ["Updated"] = "true" };
        await azureShard.UpdateBlobMetadata(newMetadata, CancellationToken.None);

        Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully, "Expected all job scheduling tasks to complete successfully"));
        Assert.True(azureShard.CommitedBlockCount > blockCountBefore, "Expected metadata update to flush pending batch");

        // Verify metadata was updated
        var props = await azureShard.BlobClient.GetPropertiesAsync();
        Assert.True(props.Value.Metadata.ContainsKey("Updated"));
        Assert.Equal("true", props.Value.Metadata["Updated"]);

        // Verify jobs were persisted (even though batch was incomplete)
        SetSiloStatus(localAddress, SiloStatus.Dead);
        var newSiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        SetSiloStatus(newSiloAddress, SiloStatus.Active);

        // Reconfigure batching to make test faster
        StorageOptions.Value.MinBatchSize = 1;
        StorageOptions.Value.MaxBatchSize = 1;
        StorageOptions.Value.BatchFlushInterval = TimeSpan.FromMilliseconds(100);

        var newManager = CreateManager(newSiloAddress);
        var shards = await newManager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Single(shards);

        var consumedJobs = new List<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var jobCtx in shards[0].ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            consumedJobs.Add(jobCtx.Job.Name);
            await shards[0].RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
        }

        Assert.Equal(5, consumedJobs.Count);
        await newManager.UnregisterShardAsync(shards[0], CancellationToken.None);
    }

    public class InMemoryClusterMembershipService : IClusterMembershipService
    {
        private readonly Dictionary<SiloAddress, ClusterMember> _silos = new();
        private int _version = 0;

        public ClusterMembershipSnapshot CurrentSnapshot =>
            new ClusterMembershipSnapshot(_silos.ToImmutableDictionary(), new MembershipVersion(_version));

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => throw new NotImplementedException();

        public void SetSiloStatus(SiloAddress address, SiloStatus status)
        {
            _silos[address] = new ClusterMember(address, status, address.ToParsableString());
            _version++;
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public Task<bool> TryKill(SiloAddress siloAddress) => throw new NotImplementedException();
    }
}
