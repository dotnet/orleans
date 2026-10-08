#nullable enable

using System.Buffers;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using Orleans.DurableJobs.Tests;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost.Diagnostics;
using Tester;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
[TestCategory("Azure"), TestCategory("DurableJobs")]
public sealed class AzureBlobJournaledJobShardManagerTests(AzureBlobJournaledJobShardManagerTestFixture fixture)
    : JobShardManagerTestsRunner(fixture), IClassFixture<AzureBlobJournaledJobShardManagerTestFixture>
{
    [Theory]
    [InlineData("schedule")]
    [InlineData("remove")]
    [InlineData("retry")]
    [InlineData("reschedule")]
    public async Task TakeoverFencesFormerOwnerMutationsAndPreservesLiveOwnerProgress(string mutation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = cts.Token;
        await using var scope = await fixture.CreateScopeAsync(token);
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var nextOwner = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        await using var original = await formerOwner.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), new Dictionary<string, string>(), token);
        var job = await original.TryScheduleJobAsync(CreateRequest(now, "before-takeover"), token);
        Assert.NotNull(job);
        await using var originalRuns = original.ConsumeDurableJobsAsync().GetAsyncEnumerator(token);
        Assert.True(await originalRuns.MoveNextAsync());
        var run = originalRuns.Current;
        scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);

        await using var adopted = Assert.Single(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 1, token));
        Assert.Equal(1, await adopted.GetJobCountAsync());
        await Assert.ThrowsAsync<InconsistentStateException>(MutateAsync);
        Assert.Same(adopted, Assert.Single(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 0, token)));
        await using var adoptedRuns = adopted.ConsumeDurableJobsAsync().GetAsyncEnumerator(token);
        Assert.True(await adoptedRuns.MoveNextAsync());
        Assert.Equal(job.Id, adoptedRuns.Current.Job.Id);
        Assert.Equal("before-takeover", adoptedRuns.Current.Job.Name);
        Assert.Equal(now, adoptedRuns.Current.Job.DueTime);
        Assert.Equal(1, adoptedRuns.Current.DequeueCount);
        Assert.Equal(DurableJobMutationResult.Applied, await adopted.RemoveJobAsync(job.Id, token));
        Assert.Equal(0, await adopted.GetJobCountAsync());
        await nextOwner.UnregisterShardAsync(adopted, token);
        Assert.Empty(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 0, token));

        async Task MutateAsync()
        {
            switch (mutation)
            {
                case "schedule":
                    await original.TryScheduleJobAsync(CreateRequest(now, "after-takeover"), token);
                    break;
                case "remove":
                    await original.RemoveJobAsync(job.Id, token);
                    break;
                case "retry":
                    await original.RetryJobLaterAsync(run, now.AddMinutes(1), token);
                    break;
                case "reschedule":
                    await original.RescheduleJobAsync(run, now.AddMinutes(1), token);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected mutation '{mutation}'.");
            }
        }
    }

    [Fact]
    public async Task TakeoverFencesAppendPausedAfterOwnershipCheck()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = cts.Token;
        GatedStorageProvider? storage = null;
        await using var scope = await fixture.CreateScopeAsync(token, inner => storage = new GatedStorageProvider(inner));
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var nextOwner = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        await using var original = await formerOwner.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), new Dictionary<string, string>(), token);
        var job = await original.TryScheduleJobAsync(CreateRequest(now, "before-takeover"), token);
        Assert.NotNull(job);
        var gate = storage!.GateNextAppend();
        var pending = original.TryScheduleJobAsync(CreateRequest(now, "in-flight"), token);
        try
        {
            await gate.Entered.Task.WaitAsync(token);
            Assert.False(pending.IsCompleted);
            scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);
            await using var adopted = Assert.Single(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 1, token));
            Assert.Equal(1, await adopted.GetJobCountAsync());

            gate.Resume.SetResult();
            await Assert.ThrowsAsync<InconsistentStateException>(() => pending.WaitAsync(token));
            Assert.Equal(DurableJobMutationResult.Applied, await adopted.RemoveJobAsync(job.Id, token));
            Assert.Equal(0, await adopted.GetJobCountAsync());
            await nextOwner.UnregisterShardAsync(adopted, token);
            Assert.Empty(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 0, token));
        }
        finally
        {
            gate.Resume.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TakeoverFencesFormerStorageCheckpointAndDeletion(bool delete)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = cts.Token;
        GatedStorageProvider? storage = null;
        await using var scope = await fixture.CreateScopeAsync(token, inner => storage = new GatedStorageProvider(inner));
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var nextOwner = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        await using var original = await formerOwner.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), new Dictionary<string, string>(), token);
        var job = await original.TryScheduleJobAsync(CreateRequest(now, "before-takeover"), token);
        Assert.NotNull(job);
        var staleStorage = storage!.FirstReadStorage;
        Assert.NotNull(staleStorage);
        scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);
        await using var adopted = Assert.Single(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 1, token));

        await Assert.ThrowsAsync<InconsistentStateException>(() => delete
            ? staleStorage.DeleteAsync(token).AsTask()
            : staleStorage.ReplaceAsync(ReadOnlySequence<byte>.Empty, token).AsTask());
        Assert.Equal(1, await adopted.GetJobCountAsync());
        Assert.Equal(DurableJobMutationResult.Applied, await adopted.RemoveJobAsync(job.Id, token));
        await nextOwner.UnregisterShardAsync(adopted, token);
        Assert.Empty(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 0, token));
    }

    [Fact]
    public async Task TakeoverFencesEmptyShardDeletionPausedAfterOwnershipCheck()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = cts.Token;
        GatedStorageProvider? storage = null;
        await using var scope = await fixture.CreateScopeAsync(token, inner => storage = new GatedStorageProvider(inner));
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var nextOwner = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        await using var original = await formerOwner.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), new Dictionary<string, string>(), token);
        var gate = storage!.GateNextDelete();
        var deletion = formerOwner.UnregisterShardAsync(original, token);
        try
        {
            await gate.Entered.Task.WaitAsync(token);
            Assert.False(deletion.IsCompleted);
            scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);
            await using var adopted = Assert.Single(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 1, token));
            gate.Resume.SetResult();
            await Assert.ThrowsAsync<InconsistentStateException>(() => deletion.WaitAsync(token));
            Assert.Equal(0, await adopted.GetJobCountAsync());
            Assert.Same(adopted, Assert.Single(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 0, token)));
            await nextOwner.UnregisterShardAsync(adopted, token);
            Assert.Empty(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 0, token));
        }
        finally
        {
            gate.Resume.TrySetResult();
        }
    }

    [Fact]
    public async Task OrdinaryMetadataAnnotationRetainsAppendRetry()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = cts.Token;
        GatedStorageProvider? storage = null;
        await using var scope = await fixture.CreateScopeAsync(token, inner => storage = new GatedStorageProvider(inner));
        var manager = scope.CreateManager(scope.ActiveSilo);
        var now = scope.Now;
        await using var shard = await manager.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), new Dictionary<string, string>(), token);
        var job = await shard.TryScheduleJobAsync(CreateRequest(now, "before-annotation"), token);
        Assert.NotNull(job);
        var metadataStorage = storage!.CreateStorage(storage.CreatedJournalId);
        Assert.NotNull(await metadataStorage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["annotation"] = "preserved" }, cancellationToken: token));

        Assert.Equal(DurableJobMutationResult.Applied, await shard.RemoveJobAsync(job.Id, token));
        var metadata = await metadataStorage.GetMetadataAsync(token);
        Assert.NotNull(metadata);
        Assert.Equal("preserved", metadata.Properties["annotation"]);
        Assert.Equal(0, await shard.GetJobCountAsync());
        await manager.UnregisterShardAsync(shard, token);
    }

    [Fact]
    public async Task SameOwnerClosureRetainsMetadataOnlyAppendRetry()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = cts.Token;
        await using var scope = await fixture.CreateScopeAsync(token);
        var manager = scope.CreateManager(scope.ActiveSilo);
        var now = scope.Now;
        await using var shard = await manager.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), new Dictionary<string, string>(), token);
        var job = await shard.TryScheduleJobAsync(CreateRequest(now, "before-closure"), token);
        Assert.NotNull(job);

        await shard.MarkAsCompleteAsync(token);
        Assert.True(shard.IsAddingCompleted);
        Assert.Equal(DurableJobMutationResult.Applied, await shard.RemoveJobAsync(job.Id, token));
        Assert.Equal(0, await shard.GetJobCountAsync());
        await manager.UnregisterShardAsync(shard, token);
        Assert.Empty(await manager.AssignJobShardsAsync(now.AddMinutes(5), 0, token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupersededClaimantFenceIsRejectedBeforePublication(bool supersedingFenceCommitsFirst)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = cts.Token;
        GatedStorageProvider? storage = null;
        await using var scope = await fixture.CreateScopeAsync(token, inner => storage = new GatedStorageProvider(inner));
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var firstClaimant = scope.CreateManager(scope.SecondActiveSilo);
        var lastClaimant = scope.CreateManager(scope.ThirdActiveSilo);
        var now = scope.Now;
        await using var original = await formerOwner.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), new Dictionary<string, string>(), token);
        var job = await original.TryScheduleJobAsync(CreateRequest(now, "before-takeover"), token);
        Assert.NotNull(job);
        scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);
        var firstGate = storage!.GateNextAppend();
        var firstClaim = firstClaimant.AssignJobShardsAsync(now.AddMinutes(5), 1, token);
        AppendGate? lastGate = null;
        IJobShard? adopted = null;
        try
        {
            await firstGate.Entered.Task.WaitAsync(token);
            Assert.False(firstClaim.IsCompleted);
            scope.SetSiloStatus(scope.SecondActiveSilo, SiloStatus.Dead);
            lastGate = storage.GateNextAppend();
            var lastClaim = lastClaimant.AssignJobShardsAsync(now.AddMinutes(5), 1, token);
            await lastGate.Entered.Task.WaitAsync(token);
            Assert.False(lastClaim.IsCompleted);

            if (supersedingFenceCommitsFirst)
            {
                lastGate.Resume.SetResult();
                adopted = Assert.Single(await lastClaim.WaitAsync(token));
                firstGate.Resume.SetResult();
                await Assert.ThrowsAsync<InconsistentStateException>(() => firstClaim.WaitAsync(token));
            }
            else
            {
                // The owner metadata has changed, but both storage commits are still gated.
                firstGate.Resume.SetResult();
                await Assert.ThrowsAsync<InconsistentStateException>(() => firstClaim.WaitAsync(token));
                lastGate.Resume.SetResult();
                await Assert.ThrowsAsync<InconsistentStateException>(() => lastClaim.WaitAsync(token));
                adopted = Assert.Single(await lastClaimant.AssignJobShardsAsync(now.AddMinutes(5), 0, token));
            }

            Assert.Empty(await firstClaimant.AssignJobShardsAsync(now.AddMinutes(5), 0, token));
            Assert.Equal(1, await adopted.GetJobCountAsync());
            Assert.Equal(DurableJobMutationResult.Applied, await adopted.RemoveJobAsync(job.Id, token));
            Assert.Equal(0, await adopted.GetJobCountAsync());
            await lastClaimant.UnregisterShardAsync(adopted, token);
            Assert.Empty(await lastClaimant.AssignJobShardsAsync(now.AddMinutes(5), 0, token));
        }
        finally
        {
            firstGate.Resume.TrySetResult();
            lastGate?.Resume.TrySetResult();
            if (adopted is not null)
            {
                await adopted.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ClaimantFenceRejectsInterveningContentAndRecoversEveryCommittedJob()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = cts.Token;
        GatedStorageProvider? storage = null;
        await using var scope = await fixture.CreateScopeAsync(token, inner => storage = new GatedStorageProvider(inner));
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var nextOwner = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        await using var original = await formerOwner.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), new Dictionary<string, string>(), token);
        var firstJob = await original.TryScheduleJobAsync(CreateRequest(now, "before-takeover"), token);
        Assert.NotNull(firstJob);
        scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);
        var gate = storage!.GateNextAppend();
        var claim = nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 1, token);
        try
        {
            await gate.Entered.Task.WaitAsync(token);
            var secondJob = await original.TryScheduleJobAsync(CreateRequest(now.AddSeconds(1), "before-fence"), token);
            Assert.NotNull(secondJob);
            gate.Resume.SetResult();
            await Assert.ThrowsAsync<InconsistentStateException>(() => claim.WaitAsync(token));

            await using var adopted = Assert.Single(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 0, token));
            Assert.Equal(2, await adopted.GetJobCountAsync());
            Assert.Equal(DurableJobMutationResult.Applied, await adopted.RemoveJobAsync(firstJob.Id, token));
            Assert.Equal(DurableJobMutationResult.Applied, await adopted.RemoveJobAsync(secondJob.Id, token));
            Assert.Equal(0, await adopted.GetJobCountAsync());
            await nextOwner.UnregisterShardAsync(adopted, token);
            Assert.Empty(await nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 0, token));
        }
        finally
        {
            gate.Resume.TrySetResult();
        }
    }

    [Fact]
    public async Task ConcurrentLocalOpensShareOneRecoveryAndCanonicalShard()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = cts.Token;
        GatedStorageProvider? storage = null;
        await using var scope = await fixture.CreateScopeAsync(token, inner => storage = new GatedStorageProvider(inner));
        var manager = scope.CreateManager(scope.ActiveSilo);
        var now = scope.Now;
        var gate = storage!.GateNextRead();
        var creation = manager.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), new Dictionary<string, string>(), token);
        using var events = new DiagnosticEventCollector("Orleans.DurableJobs");
        Task<List<IJobShard>>? discovery = null;
        IJobShard? shard = null;
        try
        {
            await gate.Entered.Task.WaitAsync(token);
            var readsBeforeDiscovery = storage.ReadCount;
            var joined = events.WaitForEventAsync(
                "Orleans.DurableJobs.ShardOpenJoined",
                item => Equals(item.Payload, storage.CreatedJournalId.Value),
                TimeSpan.FromSeconds(5),
                token);
            discovery = manager.AssignJobShardsAsync(now.AddMinutes(5), 0, token);
            await joined;
            Assert.False(creation.IsCompleted);
            Assert.False(discovery.IsCompleted);
            gate.Resume.SetResult();
            shard = await creation.WaitAsync(token);
            Assert.Same(shard, Assert.Single(await discovery.WaitAsync(token)));
            Assert.Equal(readsBeforeDiscovery, storage.ReadCount);
            Assert.Equal(0, storage.AppendCount);
            var job = await shard.TryScheduleJobAsync(CreateRequest(now, "canonical"), token);
            Assert.NotNull(job);
            Assert.Equal(DurableJobMutationResult.Applied, await shard.RemoveJobAsync(job.Id, token));
            Assert.Equal(0, await shard.GetJobCountAsync());
            await manager.UnregisterShardAsync(shard, token);
        }
        finally
        {
            gate.Resume.TrySetResult();
            shard ??= await creation;
            await shard.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentLocalClaimsShareOneContentFenceAndCanonicalShard()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = cts.Token;
        GatedStorageProvider? storage = null;
        await using var scope = await fixture.CreateScopeAsync(token, inner => storage = new GatedStorageProvider(inner));
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var nextOwner = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        await using var original = await formerOwner.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), new Dictionary<string, string>(), token);
        var job = await original.TryScheduleJobAsync(CreateRequest(now, "before-takeover"), token);
        Assert.NotNull(job);
        scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);
        var gate = storage!.GateNextAppend();
        var firstClaim = nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 1, token);
        using var events = new DiagnosticEventCollector("Orleans.DurableJobs");
        IJobShard? adopted = null;
        try
        {
            await gate.Entered.Task.WaitAsync(token);
            var appendsBeforeDiscovery = storage.AppendCount;
            var joined = events.WaitForEventAsync(
                "Orleans.DurableJobs.ShardOpenJoined",
                item => Equals(item.Payload, storage.CreatedJournalId.Value),
                TimeSpan.FromSeconds(5),
                token);
            var concurrentDiscovery = nextOwner.AssignJobShardsAsync(now.AddMinutes(5), 0, token);
            await joined;
            Assert.False(firstClaim.IsCompleted);
            Assert.False(concurrentDiscovery.IsCompleted);
            gate.Resume.SetResult();
            adopted = Assert.Single(await firstClaim.WaitAsync(token));
            Assert.Same(adopted, Assert.Single(await concurrentDiscovery.WaitAsync(token)));
            Assert.Equal(appendsBeforeDiscovery, storage.AppendCount);
            Assert.Equal(1, await adopted.GetJobCountAsync());
            Assert.Equal(DurableJobMutationResult.Applied, await adopted.RemoveJobAsync(job.Id, token));
            await nextOwner.UnregisterShardAsync(adopted, token);
        }
        finally
        {
            gate.Resume.TrySetResult();
            if (adopted is not null)
            {
                await adopted.DisposeAsync();
            }
        }
    }

    private sealed class AppendGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedStorageProvider(IJournalStorageProvider inner) : IJournalStorageProvider, IJournalStorageCatalog
    {
        private AppendGate? _nextAppend;
        private AppendGate? _nextRead;
        private AppendGate? _nextDelete;
        private int _appendCount;
        private int _readCount;
        private IJournalStorage? _firstReadStorage;

        public int AppendCount => Volatile.Read(ref _appendCount);
        public int ReadCount => Volatile.Read(ref _readCount);
        public IJournalStorage? FirstReadStorage => _firstReadStorage;
        public JournalId CreatedJournalId { get; private set; }

        public AppendGate GateNextAppend()
        {
            var gate = new AppendGate();
            Assert.Null(Interlocked.Exchange(ref _nextAppend, gate));
            return gate;
        }

        public AppendGate GateNextRead()
        {
            var gate = new AppendGate();
            Assert.Null(Interlocked.Exchange(ref _nextRead, gate));
            return gate;
        }

        public AppendGate GateNextDelete()
        {
            var gate = new AppendGate();
            Assert.Null(Interlocked.Exchange(ref _nextDelete, gate));
            return gate;
        }

        public IJournalStorage CreateStorage(JournalId journalId) => new GatedStorage(inner.CreateStorage(journalId), journalId, this);

        public IAsyncEnumerable<JournalCatalogEntry> ListAsync(JournalCatalogListOptions? options = null, CancellationToken cancellationToken = default)
            => ((IJournalStorageCatalog)inner).ListAsync(options, cancellationToken);

        private sealed class GatedStorage(IJournalStorage inner, JournalId journalId, GatedStorageProvider provider) : IJournalStorage
        {
            public bool IsCompactionRequested => inner.IsCompactionRequested;

            public async ValueTask<bool> CreateIfNotExistsAsync(IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
            {
                var created = await inner.CreateIfNotExistsAsync(metadata, cancellationToken);
                if (created)
                {
                    provider.CreatedJournalId = journalId;
                }

                return created;
            }

            public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
                => inner.GetMetadataAsync(cancellationToken);

            public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
                IReadOnlyDictionary<string, string>? set = null, IEnumerable<string>? remove = null, string? expectedETag = null, CancellationToken cancellationToken = default)
                => inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);

            public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
            {
                Interlocked.CompareExchange(ref provider._firstReadStorage, inner, null);
                Interlocked.Increment(ref provider._readCount);
                if (Interlocked.Exchange(ref provider._nextRead, null) is { } gate)
                {
                    gate.Entered.SetResult();
                    await gate.Resume.Task.WaitAsync(cancellationToken);
                }

                await inner.ReadAsync(consumer, cancellationToken);
            }

            public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref provider._appendCount);
                if (Interlocked.Exchange(ref provider._nextAppend, null) is { } gate)
                {
                    gate.Entered.SetResult();
                    await gate.Resume.Task.WaitAsync(cancellationToken);
                }

                await inner.AppendAsync(value, cancellationToken);
            }

            public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
                => inner.ReplaceAsync(value, cancellationToken);

            public async ValueTask DeleteAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.Exchange(ref provider._nextDelete, null) is { } gate)
                {
                    gate.Entered.SetResult();
                    await gate.Resume.Task.WaitAsync(cancellationToken);
                }

                await inner.DeleteAsync(cancellationToken);
            }
        }
    }
}

public sealed class AzureBlobJournaledJobShardManagerTestFixture : IJobShardManagerTestFixture
{
    public async Task<IJobShardManagerTestScope> CreateScopeAsync(CancellationToken cancellationToken)
        => await CreateScopeAsync(cancellationToken, decorateStorage: null);

    public async Task<IJobShardManagerTestScope> CreateScopeAsync(
        CancellationToken cancellationToken,
        Func<IJournalStorageProvider, IJournalStorageProvider>? decorateStorage)
    {
        TestUtils.CheckForAzureStorage();

        var containerName = "durablejobs-shard-tests-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton(TimeProvider.System);
        services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        services.UseAzureBlobDurableJobs(options =>
        {
            options.ConfigureTestDefaults();
            options.ContainerName = containerName;
        });
        var storageRegistration = services.Last(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IJournalStorageProvider));
        var createStorage = storageRegistration.ImplementationFactory;
        Assert.NotNull(createStorage);
        if (decorateStorage is not null)
        {
            services.AddSingleton<IJournalStorageProvider>(sp => decorateStorage((IJournalStorageProvider)createStorage(sp)));
        }

        var serviceProvider = services.BuildServiceProvider();
        var lifecycle = new SiloLifecycleSubject(serviceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());
        var storageProvider = (IJournalStorageProvider)createStorage(serviceProvider);
        Assert.IsAssignableFrom<ILifecycleParticipant<ISiloLifecycle>>(storageProvider).Participate(lifecycle);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        await lifecycle.OnStart(cts.Token);
        return new AzureBlobJournaledJobShardManagerTestScope(serviceProvider, lifecycle, CreateContainerClient(containerName));
    }

    private static BlobContainerClient CreateContainerClient(string containerName)
    {
        return TestDefaultConfiguration.UseAadAuthentication
            ? new BlobContainerClient(new Uri(TestDefaultConfiguration.DataBlobUri, containerName), TestDefaultConfiguration.TokenCredential)
            : new BlobContainerClient(TestDefaultConfiguration.DataConnectionString, containerName);
    }

    private sealed class AzureBlobJournaledJobShardManagerTestScope(
        ServiceProvider services,
        SiloLifecycleSubject lifecycle,
        BlobContainerClient container) : JournaledJobShardManagerTestScope(services)
    {
        public override async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await lifecycle.OnStop(cts.Token);
            }
            catch (OperationCanceledException) when (TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                // Preserve the original test cancellation after bounded cleanup.
            }

            await base.DisposeAsync();
            try
            {
                await container.DeleteIfExistsAsync(cancellationToken: cts.Token);
            }
            catch (OperationCanceledException) when (TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                // Preserve the original test cancellation after bounded cleanup.
            }
        }
    }
}
