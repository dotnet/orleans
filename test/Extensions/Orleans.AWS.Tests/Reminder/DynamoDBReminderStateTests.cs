using System.Globalization;
using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Reminders.DynamoDB;
using Orleans.Runtime;
using Xunit;

namespace AWSUtils.Tests.RemindersTest;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Reminders")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("BVT")]
public sealed class DynamoDBReminderStateTests
{
    [Fact]
    public async Task LegacyStartupAcceptsMaximumLengthTableNameWithoutProbingAnInvalidDerivedName()
    {
        using var client = new RequestClient();
        var table = CreateTable(client, DynamoDBReminderTableMode.Legacy, new string('a', 255));

        await table.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, client.ReadCount);
    }

    [Fact]
    public async Task LegacyStartupExplainsRequiredMigrationStatePermissionAndPreservesAccessDenial()
    {
        var denied = new AmazonDynamoDBException("Access denied.") { ErrorCode = "AccessDeniedException" };
        using var client = new RequestClient { Read = (_, _) => Task.FromException<GetItemResponse>(denied) };
        var table = CreateTable(client, DynamoDBReminderTableMode.Legacy);

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(
            () => table.StartAsync(TestContext.Current.CancellationToken));

        Assert.Same(denied, exception.InnerException);
        Assert.Contains("dynamodb:GetItem", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reminders-v2", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.ReadCount);
    }

    [Fact]
    public async Task LegacyStartupAcceptsAnUnprovisionedMigrationTable()
    {
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromException<GetItemResponse>(new ResourceNotFoundException("Missing table.")),
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.Legacy);

        await table.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, client.ReadCount);
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.Legacy, 1)]
    [InlineData(DynamoDBReminderTableMode.Migrate, 1)]
    [InlineData(DynamoDBReminderTableMode.Migrate, 2)]
    public async Task StartupStateReadsForwardCancellation(DynamoDBReminderTableMode mode, int blockedRead)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var client = new RequestClient
        {
            Read = async (_, token) =>
            {
                if (++reads == blockedRead)
                {
                    entered.SetResult(token);
                    await release.Task.WaitAsync(token);
                }

                return new();
            },
        };
        var table = CreateTable(client, mode);
        var startup = table.StartAsync(cancellation.Token);
        try
        {
            var forwarded = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(cancellation.Token, forwarded);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(blockedRead, client.ReadCount);
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetResult();
            await table.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.V2)]
    [InlineData(DynamoDBReminderTableMode.V2Only)]
    [InlineData(DynamoDBReminderTableMode.Rollback)]
    public async Task MissingStateRejectsReadsAndMutations(DynamoDBReminderTableMode mode)
    {
        using var client = new RequestClient { Read = (_, _) => Task.FromResult(new GetItemResponse()) };
        var table = CreateTable(client, mode);
        AttachStorage(table, client);
        var entry = Entry();

        await Assert.ThrowsAsync<InvalidOperationException>(() => table.ReadRow(entry.GrainId, entry.ReminderName));
        await Assert.ThrowsAsync<InvalidOperationException>(() => table.UpsertRow(entry));
        await Assert.ThrowsAsync<InvalidOperationException>(() => table.RemoveRow(entry.GrainId, entry.ReminderName, "1"));

        Assert.Equal(0, client.WriteCount);
    }

    [Fact]
    public async Task RollbackRechecksMissingStateAfterAcquiringTheLease()
    {
        var reads = 0;
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse { Item = ++reads < 3 ? StateItem("Cutover") : null }),
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.Rollback);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => table.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("requires existing migration metadata", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, client.ReadCount);
        Assert.Equal(2, client.WriteCount);
        Assert.Equal(1, client.DeleteCount);
    }

    [Theory]
    [InlineData("ServiceId")]
    [InlineData("GrainReference")]
    [InlineData("ReminderName")]
    public async Task V2PointReadRejectsConflictingStoredIdentity(string attribute)
    {
        var entry = Entry();
        entry.GrainId = GrainId.Create("state-test", new string('g', 400));
        Assert.Contains("#H", DynamoDBReminderTable.GetV2GrainPrefix(entry.GrainId.GetUniformHashCode(), entry.GrainId), StringComparison.Ordinal);
        entry.ReminderName = new string('r', 700);
        var item = RowItem(entry);
        item[attribute] = new("conflicting-identity");
        using var client = new RequestClient
        {
            Read = (request, _) => Task.FromResult(new GetItemResponse
            {
                Item = request.Key["SortKey"].S == "STATE" ? StateItem("Cutover") : item,
            }),
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.V2);
        AttachStorage(table, client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => table.ReadRow(entry.GrainId, entry.ReminderName));

        Assert.Contains("conflicting stored identity", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, client.ReadCount);
        Assert.Equal(0, client.WriteCount);
    }

    [Fact]
    public async Task V2GrainReadFiltersConflictingPrefixIdentitiesAcrossPages()
    {
        var entry = Entry();
        entry.GrainId = GrainId.Create("state-test", new string('g', 400));
        var wrongGrain = RowItem(entry);
        wrongGrain["GrainReference"] = new(GrainId.Create("state-test", new string('h', 400)).ToString());
        var wrongService = RowItem(entry);
        wrongService["ServiceId"] = new("another-service");
        var pages = 0;
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse { Item = StateItem("Cutover") }),
            Query = request =>
            {
                Assert.True(request.ConsistentRead);
                Assert.Equal(
                    DynamoDBReminderTable.GetV2GrainPrefix(entry.GrainId.GetUniformHashCode(), entry.GrainId),
                    request.ExpressionAttributeValues[":prefix"].S);
                if (++pages == 1)
                {
                    Assert.True(request.ExclusiveStartKey is null or { Count: 0 });
                    return new()
                    {
                        Items = [wrongGrain, wrongService],
                        LastEvaluatedKey = new() { ["SortKey"] = new("next-page") },
                    };
                }

                Assert.Equal("next-page", request.ExclusiveStartKey["SortKey"].S);
                return new() { Items = [RowItem(entry)] };
            },
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.V2);
        AttachStorage(table, client);

        var result = Assert.Single((await table.ReadRows(entry.GrainId)).Reminders);

        Assert.Equal(entry.GrainId, result.GrainId);
        Assert.Equal(entry.ReminderName, result.ReminderName);
        Assert.Equal(entry.StartAt, result.StartAt);
        Assert.Equal(2, pages);
    }

    [Theory]
    [InlineData(1u, 10u)]
    [InlineData(10u, 1u)]
    [InlineData(0u, 0u)]
    public async Task V2RangeReadRejectsConflictingServiceAcrossPages(uint begin, uint end)
    {
        var entry = Entry();
        var item = RowItem(entry);
        var pages = 0;
        using var client = new RequestClient
        {
            Read = (_, _) => Task.FromResult(new GetItemResponse { Item = StateItem("Cutover") }),
            Query = request =>
            {
                Assert.True(request.ConsistentRead);
                if (request.ExpressionAttributeValues[":partition"].S != DynamoDBReminderTable.GetV2PartitionKey("service", 0)
                    || request.ExpressionAttributeValues[":lower"].S != DynamoDBReminderTable.GetV2RangeBounds(begin + 1, uint.MaxValue).Lower)
                {
                    return new() { Items = [] };
                }

                if (++pages == 1)
                {
                    Assert.True(request.ExclusiveStartKey is null or { Count: 0 });
                    return new()
                    {
                        Items = [],
                        LastEvaluatedKey = new() { ["SortKey"] = new("next-page") },
                    };
                }

                Assert.Equal("next-page", request.ExclusiveStartKey["SortKey"].S);
                return new() { Items = [item] };
            },
        };
        var table = CreateTable(client, DynamoDBReminderTableMode.V2);
        AttachStorage(table, client);

        var result = Assert.Single((await table.ReadRows(begin, end)).Reminders);
        Assert.Equal(entry.GrainId, result.GrainId);
        Assert.Equal(entry.ReminderName, result.ReminderName);
        Assert.Equal(2, pages);

        pages = 0;
        item["ServiceId"] = new("another-service");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => table.ReadRows(begin, end));

        Assert.Contains("conflicting stored service identity", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'service'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, pages);
        Assert.Equal(0, client.WriteCount);
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.Migrate, null)]
    [InlineData(DynamoDBReminderTableMode.V2, null)]
    [InlineData(DynamoDBReminderTableMode.V2Only, null)]
    [InlineData(DynamoDBReminderTableMode.Migrate, "Ready")]
    [InlineData(DynamoDBReminderTableMode.V2, "RolledBack")]
    [InlineData(DynamoDBReminderTableMode.V2Only, "Cutover")]
    public async Task ObservedRetirementRejectsMissingOrRegressedState(DynamoDBReminderTableMode mode, string? nextState)
    {
        string? state = "Retired";
        using var client = new RequestClient
        {
            Read = (request, _) => Task.FromResult(new GetItemResponse
            {
                Item = request.Key.ContainsKey("SortKey") && request.Key["SortKey"].S == "STATE" && state is not null
                    ? new()
                    {
                        ["MigrationStatus"] = new(state),
                        ["SourceCount"] = new() { N = "0" },
                        ["TargetCount"] = new() { N = "0" },
                    }
                    : null,
            }),
        };
        var table = CreateTable(client, mode);
        AttachStorage(table, client);
        var entry = Entry();
        Assert.Null(await table.ReadRow(entry.GrainId, entry.ReminderName));
        state = nextState;

        await Assert.ThrowsAsync<InvalidOperationException>(() => table.UpsertRow(entry));

        Assert.Equal(0, client.WriteCount);
    }

    [Theory]
    [InlineData("2026-08-28T01:02:03.0000000Z", "en-US", DateTimeKind.Utc)]
    [InlineData("28/08/2026 01:02:03", "en-GB", DateTimeKind.Unspecified)]
    public async Task RowReadsPreserveRoundtripUtcAndLegacyCultureFormats(string timestamp, string culture, DateTimeKind kind)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var entry = Entry();
            using var client = new RequestClient
            {
                Read = (request, _) => Task.FromResult(new GetItemResponse
                {
                    Item = request.TableName == "reminders-v2" ? null : new()
                    {
                        ["ETag"] = new() { N = "1" },
                        ["GrainReference"] = new(entry.GrainId.ToString()),
                        ["ReminderName"] = new(entry.ReminderName),
                        ["Period"] = new("00:01:00"),
                        ["StartTime"] = new(timestamp),
                    },
                }),
            };
            var table = CreateTable(client, DynamoDBReminderTableMode.Legacy);
            await table.StartAsync(TestContext.Current.CancellationToken);

            var actual = Assert.IsType<ReminderEntry>(await table.ReadRow(entry.GrainId, entry.ReminderName));

            Assert.Equal(new DateTime(2026, 8, 28, 1, 2, 3, kind), actual.StartAt);
            Assert.Equal(kind, actual.StartAt.Kind);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static ReminderEntry Entry() => new()
    {
        GrainId = GrainId.Create("state-test", "grain"),
        ReminderName = "reminder",
        StartAt = new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc),
        Period = TimeSpan.FromMinutes(1),
    };

    private static Dictionary<string, AttributeValue> RowItem(ReminderEntry entry) => new()
    {
        ["ServiceId"] = new("service"),
        ["GrainReference"] = new(entry.GrainId.ToString()),
        ["ReminderName"] = new(entry.ReminderName),
        ["ETag"] = new() { N = "1" },
        ["Period"] = new(entry.Period.ToString()),
        ["StartTime"] = new(entry.StartAt.ToString("O", CultureInfo.InvariantCulture)),
    };

    private static Dictionary<string, AttributeValue> StateItem(string state) => new()
    {
        ["MigrationStatus"] = new(state),
        ["SourceCount"] = new() { N = "0" },
        ["TargetCount"] = new() { N = "0" },
    };

    private static DynamoDBReminderTable CreateTable(RequestClient client, DynamoDBReminderTableMode mode, string tableName = "reminders")
        => new(
            NullLoggerFactory.Instance,
            Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" }),
            Options.Create(new DynamoDBReminderStorageOptions
            {
                Service = "http://localhost",
                TableName = tableName,
                TableMode = mode,
                CreateIfNotExists = false,
                UpdateIfExists = false,
            }),
            null, null, null,
            new() { ConfigureStorage = storage => AttachClient(storage, client) });

    private static void AttachStorage(DynamoDBReminderTable table, RequestClient client)
    {
        var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, "http://localhost");
        AttachClient(storage, client);
        typeof(DynamoDBReminderTable).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, storage);
    }

    private static void AttachClient(DynamoDBStorage storage, RequestClient client)
    {
        var field = typeof(DynamoDBStorage).GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsAssignableFrom<IDisposable>(field.GetValue(storage)).Dispose();
        field.SetValue(storage, client);
    }

    private sealed class RequestClient() : AmazonDynamoDBClient(
        new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
    {
        public Func<GetItemRequest, CancellationToken, Task<GetItemResponse>> Read { get; init; }
            = (_, _) => throw new InvalidOperationException("Unexpected read.");

        public Func<QueryRequest, QueryResponse> Query { get; init; }
            = _ => throw new InvalidOperationException("Unexpected query.");

        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            Assert.True(request.ConsistentRead);
            return Read(request, cancellationToken);
        }

        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return Task.FromResult(new PutItemResponse());
        }

        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Query(request));

        public override Task<DeleteItemResponse> DeleteItemAsync(DeleteItemRequest request, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return Task.FromResult(new DeleteItemResponse());
        }

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            throw new InvalidOperationException("Unexpected transaction.");
        }
    }
}
