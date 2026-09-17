using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

public partial class JournaledJobShardManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderLookup_OneSelectedProviderReadsExactMetadataOnceDespiteUnrelatedRegistrations(bool exists)
    {
        await using var fixture = new ProviderMigrationFixture();
        var id = exists
            ? await fixture.AddShardAsync("B", fixture.Now.AddYears(10), fixture.Silo)
            : JobShardId.New(fixture.Now.AddYears(10)).ToJournalId();
        var manager = fixture.CreateManager("B");
        fixture.ClearObservations();

        var owner = await manager.GetShardOwnerAsync(JobShardId.FromJournalId(id).Value, TestContext.Current.CancellationToken);

        Assert.Equal(exists ? fixture.Silo : null, owner);
        Assert.Equal(new[] { ("B", id) }, fixture.LookupOrder);
        Assert.Equal(new[] { id }, fixture.B.MetadataReads);
        Assert.Empty(fixture.A.MetadataReads);
        Assert.Empty(fixture.Unrelated.MetadataReads);
        fixture.AssertNoCatalogQueries();
    }

    [Theory]
    [InlineData("B")]
    [InlineData("A")]
    [InlineData(null)]
    public async Task ProviderLookup_FutureShardChecksWriteThenDrainsAndStopsOnHit(string? location)
    {
        await using var fixture = new ProviderMigrationFixture();
        var id = location is null
            ? JobShardId.New(fixture.Now.AddYears(10)).ToJournalId()
            : await fixture.AddShardAsync(location, fixture.Now.AddYears(10), fixture.Silo);
        var manager = fixture.CreateManager("B", "A");
        fixture.ClearObservations();

        var owner = await manager.GetShardOwnerAsync(JobShardId.FromJournalId(id).Value, TestContext.Current.CancellationToken);

        Assert.Equal(location is null ? null : fixture.Silo, owner);
        Assert.Equal(location == "B" ? new[] { ("B", id) } : [("B", id), ("A", id)], fixture.LookupOrder);
        fixture.AssertNoCatalogQueries();
        Assert.Empty(fixture.Unrelated.OpenedJournalIds);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ProviderLookup_FailedWriteReadUsesConclusiveDrainHitButNeverReportsIncompleteSearchAsAbsent(bool drainHasShard, bool providerCancellation)
    {
        await using var fixture = new ProviderMigrationFixture();
        var id = drainHasShard
            ? await fixture.AddShardAsync("A", fixture.Now.AddYears(10), fixture.Silo)
            : JobShardId.New(fixture.Now.AddYears(10)).ToJournalId();
        Exception failure = providerCancellation
            ? new OperationCanceledException("provider-local timeout")
            : new IOException("write namespace unavailable");
        fixture.B.BeforeMetadataRead = (journalId, _) =>
        {
            fixture.LookupOrder.Enqueue(("B", journalId));
            throw failure;
        };
        var manager = fixture.CreateManager("B", "A");
        fixture.ClearObservations();

        if (drainHasShard)
        {
            Assert.Equal(fixture.Silo, await manager.GetShardOwnerAsync(JobShardId.FromJournalId(id).Value, TestContext.Current.CancellationToken));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => manager.GetShardOwnerAsync(JobShardId.FromJournalId(id).Value, TestContext.Current.CancellationToken).AsTask());
            var providerFailure = Assert.Single(exception.InnerExceptions);
            Assert.Contains("'B'", providerFailure.Message);
            Assert.Same(failure, providerFailure.InnerException);
        }

        Assert.Equal(new[] { ("B", id), ("A", id) }, fixture.LookupOrder);
        Assert.Contains(fixture.Log.Messages, message => message.Contains("'B'") && message.Contains("shard lookup"));
        fixture.AssertNoCatalogQueries();
    }

    [Fact]
    public async Task ProviderLookup_DrainFailureAfterWriteMissIsNotAbsence()
    {
        await using var fixture = new ProviderMigrationFixture();
        var id = JobShardId.New(fixture.Now.AddYears(10)).ToJournalId();
        var failure = new IOException("drain unavailable");
        fixture.A.BeforeMetadataRead = (journalId, _) =>
        {
            fixture.LookupOrder.Enqueue(("A", journalId));
            throw failure;
        };
        var manager = fixture.CreateManager("B", "A");

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => manager.GetShardOwnerAsync(JobShardId.FromJournalId(id).Value, TestContext.Current.CancellationToken).AsTask());

        Assert.Same(failure, Assert.Single(exception.InnerExceptions).InnerException);
        Assert.Contains("'A'", exception.InnerExceptions[0].Message);
        Assert.Equal(new[] { ("B", id), ("A", id) }, fixture.LookupOrder);
        fixture.AssertNoCatalogQueries();
    }

    [Fact]
    public async Task ProviderLookup_CancellationStopsBeforeDrainingProvider()
    {
        await using var fixture = new ProviderMigrationFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var id = JobShardId.New(fixture.Now).ToJournalId();
        fixture.B.BeforeMetadataRead = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        };
        var manager = fixture.CreateManager("B", "A");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.GetShardOwnerAsync(JobShardId.FromJournalId(id).Value, cancellation.Token).AsTask());

        Assert.Equal(new[] { id }, fixture.B.MetadataReads);
        Assert.Empty(fixture.A.MetadataReads);
        Assert.Empty(fixture.Log.Messages);
    }

    [Fact]
    public async Task ProviderLookup_SingleSelectedProviderPropagatesFailureWithoutQueryingOtherRegistrations()
    {
        await using var fixture = new ProviderMigrationFixture();
        var id = JobShardId.New(fixture.Now.AddYears(10)).ToJournalId();
        var failure = new IOException("single selected provider unavailable");
        fixture.B.BeforeMetadataRead = (_, _) => throw failure;
        var manager = fixture.CreateManager("B");

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            manager.GetShardOwnerAsync(JobShardId.FromJournalId(id).Value, TestContext.Current.CancellationToken).AsTask());

        Assert.Same(failure, exception);
        Assert.Equal(new[] { id }, fixture.B.MetadataReads);
        Assert.Empty(fixture.A.MetadataReads);
        Assert.Empty(fixture.Unrelated.MetadataReads);
        fixture.AssertNoCatalogQueries();
    }

    [Fact]
    public async Task ProviderLookup_UnrecognizedMetadataCannotBeMistakenForAbsence()
    {
        await using var fixture = new ProviderMigrationFixture();
        var id = JobShardId.New(fixture.Now.AddYears(10)).ToJournalId();
        await fixture.A.CreateStorage(id).CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["DurableJobsMinDueTime"] = "invalid" }, TestContext.Current.CancellationToken);
        var manager = fixture.CreateManager("B", "A");
        fixture.ClearObservations();

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            manager.GetShardOwnerAsync(JobShardId.FromJournalId(id).Value, TestContext.Current.CancellationToken).AsTask());

        var providerFailure = Assert.Single(exception.InnerExceptions);
        Assert.Contains("'A'", providerFailure.Message);
        Assert.Contains("unrecognized shard metadata", providerFailure.InnerException!.Message);
        Assert.Equal(new[] { ("B", id), ("A", id) }, fixture.LookupOrder);
        fixture.AssertNoCatalogQueries();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderCutover_ReplaysOldJobsAndPersistsRetryOrRescheduleAndRemovalOnlyInOriginalJournal(bool reschedule)
    {
        await using var fixture = new ProviderMigrationFixture();
        var token = TestContext.Current.CancellationToken;
        var originalManager = fixture.CreateManager("A", "B");
        var original = await fixture.CreateShardAsync(originalManager, fixture.Now.AddHours(-1));
        var job = await original.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "migration"),
            JobName = "old-job",
            DueTime = fixture.Now,
            Metadata = new Dictionary<string, string> { ["origin"] = "A" }
        }, token);
        Assert.NotNull(job);
        await originalManager.UnregisterShardAsync(original, token);
        var storageId = ((JournaledJobShard)original).StorageId;

        var afterCutover = fixture.CreateManager("B", "A");
        var recovered = Assert.Single(await fixture.DiscoverAsync(afterCutover));
        Assert.Equal(original.Id, recovered.Id);
        Assert.Same(fixture.BindingA, ((JournaledJobShard)recovered).Provider);
        Assert.True(recovered.IsAddingCompleted);
        Assert.Equal("A", recovered.Metadata!["provider"]);
        Assert.Null(await recovered.TryScheduleJobAsync(new()
        {
            Target = job.TargetGrainId,
            JobName = "must-not-enter-drain",
            DueTime = fixture.Now
        }, token));
        var newShard = await fixture.CreateShardAsync(afterCutover, fixture.Now);
        Assert.Same(fixture.BindingB, ((JournaledJobShard)newShard).Provider);
        Assert.NotEqual(original.Id, newShard.Id);
        Assert.Null(await fixture.A.CreateStorage(((JournaledJobShard)newShard).StorageId).GetMetadataAsync(token));
        Assert.Null(await fixture.B.CreateStorage(storageId).GetMetadataAsync(token));

        await using (var enumerator = recovered.ConsumeDurableJobsAsync().GetAsyncEnumerator(token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            var context = enumerator.Current;
            Assert.Equal(job.Id, context.Job.Id);
            Assert.Equal(job.ShardId, context.Job.ShardId);
            Assert.Equal("A", context.Job.Metadata!["origin"]);
            Assert.Equal(1, context.DequeueCount);
            Assert.Equal(DurableJobMutationResult.Applied, await recovered.TryStartAttemptAsync(context, token));
            var future = fixture.Now.AddYears(1);
            fixture.ClearObservations();
            Assert.Equal(DurableJobMutationResult.Applied, reschedule
                ? await recovered.RescheduleJobAsync(context, future, token)
                : await recovered.RetryJobLaterAsync(context, future, token));
            Assert.Equal(new[] { storageId }, fixture.A.JournalAppends);
            Assert.Empty(fixture.B.JournalAppends);
        }

        await afterCutover.UnregisterShardAsync(recovered, token);
        var restarted = fixture.CreateManager("B", "A");
        fixture.ClearObservations();
        var replayed = Assert.Single(await fixture.DiscoverAsync(restarted), shard => shard.Id == original.Id);
        Assert.Contains(storageId, fixture.A.JournalReads);
        Assert.DoesNotContain(storageId, fixture.B.JournalReads);
        fixture.Clock.Advance(TimeSpan.FromDays(366));
        await using (var enumerator = replayed.ConsumeDurableJobsAsync().GetAsyncEnumerator(token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            var context = enumerator.Current;
            Assert.Equal(job.Id, context.Job.Id);
            Assert.Equal(original.Id, context.Job.ShardId);
            Assert.Equal(fixture.Now.AddYears(1), context.Job.DueTime);
            Assert.Equal(reschedule ? 1 : 2, context.DequeueCount);
            Assert.Equal(reschedule ? 1 : 0, context.Job.ExecutionGeneration);
            fixture.ClearObservations();
            Assert.Equal(DurableJobMutationResult.Applied, await replayed.RemoveJobAsync(job.Id, token));
            Assert.Equal(0, await replayed.GetJobCountAsync());
            Assert.Equal(new[] { storageId }, fixture.A.JournalAppends);
            Assert.Empty(fixture.B.JournalAppends);
        }

        await restarted.UnregisterShardAsync(replayed, token);
        Assert.Equal(new[] { storageId }, fixture.A.JournalDeletes);
        Assert.Empty(fixture.B.JournalDeletes);
        Assert.Null(await fixture.A.CreateStorage(storageId).GetMetadataAsync(token));
        Assert.NotNull(await fixture.B.CreateStorage(((JournaledJobShard)newShard).StorageId).GetMetadataAsync(token));
    }

    [Fact]
    public async Task ProviderCutover_OwnerDeathRebindsReplayAndCancellationToOldProvider()
    {
        await using var fixture = new ProviderMigrationFixture();
        var token = TestContext.Current.CancellationToken;
        var oldOwner = fixture.CreateManager("A", "B");
        var shard = await fixture.CreateShardAsync(oldOwner, fixture.Now);
        var job = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "cancel-after-cutover"),
            JobName = "cancel-future",
            DueTime = fixture.Now.AddMinutes(30)
        }, token);
        Assert.NotNull(job);
        await shard.DisposeAsync(); // Crash: do not gracefully release its metadata.
        fixture.Membership.SetSiloStatus(fixture.Silo, SiloStatus.Dead);
        var nextSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5111), 0);
        fixture.Membership.SetSiloStatus(nextSilo, SiloStatus.Active);
        var afterCutover = fixture.CreateManager("B", "A", silo: nextSilo);

        var recovered = Assert.Single(await fixture.DiscoverAsync(afterCutover));
        fixture.ClearObservations();
        Assert.Equal(nextSilo, await afterCutover.GetShardOwnerAsync(job.ShardId, token));
        Assert.Equal(new[] { ((JournaledJobShard)shard).StorageId }, fixture.A.MetadataReads);
        Assert.Empty(fixture.B.MetadataReads);
        Assert.Equal(DurableJobMutationResult.Applied, await recovered.RemoveJobAsync(job.Id, token));
        Assert.Equal(0, await recovered.GetJobCountAsync());
        await afterCutover.UnregisterShardAsync(recovered, token);
        Assert.Equal(new[] { ((JournaledJobShard)shard).StorageId }, fixture.A.JournalDeletes);
        Assert.Empty(fixture.B.JournalAppends);
        Assert.Empty(fixture.B.JournalDeletes);
        Assert.Empty(await fixture.DiscoverAsync(fixture.CreateManager("B", "A", silo: nextSilo)));
    }

    [Fact]
    public async Task ProviderCutover_SameOwnerRestartClosesDrainingShardBeforeScheduling()
    {
        await using var fixture = new ProviderMigrationFixture();
        var token = TestContext.Current.CancellationToken;
        var original = await fixture.CreateShardAsync(fixture.CreateManager("A", "B"), fixture.Now);
        var job = await original.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "same-owner"),
            JobName = "existing",
            DueTime = fixture.Now
        }, token);
        Assert.NotNull(job);
        await original.DisposeAsync(); // Same silo identity restarts without graceful release.
        var id = ((JournaledJobShard)original).StorageId;
        fixture.ClearObservations();

        var recovered = Assert.Single(await fixture.DiscoverAsync(fixture.CreateManager("B", "A")));

        Assert.True(recovered.IsAddingCompleted);
        Assert.Equal(original.Id, recovered.Id);
        Assert.Equal(1, await recovered.GetJobCountAsync());
        Assert.Equal(new[] { id }, fixture.A.MetadataUpdates.Select(update => update.Id));
        var metadata = await fixture.A.CreateStorage(id).GetMetadataAsync(token);
        Assert.NotNull(metadata);
        Assert.Equal(bool.TrueString, metadata.Properties["DurableJobsClosed"]);
        Assert.Equal(fixture.Silo.ToParsableString(), metadata.Properties["DurableJobsOwner"]);
        Assert.Null(await recovered.TryScheduleJobAsync(new()
        {
            Target = job.TargetGrainId,
            JobName = "rejected-after-cutover",
            DueTime = fixture.Now
        }, token));
        Assert.Empty(fixture.B.JournalAppends);
        Assert.Empty(fixture.B.MetadataUpdates);
        Assert.Contains(id, fixture.A.JournalReads);
        Assert.DoesNotContain(id, fixture.B.JournalReads);
    }

    [Fact]
    public async Task ProviderDiscovery_AppliesGlobalOldestOrderAndAggregateBudgetWhileRetainingLocalShards()
    {
        await using var fixture = new ProviderMigrationFixture();
        var oldA = await fixture.AddShardAsync("A", fixture.Now.AddDays(-3));
        var nextB = await fixture.AddShardAsync("B", fixture.Now.AddDays(-2));
        var laterA = await fixture.AddShardAsync("A", fixture.Now.AddDays(-1));
        var localB = await fixture.AddShardAsync("B", fixture.Now, fixture.Silo);
        var manager = fixture.CreateManager("B", "A");

        var first = await fixture.DiscoverAsync(manager, 1);
        AssertAssignedIds([oldA, localB], first);
        Assert.Equal(new[] { oldA }, fixture.A.MetadataUpdates.Select(update => update.Id));
        Assert.Empty(fixture.B.MetadataUpdates);

        var second = await fixture.DiscoverAsync(manager, 1);
        AssertAssignedIds([oldA, nextB, localB], second);
        Assert.Same(first[0], second[0]);
        Assert.Equal(new[] { nextB }, fixture.B.MetadataUpdates.Select(update => update.Id));
        Assert.DoesNotContain(fixture.A.MetadataUpdates, update => update.Id == laterA);
        Assert.All(fixture.A.ListRequests.Concat(fixture.B.ListRequests), request =>
        {
            Assert.NotNull(request);
            Assert.Equal(JobShardId.GetMaxJournalId(fixture.Now.AddHours(1)), request.MaxId);
            Assert.True(request.IncludeMetadata);
        });
    }

    [Fact]
    public async Task ProviderDiscovery_IncludesExactHorizonAcrossCatalogsButExcludesNextTickPoisonedAndLiveForeignOwners()
    {
        await using var fixture = new ProviderMigrationFixture();
        var horizon = fixture.Now.AddHours(1);
        var other = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5112), 0);
        fixture.Membership.SetSiloStatus(other, SiloStatus.Active);
        var poisoned = await fixture.AddShardAsync("B", fixture.Now.AddDays(-2), poisoned: true);
        var liveForeign = await fixture.AddShardAsync("A", fixture.Now.AddDays(-1), other);
        var before = await fixture.AddShardAsync("A", horizon.AddTicks(-1));
        var exact = await fixture.AddShardAsync("B", horizon);
        var after = await fixture.AddShardAsync("A", horizon.AddTicks(1));

        AssertAssignedIds([before, exact], await fixture.DiscoverAsync(fixture.CreateManager("B", "A"), 2));

        Assert.Equal(new[] { before }, fixture.A.MetadataUpdates.Select(update => update.Id));
        Assert.Equal(new[] { exact }, fixture.B.MetadataUpdates.Select(update => update.Id));
        Assert.DoesNotContain(poisoned, fixture.B.JournalReads);
        Assert.DoesNotContain(liveForeign, fixture.A.JournalReads);
        Assert.DoesNotContain(after, fixture.A.JournalReads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProviderDiscovery_FailedCatalogOrMetadataIsNamedAndHealthyProviderContinuesThenRetries(bool metadataFailure, bool providerCancellation)
    {
        await using var fixture = new ProviderMigrationFixture();
        var oldB = await fixture.AddShardAsync("B", fixture.Now.AddDays(-2));
        var oldA = await fixture.AddShardAsync("A", fixture.Now.AddDays(-1));
        Exception failure = providerCancellation
            ? new OperationCanceledException("provider-local timeout")
            : new IOException("provider B unavailable");
        var catalog = new ScriptedCatalog();
        catalog.Ids.Add(oldB);
        fixture.B.ListOverride = catalog.ListAsync;
        if (metadataFailure)
        {
            fixture.B.BeforeMetadataRead = (_, _) => throw failure;
        }
        else
        {
            catalog.BeforeMoveNext = (index, _) => index == 1 ? throw failure : ValueTask.CompletedTask;
        }
        var manager = fixture.CreateManager("B", "A");

        AssertAssignedIds([oldA], await fixture.DiscoverAsync(manager, 1));
        Assert.Empty(fixture.B.MetadataUpdates);
        Assert.Contains(fixture.Log.Messages, message => message.Contains("'B'") && message.Contains(metadataFailure ? "shard assignment" : "catalog discovery"));
        catalog.BeforeMoveNext = null;
        fixture.B.BeforeMetadataRead = null;

        AssertAssignedIds([oldB, oldA], await fixture.DiscoverAsync(manager, 1));
        Assert.Equal(2, catalog.ListCalls);
        Assert.Equal(new[] { oldB }, fixture.B.MetadataUpdates.Select(update => update.Id));
    }

    [Theory]
    [InlineData("A", false)]
    [InlineData("B", true)]
    public async Task ProviderInventory_IncludesFuturePoisonedOwnedAndMalformedEntriesWithoutOpeningOrMutating(string providerName, bool isWrite)
    {
        await using var fixture = new ProviderMigrationFixture();
        var provider = providerName == "A" ? fixture.A : fixture.B;
        var oldest = fixture.Now.AddYears(-2);
        var newest = fixture.Now.AddYears(20);
        var old = await fixture.AddShardAsync(providerName, oldest);
        var future = await fixture.AddShardAsync(providerName, newest, fixture.Silo, poisoned: true);
        var malformed = JobShardId.New(fixture.Now).ToJournalId();
        await provider.CreateStorage(malformed).CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["DurableJobsMinDueTime"] = "invalid" }, TestContext.Current.CancellationToken);
        var catalog = new ScriptedCatalog { ExpectUnbounded = true };
        catalog.Ids.AddRange([future, malformed, old, future]);
        catalog.Metadata.Add(future, (await provider.CreateStorage(future).GetMetadataAsync(TestContext.Current.CancellationToken))!);
        provider.ListOverride = catalog.ListAsync;
        var inspector = fixture.CreateInspector();
        fixture.ClearObservations();

        var status = await inspector.InspectAsync(providerName, TestContext.Current.CancellationToken);

        Assert.Equal(providerName, status.ProviderName);
        Assert.Equal(isWrite, status.IsWriteProvider);
        Assert.Equal(3, status.ShardCount);
        Assert.Equal(1, status.OwnedShardCount);
        Assert.Equal(1, status.PoisonedShardCount);
        Assert.Equal(1, status.UnrecognizedShardCount);
        Assert.Equal(oldest, status.OldestShardStartTime);
        Assert.Equal(newest, status.NewestShardStartTime);
        Assert.True(Assert.Single(catalog.Requests).MaxId.IsDefault);
        Assert.Equal(new[] { malformed, old }, provider.MetadataReads);
        Assert.Empty(provider.MetadataUpdates);
        Assert.Empty(provider.JournalReads);
        Assert.Empty(provider.JournalAppends);
        Assert.Empty(provider.JournalDeletes);
        Assert.Empty((providerName == "A" ? fixture.B : fixture.A).ListRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderInventory_PartialReadOrListFailureFaultsInsteadOfReportingDrained(bool metadataFailure)
    {
        await using var fixture = new ProviderMigrationFixture();
        var id = await fixture.AddShardAsync("A", fixture.Now.AddYears(10));
        var catalog = new ScriptedCatalog { ExpectUnbounded = true };
        catalog.Ids.Add(id);
        fixture.A.ListOverride = catalog.ListAsync;
        var failure = new IOException("retirement inventory unavailable");
        if (metadataFailure)
        {
            fixture.A.BeforeMetadataRead = (_, _) => throw failure;
        }
        else
        {
            catalog.BeforeMoveNext = (index, _) => index == 1 ? throw failure : ValueTask.CompletedTask;
        }
        var inspector = fixture.CreateInspector();

        var exception = await Assert.ThrowsAsync<IOException>(
            () => inspector.InspectAsync("A", TestContext.Current.CancellationToken).AsTask());

        Assert.Same(failure, exception);
        Assert.Contains(fixture.InventoryLog.Messages, message => message.Contains("'A'") && message.Contains("failed"));
        Assert.Empty(fixture.A.MetadataUpdates);
        Assert.Empty(fixture.A.JournalReads);
        Assert.Empty(fixture.A.JournalDeletes);
    }

    [Fact]
    public async Task ProviderInventory_EmptyNamespaceHasZeroCountsAndUnknownProviderCannotBeInspected()
    {
        await using var fixture = new ProviderMigrationFixture();
        var inspector = fixture.CreateInspector();

        var status = await inspector.InspectAsync("A", TestContext.Current.CancellationToken);

        Assert.Equal("A", status.ProviderName);
        Assert.False(status.IsWriteProvider);
        Assert.Equal(0, status.ShardCount);
        Assert.Equal(0, status.OwnedShardCount);
        Assert.Equal(0, status.PoisonedShardCount);
        Assert.Equal(0, status.UnrecognizedShardCount);
        Assert.Null(status.OldestShardStartTime);
        Assert.Null(status.NewestShardStartTime);
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => inspector.InspectAsync("unrelated", TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("unrelated", exception.Message);
        Assert.Empty(fixture.Unrelated.ListRequests);
    }

    [Fact]
    public async Task ProviderInventory_CancellationAfterPartialEnumerationNeverReturnsPartialSuccess()
    {
        await using var fixture = new ProviderMigrationFixture();
        var id = await fixture.AddShardAsync("A", fixture.Now);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var catalog = new ScriptedCatalog { ExpectUnbounded = true };
        catalog.Ids.Add(id);
        catalog.BeforeMoveNext = (index, token) =>
        {
            if (index == 1)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return ValueTask.CompletedTask;
        };
        fixture.A.ListOverride = catalog.ListAsync;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.CreateInspector().InspectAsync("A", cancellation.Token).AsTask());

        Assert.Equal(1, catalog.YieldedIds);
        Assert.Equal(1, catalog.DisposeCalls);
        Assert.Empty(fixture.InventoryLog.Messages);
        Assert.Empty(fixture.A.JournalReads);
        Assert.Empty(fixture.A.MetadataUpdates);
    }

    private sealed class ProviderMigrationFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _servicesA;
        private readonly ServiceProvider _servicesB;
        private readonly ServiceProvider _registry;
        private readonly HashSet<IJobShard> _opened = [];

        public ProviderMigrationFixture()
        {
            Clock = new FakeTimeProvider(Now);
            _servicesA = CreateServices(A, Clock);
            _servicesB = CreateServices(B, Clock);
            BindingA = new("A", A, A, _servicesA.GetRequiredService<IJournaledStateManagerFactory>());
            BindingB = new("B", B, B, _servicesB.GetRequiredService<IJournaledStateManagerFactory>());
            var registry = new ServiceCollection();
            foreach (var binding in new[] { BindingA, BindingB, new("unrelated", Unrelated, Unrelated, BindingA.Factory) })
            {
                registry.AddKeyedSingleton<IJournalStorageProvider>(binding.Name, binding.Storage);
                registry.AddKeyedSingleton<IJournalStorageCatalog>(binding.Name, binding.Catalog);
                registry.AddKeyedSingleton<IJournaledStateManagerFactory>(binding.Name, binding.Factory);
            }
            _registry = registry.BuildServiceProvider();
            A.BeforeMetadataRead = (id, _) => { LookupOrder.Enqueue(("A", id)); return ValueTask.CompletedTask; };
            B.BeforeMetadataRead = (id, _) => { LookupOrder.Enqueue(("B", id)); return ValueTask.CompletedTask; };
            Membership.SetSiloStatus(Silo, SiloStatus.Active);
        }

        public DateTimeOffset Now { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public FakeTimeProvider Clock { get; }
        public SiloAddress Silo { get; } = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5110), 0);
        public TestClusterMembershipService Membership { get; } = new();
        public CountingJournalStorageProvider A { get; } = new(false);
        public CountingJournalStorageProvider B { get; } = new(false);
        public CountingJournalStorageProvider Unrelated { get; } = new(false);
        public DurableJobsJournalProvider BindingA { get; }
        public DurableJobsJournalProvider BindingB { get; }
        public ConcurrentQueue<(string Provider, JournalId Id)> LookupOrder { get; } = new();
        public MigrationLogger<JournaledJobShardManager> Log { get; } = new();
        public MigrationLogger<DurableJobsStorageInspector> InventoryLog { get; } = new();

        public JournaledJobShardManager CreateManager(string write, string? drain = null, SiloAddress? silo = null)
        {
            var options = new DurableJobsOptions { ActiveProviderName = write };
            if (drain is not null) options.DrainingProviderNames.Add(drain);
            var resolved = new DurableJobsJournalProviders(_registry, Options.Create(options));
            // Reuse fixture bindings so tests can assert identity, not just provider names.
            var bindings = new DurableJobsJournalProviders(resolved.Providers
                .Select(binding => binding.Name == "A" ? BindingA : BindingB).ToArray());
            return new(new TestLocalSiloDetails(silo ?? Silo), bindings, Membership, _servicesB,
                Options.Create(options), _servicesB.GetRequiredService<IOptions<JournaledStateManagerOptions>>(), logger: Log);
        }

        public IDurableJobsStorageInspector CreateInspector() => new DurableJobsStorageInspector(
            new DurableJobsJournalProviders(BindingB, BindingA), InventoryLog);

        public async Task<JournalId> AddShardAsync(string provider, DateTimeOffset start, SiloAddress? owner = null, bool poisoned = false)
        {
            var id = JobShardId.New(start).ToJournalId();
            var properties = new Dictionary<string, string>
            {
                ["DurableJobsMinDueTime"] = start.ToString("O"),
                ["DurableJobsMaxDueTime"] = start.AddHours(1).ToString("O"),
                ["DurableJobsClosed"] = bool.TrueString,
                ["DurableJobsPoisoned"] = poisoned.ToString()
            };
            if (owner is not null) properties["DurableJobsOwner"] = owner.ToParsableString();
            await (provider == "A" ? A : B).CreateStorage(id).CreateIfNotExistsAsync(properties, TestContext.Current.CancellationToken);
            return id;
        }

        public async Task<IJobShard> CreateShardAsync(JournaledJobShardManager manager, DateTimeOffset start)
        {
            var shard = await manager.CreateShardAsync(start, start.AddHours(1),
                new Dictionary<string, string> { ["provider"] = manager.WriteProvider.Name }, TestContext.Current.CancellationToken);
            _opened.Add(shard);
            return shard;
        }

        public async Task<List<IJobShard>> DiscoverAsync(JournaledJobShardManager manager, int budget = int.MaxValue)
        {
            var shards = await manager.AssignJobShardsAsync(Now.AddHours(1), budget, TestContext.Current.CancellationToken);
            _opened.UnionWith(shards);
            return shards;
        }

        public void ClearObservations()
        {
            LookupOrder.Clear();
            foreach (var storage in new[] { A, B, Unrelated })
            {
                storage.MetadataReads.Clear();
                storage.MetadataUpdates.Clear();
                storage.JournalReads.Clear();
                storage.JournalAppends.Clear();
                storage.JournalReplacements.Clear();
                storage.JournalDeletes.Clear();
                storage.OpenedJournalIds.Clear();
                storage.ListRequests.Clear();
            }
        }

        public void AssertNoCatalogQueries()
        {
            Assert.Empty(A.ListRequests);
            Assert.Empty(B.ListRequests);
            Assert.Empty(Unrelated.ListRequests);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var shard in _opened) await shard.DisposeAsync();
            await _registry.DisposeAsync();
            await _servicesA.DisposeAsync();
            await _servicesB.DisposeAsync();
        }
    }

    private sealed class MigrationLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
