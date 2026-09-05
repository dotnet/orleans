using System.Collections.Concurrent;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Streams;
using TestExtensions;
using Tester.AzureUtils;
using Xunit;

namespace ServiceBus.Tests.CheckpointerTests;

[TestSuite("Functional")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("AzureStorage"), TestCategory("Streaming")]
public sealed class AzureTableStreamQueueCheckpointerAzuriteTests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;
    private static readonly DateTime UpdateTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoCheckpointers_InitialInsertRace_PreservesMonotonicCheckpoint(bool higherWinsInsert)
    {
        await using var fixture = new Fixture();
        var lowRequests = new TableRequests(fixture.TableName, holdInsert: true);
        var highRequests = new TableRequests(fixture.TableName, holdInsert: true);
        var low = await fixture.CreateCheckpointer(lowRequests);
        var high = await fixture.CreateCheckpointer(highRequests);
        Assert.Equal(string.Empty, await low.Load(TestCancellation));
        Assert.Equal(string.Empty, await high.Load(TestCancellation));

        low.Update("9", UpdateTime, TestCancellation);
        high.Update("10", UpdateTime, TestCancellation);
        try
        {
            await Task.WhenAll(
                lowRequests.InsertStarted.Task.WaitAsync(TestCancellation),
                highRequests.InsertStarted.Task.WaitAsync(TestCancellation));
            var winnerRequests = higherWinsInsert ? highRequests : lowRequests;
            var loserRequests = higherWinsInsert ? lowRequests : highRequests;
            var winner = higherWinsInsert ? high : low;
            var loser = higherWinsInsert ? low : high;

            winnerRequests.ReleaseInsert.TrySetResult();
            await winner.FlushAsync(TestCancellation);
            loserRequests.ReleaseInsert.TrySetResult();
            await loser.FlushAsync(TestCancellation);

            Assert.Equal(204, Assert.Single(winnerRequests.Inserts).Status);
            Assert.Equal(409, Assert.Single(loserRequests.Inserts).Status);
            if (higherWinsInsert)
            {
                Assert.Empty(loserRequests.Updates);
            }
            else
            {
                var retry = Assert.Single(loserRequests.Updates);
                Assert.Equal(204, retry.Status);
                Assert.Equal(Assert.Single(winnerRequests.Inserts).ETag, retry.IfMatch);
            }

            await fixture.AssertCheckpoint("10");
            var reconciled = await loser.Store.Load(TestCancellation);
            Assert.Equal("10", reconciled.Checkpoint);
            Assert.False(string.IsNullOrEmpty(reconciled.Version));
            Assert.NotEqual("*", reconciled.Version);
        }
        finally
        {
            lowRequests.ReleaseInsert.TrySetResult();
            highRequests.ReleaseInsert.TrySetResult();
            await Task.WhenAll(low.FlushAsync(TestCancellation), high.FlushAsync(TestCancellation));
        }
    }

    [Fact]
    public async Task TwoCheckpointers_StaleETagReloadsAndRetriesHigherNumericCheckpoint()
    {
        await using var fixture = new Fixture();
        var winner = await fixture.CreateCheckpointer();
        Assert.Equal(string.Empty, await winner.Load(TestCancellation));
        await Persist(winner, "8");
        var initial = await fixture.ReadEntity();
        var staleRequests = new TableRequests(fixture.TableName);
        var stale = await fixture.CreateCheckpointer(staleRequests);
        Assert.Equal("8", await stale.Load(TestCancellation));
        await Persist(winner, "10");
        var current = await fixture.ReadEntity();
        Assert.NotEqual(initial.ETag, current.ETag);

        await Persist(stale, "9");

        var conflict = Assert.Single(staleRequests.Updates);
        Assert.Equal(412, conflict.Status);
        Assert.Equal(initial.ETag.ToString(), conflict.IfMatch);
        Assert.Contains(staleRequests.Reads, request => request.Status == 200 && request.ETag == current.ETag.ToString());
        await fixture.AssertCheckpoint("10");

        await Persist(stale, "11");

        Assert.Collection(staleRequests.Updates,
            request => Assert.Equal(412, request.Status),
            request =>
            {
                Assert.Equal(204, request.Status);
                Assert.Equal(current.ETag.ToString(), request.IfMatch);
            });
        await fixture.AssertCheckpoint("11");
    }

    [Fact]
    public async Task TwoCheckpointers_StaleETagRetriesPendingHigherCheckpointWithinSameFlush()
    {
        await using var fixture = new Fixture();
        var first = await fixture.CreateCheckpointer();
        await first.Load(TestCancellation);
        await Persist(first, "8");
        var requests = new TableRequests(fixture.TableName);
        var second = await fixture.CreateCheckpointer(requests);
        Assert.Equal("8", await second.Load(TestCancellation));
        await Persist(first, "9");
        var current = await fixture.ReadEntity();

        await Persist(second, "10");

        Assert.Collection(requests.Updates,
            request => Assert.Equal(412, request.Status),
            request =>
            {
                Assert.Equal(204, request.Status);
                Assert.Equal(current.ETag.ToString(), request.IfMatch);
            });
        await fixture.AssertCheckpoint("10");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoCheckpointers_DeleteRecreateInvalidatesCachedETagAndRecovers(bool recreateBeforeUpdate)
    {
        await using var fixture = new Fixture();
        var oldRequests = new TableRequests(fixture.TableName);
        var old = await fixture.CreateCheckpointer(oldRequests);
        await old.Load(TestCancellation);
        await Persist(old, "8");
        var initial = await fixture.ReadEntity();
        await fixture.Table.DeleteEntityAsync(initial.PartitionKey, initial.RowKey, initial.ETag, TestCancellation);
        if (recreateBeforeUpdate)
        {
            var replacement = await fixture.CreateCheckpointer();
            Assert.Equal(string.Empty, await replacement.Load(TestCancellation));
            await Persist(replacement, "10");
            Assert.NotEqual(initial.ETag, (await fixture.ReadEntity()).ETag);
        }

        await Persist(old, "9");

        var conflict = Assert.Single(oldRequests.Updates);
        Assert.Equal(recreateBeforeUpdate ? 412 : 404, conflict.Status);
        Assert.Equal(initial.ETag.ToString(), conflict.IfMatch);
        Assert.Equal(recreateBeforeUpdate ? 1 : 2, oldRequests.Inserts.Count());
        await fixture.AssertCheckpoint(recreateBeforeUpdate ? "10" : "9");

        await Persist(old, "11");

        Assert.Equal(204, oldRequests.Updates.Last().Status);
        Assert.NotEqual(initial.ETag.ToString(), oldRequests.Updates.Last().IfMatch);
        Assert.NotEqual("*", oldRequests.Updates.Last().IfMatch);
        await fixture.AssertCheckpoint("11");
    }

    private static async Task Persist(IStreamQueueCheckpointer<string> checkpointer, string checkpoint)
    {
        checkpointer.Update(checkpoint, UpdateTime, TestCancellation);
        await checkpointer.FlushAsync(TestCancellation);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Provider = "etag-provider";
        private const string Service = "etag-service";
        private const string Partition = "shard-1";
        private readonly TableServiceClient _serviceClient;

        public Fixture()
        {
            if (TestDefaultConfiguration.UseAadAuthentication
                ? TestDefaultConfiguration.TableEndpoint is null
                : string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
            {
                throw Xunit.Sdk.SkipException.ForSkip("Azure Table tests require the existing AzureStorage test connection configuration.");
            }

            _serviceClient = AzureStorageOperationOptionsExtensions.GetTableServiceClient();
            Table = _serviceClient.GetTableClient(TableName);
        }

        public string TableName { get; } = $"Checkpoint{Guid.NewGuid():N}";
        public TableClient Table { get; }

        public async Task<AzureTableStreamQueueCheckpointer> CreateCheckpointer(TableRequests? requests = null)
        {
            var serviceClient = _serviceClient;
            if (requests is not null)
            {
                var clientOptions = new TableClientOptions();
                clientOptions.Retry.MaxRetries = 2;
                clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
                clientOptions.AddPolicy(requests, HttpPipelinePosition.PerCall);
                serviceClient = TestDefaultConfiguration.UseAadAuthentication
                    ? new TableServiceClient(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential, clientOptions)
                    : new TableServiceClient(TestDefaultConfiguration.DataConnectionString, clientOptions);
            }

            return Assert.IsType<AzureTableStreamQueueCheckpointer>(
                await AzureTableStreamQueueCheckpointer.Create(
                    new AzureTableStreamCheckpointerOptions
                    {
                        TableName = TableName,
                        TableServiceClient = serviceClient,
                        PersistInterval = TimeSpan.FromMinutes(1),
                        CheckpointComparer = StreamCheckpointComparers.Numeric,
                    },
                    Provider, Partition, Service, NullLoggerFactory.Instance, TestCancellation));
        }

        public async Task<StreamQueueCheckpointEntity> ReadEntity()
        {
            var key = StreamQueueCheckpointEntity.Create(string.Empty, Provider, Service, Partition);
            var response = await Table.GetEntityAsync<StreamQueueCheckpointEntity>(
                key.PartitionKey, key.RowKey, cancellationToken: TestCancellation);
            return response.Value;
        }

        public async Task AssertCheckpoint(string expected)
        {
            var fresh = await CreateCheckpointer();
            Assert.Equal(expected, await fresh.Load(TestCancellation));
            Assert.True(fresh.CheckpointExists);
            var entities = new List<StreamQueueCheckpointEntity>();
            await foreach (var entity in Table.QueryAsync<StreamQueueCheckpointEntity>(cancellationToken: TestCancellation))
            {
                entities.Add(entity);
            }

            Assert.Equal(expected, Assert.Single(entities).Offset);
        }

        public async ValueTask DisposeAsync()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _serviceClient.DeleteTableAsync(TableName, cancellation.Token);
        }
    }

    private sealed record TableExchange(string Method, int Status, string? IfMatch, string? ETag);

    private sealed class TableRequests(string tableName, bool holdInsert = false) : HttpPipelinePolicy
    {
        private readonly ConcurrentQueue<TableExchange> _exchanges = new();

        public TaskCompletionSource InsertStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseInsert { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IEnumerable<TableExchange> Inserts => _exchanges.Where(exchange => exchange.Method == "POST");
        public IEnumerable<TableExchange> Updates => _exchanges.Where(exchange => exchange.Method == "PUT");
        public IEnumerable<TableExchange> Reads => _exchanges.Where(exchange => exchange.Method == "GET");

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
            => throw new InvalidOperationException("Checkpoint persistence uses asynchronous Azure Table requests.");

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            var path = message.Request.Uri.ToUri().AbsolutePath;
            var isEntityRequest = path.EndsWith($"/{tableName}", StringComparison.Ordinal)
                || path.Contains($"/{tableName}(", StringComparison.Ordinal);
            var method = message.Request.Method.ToString();
            if (isEntityRequest && method == "POST" && holdInsert)
            {
                InsertStarted.TrySetResult();
                await ReleaseInsert.Task.WaitAsync(message.CancellationToken);
            }

            await ProcessNextAsync(message, pipeline);
            if (isEntityRequest)
            {
                message.Request.Headers.TryGetValue("If-Match", out var ifMatch);
                message.Response.Headers.TryGetValue("ETag", out var eTag);
                _exchanges.Enqueue(new(method, message.Response.Status, ifMatch, eTag));
            }
        }
    }
}
