using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Clustering.Cosmos;
using Orleans.Clustering.Cosmos.Models;
using Orleans.Configuration;
using Orleans.Runtime;
using static Tester.Cosmos.Clustering.CosmosMembershipTestStorage;

namespace Tester.Cosmos.Clustering;

[TestCategory("Membership"), TestCategory("Cosmos"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("Cosmos")]
[TestArea("Membership")]
public class CosmosMembershipTableCancellationTests
{
    [Theory]
    [InlineData("Initialize")]
    [InlineData("Delete")]
    [InlineData("Cleanup")]
    [InlineData("ReadRow")]
    [InlineData("ReadAll")]
    [InlineData("Insert")]
    [InlineData("Update")]
    [InlineData("Heartbeat")]
    public async Task CanceledOperationsDoNotAccessStorage(string operation)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var table = CreateTable(services, new CosmosClusteringOptions());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var token = cancellation.Token;
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
        var entry = new MembershipEntry { SiloAddress = silo };
        var version = new TableVersion(1, "etag");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "Initialize" => table.InitializeMembershipTableAsync(true, token),
            "Delete" => table.DeleteMembershipTableEntriesAsync("cluster", token),
            "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, token),
            "ReadRow" => table.ReadRowAsync(silo, token),
            "ReadAll" => table.ReadAllAsync(token),
            "Insert" => table.InsertRowAsync(entry, version, token),
            "Update" => table.UpdateRowAsync(entry, "etag", version, token),
            "Heartbeat" => table.UpdateIAmAliveAsync(entry, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Equal(token, exception.CancellationToken);
    }

    [Fact]
    public async Task CanceledInitializationRetainsSharedClientForRetry()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var completion = new TaskCompletionSource<CosmosClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new TrackingCosmosClient();
        var calls = 0;
        var options = new CosmosClusteringOptions { DatabaseName = "database", ContainerName = "container" };
        options.ConfigureCosmosClient(_ =>
        {
            calls++;
            return new ValueTask<CosmosClient>(completion.Task);
        });
        var table = CreateTable(services, options);

        var initialization = table.InitializeMembershipTableAsync(false, cancellation.Token);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initialization);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(initialization.IsCanceled);

        completion.SetResult(client);
        Assert.Equal(0, client.ContainerCalls);
        Assert.Equal(0, client.DisposeCalls);

        var failure = new InvalidOperationException("retry reached the retained client");
        client.ContainerFailure = failure;
        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => table.InitializeMembershipTableAsync(false, TestContext.Current.CancellationToken));

        Assert.Same(failure, retryException);
        Assert.Equal(1, calls);
        Assert.Equal(1, client.ContainerCalls);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Fact]
    public async Task NativeDatabaseCancellationIsNotWrapped()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var database = Substitute.For<Database>();
        using var client = new TrackingCosmosClient { Database = database };
        _ = database.DeleteAsync(Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Assert.Equal(cancellation.Token, call.Arg<CancellationToken>());
            cancellation.Cancel();
            return Task.FromCanceled<DatabaseResponse>(cancellation.Token);
        });
        var options = new CosmosClusteringOptions
        {
            DatabaseName = "database",
            ContainerName = "container",
            IsResourceCreationEnabled = true,
            CleanResourcesOnInitialization = true
        };
        options.ConfigureCosmosClient(_ => new ValueTask<CosmosClient>(client));
        var table = CreateTable(services, options);

        var initialization = table.InitializeMembershipTableAsync(true, cancellation.Token);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(initialization.IsCanceled);
        Assert.Equal(0, client.ContainerCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipReadsRetryChangedVersionAndCarrySessionFence(bool pointRead)
    {
        using var storage = new CosmosMembershipTestStorage();
        var oldSilo = Silo();
        var currentSilo = Silo(status: SiloStatus.Dead);
        storage.Container.ReadItemAsync<ClusterVersionEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(Version(7, "v7", "0:7"), Version(8, "v8", "0:9"), Version(8, "v8", "0:9"), Version(8, "v8", "0:10"));
        storage.Container.ReadItemAsync<SiloEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(Item(oldSilo, "0:8"), Item(currentSilo, "0:10"));
        storage.SetPages(Page("0:8", null, oldSilo), Page("0:10", null, currentSilo));

        var result = pointRead
            ? await storage.Table.ReadRowAsync(Entry().SiloAddress, Token)
            : await storage.Table.ReadAllAsync(Token);

        Assert.Equal(SiloStatus.Dead, Assert.Single(result.Members).Item1.Status);
        Assert.Equal(8, result.Version.Version);
        Assert.Equal("v8", result.Version.VersionEtag);
        var versionReads = storage.Container.ReceivedCalls().Where(call =>
            call.GetMethodInfo().Name == "ReadItemAsync"
            && call.GetMethodInfo().GetGenericArguments().Contains(typeof(ClusterVersionEntity))).ToArray();
        Assert.Equal(4, versionReads.Length);
        Assert.Equal(new string?[] { null, "0:8", null, "0:10" }, versionReads.Select(call =>
            Assert.IsType<ItemRequestOptions>(call.GetArguments()[2]).SessionToken));
        Assert.All(versionReads, call =>
        {
            Assert.Equal(Partition, call.GetArguments()[1]);
            Assert.Equal(ConsistencyLevel.Session, Assert.IsType<ItemRequestOptions>(call.GetArguments()[2]).ConsistencyLevel);
        });
        if (pointRead)
        {
            var reads = storage.Container.ReceivedCalls().Where(call =>
                call.GetMethodInfo().Name == "ReadItemAsync"
                && call.GetMethodInfo().GetGenericArguments().Contains(typeof(SiloEntity))).ToArray();
            Assert.Equal(new[] { "0:7", "0:9" }, reads.Select(call =>
                Assert.IsType<ItemRequestOptions>(call.GetArguments()[2]).SessionToken));
        }
    }

    [Fact]
    public async Task ReadAllCarriesSessionAcrossEmptyPagesInImmutableIdOrder()
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetVersion();
        storage.SetPages(Page("0:8", "page2", Silo()), Page("0:9", "page3"), Page("0:10", null, Silo(2)));

        var result = await storage.Table.ReadAllAsync(Token);

        Assert.Equal(new[] { 1, 2 }, result.Members.Select(row => row.Item1.SiloAddress.Generation));
        Assert.Equal(7, result.Version.Version);
        var queries = storage.Container.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "GetItemQueryIterator").ToArray();
        Assert.Equal(new string?[] { null, "page2", "page3" }, queries.Select(call => call.GetArguments()[1]));
        Assert.Equal(new[] { "0:7", "0:8", "0:9" }, queries.Select(call =>
            Assert.IsType<QueryRequestOptions>(call.GetArguments()[2]).SessionToken));
        Assert.All(queries, call =>
        {
            var query = Assert.IsType<QueryDefinition>(call.GetArguments()[0]);
            Assert.Equal("SELECT * FROM c WHERE c.EntityType = @entityType ORDER BY c.id", query.QueryText);
            Assert.Equal(nameof(SiloEntity), Assert.Single(query.GetQueryParameters()).Value);
            var options = Assert.IsType<QueryRequestOptions>(call.GetArguments()[2]);
            Assert.Equal(Partition, options.PartitionKey);
            Assert.Equal(ConsistencyLevel.Session, options.ConsistencyLevel);
        });
        storage.AssertVersionRead("0:10");
    }

    [Fact]
    public async Task ReadAllFailsWhenVersionNeverStabilizes()
    {
        using var storage = new CosmosMembershipTestStorage();
        var reads = 0;
        storage.Container.ReadItemAsync<ClusterVersionEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(_ => Version(++reads, $"v{reads}", $"0:{reads}"));
        storage.SetPages(Enumerable.Range(0, 5).Select(_ => Page("0:10", null, Silo())).ToArray());

        var exception = await Assert.ThrowsAsync<WrappedException>(() => storage.Table.ReadAllAsync(Token));

        Assert.Contains("after 5 attempts", exception.Message);
        Assert.Equal(10, reads);
    }

    [Fact]
    public async Task MissingRowReturnsEmptyMembershipWithSurvivingVersion()
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetVersion();
        storage.Container.ReadItemAsync<SiloEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(Failure(HttpStatusCode.NotFound)));

        var result = await storage.Table.ReadRowAsync(Entry().SiloAddress, Token);

        Assert.Empty(result.Members);
        Assert.Equal(7, result.Version.Version);
        Assert.Equal("v7", result.Version.VersionEtag);
        storage.AssertVersionRead("0:9");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipWritesConditionBothRowsAndPreserveMaximumHeartbeat(bool update)
    {
        using var storage = new CosmosMembershipTestStorage();
        var current = Silo();
        current.IAmAliveTime = DateTime.UnixEpoch.AddHours(2);
        storage.SetSilo(current);
        var batch = storage.SetBatch(HttpStatusCode.OK, HttpStatusCode.OK);
        var entry = Entry();
        var version = new TableVersion(8, "v7");

        var result = update
            ? await storage.Table.UpdateRowAsync(entry, "s1", version, Token)
            : await storage.Table.InsertRowAsync(entry, version, Token);

        Assert.True(result);
        storage.Container.Received(1).CreateTransactionalBatch(Partition);
        batch.Received(1).ReplaceItem(
            "ClusterVersion", Arg.Is<ClusterVersionEntity>(value => value.ClusterVersion == 8 && value.ClusterId == "cluster"),
            Arg.Is<TransactionalBatchItemRequestOptions>(options => options.IfMatchEtag == "v7"));
        if (update)
        {
            batch.Received(1).ReplaceItem(
                current.Id, Arg.Is<SiloEntity>(value => value.IAmAliveTime == current.IAmAliveTime && value.Status == (int)entry.Status),
                Arg.Is<TransactionalBatchItemRequestOptions>(options => options.IfMatchEtag == "s1"));
        }
        else
        {
            batch.Received(1).CreateItem(Arg.Is<SiloEntity>(value =>
                value.Id == current.Id && value.ClusterId == "cluster" && value.IAmAliveTime == entry.IAmAliveTime));
        }

        await batch.Received(1).ExecuteAsync(Token);
        Assert.Equal(DateTime.UnixEpoch.AddHours(1), entry.IAmAliveTime);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.PreconditionFailed)]
    [InlineData(true, HttpStatusCode.PreconditionFailed)]
    [InlineData(false, HttpStatusCode.Conflict)]
    public async Task MembershipWriteContentionReturnsFalse(bool update, HttpStatusCode status)
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetSilo(Silo());
        storage.SetBatch(HttpStatusCode.FailedDependency, status);

        var result = update
            ? await storage.Table.UpdateRowAsync(Entry(), "s1", new TableVersion(8, "v7"), Token)
            : await storage.Table.InsertRowAsync(Entry(), new TableVersion(8, "v7"), Token);

        Assert.False(result);
    }

    [Fact]
    public async Task UpdateWithStaleRowEtagDoesNotSubmitBatch()
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetSilo(Silo());

        Assert.False(await storage.Table.UpdateRowAsync(Entry(), "stale", new TableVersion(8, "v7"), Token));

        storage.Container.DidNotReceive().CreateTransactionalBatch(Arg.Any<PartitionKey>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateOfRetiredRowReturnsFalseWithSurvivingVersion(bool deletionRacesBatch)
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetVersion();
        if (deletionRacesBatch)
        {
            storage.SetSilo(Silo());
            storage.SetBatch(HttpStatusCode.FailedDependency, HttpStatusCode.NotFound);
        }
        else
        {
            storage.Container.ReadItemAsync<SiloEntity>(
                "", default, null, Token)
                .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(Failure(HttpStatusCode.NotFound)));
        }

        Assert.False(await storage.Table.UpdateRowAsync(Entry(), "s1", new TableVersion(8, "v7"), Token));

        storage.AssertVersionRead("0:9");
    }

    [Theory]
    [InlineData(false, HttpStatusCode.NotFound)]
    [InlineData(true, HttpStatusCode.NotFound)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task MembershipBatchInfrastructureFailuresRemainVisible(bool update, HttpStatusCode status)
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetSilo(Silo());
        storage.SetBatch(status, HttpStatusCode.FailedDependency);

        var exception = await Assert.ThrowsAsync<WrappedException>(() => update
            ? storage.Table.UpdateRowAsync(Entry(), "s1", new TableVersion(8, "v7"), Token)
            : storage.Table.InsertRowAsync(Entry(), new TableVersion(8, "v7"), Token));

        Assert.Contains("native batch failure", exception.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatAfterCompactionPreservesAbsenceAndVersion(bool deletionRacesReplace)
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetVersion();
        if (deletionRacesReplace)
        {
            storage.SetSilo(Silo());
            storage.Container.ReplaceItemAsync(
                new SiloEntity(), "", null, null, Token)
                .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(Failure(HttpStatusCode.NotFound)));
        }
        else
        {
            storage.Container.ReadItemAsync<SiloEntity>(
                "", default, null, Token)
                .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(Failure(HttpStatusCode.NotFound)));
        }

        await storage.Table.UpdateIAmAliveAsync(Entry(), Token);

        Assert.Equal(deletionRacesReplace ? 3 : 2, storage.Container.ReceivedCalls().Count());
        Assert.DoesNotContain(storage.Container.ReceivedCalls(), call =>
            call.GetMethodInfo().Name is "CreateItemAsync" or "UpsertItemAsync" or "CreateTransactionalBatch");
        storage.AssertVersionRead("0:9");
    }

    [Fact]
    public async Task HeartbeatRetriesConcurrentMembershipUpdateWithoutOverwritingIt()
    {
        using var storage = new CosmosMembershipTestStorage();
        var first = Silo();
        var concurrent = Silo(status: SiloStatus.Dead);
        concurrent.ETag = "s2";
        concurrent.SuspectingSilos.Add(Entry(2).SiloAddress.ToParsableString());
        concurrent.SuspectingTimes.Add(LogFormatter.PrintDate(DateTime.UnixEpoch.AddMinutes(30)));
        storage.Container.ReadItemAsync<SiloEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(Item(Clone(first)), Item(Clone(concurrent)));
        storage.Container.ReplaceItemAsync(
            new SiloEntity(), "", null, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(Failure(HttpStatusCode.PreconditionFailed)), Task.FromResult(Item(concurrent)));

        await storage.Table.UpdateIAmAliveAsync(Entry(), Token);

        var replacement = Assert.Single(storage.Container.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == "ReplaceItemAsync"
            && ((ItemRequestOptions)call.GetArguments()[3]!).IfMatchEtag == "s2");
        var value = Assert.IsType<SiloEntity>(replacement.GetArguments()[0]);
        Assert.Equal((int)SiloStatus.Dead, value.Status);
        Assert.Equal(DateTime.UnixEpoch.AddHours(1), value.IAmAliveTime);
        Assert.Equal(concurrent.SuspectingTimes, value.SuspectingTimes);
        Assert.Equal(concurrent.SuspectingSilos, value.SuspectingSilos);
        Assert.Equal(first.Id, replacement.GetArguments()[1]);
        Assert.Equal(Partition, replacement.GetArguments()[2]);
        Assert.Equal(Token, replacement.GetArguments()[4]);
        Assert.Equal(4, storage.Container.ReceivedCalls().Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OlderOrEqualHeartbeatPreservesStoredRow(int storedHours)
    {
        using var storage = new CosmosMembershipTestStorage();
        var silo = Silo();
        silo.IAmAliveTime = DateTime.UnixEpoch.AddHours(storedHours);
        storage.SetSilo(silo);

        await storage.Table.UpdateIAmAliveAsync(Entry(), Token);

        Assert.Equal("ReadItemAsync", Assert.Single(storage.Container.ReceivedCalls()).GetMethodInfo().Name);
        Assert.Equal(DateTime.UnixEpoch.AddHours(storedHours), silo.IAmAliveTime);
    }

    [Fact]
    public async Task CleanupUsesDeadStatusAndMaximumTimestampWithExclusiveUtcCutoff()
    {
        using var storage = new CosmosMembershipTestStorage();
        var cutoff = DateTimeOffset.UnixEpoch.AddDays(1).ToOffset(TimeSpan.FromHours(5));
        var expired = Silo(status: SiloStatus.Dead);
        expired.SuspectingTimes.Add(LogFormatter.PrintDate(cutoff.UtcDateTime.AddTicks(-1)));
        var recentStart = Silo(2, SiloStatus.Dead);
        recentStart.StartTime = cutoff;
        var recentHeartbeat = Silo(3, SiloStatus.Dead);
        recentHeartbeat.IAmAliveTime = cutoff;
        var recentVote = Silo(4, SiloStatus.Dead);
        recentVote.SuspectingTimes.Add(LogFormatter.PrintDate(cutoff.UtcDateTime));
        recentVote.SuspectingTimes.Add(LogFormatter.PrintDate(DateTime.UnixEpoch));
        storage.SetPages(Page("0:8", null, expired, recentStart, recentHeartbeat, recentVote));
        storage.Container.DeleteItemAsync<SiloEntity>(
            "", default, null, Token).ReturnsForAnyArgs(Item(expired));

        await storage.Table.CleanupDefunctSiloEntriesAsync(cutoff, Token);

        var queryCall = Assert.Single(storage.Container.ReceivedCalls(), call => call.GetMethodInfo().Name == "GetItemQueryIterator");
        var query = Assert.IsType<QueryDefinition>(queryCall.GetArguments()[0]);
        Assert.Equal("SELECT * FROM c WHERE c.EntityType = @entityType AND c.Status = @status ORDER BY c.id", query.QueryText);
        Assert.Contains(query.GetQueryParameters(), parameter => parameter.Name == "@status" && (int)parameter.Value == (int)SiloStatus.Dead);
        storage.AssertConditionalDelete(expired);
        Assert.Equal(2, storage.Container.ReceivedCalls().Count());
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task CleanupHonorsCapturedEtagAndConcurrentRetirement(HttpStatusCode status)
    {
        using var storage = new CosmosMembershipTestStorage();
        var dead = Silo(status: SiloStatus.Dead);
        storage.SetVersion(int.MaxValue);
        storage.SetPages(Page("0:8", null, dead));
        storage.Container.DeleteItemAsync<SiloEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(Failure(status)));

        await storage.Table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch.AddDays(1), Token);

        storage.AssertConditionalDelete(dead);
        Assert.Equal(status == HttpStatusCode.NotFound ? 3 : 2, storage.Container.ReceivedCalls().Count());
        storage.Container.DidNotReceive().CreateTransactionalBatch(Arg.Any<PartitionKey>());
    }

    [Fact]
    public async Task CleanupDeletesAllCandidatesWithoutVersionTransactions()
    {
        using var storage = new CosmosMembershipTestStorage();
        var dead = Enumerable.Range(1, 101).Select(index => Silo(index, SiloStatus.Dead)).ToArray();
        storage.SetPages(Page("0:8", "next", dead[..100]), Page("0:9", null, dead[100]));
        storage.Container.DeleteItemAsync<SiloEntity>(
            "", default, null, Token).ReturnsForAnyArgs(Item(dead[0]));

        await storage.Table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch.AddDays(1), Token);

        foreach (var silo in dead)
        {
            storage.AssertConditionalDelete(silo);
        }

        Assert.Equal(102, storage.Container.ReceivedCalls().Count());
    }

    [Fact]
    public async Task ScopedDeletionBatchesRowsAndDeletesVersionLast()
    {
        using var storage = new CosmosMembershipTestStorage();
        var silos = Enumerable.Range(1, 101).Select(index => Silo(index)).ToArray();
        storage.SetPages(Page("0:8", null, silos));
        var batches = new List<TransactionalBatch>();
        storage.Container.CreateTransactionalBatch(Arg.Any<PartitionKey>()).Returns(_ =>
        {
            var batch = Substitute.For<TransactionalBatch>();
            batch.DeleteItem(Arg.Any<string>(), Arg.Any<TransactionalBatchItemRequestOptions>()).Returns(batch);
            var response = BatchResponse(HttpStatusCode.OK);
            batch.ExecuteAsync(Token).Returns(response);
            batches.Add(batch);
            return batch;
        });
        storage.Container.DeleteItemAsync<ClusterVersionEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(_ =>
            {
                Assert.Equal(2, batches.Count);
                Assert.All(batches, batch => Assert.Contains(batch.ReceivedCalls(), call => call.GetMethodInfo().Name == "ExecuteAsync"));
                return Version(7, "v7", "0:7");
            });

        await storage.Table.DeleteMembershipTableEntriesAsync("cluster", Token);

        Assert.Equal(new[] { 100, 1 }, batches.Select(batch =>
            batch.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "DeleteItem")));
        storage.Container.Received(2).CreateTransactionalBatch(Partition);
        var deletion = Assert.Single(storage.Container.ReceivedCalls(), call => call.GetMethodInfo().Name == "DeleteItemAsync");
        Assert.Equal(typeof(ClusterVersionEntity), Assert.Single(deletion.GetMethodInfo().GetGenericArguments()));
        Assert.Equal("ClusterVersion", deletion.GetArguments()[0]);
        Assert.Equal(Partition, deletion.GetArguments()[1]);
        Assert.Equal(Token, deletion.GetArguments()[3]);
    }

    [Fact]
    public async Task ScopedDeletionRejectsDifferentClusterBeforeStorageAccess()
    {
        using var storage = new CosmosMembershipTestStorage();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => storage.Table.DeleteMembershipTableEntriesAsync("different", Token));

        Assert.Equal("clusterId", exception.ParamName);
        Assert.Empty(storage.Container.ReceivedCalls());
    }

    [Theory]
    [InlineData("Heartbeat", HttpStatusCode.NotFound, 0)]
    [InlineData("Heartbeat", HttpStatusCode.NotFound, 1002)]
    [InlineData("Heartbeat", HttpStatusCode.Forbidden, 0)]
    [InlineData("Heartbeat", HttpStatusCode.ServiceUnavailable, 0)]
    [InlineData("ReadRow", HttpStatusCode.NotFound, 0)]
    [InlineData("ReadRow", HttpStatusCode.NotFound, 1002)]
    [InlineData("ReadAll", HttpStatusCode.NotFound, 0)]
    [InlineData("Update", HttpStatusCode.NotFound, 0)]
    [InlineData("Cleanup", HttpStatusCode.NotFound, 0)]
    [InlineData("Cleanup", HttpStatusCode.Forbidden, 0)]
    public async Task MissingHistoryAndInfrastructureFailuresRemainVisible(string operation, HttpStatusCode status, int substatus)
    {
        using var storage = new CosmosMembershipTestStorage();
        var failure = Failure(status, substatus);
        storage.Container.ReadItemAsync<SiloEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(failure));
        storage.Container.ReadItemAsync<ClusterVersionEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<ClusterVersionEntity>>(failure));
        storage.SetPages(Page("0:8", null, Silo(status: SiloStatus.Dead)));
        storage.Container.DeleteItemAsync<SiloEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(failure));

        var exception = await Assert.ThrowsAsync<WrappedException>(() => operation switch
        {
            "Heartbeat" => storage.Table.UpdateIAmAliveAsync(Entry(), Token),
            "ReadRow" => storage.Table.ReadRowAsync(Entry().SiloAddress, Token),
            "ReadAll" => storage.Table.ReadAllAsync(Token),
            "Update" => storage.Table.UpdateRowAsync(Entry(), "s1", new TableVersion(8, "v7"), Token),
            "Cleanup" => storage.Table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch.AddDays(1), Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Contains("storage failure", exception.ToString());
        Assert.DoesNotContain(storage.Container.ReceivedCalls(), call =>
            call.GetMethodInfo().Name is "CreateItemAsync" or "UpsertItemAsync" or "CreateTransactionalBatch");
    }

    [Fact]
    public async Task NativeReadCancellationRetainsToken()
    {
        using var storage = new CosmosMembershipTestStorage();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        storage.Container.ReadItemAsync<ClusterVersionEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(call =>
            {
                Assert.Equal(cancellation.Token, call.Arg<CancellationToken>());
                cancellation.Cancel();
                return Task.FromCanceled<ItemResponse<ClusterVersionEntity>>(cancellation.Token);
            });

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.Table.ReadAllAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Single(storage.Container.ReceivedCalls());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ScopedDeletionFailurePreservesVersionAndRemainsVisible(HttpStatusCode status)
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetPages(Page("0:8", null, Silo()));
        storage.SetBatch(status);

        var exception = await Assert.ThrowsAsync<WrappedException>(() => storage.Table.DeleteMembershipTableEntriesAsync("cluster", Token));

        Assert.Contains("native batch failure", exception.Message);
        Assert.DoesNotContain(storage.Container.ReceivedCalls(), call => call.GetMethodInfo().Name == "DeleteItemAsync");
    }

    [Theory]
    [InlineData("Heartbeat")]
    [InlineData("ReadRow")]
    [InlineData("Update")]
    public async Task SessionUnavailableRemainsVisibleWithSurvivingVersion(string operation)
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetVersion();
        storage.Container.ReadItemAsync<SiloEntity>("", default, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(Failure(HttpStatusCode.NotFound, 1002)));

        var exception = await Assert.ThrowsAsync<WrappedException>(() => operation switch
        {
            "Heartbeat" => storage.Table.UpdateIAmAliveAsync(Entry(), Token),
            "ReadRow" => storage.Table.ReadRowAsync(Entry().SiloAddress, Token),
            "Update" => storage.Table.UpdateRowAsync(Entry(), "s1", new TableVersion(8, "v7"), Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Contains("storage failure", exception.Message);
        Assert.Equal(operation == "ReadRow" ? 2 : 1, storage.Container.ReceivedCalls().Count());
    }

    [Fact]
    public async Task HeartbeatTransportFailureRemainsVisible()
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.Container.ReadItemAsync<SiloEntity>("", default, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(new HttpRequestException("transport unavailable")));

        var exception = await Assert.ThrowsAsync<WrappedException>(() => storage.Table.UpdateIAmAliveAsync(Entry(), Token));

        Assert.Contains("transport unavailable", exception.Message);
        Assert.Single(storage.Container.ReceivedCalls());
    }

    [Fact]
    public async Task ConcurrentInitializationRetainsExistingVersion()
    {
        using var storage = new CosmosMembershipTestStorage();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var client = new TrackingCosmosClient { Container = storage.Container };
        storage.Container.ReadItemAsync<ClusterVersionEntity>("", default, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<ClusterVersionEntity>>(Failure(HttpStatusCode.NotFound)));
        storage.Container.CreateItemAsync(new ClusterVersionEntity(), null, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<ClusterVersionEntity>>(Failure(HttpStatusCode.Conflict)));
        var options = new CosmosClusteringOptions { DatabaseName = "database", ContainerName = "container" };
        options.ConfigureCosmosClient(_ => new ValueTask<CosmosClient>(client));
        var table = CreateTable(services, options);

        await table.InitializeMembershipTableAsync(true, Token);

        var creation = Assert.Single(storage.Container.ReceivedCalls(), call => call.GetMethodInfo().Name == "CreateItemAsync");
        var version = Assert.IsType<ClusterVersionEntity>(creation.GetArguments()[0]);
        Assert.Equal("ClusterVersion", version.Id);
        Assert.Equal("cluster", version.ClusterId);
        Assert.Equal(0, version.ClusterVersion);
        Assert.Equal(Partition, creation.GetArguments()[1]);
        Assert.Equal(Token, creation.GetArguments()[3]);
        Assert.Equal(2, storage.Container.ReceivedCalls().Count());
        Assert.Equal(0, client.DisposeCalls);
    }

    private static CosmosMembershipTable CreateTable(IServiceProvider services, CosmosClusteringOptions options)
        => new(
            NullLoggerFactory.Instance,
            services,
            Options.Create(options),
            Options.Create(new ClusterOptions { ClusterId = "cluster" }));

    private sealed class TrackingCosmosClient : CosmosClient
    {
        public Container? Container { get; init; }
        public Database? Database { get; init; }
        public InvalidOperationException? ContainerFailure { get; set; }
        public int ContainerCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public override Container GetContainer(string databaseId, string containerId)
        {
            Assert.Equal("database", databaseId);
            Assert.Equal("container", containerId);
            ContainerCalls++;
            return Container ?? throw ContainerFailure ?? new InvalidOperationException("Unexpected container access.");
        }

        public override Database GetDatabase(string id)
        {
            Assert.Equal("database", id);
            return Database ?? throw new InvalidOperationException("Unexpected database access.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
            }
        }
    }
}
