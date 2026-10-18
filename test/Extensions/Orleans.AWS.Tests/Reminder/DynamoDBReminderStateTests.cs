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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(blockedRead, client.ReadCount);
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetResult();
            await table.StopAsync();
        }
    }

    [Theory]
    [InlineData(DynamoDBReminderTableMode.V2)]
    [InlineData(DynamoDBReminderTableMode.V2Only)]
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

        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }

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

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            throw new InvalidOperationException("Unexpected transaction.");
        }
    }
}
