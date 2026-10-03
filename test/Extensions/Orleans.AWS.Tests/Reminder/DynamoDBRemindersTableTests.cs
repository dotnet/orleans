using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Reminders.DynamoDB;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests;
using UnitTests.RemindersTest;
using UnitTests.TimerTests;
using Xunit;

namespace AWSUtils.Tests.RemindersTest
{
    public sealed class DynamoDBReminderServiceLifecycleFixture : BaseInProcessTestClusterFixture
    {
        private ReminderTestClock? _clock;

        public ReminderTestClock Clock
        {
            get
            {
                EnsurePreconditionsMet();
                return _clock ?? throw new InvalidOperationException("The reminder clock has not been configured.");
            }
        }

        protected override void CheckPreconditionsOrThrow()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
            {
                throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");
            }
        }

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            _clock = builder.AddReminderTestClock();
            builder.ConfigureSilo((_, siloBuilder) =>
                siloBuilder.UseDynamoDBReminderService(options =>
                    options.ParseConnectionString($"Service={AWSTestConstants.DynamoDbService}")));
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                _clock?.Dispose();
            }
        }
    }

    [TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestSuite("Functional")]
    [TestProvider("DynamoDB")]
    [TestArea("Reminders")]
    public sealed class DynamoDBReminderServiceLifecycleTests
        : ReminderServiceLifecycleTestsBase, IClassFixture<DynamoDBReminderServiceLifecycleFixture>
    {
        public DynamoDBReminderServiceLifecycleTests(DynamoDBReminderServiceLifecycleFixture fixture)
            : base(fixture.Clock, fixture.HostedCluster, "DynamoDB")
        {
            fixture.EnsurePreconditionsMet();
        }
    }

    /// <summary>
    /// Tests DynamoDB implementation of the Orleans reminders table for storing and retrieving grain reminders.
    /// </summary>
    [TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestSuite("Functional")]
    [TestProvider("DynamoDB")]
    [TestArea("Reminders")]
    public class DynamoDBRemindersTableTests : ReminderTableTestsBase, IClassFixture<DynamoDBStorageTestsFixture>
    {
        public DynamoDBRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, new LoggerFilterOptions())
        {
        }

        protected override IReminderTable CreateRemindersTable()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");

            var options = new DynamoDBReminderStorageOptions();
            options.ParseConnectionString(this.connectionStringFixture.ConnectionString);

            return new DynamoDBReminderTable(
                this.loggerFactory,
                this.clusterOptions,
                Options.Create(options));
        }

        protected override Task<string> GetConnectionString()
        {
            return Task.FromResult(AWSTestConstants.IsDynamoDbAvailable ? $"Service={AWSTestConstants.DynamoDbService}" : null!);
        }

        [Fact]
        public void RemindersTable_AWS_Init()
        {
        }

        [Fact]
        public async Task RemindersTable_AWS_RemindersRange()
        {
            await RemindersRange(50);
        }

        [Fact]
        public async Task RemindersTable_AWS_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [Fact]
        public async Task RemindersTable_AWS_ReminderSimple()
        {
            await ReminderSimple();
        }

        [Fact]
        public async Task DynamoDBReminderTable_Init_CreatesExpectedCurrentSchema()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");

            const string ServiceId = "phase-1-schema-service";
            var tableName = $"OrleansReminders-{Guid.NewGuid():N}";
            var reminderTable = CreateIsolatedReminderTable(tableName, ServiceId);
            using var client = CreateDynamoDBClient();

            try
            {
                await reminderTable.Init().WaitAsync(TestContext.Current.CancellationToken);

                var response = await client.DescribeTableAsync(
                    new Amazon.DynamoDBv2.Model.DescribeTableRequest { TableName = tableName },
                    TestContext.Current.CancellationToken);
                var description = response.Table;

                Assert.Collection(
                    description.KeySchema,
                    key =>
                    {
                        Assert.Equal("ReminderId", key.AttributeName);
                        Assert.Equal(Amazon.DynamoDBv2.KeyType.HASH, key.KeyType);
                    },
                    key =>
                    {
                        Assert.Equal("GrainHash", key.AttributeName);
                        Assert.Equal(Amazon.DynamoDBv2.KeyType.RANGE, key.KeyType);
                    });

                Assert.Equal(
                    [
                        ("GrainHash", "N"),
                        ("GrainReference", "S"),
                        ("ReminderId", "S"),
                        ("ServiceId", "S"),
                    ],
                    description.AttributeDefinitions
                        .OrderBy(attribute => attribute.AttributeName, StringComparer.Ordinal)
                        .Select(attribute => (attribute.AttributeName, attribute.AttributeType.Value))
                        .ToArray());

                var indexes = description.GlobalSecondaryIndexes
                    .OrderBy(index => index.IndexName, StringComparer.Ordinal)
                    .ToArray();
                Assert.Collection(
                    indexes,
                    index =>
                    {
                        Assert.Equal("ServiceIdGrainReferenceIndex", index.IndexName);
                        Assert.Collection(
                            index.KeySchema,
                            key =>
                            {
                                Assert.Equal("ServiceId", key.AttributeName);
                                Assert.Equal(Amazon.DynamoDBv2.KeyType.HASH, key.KeyType);
                            },
                            key =>
                            {
                                Assert.Equal("GrainReference", key.AttributeName);
                                Assert.Equal(Amazon.DynamoDBv2.KeyType.RANGE, key.KeyType);
                            });
                    },
                    index =>
                    {
                        Assert.Equal("ServiceIdIndex", index.IndexName);
                        Assert.Collection(
                            index.KeySchema,
                            key =>
                            {
                                Assert.Equal("ServiceId", key.AttributeName);
                                Assert.Equal(Amazon.DynamoDBv2.KeyType.HASH, key.KeyType);
                            },
                            key =>
                            {
                                Assert.Equal("GrainHash", key.AttributeName);
                                Assert.Equal(Amazon.DynamoDBv2.KeyType.RANGE, key.KeyType);
                            });
                    });
            }
            finally
            {
                await DeleteTableIfExistsAsync(client, tableName);
            }
        }

        [Fact]
        public async Task DynamoDBReminderTable_UpsertRow_EncodesV1PrimaryKeyExactly()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");

            const string ServiceId = "phase-1-key-service";
            const string ReminderName = "foo/bar\\#b_a_z?";
            var tableName = $"OrleansReminders-{Guid.NewGuid():N}";
            var grainId = Orleans.Runtime.GrainId.Create("phase-1", "deterministic-key");
            var reminderTable = CreateIsolatedReminderTable(tableName, ServiceId);
            using var client = CreateDynamoDBClient();
            var initialized = false;

            try
            {
                await reminderTable.Init().WaitAsync(TestContext.Current.CancellationToken);
                initialized = true;

                var entry = new Orleans.ReminderEntry
                {
                    GrainId = grainId,
                    ReminderName = ReminderName,
                    StartAt = new DateTime(2026, 8, 28, 12, 34, 56, DateTimeKind.Utc),
                    Period = TimeSpan.FromMinutes(17),
                };
                var etag = await reminderTable.UpsertRow(entry).WaitAsync(TestContext.Current.CancellationToken);
                Assert.False(string.IsNullOrEmpty(etag));

                var expectedReminderId = $"{ServiceId}_{grainId}_{ReminderName}";
                var expectedHash = grainId.GetUniformHashCode();
                var response = await client.GetItemAsync(
                    new Amazon.DynamoDBv2.Model.GetItemRequest
                    {
                        TableName = tableName,
                        ConsistentRead = true,
                        Key = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
                        {
                            ["ReminderId"] = new(expectedReminderId),
                            ["GrainHash"] = new() { N = expectedHash.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                        },
                    },
                    TestContext.Current.CancellationToken);

                Assert.NotNull(response.Item);
                Assert.Equal(8, response.Item.Count);
                Assert.Equal(expectedReminderId, response.Item["ReminderId"].S);
                Assert.Equal(expectedHash.ToString(System.Globalization.CultureInfo.InvariantCulture), response.Item["GrainHash"].N);
                Assert.Equal(ServiceId, response.Item["ServiceId"].S);
                Assert.Equal(grainId.ToString(), response.Item["GrainReference"].S);
                Assert.Equal(ReminderName, response.Item["ReminderName"].S);
                Assert.Equal(etag, response.Item["ETag"].N);
            }
            finally
            {
                try
                {
                    if (initialized)
                    {
                        using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await reminderTable.TestOnlyClearTable().WaitAsync(cleanupCancellation.Token);
                    }
                }
                finally
                {
                    await DeleteTableIfExistsAsync(client, tableName);
                }
            }
        }

        [Fact]
        public async Task DynamoDBReminderTable_PointRead_IsImmediatelyConsistent_AfterWriteAndDelete()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");

            const string ServiceId = "phase-2-consistency-service";
            const string ReminderName = "immediate-point-read";
            var tableName = $"OrleansReminders-{Guid.NewGuid():N}";
            var grainId = Orleans.Runtime.GrainId.Create("phase-2", "point-consistency");
            var reminderTable = CreateIsolatedReminderTable(tableName, ServiceId);
            using var client = CreateDynamoDBClient();
            var initializedTables = new List<DynamoDBReminderTable>();

            try
            {
                await reminderTable.Init().WaitAsync(TestContext.Current.CancellationToken);
                initializedTables.Add(reminderTable);

                var entry = new Orleans.ReminderEntry
                {
                    GrainId = grainId,
                    ReminderName = ReminderName,
                    StartAt = new DateTime(2026, 8, 28, 15, 16, 17, DateTimeKind.Utc),
                    Period = TimeSpan.FromMinutes(23),
                };
                var etag = await reminderTable.UpsertRow(entry).WaitAsync(TestContext.Current.CancellationToken);
                Assert.False(string.IsNullOrEmpty(etag));

                var afterWrite = await reminderTable.ReadRow(grainId, ReminderName).WaitAsync(TestContext.Current.CancellationToken);
                AssertReminder(entry, etag, Assert.IsType<Orleans.ReminderEntry>(afterWrite));

                var removed = await reminderTable.RemoveRow(grainId, ReminderName, etag!).WaitAsync(TestContext.Current.CancellationToken);
                Assert.True(removed);

                var afterDelete = await reminderTable.ReadRow(grainId, ReminderName).WaitAsync(TestContext.Current.CancellationToken);
                Assert.Null(afterDelete);
            }
            finally
            {
                await ClearServicesAndDeleteTableAsync(client, tableName, initializedTables);
            }
        }

        [Fact]
        public async Task DynamoDBReminderTable_TwoServicesSharingTable_AreFullyIsolated()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");

            const string ServiceA = "phase-2-service-a";
            const string ServiceB = "phase-2-service-b";
            const string ReminderName = "shared-reminder";
            var tableName = $"OrleansReminders-{Guid.NewGuid():N}";
            var grainId = Orleans.Runtime.GrainId.Create("phase-2", "shared-grain");
            var serviceA = CreateIsolatedReminderTable(tableName, ServiceA);
            var serviceB = CreateIsolatedReminderTable(tableName, ServiceB);
            using var client = CreateDynamoDBClient();
            var initializedTables = new List<DynamoDBReminderTable>();

            try
            {
                await serviceA.Init().WaitAsync(TestContext.Current.CancellationToken);
                initializedTables.Add(serviceA);
                await serviceB.Init().WaitAsync(TestContext.Current.CancellationToken);
                initializedTables.Add(serviceB);

                var entryA = new Orleans.ReminderEntry
                {
                    GrainId = grainId,
                    ReminderName = ReminderName,
                    StartAt = new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc),
                    Period = TimeSpan.FromMinutes(11),
                };
                var entryB = new Orleans.ReminderEntry
                {
                    GrainId = grainId,
                    ReminderName = ReminderName,
                    StartAt = new DateTime(2026, 8, 29, 4, 5, 6, DateTimeKind.Utc),
                    Period = TimeSpan.FromMinutes(37),
                };
                var etagA = await serviceA.UpsertRow(entryA).WaitAsync(TestContext.Current.CancellationToken);
                var etagB = await serviceB.UpsertRow(entryB).WaitAsync(TestContext.Current.CancellationToken);
                Assert.False(string.IsNullOrEmpty(etagA));
                Assert.False(string.IsNullOrEmpty(etagB));

                var pointA = await serviceA.ReadRow(grainId, ReminderName).WaitAsync(TestContext.Current.CancellationToken);
                var pointB = await serviceB.ReadRow(grainId, ReminderName).WaitAsync(TestContext.Current.CancellationToken);
                AssertReminder(entryA, etagA, Assert.IsType<Orleans.ReminderEntry>(pointA));
                AssertReminder(entryB, etagB, Assert.IsType<Orleans.ReminderEntry>(pointB));

                var expectedA = new (Orleans.ReminderEntry Entry, string? ETag)[] { (entryA, etagA) };
                var expectedB = new (Orleans.ReminderEntry Entry, string? ETag)[] { (entryB, etagB) };
                AssertReminderRows(
                    expectedA,
                    await ReadRowsUntilExactlyAsync(
                        () => serviceA.ReadRows(grainId),
                        expectedA,
                        TestContext.Current.CancellationToken));
                AssertReminderRows(
                    expectedB,
                    await ReadRowsUntilExactlyAsync(
                        () => serviceB.ReadRows(grainId),
                        expectedB,
                        TestContext.Current.CancellationToken));
                AssertReminderRows(
                    expectedA,
                    await ReadRowsUntilExactlyAsync(
                        () => serviceA.ReadRows(0, 0),
                        expectedA,
                        TestContext.Current.CancellationToken));
                AssertReminderRows(
                    expectedB,
                    await ReadRowsUntilExactlyAsync(
                        () => serviceB.ReadRows(0, 0),
                        expectedB,
                        TestContext.Current.CancellationToken));

                await serviceA.TestOnlyClearTable().WaitAsync(TestContext.Current.CancellationToken);

                var clearedPointA = await serviceA.ReadRow(grainId, ReminderName).WaitAsync(TestContext.Current.CancellationToken);
                var retainedPointB = await serviceB.ReadRow(grainId, ReminderName).WaitAsync(TestContext.Current.CancellationToken);
                Assert.Null(clearedPointA);
                AssertReminder(entryB, etagB, Assert.IsType<Orleans.ReminderEntry>(retainedPointB));

                var noReminders = Array.Empty<(Orleans.ReminderEntry Entry, string? ETag)>();
                AssertReminderRows(
                    noReminders,
                    await ReadRowsUntilExactlyAsync(
                        () => serviceA.ReadRows(grainId),
                        noReminders,
                        TestContext.Current.CancellationToken));
                AssertReminderRows(
                    expectedB,
                    await ReadRowsUntilExactlyAsync(
                        () => serviceB.ReadRows(grainId),
                        expectedB,
                        TestContext.Current.CancellationToken));
                AssertReminderRows(
                    noReminders,
                    await ReadRowsUntilExactlyAsync(
                        () => serviceA.ReadRows(0, 0),
                        noReminders,
                        TestContext.Current.CancellationToken));
                AssertReminderRows(
                    expectedB,
                    await ReadRowsUntilExactlyAsync(
                        () => serviceB.ReadRows(0, 0),
                        expectedB,
                        TestContext.Current.CancellationToken));
            }
            finally
            {
                await ClearServicesAndDeleteTableAsync(client, tableName, initializedTables);
            }
        }

        private DynamoDBReminderTable CreateIsolatedReminderTable(string tableName, string serviceId)
        {
            var storageOptions = new DynamoDBReminderStorageOptions
            {
                Service = AWSTestConstants.DynamoDbService,
                AccessKey = AWSTestConstants.DynamoDbAccessKey,
                SecretKey = AWSTestConstants.DynamoDbSecretKey,
                TableName = tableName,
                CreateIfNotExists = true,
                UpdateIfExists = false,
                UseProvisionedThroughput = false,
            };

            return new DynamoDBReminderTable(
                loggerFactory,
                Options.Create(new ClusterOptions { ClusterId = serviceId, ServiceId = serviceId }),
                Options.Create(storageOptions));
        }

        private static async Task<Orleans.ReminderTableData> ReadRowsUntilExactlyAsync(
            Func<Task<Orleans.ReminderTableData>> read,
            IReadOnlyList<(Orleans.ReminderEntry Entry, string? ETag)> expected,
            CancellationToken cancellationToken)
        {
            Orleans.ReminderTableData? observed = null;
            await TestingUtils.WaitUntilAsync(
                async (lastTry, attemptCancellation) =>
                {
                    observed = await read().WaitAsync(attemptCancellation);
                    var matches = ReminderRowsMatch(expected, observed);
                    if (lastTry && !matches)
                    {
                        AssertReminderRows(expected, observed);
                    }

                    return matches;
                },
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(100),
                cancellationToken);
            return Assert.IsType<Orleans.ReminderTableData>(observed);
        }

        private static bool ReminderRowsMatch(
            IReadOnlyList<(Orleans.ReminderEntry Entry, string? ETag)> expected,
            Orleans.ReminderTableData actual)
            => actual.Reminders.Count == expected.Count
                && expected.All(item => actual.Reminders.Any(
                    candidate => ReminderMatches(item.Entry, item.ETag, candidate)));

        private static bool ReminderMatches(
            Orleans.ReminderEntry expected,
            string? expectedETag,
            Orleans.ReminderEntry actual)
            => actual.GrainId.Equals(expected.GrainId)
                && string.Equals(actual.ReminderName, expected.ReminderName, StringComparison.Ordinal)
                && actual.StartAt.Ticks == expected.StartAt.Ticks
                && actual.Period == expected.Period
                && string.Equals(actual.ETag, expectedETag, StringComparison.Ordinal);

        private static void AssertReminderRows(
            IReadOnlyList<(Orleans.ReminderEntry Entry, string? ETag)> expected,
            Orleans.ReminderTableData actual)
        {
            Assert.Equal(expected.Count, actual.Reminders.Count);
            foreach (var item in expected)
            {
                var actualEntry = Assert.Single(
                    actual.Reminders,
                    candidate => candidate.GrainId.Equals(item.Entry.GrainId)
                        && string.Equals(candidate.ReminderName, item.Entry.ReminderName, StringComparison.Ordinal));
                AssertReminder(item.Entry, item.ETag, actualEntry);
            }
        }

        private static void AssertReminder(
            Orleans.ReminderEntry expected,
            string? expectedETag,
            Orleans.ReminderEntry actual)
        {
            Assert.Equal(expected.GrainId, actual.GrainId);
            Assert.Equal(expected.ReminderName, actual.ReminderName);
            Assert.Equal(expected.StartAt.Ticks, actual.StartAt.Ticks);
            Assert.Equal(expected.Period, actual.Period);
            Assert.Equal(expectedETag, actual.ETag);
        }

        private static async Task ClearServicesAndDeleteTableAsync(
            Amazon.DynamoDBv2.AmazonDynamoDBClient client,
            string tableName,
            IReadOnlyList<DynamoDBReminderTable> initializedTables)
        {
            List<Exception>? failures = null;
            foreach (var table in initializedTables)
            {
                try
                {
                    using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await table.TestOnlyClearTable().WaitAsync(cleanupCancellation.Token);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            try
            {
                await DeleteTableIfExistsAsync(client, tableName);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (failures is { Count: 1 })
            {
                throw failures[0];
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(failures);
            }
        }

        private static Amazon.DynamoDBv2.AmazonDynamoDBClient CreateDynamoDBClient()
        {
            var service = AWSTestConstants.DynamoDbService;
            if (service.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || service.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new Amazon.DynamoDBv2.AmazonDynamoDBClient(
                    new Amazon.Runtime.BasicAWSCredentials("dummy", "dummyKey"),
                    new Amazon.DynamoDBv2.AmazonDynamoDBConfig { ServiceURL = service });
            }

            var config = new Amazon.DynamoDBv2.AmazonDynamoDBConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(service),
            };
            return string.IsNullOrEmpty(AWSTestConstants.DynamoDbAccessKey)
                || string.IsNullOrEmpty(AWSTestConstants.DynamoDbSecretKey)
                    ? new Amazon.DynamoDBv2.AmazonDynamoDBClient(config)
                    : new Amazon.DynamoDBv2.AmazonDynamoDBClient(
                        new Amazon.Runtime.BasicAWSCredentials(
                            AWSTestConstants.DynamoDbAccessKey,
                            AWSTestConstants.DynamoDbSecretKey),
                        config);
        }

        private static async Task DeleteTableIfExistsAsync(
            Amazon.DynamoDBv2.AmazonDynamoDBClient client,
            string tableName)
        {
            using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await client.DeleteTableAsync(
                    new Amazon.DynamoDBv2.Model.DeleteTableRequest { TableName = tableName },
                    cleanupCancellation.Token);
            }
            catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
            {
            }
        }
    }
}
