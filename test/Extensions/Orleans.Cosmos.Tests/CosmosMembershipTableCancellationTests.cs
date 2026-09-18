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
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task ResourceInitializationUsesOneNativeCreateCall(HttpStatusCode containerStatus)
    {
        using var storage = new CosmosMembershipTestStorage();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var client = Substitute.For<CosmosClient>();
        var database = Substitute.For<Database>();
        var databaseResponse = Substitute.For<DatabaseResponse>();
        databaseResponse.Database.Returns(database);
        databaseResponse.StatusCode.Returns(HttpStatusCode.Created);
        var containerResponse = Substitute.For<ContainerResponse>();
        containerResponse.StatusCode.Returns(containerStatus);
        client.CreateDatabaseIfNotExistsAsync(
            Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<RequestOptions>(), Token).Returns(databaseResponse);
        database.CreateContainerIfNotExistsAsync(
            Arg.Any<ContainerProperties>(), Arg.Any<ThroughputProperties>(), Arg.Any<RequestOptions>(), Token).Returns(containerResponse);
        client.GetContainer("database", "container").Returns(storage.Container);
        storage.SetVersion();
        var options = new CosmosClusteringOptions
        {
            DatabaseName = "database",
            ContainerName = "container",
            IsResourceCreationEnabled = true
        };
        options.ConfigureCosmosClient(_ => new ValueTask<CosmosClient>(client));
        var table = CreateTable(services, options);

        await table.InitializeMembershipTableAsync(true, Token);

        await client.Received(1).CreateDatabaseIfNotExistsAsync("database", options.DatabaseThroughput, cancellationToken: Token);
        await database.Received(1).CreateContainerIfNotExistsAsync(
            Arg.Is<ContainerProperties>(properties => properties.Id == "container" && properties.PartitionKeyPath == "/ClusterId"),
            options.ContainerThroughputProperties, cancellationToken: Token);
        Assert.Equal("CreateContainerIfNotExistsAsync", Assert.Single(database.ReceivedCalls()).GetMethodInfo().Name);
        storage.AssertVersionReads();
    }

    [Theory]
    [InlineData("Initialize", 1, 0)]
    [InlineData("ReadRow", 3, 0)]
    [InlineData("ReadAll", 2, 1)]
    [InlineData("Cleanup", 0, 1)]
    [InlineData("Delete", 2, 1)]
    public async Task MembershipOperationsRequestStrongConsistency(string operation, int expectedItemReads, int expectedQueries)
    {
        using var storage = new CosmosMembershipTestStorage();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var client = new TrackingCosmosClient { Container = storage.Container };
        var options = new CosmosClusteringOptions { DatabaseName = "database", ContainerName = "container" };
        options.ConfigureCosmosClient(_ => new ValueTask<CosmosClient>(client));
        var silo = Silo(status: SiloStatus.Dead);
        storage.SetVersion();
        storage.SetSilo(silo);
        storage.SetPages(Page("0:8", null, silo));
        storage.SetBatch(HttpStatusCode.OK, HttpStatusCode.OK);
        storage.Container.DeleteItemAsync<SiloEntity>("", default, null, Token).ReturnsForAnyArgs(Item(silo));
        storage.Container.DeleteItemAsync<ClusterVersionEntity>("", default, null, Token).ReturnsForAnyArgs(Version(7, "v7", "0:7"));

        await (operation switch
        {
            "Initialize" => CreateTable(services, options).InitializeMembershipTableAsync(true, Token),
            "ReadRow" => storage.Table.ReadRowAsync(Entry().SiloAddress, Token),
            "ReadAll" => storage.Table.ReadAllAsync(Token),
            "Cleanup" => storage.Table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch.AddDays(1), Token),
            "Delete" => storage.Table.DeleteMembershipTableEntriesAsync("cluster", Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        var reads = storage.Container.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "ReadItemAsync").ToArray();
        Assert.Equal(expectedItemReads, reads.Length);
        Assert.All(reads, call =>
        {
            Assert.Equal(Partition, call.GetArguments()[1]);
            var request = Assert.IsType<ItemRequestOptions>(call.GetArguments()[2]);
            Assert.Equal(ConsistencyLevel.Strong, request.ConsistencyLevel);
            Assert.Null(request.SessionToken);
            Assert.Equal(Token, call.GetArguments()[3]);
        });
        var queries = storage.Container.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "GetItemQueryIterator").ToArray();
        Assert.Equal(expectedQueries, queries.Length);
        Assert.All(queries, call =>
        {
            var request = Assert.IsType<QueryRequestOptions>(call.GetArguments()[2]);
            Assert.Equal(Partition, request.PartitionKey);
            Assert.Equal(ConsistencyLevel.Strong, request.ConsistencyLevel);
            Assert.Null(request.SessionToken);
        });
    }

    [Fact]
    public async Task StrongConsistencyRejectionRemainsVisible()
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.Container.ReadItemAsync<ClusterVersionEntity>("", default, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<ClusterVersionEntity>>(Failure(HttpStatusCode.BadRequest)));

        var exception = await Assert.ThrowsAsync<WrappedException>(() => storage.Table.ReadAllAsync(Token));

        Assert.Contains("storage failure", exception.Message);
        storage.AssertVersionReads();
        Assert.Single(storage.Container.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrongMembershipReadsRetryChangedVersion(bool pointRead)
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
        Assert.Equal("v8", Assert.Single(result.Members).Item2);
        storage.AssertVersionReads(4);
        if (pointRead)
        {
            var reads = storage.Container.ReceivedCalls().Where(call =>
                call.GetMethodInfo().Name == "ReadItemAsync"
                && call.GetMethodInfo().GetGenericArguments().Contains(typeof(SiloEntity))).ToArray();
            Assert.Equal(2, reads.Length);
            Assert.All(reads, call =>
            {
                var options = Assert.IsType<ItemRequestOptions>(call.GetArguments()[2]);
                Assert.Equal(ConsistencyLevel.Strong, options.ConsistencyLevel);
                Assert.Null(options.SessionToken);
            });
        }
    }

    [Fact]
    public async Task ReadAllUsesStrongConsistencyAcrossEmptyPagesInImmutableIdOrder()
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetVersion();
        storage.SetPages(Page("0:8", "page2", Silo()), Page("0:9", "page3"), Page("0:10", null, Silo(2)));

        var result = await storage.Table.ReadAllAsync(Token);

        Assert.Equal(new[] { 1, 2 }, result.Members.Select(row => row.Item1.SiloAddress.Generation));
        Assert.Equal(7, result.Version.Version);
        Assert.All(result.Members, row => Assert.Equal("v7", row.Item2));
        var queryCall = Assert.Single(storage.Container.ReceivedCalls(), call => call.GetMethodInfo().Name == "GetItemQueryIterator");
        Assert.Null(queryCall.GetArguments()[1]);
        Assert.Equal(3, storage.PageReadCount);
        var query = Assert.IsType<QueryDefinition>(queryCall.GetArguments()[0]);
        Assert.Equal("SELECT * FROM c WHERE c.EntityType = @entityType ORDER BY c.id", query.QueryText);
        Assert.Equal(nameof(SiloEntity), Assert.Single(query.GetQueryParameters()).Value);
        var options = Assert.IsType<QueryRequestOptions>(queryCall.GetArguments()[2]);
        Assert.Equal(Partition, options.PartitionKey);
        Assert.Equal(ConsistencyLevel.Strong, options.ConsistencyLevel);
        Assert.Null(options.SessionToken);
        storage.AssertVersionReads(2);
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
        Assert.Equal(5, storage.PageReadCount);
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
        storage.AssertVersionReads(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipWritesUseAtomicCanonicalVersionConditions(bool update)
    {
        using var storage = new CosmosMembershipTestStorage();
        var batch = storage.SetBatch(HttpStatusCode.OK, HttpStatusCode.OK);
        var entry = Entry();
        var version = new TableVersion(8, "v7");

        var result = update
            ? await storage.Table.UpdateRowAsync(entry, "v7", version, Token)
            : await storage.Table.InsertRowAsync(entry, version, Token);

        Assert.True(result);
        storage.Container.Received(1).CreateTransactionalBatch(Partition);
        batch.Received(1).ReplaceItem(
            "ClusterVersion", Arg.Is<ClusterVersionEntity>(value => value.ClusterVersion == 8 && value.ClusterId == "cluster"),
            Arg.Is<TransactionalBatchItemRequestOptions>(options => options.IfMatchEtag == "v7"
                && options.EnableContentResponseOnWrite == false));
        if (update)
        {
            batch.Received(1).ReplaceItem(
                Silo().Id, Arg.Is<SiloEntity>(value => value.IAmAliveTime == entry.IAmAliveTime && value.Status == (int)entry.Status),
                Arg.Is<TransactionalBatchItemRequestOptions>(options => options.IfMatchEtag == null
                    && options.IfNoneMatchEtag == null && options.EnableContentResponseOnWrite == false));
        }
        else
        {
            batch.Received(1).CreateItem(
                Arg.Is<SiloEntity>(value => value.Id == Silo().Id && value.ClusterId == "cluster" && value.IAmAliveTime == entry.IAmAliveTime),
                Arg.Is<TransactionalBatchItemRequestOptions>(options => options.IfMatchEtag == null
                    && options.IfNoneMatchEtag == null && options.EnableContentResponseOnWrite == false));
        }

        await batch.Received(1).ExecuteAsync(Token);
        Assert.Equal(DateTime.UnixEpoch.AddHours(1), entry.IAmAliveTime);
        Assert.Equal("CreateTransactionalBatch", Assert.Single(storage.Container.ReceivedCalls()).GetMethodInfo().Name);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.PreconditionFailed)]
    [InlineData(true, HttpStatusCode.PreconditionFailed)]
    [InlineData(false, HttpStatusCode.Conflict)]
    public async Task MembershipWriteContentionReturnsFalse(bool update, HttpStatusCode status)
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetBatch(status == HttpStatusCode.Conflict
            ? [HttpStatusCode.FailedDependency, status]
            : [status, HttpStatusCode.FailedDependency]);

        var result = update
            ? await storage.Table.UpdateRowAsync(Entry(), "v7", new TableVersion(8, "v7"), Token)
            : await storage.Table.InsertRowAsync(Entry(), new TableVersion(8, "v7"), Token);

        Assert.False(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatPreservesCanonicalTokensForMembershipUpdate(bool pointRead)
    {
        using var storage = new CosmosMembershipTestStorage();
        var silo = Silo();
        storage.SetVersion();
        storage.SetSilo(silo);
        storage.SetPages(Page("0:8", null, Clone(silo)));
        var before = pointRead
            ? await storage.Table.ReadRowAsync(Entry().SiloAddress, Token)
            : await storage.Table.ReadAllAsync(Token);
        var (entry, rowToken) = Assert.Single(before.Members);
        Assert.Equal(before.Version.VersionEtag, rowToken);
        Assert.NotEqual(silo.ETag, rowToken);

        using var response = new ResponseMessage(HttpStatusCode.OK);
        storage.Container.PatchItemStreamAsync(
            Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Any<PatchItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var patch = Assert.IsAssignableFrom<PatchOperation<DateTimeOffset>>(Assert.Single(call.Arg<IReadOnlyList<PatchOperation>>()));
            silo.IAmAliveTime = patch.Value;
            silo.ETag = "heartbeat-physical-etag";
            return response;
        });
        var batch = storage.SetBatch(HttpStatusCode.OK, HttpStatusCode.OK);
        storage.Container.ClearReceivedCalls();

        await storage.Table.UpdateIAmAliveAsync(Entry(), Token);
        entry.Status = SiloStatus.Dead;
        entry.AddSuspector(Entry(2).SiloAddress, DateTime.UnixEpoch.AddHours(2));
        var updated = await storage.Table.UpdateRowAsync(entry, rowToken, before.Version.Next(), Token);

        Assert.True(updated);
        Assert.Equal("heartbeat-physical-etag", silo.ETag);
        Assert.Equal(new[] { "PatchItemStreamAsync", "CreateTransactionalBatch" },
            storage.Container.ReceivedCalls().Select(call => call.GetMethodInfo().Name));
        batch.Received(1).ReplaceItem(
            "ClusterVersion", Arg.Is<ClusterVersionEntity>(version => version.ClusterVersion == 8),
            Arg.Is<TransactionalBatchItemRequestOptions>(options => options.IfMatchEtag == before.Version.VersionEtag));
        batch.Received(1).ReplaceItem(
            silo.Id, Arg.Is<SiloEntity>(value => value.Status == (int)SiloStatus.Dead
                && value.SuspectingSilos.Count == 1 && value.SuspectingTimes.Count == 1
                && value.IAmAliveTime == entry.IAmAliveTime),
            Arg.Is<TransactionalBatchItemRequestOptions>(options => options.IfMatchEtag == null
                && options.IfNoneMatchEtag == null && options.EnableContentResponseOnWrite == false));
        await batch.Received(1).ExecuteAsync(Token);
    }

    [Fact]
    public async Task UpdateWithStaleCanonicalRowTokenDoesNotAccessStorage()
    {
        using var storage = new CosmosMembershipTestStorage();

        Assert.False(await storage.Table.UpdateRowAsync(Entry(), "stale", new TableVersion(8, "v7"), Token));

        Assert.Empty(storage.Container.ReceivedCalls());
    }

    [Fact]
    public async Task UpdateOfMissingRowReturnsFalseWithoutAdditionalRequests()
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetBatch(HttpStatusCode.FailedDependency, HttpStatusCode.NotFound);

        Assert.False(await storage.Table.UpdateRowAsync(Entry(), "v7", new TableVersion(8, "v7"), Token));

        Assert.Equal("CreateTransactionalBatch", Assert.Single(storage.Container.ReceivedCalls()).GetMethodInfo().Name);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.NotFound)]
    [InlineData(true, HttpStatusCode.NotFound)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task MembershipBatchInfrastructureFailuresRemainVisible(bool update, HttpStatusCode status)
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetBatch(status, HttpStatusCode.FailedDependency);

        var exception = await Assert.ThrowsAsync<WrappedException>(() => update
            ? storage.Table.UpdateRowAsync(Entry(), "v7", new TableVersion(8, "v7"), Token)
            : storage.Table.InsertRowAsync(Entry(), new TableVersion(8, "v7"), Token));

        Assert.Contains("native batch failure", exception.ToString());
    }

    [Fact]
    public async Task HeartbeatUsesOneBlindTimestampPatch()
    {
        using var storage = new CosmosMembershipTestStorage();
        using var response = new ResponseMessage(HttpStatusCode.OK);
        storage.Container.PatchItemStreamAsync(
            Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Any<PatchItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(response);
        var entry = Entry();

        await storage.Table.UpdateIAmAliveAsync(entry, Token);

        var call = Assert.Single(storage.Container.ReceivedCalls());
        Assert.Equal("PatchItemStreamAsync", call.GetMethodInfo().Name);
        Assert.Equal(Silo().Id, call.GetArguments()[0]);
        Assert.Equal(Partition, call.GetArguments()[1]);
        var patch = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<PatchOperation>>(call.GetArguments()[2]));
        Assert.Equal(PatchOperationType.Set, patch.OperationType);
        Assert.Equal("/IAmAliveTime", patch.Path);
        Assert.Equal(new DateTimeOffset(entry.IAmAliveTime), Assert.IsAssignableFrom<PatchOperation<DateTimeOffset>>(patch).Value);
        var options = Assert.IsType<PatchItemRequestOptions>(call.GetArguments()[3]);
        Assert.Null(options.IfMatchEtag);
        Assert.Null(options.IfNoneMatchEtag);
        Assert.Null(options.FilterPredicate);
        Assert.False(options.EnableContentResponseOnWrite);
        Assert.Equal(Token, call.GetArguments()[4]);
    }

    [Fact]
    public async Task NativeHeartbeatPatchCancellationRetainsToken()
    {
        using var storage = new CosmosMembershipTestStorage();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        storage.Container.PatchItemStreamAsync(
            Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Any<PatchItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Assert.Equal(cancellation.Token, call.Arg<CancellationToken>());
            cancellation.Cancel();
            return Task.FromCanceled<ResponseMessage>(cancellation.Token);
        });

        var operation = storage.Table.UpdateIAmAliveAsync(Entry(), cancellation.Token);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(operation.IsCanceled);
        Assert.Equal("PatchItemStreamAsync", Assert.Single(storage.Container.ReceivedCalls()).GetMethodInfo().Name);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HeartbeatPatchFailuresRemainVisibleWithoutAdditionalRequests(HttpStatusCode status)
    {
        using var storage = new CosmosMembershipTestStorage();
        using var response = new ResponseMessage(status);
        storage.Container.PatchItemStreamAsync(
            Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Any<PatchItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(response);

        var exception = await Assert.ThrowsAsync<WrappedException>(() => storage.Table.UpdateIAmAliveAsync(Entry(), Token));

        Assert.Contains($"({(int)status})", exception.Message);
        Assert.Equal("PatchItemStreamAsync", Assert.Single(storage.Container.ReceivedCalls()).GetMethodInfo().Name);
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
        storage.SetVersion();
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
        foreach (var silo in silos)
        {
            var rowDeletion = Assert.Single(batches.SelectMany(batch => batch.ReceivedCalls()), call =>
                call.GetMethodInfo().Name == "DeleteItem" && Equals(call.GetArguments()[0], silo.Id));
            Assert.Equal(silo.ETag, Assert.IsType<TransactionalBatchItemRequestOptions>(rowDeletion.GetArguments()[1]).IfMatchEtag);
        }

        var query = Assert.Single(storage.Container.ReceivedCalls(), call => call.GetMethodInfo().Name == "GetItemQueryIterator");
        Assert.Equal(ConsistencyLevel.Strong, Assert.IsType<QueryRequestOptions>(query.GetArguments()[2]).ConsistencyLevel);
        storage.Container.Received(2).CreateTransactionalBatch(Partition);
        var deletion = Assert.Single(storage.Container.ReceivedCalls(), call => call.GetMethodInfo().Name == "DeleteItemAsync");
        Assert.Equal(typeof(ClusterVersionEntity), Assert.Single(deletion.GetMethodInfo().GetGenericArguments()));
        Assert.Equal("ClusterVersion", deletion.GetArguments()[0]);
        Assert.Equal(Partition, deletion.GetArguments()[1]);
        Assert.Equal("v7", Assert.IsType<ItemRequestOptions>(deletion.GetArguments()[2]).IfMatchEtag);
        Assert.Equal(Token, deletion.GetArguments()[3]);
    }

    [Fact]
    public async Task ScopedDeletionUsesStableSnapshotBeforeDeletingRows()
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.Container.ReadItemAsync<ClusterVersionEntity>("", default, null, Token)
            .ReturnsForAnyArgs(Version(7, "v7", "0:7"), Version(8, "v8", "0:9"), Version(8, "v8", "0:9"), Version(8, "v8", "0:10"));
        var current = Silo(2);
        storage.SetPages(Page("0:8", null, Silo()), Page("0:10", null, current));
        var batch = storage.SetBatch(HttpStatusCode.OK);
        storage.Container.DeleteItemAsync<ClusterVersionEntity>("", default, null, Token)
            .ReturnsForAnyArgs(Version(8, "v8", "0:10"));

        await storage.Table.DeleteMembershipTableEntriesAsync("cluster", Token);

        var rowDeletion = Assert.Single(batch.ReceivedCalls(), call => call.GetMethodInfo().Name == "DeleteItem");
        Assert.Equal(current.Id, rowDeletion.GetArguments()[0]);
        Assert.Equal(current.ETag, Assert.IsType<TransactionalBatchItemRequestOptions>(rowDeletion.GetArguments()[1]).IfMatchEtag);
        var versionDeletion = Assert.Single(storage.Container.ReceivedCalls(), call => call.GetMethodInfo().Name == "DeleteItemAsync");
        Assert.Equal("v8", Assert.IsType<ItemRequestOptions>(versionDeletion.GetArguments()[2]).IfMatchEtag);
        Assert.Equal(4, storage.Container.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "ReadItemAsync"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedDeletionPreservesChangedVersionAfterEarlierChunk(bool recreateVersion)
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetVersion();
        var silos = Enumerable.Range(1, 101).Select(index => Silo(index)).ToArray();
        var rows = silos.ToDictionary(silo => silo.Id);
        var newSilo = Silo(102);
        ItemResponse<ClusterVersionEntity>? currentVersion = Version(7, "v7", "0:7");
        storage.SetPages(Page("0:8", null, silos));
        var completedChunks = 0;
        var success = BatchResponse(HttpStatusCode.OK);
        storage.Container.CreateTransactionalBatch(Arg.Any<PartitionKey>()).Returns(_ =>
        {
            var batch = Substitute.For<TransactionalBatch>();
            var deletions = new List<string>();
            batch.DeleteItem(Arg.Any<string>(), Arg.Any<TransactionalBatchItemRequestOptions>()).Returns(call =>
            {
                var id = call.Arg<string>();
                Assert.Equal(rows[id].ETag, call.Arg<TransactionalBatchItemRequestOptions>().IfMatchEtag);
                deletions.Add(id);
                return batch;
            });
            batch.ExecuteAsync(Token).Returns(_ =>
            {
                foreach (var id in deletions)
                {
                    Assert.True(rows.Remove(id));
                }

                if (++completedChunks == 1)
                {
                    currentVersion = Version(recreateVersion ? 0 : 8, "concurrent-version", "0:9");
                    rows.Add(newSilo.Id, newSilo);
                }

                return success;
            });
            return batch;
        });
        storage.Container.DeleteItemAsync<ClusterVersionEntity>("", default, null, Token).ReturnsForAnyArgs(call =>
        {
            Assert.Equal(2, completedChunks);
            if (call.Arg<ItemRequestOptions>().IfMatchEtag != currentVersion!.ETag)
            {
                return Task.FromException<ItemResponse<ClusterVersionEntity>>(Failure(HttpStatusCode.PreconditionFailed));
            }

            var deleted = currentVersion;
            currentVersion = null;
            return Task.FromResult(deleted);
        });

        var exception = await Assert.ThrowsAsync<WrappedException>(() => storage.Table.DeleteMembershipTableEntriesAsync("cluster", Token));

        Assert.Contains("storage failure", exception.Message);
        Assert.NotNull(currentVersion);
        Assert.Equal("concurrent-version", currentVersion.ETag);
        Assert.Equal(recreateVersion ? 0 : 8, currentVersion.Resource.ClusterVersion);
        Assert.Same(newSilo, Assert.Single(rows.Values));
    }

    [Fact]
    public async Task ScopedDeletionPreservesRowChangedBeforeLaterChunk()
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetVersion();
        var silos = Enumerable.Range(1, 101).Select(index => Silo(index)).ToArray();
        var rows = silos.ToDictionary(silo => silo.Id);
        storage.SetPages(Page("0:8", null, silos));
        var completedChunks = 0;
        SiloEntity? concurrentRow = null;
        var success = BatchResponse(HttpStatusCode.OK);
        var conflict = BatchResponse(HttpStatusCode.PreconditionFailed);
        storage.Container.CreateTransactionalBatch(Arg.Any<PartitionKey>()).Returns(_ =>
        {
            var batch = Substitute.For<TransactionalBatch>();
            var deletions = new List<(string Id, string Etag)>();
            batch.DeleteItem(Arg.Any<string>(), Arg.Any<TransactionalBatchItemRequestOptions>()).Returns(call =>
            {
                deletions.Add((call.Arg<string>(), call.Arg<TransactionalBatchItemRequestOptions>().IfMatchEtag));
                return batch;
            });
            batch.ExecuteAsync(Token).Returns(_ =>
            {
                if (deletions.Any(deletion => rows[deletion.Id].ETag != deletion.Etag))
                {
                    return conflict;
                }

                foreach (var deletion in deletions)
                {
                    Assert.True(rows.Remove(deletion.Id));
                }

                completedChunks++;
                var changed = Clone(Assert.Single(rows.Values));
                changed.ETag = "concurrent-heartbeat";
                changed.IAmAliveTime = DateTime.UnixEpoch.AddHours(1);
                rows[changed.Id] = changed;
                concurrentRow = changed;
                return success;
            });
            return batch;
        });

        var exception = await Assert.ThrowsAsync<WrappedException>(() => storage.Table.DeleteMembershipTableEntriesAsync("cluster", Token));

        Assert.Contains("native batch failure", exception.Message);
        Assert.Equal(1, completedChunks);
        var remaining = Assert.Single(rows.Values);
        Assert.Same(concurrentRow, remaining);
        Assert.Equal("concurrent-heartbeat", remaining.ETag);
        Assert.Equal(DateTime.UnixEpoch.AddHours(1), remaining.IAmAliveTime);
        Assert.DoesNotContain(storage.Container.ReceivedCalls(), call => call.GetMethodInfo().Name == "DeleteItemAsync");
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
    [InlineData("ReadRow", HttpStatusCode.NotFound, 0)]
    [InlineData("ReadRow", HttpStatusCode.NotFound, 1002)]
    [InlineData("ReadAll", HttpStatusCode.NotFound, 0)]
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
            "ReadRow" => storage.Table.ReadRowAsync(Entry().SiloAddress, Token),
            "ReadAll" => storage.Table.ReadAllAsync(Token),
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
        storage.SetVersion();
        storage.SetPages(Page("0:8", null, Silo()));
        storage.SetBatch(status);

        var exception = await Assert.ThrowsAsync<WrappedException>(() => storage.Table.DeleteMembershipTableEntriesAsync("cluster", Token));

        Assert.Contains("native batch failure", exception.Message);
        Assert.DoesNotContain(storage.Container.ReceivedCalls(), call => call.GetMethodInfo().Name == "DeleteItemAsync");
    }

    [Theory]
    [InlineData("ReadRow")]
    public async Task SessionUnavailableRemainsVisibleWithSurvivingVersion(string operation)
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.SetVersion();
        storage.Container.ReadItemAsync<SiloEntity>("", default, null, Token)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(Failure(HttpStatusCode.NotFound, 1002)));

        var exception = await Assert.ThrowsAsync<WrappedException>(() => operation switch
        {
            "ReadRow" => storage.Table.ReadRowAsync(Entry().SiloAddress, Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Contains("storage failure", exception.Message);
        Assert.Equal(operation == "ReadRow" ? 2 : 1, storage.Container.ReceivedCalls().Count());
    }

    [Fact]
    public async Task HeartbeatTransportFailureRemainsVisible()
    {
        using var storage = new CosmosMembershipTestStorage();
        storage.Container.PatchItemStreamAsync(
            Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Any<PatchItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ResponseMessage>(new HttpRequestException("transport unavailable")));

        var exception = await Assert.ThrowsAsync<WrappedException>(() => storage.Table.UpdateIAmAliveAsync(Entry(), Token));

        Assert.Contains("transport unavailable", exception.Message);
        Assert.Equal("PatchItemStreamAsync", Assert.Single(storage.Container.ReceivedCalls()).GetMethodInfo().Name);
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
