using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
public sealed class AzureStorageColdShardCreationTests : BaseInProcessTestClusterFixture
{
    private readonly string _containerName = "durablejobs-cold-shard-" + Guid.NewGuid().ToString("N");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 6, 13, 59, 59, TimeSpan.Zero));
    private readonly CreatedWalBarrier _barrier;

    public AzureStorageColdShardCreationTests()
    {
        _barrier = new CreatedWalBarrier("/" + _containerName + "/wal/" + JobShardId.StoragePrefix.Value + "/");
    }

    protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForAzureStorage();

    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 2;
        builder.ConfigureSilo((_, silo) =>
        {
            var clientOptions = new BlobClientOptions();
            clientOptions.AddPolicy(_barrier, HttpPipelinePosition.PerCall);
            silo.UseAzureBlobDurableJobs(options =>
            {
                options.ClientOptions = clientOptions;
                options.ConfigureTestDefaults();
                options.ContainerName = _containerName;
            });
            silo.AddMemoryGrainStorageAsDefault();
            // Keep membership and other runtime timers on real time; only jobs use this clock.
            silo.Services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, _clock);
        });
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task DiscoveryBeforeFirstEnqueue_ExecutesJobAndCancelsRemotelyInBothDirections()
    {
        EnsurePreconditionsMet();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        var token = cancellation.Token;
        var grain = GrainFactory.GetGrain<IDurableJobGrain>(Guid.NewGuid().ToString("N"));
        Assert.False(await grain.HasJobRan("before-scheduling").WaitAsync(token));
        Assert.True(HostedCluster.TryGetGrainContext(grain.GetGrainId(), out var context));
        var ownerAddress = context!.Address.SiloAddress!;
        var otherAddress = HostedCluster.Silos.Single(silo => !silo.SiloAddress.Equals(ownerAddress)).SiloAddress;
        var owner = HostedCluster.GetSiloServiceProvider(ownerAddress).GetRequiredService<ILocalDurableJobManager>();
        var other = HostedCluster.GetSiloServiceProvider(otherAddress).GetRequiredService<ILocalDurableJobManager>();

        _barrier.Arm();
        var scheduling = grain.ScheduleJobAsync("first-new-hour-job", _clock.GetUtcNow().AddSeconds(2));
        string createdShard;
        try
        {
            // The actual Azure AppendBlob already exists, but its creator has not opened it.
            createdShard = await _barrier.Created.WaitAsync(token);
            await ((LocalDurableJobManager)owner).ProcessShardCheckCycleAsync(token).WaitAsync(token);
        }
        finally
        {
            _barrier.Release();
        }

        var first = await scheduling.WaitAsync(token);
        Assert.Equal(createdShard, first.ShardId);
        var firstCompletion = grain.WaitForJobToRun(first.Id);
        _clock.Advance(TimeSpan.FromSeconds(2));
        await firstCompletion.WaitAsync(token);
        Assert.True(await grain.HasJobRan(first.Id).WaitAsync(token));
        Assert.Equal(first.Id, (await grain.GetJobRun(first.Id).WaitAsync(token)).Job.Id);

        var due = _clock.GetUtcNow().AddMinutes(1);
        var cancelledOnOwner = await grain.ScheduleJobAsync("cancel-on-original-owner", due).WaitAsync(token);
        Assert.Equal(first.ShardId, cancelledOnOwner.ShardId);
        Assert.True(await other.CancelAsync(cancelledOnOwner, token).WaitAsync(token));
        Assert.False(await owner.CancelAsync(cancelledOnOwner, token).WaitAsync(token));

        var cancelledOnOther = await other.ScheduleJobAsync(new ScheduleJobRequest
        {
            Target = grain.GetGrainId(),
            JobName = "cancel-on-other-owner",
            DueTime = due
        }, token).WaitAsync(token);
        Assert.True(await owner.CancelAsync(cancelledOnOther, token).WaitAsync(token));
        Assert.False(await other.CancelAsync(cancelledOnOther, token).WaitAsync(token));

        // A real terminal marker passes the cancelled jobs' due time without a wall-clock sleep.
        var marker = await grain.ScheduleJobAsync("after-remote-cancellation", due).WaitAsync(token);
        var markerCompletion = grain.WaitForJobToRun(marker.Id);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await markerCompletion.WaitAsync(token);
        Assert.Equal(marker.Id, (await grain.GetJobRun(marker.Id).WaitAsync(token)).Job.Id);
        Assert.False(await grain.HasJobRan(cancelledOnOwner.Id).WaitAsync(token));
        Assert.False(await grain.HasJobRan(cancelledOnOther.Id).WaitAsync(token));
    }

    public override async ValueTask DisposeAsync()
    {
        _barrier.Release();
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (PreconditionsMet)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                var container = TestDefaultConfiguration.UseAadAuthentication
                    ? new BlobContainerClient(new Uri(TestDefaultConfiguration.DataBlobUri, _containerName), TestDefaultConfiguration.TokenCredential)
                    : new BlobContainerClient(TestDefaultConfiguration.DataConnectionString, _containerName);
                await container.DeleteIfExistsAsync(cancellationToken: cancellation.Token);
            }
        }
    }

    // This policy only delays a successful response from the real Azure SDK transport.
    // It neither fabricates responses nor replaces any journal, catalog or job owner.
    private sealed class CreatedWalBarrier(string walPathPrefix) : HttpPipelinePolicy
    {
        private const string BlobTypeHeader = "x-ms-blob-type";
        private const string AppendBlobType = "AppendBlob";
        private readonly TaskCompletionSource<string> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task<string> Created => _created.Task;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) => ProcessNext(message, pipeline);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
            if (message.Request.Method != RequestMethod.Put || message.Response.Status != 201 ||
                !message.Request.Headers.TryGetValue(BlobTypeHeader, out var blobType) || blobType != AppendBlobType)
            {
                return;
            }
            var path = message.Request.Uri.ToUri().AbsolutePath;
            var prefixIndex = path.IndexOf(walPathPrefix, StringComparison.Ordinal);
            if (prefixIndex < 0 || Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
            {
                return;
            }
            _created.TrySetResult(Uri.UnescapeDataString(path[(prefixIndex + walPathPrefix.Length)..]));
            await _release.Task.WaitAsync(message.CancellationToken).ConfigureAwait(false);
        }
    }
}
