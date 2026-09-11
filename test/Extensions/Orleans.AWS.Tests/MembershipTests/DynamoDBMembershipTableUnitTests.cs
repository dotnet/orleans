using System.Globalization;
using System.Net;
using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace AWSUtils.Tests.MembershipTests
{
    [TestCategory("Membership"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [TestSuite("BVT")]
    [TestProvider("DynamoDB")]
    [TestArea("Membership")]
    public class DynamoDBMembershipTableUnitTests
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
            var table = new DynamoDBMembershipTable(
                NullLoggerFactory.Instance,
                Options.Create(new DynamoDBClusteringOptions()),
                Options.Create(new ClusterOptions { ClusterId = "cluster" }));
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchDeletesRunConcurrentlyAndForwardCancellation(bool cleanup)
        {
            using var client = new BatchDeleteClient();
            var table = CreateTable(client);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            var pending = DeleteEntries(table, cleanup, cancellation.Token);
            try
            {
                Assert.Equal(3, client.Requests.Count);
                Assert.Equal(new[] { 25, 25, 1 }, client.Requests.Select(request => request.RequestItems["membership"].Count));
                Assert.All(client.Tokens, token => Assert.Equal(cancellation.Token, token));
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                client.CompleteAll();
                await pending;
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchCancellationWaitsForAlreadyStartedDeletes(bool cleanup)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var client = new BatchDeleteClient { OnFirstBatch = cancellation.Cancel };
            var table = CreateTable(client);

            var pending = DeleteEntries(table, cleanup, cancellation.Token);
            try
            {
                Assert.Single(client.Requests);
                Assert.Equal(cancellation.Token, Assert.Single(client.Tokens));
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                client.CompleteAll();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.Equal(cancellation.Token, exception.CancellationToken);
            }
        }

        private static Task DeleteEntries(DynamoDBMembershipTable table, bool cleanup, CancellationToken cancellationToken) =>
            cleanup
                ? table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, cancellationToken)
                : table.DeleteMembershipTableEntriesAsync("cluster", cancellationToken);

        private static DynamoDBMembershipTable CreateTable(BatchDeleteClient client)
        {
            var table = new DynamoDBMembershipTable(
                NullLoggerFactory.Instance,
                Options.Create(new DynamoDBClusteringOptions { TableName = "membership" }),
                Options.Create(new ClusterOptions { ClusterId = "cluster" }));
            var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, "http://localhost");
            var clientField = typeof(DynamoDBStorage).GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsAssignableFrom<IDisposable>(clientField.GetValue(storage)).Dispose();
            clientField.SetValue(storage, client);
            typeof(DynamoDBMembershipTable).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, storage);
            return table;
        }

        private sealed class BatchDeleteClient() : AmazonDynamoDBClient(
            new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
        {
            private readonly List<TaskCompletionSource<BatchWriteItemResponse>> _completions = [];
            private bool _released;

            public Action? OnFirstBatch { get; init; }
            public List<BatchWriteItemRequest> Requests { get; } = [];
            public List<CancellationToken> Tokens { get; } = [];

            public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new QueryResponse
                {
                    Items = Enumerable.Range(0, 51).Select(i => new SiloInstanceRecord
                    {
                        DeploymentId = "cluster",
                        SiloIdentity = $"silo-{i}",
                        Status = (int)SiloStatus.Dead,
                        IAmAliveTime = "2026-01-01 00:00:00.000 GMT",
                    }.GetFields(includeKeys: true)).ToList(),
                    LastEvaluatedKey = [],
                });
            }

            public override Task<BatchWriteItemResponse> BatchWriteItemAsync(BatchWriteItemRequest request, CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                Tokens.Add(cancellationToken);
                var completion = new TaskCompletionSource<BatchWriteItemResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                _completions.Add(completion);
                if (_released)
                {
                    completion.SetResult(new BatchWriteItemResponse());
                }

                if (Requests.Count == 1)
                {
                    OnFirstBatch?.Invoke();
                }

                return completion.Task;
            }

            public void CompleteAll()
            {
                _released = true;
                foreach (var completion in _completions.ToArray())
                {
                    completion.TrySetResult(new BatchWriteItemResponse());
                }
            }
        }

        [Fact]
        public void SiloIsDefunct_ParsesPersistedTimestampUsingInvariantCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                var record = new SiloInstanceRecord
                {
                    IAmAliveTime = "2026-09-03 20:00:00.000 GMT",
                    Status = (int)SiloStatus.Dead
                };
                var cutoff = new DateTimeOffset(2026, 9, 3, 21, 0, 0, TimeSpan.Zero);

                Assert.True(DynamoDBMembershipTable.SiloIsDefunct(record, cutoff));
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }
    }
}
