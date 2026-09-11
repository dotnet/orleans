using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Clustering.Cosmos;
using Orleans.Configuration;
using Orleans.Runtime;

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

    private static CosmosMembershipTable CreateTable(IServiceProvider services, CosmosClusteringOptions options)
        => new(
            NullLoggerFactory.Instance,
            services,
            Options.Create(options),
            Options.Create(new ClusterOptions { ClusterId = "cluster" }));

    private sealed class TrackingCosmosClient : CosmosClient
    {
        public Database? Database { get; init; }
        public InvalidOperationException? ContainerFailure { get; set; }
        public int ContainerCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public override Container GetContainer(string databaseId, string containerId)
        {
            Assert.Equal("database", databaseId);
            Assert.Equal("container", containerId);
            ContainerCalls++;
            throw ContainerFailure ?? new InvalidOperationException("Unexpected container access.");
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
