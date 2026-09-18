using System.Net;
using System.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Extensions;
using Orleans.Clustering.Cosmos;
using Orleans.Clustering.Cosmos.Models;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Tester.Cosmos.Clustering;

internal sealed class CosmosMembershipTestStorage : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    public CosmosMembershipTestStorage()
    {
        Container.ReturnsForAll<Task<ItemResponse<SiloEntity>>>(
            Task.FromException<ItemResponse<SiloEntity>>(new InvalidOperationException("Unexpected silo operation.")));
        Container.ReturnsForAll<Task<ItemResponse<ClusterVersionEntity>>>(
            Task.FromException<ItemResponse<ClusterVersionEntity>>(new InvalidOperationException("Unexpected version operation.")));
        Container.ReturnsForAll<FeedIterator<SiloEntity>>(new PageIterator(new()));
        Table = new CosmosMembershipTable(
            NullLoggerFactory.Instance, _services,
            Options.Create(new CosmosClusteringOptions()),
            Options.Create(new ClusterOptions { ClusterId = "cluster" }));
        typeof(CosmosMembershipTable).GetField("_container", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Table, Container);
    }

    public Container Container { get; } = Substitute.For<Container>();
    public CosmosMembershipTable Table { get; }
    public static PartitionKey Partition => new("cluster");
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _services.Dispose();

    public void SetVersion(int version = 7, string etag = "v7", string session = "0:7")
        => Container.ReadItemAsync<ClusterVersionEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(Version(version, etag, session));

    public void SetSilo(SiloEntity silo)
        => Container.ReadItemAsync<SiloEntity>(
            "", default, null, Token)
            .ReturnsForAnyArgs(_ => Item(Clone(silo), "0:8"));

    public void SetPages(params FeedResponse<SiloEntity>[] pages)
    {
        var pending = new Queue<FeedResponse<SiloEntity>>(pages);
        Container.GetItemQueryIterator<SiloEntity>(
            new QueryDefinition("SELECT * FROM c"), null, null)
            .ReturnsForAnyArgs(_ => new PageIterator(pending));
    }

    public TransactionalBatch SetBatch(params HttpStatusCode[] statuses)
    {
        var response = BatchResponse(statuses);
        var batch = Substitute.For<TransactionalBatch>();
        batch.ReplaceItem(
            Arg.Any<string>(), Arg.Any<ClusterVersionEntity>(), Arg.Any<TransactionalBatchItemRequestOptions>()).Returns(batch);
        batch.ReplaceItem(
            Arg.Any<string>(), Arg.Any<SiloEntity>(), Arg.Any<TransactionalBatchItemRequestOptions>()).Returns(batch);
        batch.CreateItem(Arg.Any<SiloEntity>(), Arg.Any<TransactionalBatchItemRequestOptions>()).Returns(batch);
        batch.DeleteItem(Arg.Any<string>(), Arg.Any<TransactionalBatchItemRequestOptions>()).Returns(batch);
        batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(response);
        Container.CreateTransactionalBatch(Arg.Any<PartitionKey>()).Returns(batch);
        return batch;
    }

    public static TransactionalBatchResponse BatchResponse(params HttpStatusCode[] statuses)
    {
        var results = statuses.Select(status =>
        {
            var result = Substitute.For<TransactionalBatchOperationResult>();
            result.StatusCode.Returns(status);
            return result;
        }).ToList();
        var response = Substitute.For<TransactionalBatchResponse>();
        var success = statuses.All(status => (int)status is >= 200 and < 300);
        response.IsSuccessStatusCode.Returns(success);
        response.StatusCode.Returns(success ? HttpStatusCode.OK : statuses.First(status => status != HttpStatusCode.FailedDependency));
        response.ErrorMessage.Returns("native batch failure");
        response.Headers.Returns(Headers("0:9"));
        response.Count.Returns(results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            response[i].Returns(results[i]);
        }

        response.GetEnumerator().Returns(_ => results.GetEnumerator());
        return response;
    }

    public static SiloEntity Silo(int generation = 1, SiloStatus status = SiloStatus.Active)
        => new()
        {
            Id = $"127.0.0.1-11111-{generation}",
            ClusterId = "cluster",
            ETag = $"s{generation}",
            Address = "127.0.0.1",
            Port = 11111,
            Generation = generation,
            Hostname = "host",
            SiloName = $"silo-{generation}",
            Status = (int)status,
            StartTime = DateTime.UnixEpoch,
            IAmAliveTime = DateTime.UnixEpoch
        };

    public static MembershipEntry Entry(int generation = 1)
        => new()
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, generation),
            Status = SiloStatus.Active,
            HostName = "host",
            SiloName = $"silo-{generation}",
            StartTime = DateTime.UnixEpoch,
            IAmAliveTime = DateTime.UnixEpoch.AddHours(1)
        };

    public static SiloEntity Clone(SiloEntity silo)
        => Newtonsoft.Json.JsonConvert.DeserializeObject<SiloEntity>(Newtonsoft.Json.JsonConvert.SerializeObject(silo))!;

    public static ItemResponse<ClusterVersionEntity> Version(int version, string etag, string session)
        => Item(new ClusterVersionEntity { Id = "ClusterVersion", ClusterId = "cluster", ClusterVersion = version, ETag = etag }, session);

    public static ItemResponse<T> Item<T>(T value, string session = "0:7") where T : BaseEntity
        => new ResourceResponse<T>(value, session);

    public static FeedResponse<SiloEntity> Page(string session, string? continuation, params SiloEntity[] silos)
        => new PageResponse(silos, session, continuation);

    public static CosmosException Failure(HttpStatusCode status, int substatus = 0)
    {
        var exception = new CosmosException("storage failure", status, substatus, "activity", 0);
        exception.Headers["x-ms-session-token"] = "0:9";
        return exception;
    }

    private static Headers Headers(string session) => new() { ["x-ms-session-token"] = session };

    public void AssertVersionRead(string session)
    {
        var call = Assert.Single(Container.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == "ReadItemAsync"
            && call.GetMethodInfo().GetGenericArguments().Contains(typeof(ClusterVersionEntity))
            && ((ItemRequestOptions)call.GetArguments()[2]!).SessionToken == session);
        Assert.Equal("ClusterVersion", call.GetArguments()[0]);
        Assert.Equal(Partition, call.GetArguments()[1]);
        Assert.Equal(ConsistencyLevel.Session, Assert.IsType<ItemRequestOptions>(call.GetArguments()[2]).ConsistencyLevel);
        Assert.Equal(Token, call.GetArguments()[3]);
    }

    public void AssertConditionalDelete(SiloEntity silo)
    {
        var call = Assert.Single(Container.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == "DeleteItemAsync" && Equals(call.GetArguments()[0], silo.Id));
        Assert.Equal(typeof(SiloEntity), Assert.Single(call.GetMethodInfo().GetGenericArguments()));
        Assert.Equal(Partition, call.GetArguments()[1]);
        Assert.Equal(silo.ETag, Assert.IsType<ItemRequestOptions>(call.GetArguments()[2]).IfMatchEtag);
        Assert.Equal(Token, call.GetArguments()[3]);
    }

    private sealed class ResourceResponse<T>(T value, string session) : ItemResponse<T> where T : BaseEntity
    {
        public override T Resource => value;
        public override string ETag => value.ETag!;
        public override Headers Headers { get; } = CosmosMembershipTestStorage.Headers(session);
        public override HttpStatusCode StatusCode => HttpStatusCode.OK;
    }

    private sealed class PageResponse(SiloEntity[] silos, string session, string? continuation) : FeedResponse<SiloEntity>
    {
        public override int Count => silos.Length;
        public override string ContinuationToken => continuation!;
        public override string IndexMetrics => "";
        public override Headers Headers { get; } = CosmosMembershipTestStorage.Headers(session);
        public override IEnumerable<SiloEntity> Resource => silos;
        public override HttpStatusCode StatusCode => HttpStatusCode.OK;
        public override double RequestCharge => 0;
        public override string ActivityId => "";
        public override string ETag => "";
        public override CosmosDiagnostics Diagnostics => null!;
        public override IEnumerator<SiloEntity> GetEnumerator() => ((IEnumerable<SiloEntity>)silos).GetEnumerator();
    }

    private sealed class PageIterator(Queue<FeedResponse<SiloEntity>> pages) : FeedIterator<SiloEntity>
    {
        public override bool HasMoreResults => pages.Count > 0;

        public override Task<FeedResponse<SiloEntity>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Token, cancellationToken);
            return Task.FromResult(pages.Dequeue());
        }
    }
}
