using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.DynamoDB;
using Orleans.Transactions.DynamoDB.TransactionalState;
using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.DynamoDB.Tests
{
    public class TestState : IEquatable<TestState>
    {
        public int State { get; set; }

        public string? SensitiveValue { get; set; }

        public bool Equals(TestState? other)
        {
            return other is not null
                && this.State.Equals(other.State)
                && this.SensitiveValue == other.SensitiveValue;
        }
    }

    /// <summary>
    /// Tests for DynamoDB implementation of transactional state storage.
    /// </summary>
    public class DynamoDBTransactionalStateStorageTests : TransactionalStateStorageTestRunnerxUnit<TestState>, IClassFixture<TestFixture>
    {
        private const string tableName = "StateStorageTests";
        private const string partition = "testpartition";
        public DynamoDBTransactionalStateStorageTests(TestFixture fixture, ITestOutputHelper testOutput)
            : base(() => StateStorageFactory(fixture), (seed) => new TestState() { State = seed }, fixture.GrainFactory, testOutput)
        {
        }

        [Fact]
        public override Task StoreWithoutChanges() => base.StoreWithoutChanges();

        [Fact]
        public override async Task WrongEtags()
        {
            var storage = await this.stateStorageFactory();
            var empty = await storage.Load();

            await Assert.ThrowsAsync<ArgumentException>(() => storage.Store(
                "wrong-etag",
                empty.Metadata,
                [],
                commitUpTo: null,
                abortAfter: null));

            var etag = await storage.Store(empty.ETag, empty.Metadata, [], commitUpTo: null, abortAfter: null);

            await Assert.ThrowsAsync<ArgumentException>(() => storage.Store(
                null,
                empty.Metadata,
                [],
                commitUpTo: null,
                abortAfter: null));

            var durable = await storage.Load();
            Assert.Equal(etag, durable.ETag);
            Assert.Equal(0, durable.CommittedSequenceId);
            Assert.Empty(durable.PendingStates);
        }

        private static async Task<ITransactionalStateStorage<TestState>> StateStorageFactory(TestFixture fixture)
        {
            var storage = await InitTableAsync(NullLogger.Instance);
            var stateStorage = new DynamoDBTransactionalStateStorage<TestState>(
                storage,
                CreateOptions(),
                $"{partition}{DateTime.UtcNow.Ticks}",
                NullLoggerFactory.Instance.CreateLogger<DynamoDBTransactionalStateStorage<TestState>>());
            return stateStorage;
        }

        [Fact]
        public async Task Store_NewRowConflict_ReportsOperationsAndRequiresLoadBeforeReuse()
        {
            var partitionKey = $"{partition}-{Guid.NewGuid():N}";
            var logger = new RecordingLogger();
            var (winner, loser) = await CreateStoragePairAsync(partitionKey, logger);
            var winnerSnapshot = await winner.Load();
            var staleSnapshot = await loser.Load();
            const string loserPayload = "new-row-loser-payload-must-not-appear";

            var winnerETag = await winner.Store(
                winnerSnapshot.ETag,
                winnerSnapshot.Metadata,
                [CreatePendingState(1, 101)],
                commitUpTo: 1,
                abortAfter: null);

            var conflict = await Assert.ThrowsAsync<InconsistentStateException>(() => loser.Store(
                staleSnapshot.ETag,
                staleSnapshot.Metadata,
                [CreatePendingState(1, 999, loserPayload)],
                commitUpTo: 1,
                abortAfter: null));

            AssertConflictDiagnostics(conflict, partitionKey, expectedDataETag: null, expectedKeyETag: null, loserPayload);
            AssertConflictWarning(logger, conflict, loserPayload);
            var reuseError = await Assert.ThrowsAsync<InvalidOperationException>(() => loser.Store(
                staleSnapshot.ETag,
                staleSnapshot.Metadata,
                [CreatePendingState(1, 998)],
                commitUpTo: 1,
                abortAfter: null));
            Assert.Contains("Load must be called after a failed Store", reuseError.Message);

            var durableWinner = await winner.Load();
            Assert.Equal(winnerETag, durableWinner.ETag);
            Assert.Equal(1, durableWinner.CommittedSequenceId);
            Assert.Equal(101, durableWinner.CommittedState.State);

            var restoredLoser = await loser.Load();
            Assert.Equal(winnerETag, restoredLoser.ETag);
            Assert.Equal(101, restoredLoser.CommittedState.State);

            var recoveredETag = await loser.Store(
                restoredLoser.ETag,
                restoredLoser.Metadata,
                [CreatePendingState(1, 202)],
                commitUpTo: 1,
                abortAfter: null);
            var recovered = await loser.Load();
            Assert.Equal(recoveredETag, recovered.ETag);
            Assert.Equal(202, recovered.CommittedState.State);
        }

        [Fact]
        public async Task Store_ExistingRowETagConflict_MapsCancellationReasonsInOperationOrder()
        {
            var partitionKey = $"{partition}-{Guid.NewGuid():N}";
            var logger = new RecordingLogger();
            var (winner, loser) = await CreateStoragePairAsync(partitionKey, logger);
            var empty = await winner.Load();
            await winner.Store(
                empty.ETag,
                empty.Metadata,
                [CreatePendingState(1, 10)],
                commitUpTo: 1,
                abortAfter: null);

            var winnerSnapshot = await winner.Load();
            var staleSnapshot = await loser.Load();
            const string loserPayload = "existing-row-loser-payload-must-not-appear";

            var winnerETag = await winner.Store(
                winnerSnapshot.ETag,
                winnerSnapshot.Metadata,
                [CreatePendingState(1, 20)],
                commitUpTo: 1,
                abortAfter: null);

            var conflict = await Assert.ThrowsAsync<InconsistentStateException>(() => loser.Store(
                staleSnapshot.ETag,
                staleSnapshot.Metadata,
                [CreatePendingState(1, 30, loserPayload), CreatePendingState(2, 31)],
                commitUpTo: 1,
                abortAfter: null));

            AssertConflictDiagnostics(
                conflict,
                partitionKey,
                expectedDataETag: "0",
                expectedKeyETag: staleSnapshot.ETag,
                loserPayload,
                hasSecondDataOperation: true);
            AssertConflictWarning(logger, conflict, loserPayload);

            var durableWinner = await winner.Load();
            Assert.Equal(winnerETag, durableWinner.ETag);
            Assert.Equal(20, durableWinner.CommittedState.State);

            var restoredLoser = await loser.Load();
            Assert.Equal(winnerETag, restoredLoser.ETag);
            Assert.Equal(20, restoredLoser.CommittedState.State);

            var recoveredETag = await loser.Store(
                restoredLoser.ETag,
                restoredLoser.Metadata,
                [CreatePendingState(1, 40)],
                commitUpTo: 1,
                abortAfter: null);
            var recovered = await loser.Load();
            Assert.Equal(recoveredETag, recovered.ETag);
            Assert.Equal(40, recovered.CommittedState.State);
        }

        [Fact]
        public async Task Store_StaleDeleteConflict_ReportsDeleteOperation()
        {
            var partitionKey = $"{partition}-{Guid.NewGuid():N}";
            var logger = new RecordingLogger();
            var (winner, loser) = await CreateStoragePairAsync(partitionKey, logger);
            var empty = await winner.Load();
            var initialETag = await winner.Store(
                empty.ETag,
                empty.Metadata,
                [CreatePendingState(1, 10), CreatePendingState(2, 20)],
                commitUpTo: 1,
                abortAfter: null);

            var staleSnapshot = await loser.Load();
            var winnerETag = await winner.Store(
                initialETag,
                empty.Metadata,
                [CreatePendingState(2, 21)],
                commitUpTo: 2,
                abortAfter: null);

            var conflict = await Assert.ThrowsAsync<InconsistentStateException>(() => loser.Store(
                staleSnapshot.ETag,
                staleSnapshot.Metadata,
                statesToPrepare: null,
                commitUpTo: null,
                abortAfter: 1));

            AssertConflictDiagnostics(
                conflict,
                partitionKey,
                expectedDataETag: "0",
                expectedKeyETag: staleSnapshot.ETag,
                payload: string.Empty,
                dataOperation: "Delete",
                dataRowKey: "state_0000000000000002");
            AssertConflictWarning(logger, conflict, string.Empty);

            var durableWinner = await winner.Load();
            Assert.Equal(winnerETag, durableWinner.ETag);
            Assert.Equal(2, durableWinner.CommittedSequenceId);
            Assert.Equal(21, durableWinner.CommittedState.State);
        }

        [Fact]
        public async Task Store_DuplicateSequenceInSingleBatch_IsRejectedAndRequiresLoad()
        {
            var partitionKey = $"{partition}-{Guid.NewGuid():N}";
            var logger = new RecordingLogger();
            var (storage, _) = await CreateStoragePairAsync(partitionKey, logger);
            var empty = await storage.Load();

            var exception = await Assert.ThrowsAsync<AmazonDynamoDBException>(() => storage.Store(
                empty.ETag,
                empty.Metadata,
                [CreatePendingState(1, 10), CreatePendingState(1, 20)],
                commitUpTo: 1,
                abortAfter: null));

            Assert.Equal("ValidationException", exception.ErrorCode);
            Assert.Contains("multiple operations on one item", exception.Message, StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Store(
                empty.ETag,
                empty.Metadata,
                [CreatePendingState(1, 30)],
                commitUpTo: 1,
                abortAfter: null));

            var restored = await storage.Load();
            var recoveredETag = await storage.Store(
                restored.ETag,
                restored.Metadata,
                [CreatePendingState(1, 40)],
                commitUpTo: 1,
                abortAfter: null);
            var recovered = await storage.Load();
            Assert.Equal(recoveredETag, recovered.ETag);
            Assert.Equal(40, recovered.CommittedState.State);
        }

        [Fact]
        public async Task TransactWriteItems_ExplicitClientRequestToken_ReplaysIdenticalRequest()
        {
            _ = await InitTableAsync(NullLogger.Instance);
            using var client = CreateDynamoDBClient();
            var partitionKey = $"{partition}-{Guid.NewGuid():N}";
            var request = new TransactWriteItemsRequest
            {
                ClientRequestToken = Guid.NewGuid().ToString("N"),
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Put = new Put
                        {
                            TableName = tableName,
                            Item = new Dictionary<string, AttributeValue>
                            {
                                [DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME] = new AttributeValue { S = partitionKey },
                                [DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME] = new AttributeValue { S = "idempotency" },
                                ["Value"] = new AttributeValue { N = "1" }
                            },
                            ConditionExpression =
                                $"attribute_not_exists({DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME}) AND attribute_not_exists({DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME})"
                        }
                    }
                ]
            };

            await client.TransactWriteItemsAsync(request);
            await client.TransactWriteItemsAsync(request);

            var response = await client.GetItemAsync(new GetItemRequest
            {
                TableName = tableName,
                ConsistentRead = true,
                Key = new Dictionary<string, AttributeValue>
                {
                    [DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME] = new AttributeValue { S = partitionKey },
                    [DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME] = new AttributeValue { S = "idempotency" }
                }
            });
            Assert.Equal("1", response.Item["Value"].N);
        }

        [Fact]
        public void TransactionConflictCancellation_IsNotClassifiedAsStorageConflict()
        {
            var batchOperation = typeof(DynamoDBTransactionalStateStorage<TestState>).GetNestedType(
                "BatchOperation",
                System.Reflection.BindingFlags.NonPublic);
            if (batchOperation?.ContainsGenericParameters is true)
            {
                batchOperation = batchOperation.MakeGenericType(typeof(TestState));
            }

            var isStorageConflict = batchOperation?.GetMethod(
                "IsStorageConflict",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(isStorageConflict);
            var exception = new TransactionCanceledException("Transaction conflict")
            {
                CancellationReasons =
                [
                    new CancellationReason { Code = "TransactionConflict", Message = "Transaction is ongoing for the item" },
                    new CancellationReason { Code = "TransactionConflict", Message = "Transaction is ongoing for the item" }
                ]
            };

            Assert.False(Assert.IsType<bool>(isStorageConflict.Invoke(null, [exception])));
        }

        private static async Task<(
            DynamoDBTransactionalStateStorage<TestState> Winner,
            DynamoDBTransactionalStateStorage<TestState> Loser)> CreateStoragePairAsync(
                string partitionKey,
                ILogger<DynamoDBTransactionalStateStorage<TestState>> logger)
        {
            var storage = await InitTableAsync(NullLogger.Instance);
            var options = CreateOptions();
            return (
                new DynamoDBTransactionalStateStorage<TestState>(storage, options, partitionKey, logger),
                new DynamoDBTransactionalStateStorage<TestState>(storage, options, partitionKey, logger));
        }

        private static DynamoDBTransactionalStorageOptions CreateOptions()
        {
            var orleansJsonSerializer = new OrleansJsonSerializer(
                new OptionsWrapper<OrleansJsonSerializerOptions>(new OrleansJsonSerializerOptions()));
            return new DynamoDBTransactionalStorageOptions
            {
                TableName = tableName,
                GrainStorageSerializer = new JsonGrainStorageSerializer(orleansJsonSerializer)
            };
        }

        private static PendingTransactionState<TestState> CreatePendingState(long sequenceId, int value, string? sensitiveValue = null)
        {
            return new PendingTransactionState<TestState>
            {
                SequenceId = sequenceId,
                TransactionId = Guid.NewGuid().ToString(),
                TimeStamp = DateTime.UtcNow,
                TransactionManager = default!,
                State = new TestState { State = value, SensitiveValue = sensitiveValue }
            };
        }

        private static void AssertConflictDiagnostics(
            InconsistentStateException conflict,
            string partitionKey,
            string? expectedDataETag,
            string? expectedKeyETag,
            string payload,
            bool hasSecondDataOperation = false,
            string dataOperation = "Put",
            string dataRowKey = "state_0000000000000001")
        {
            var cancellation = Assert.IsType<TransactionCanceledException>(conflict.InnerException);
            var reasons = Assert.IsAssignableFrom<IReadOnlyList<CancellationReason>>(cancellation.CancellationReasons);
            Assert.Equal(hasSecondDataOperation ? 3 : 2, reasons.Count);
            Assert.Equal("ConditionalCheckFailed", reasons[0].Code);
            Assert.Equal("ConditionalCheckFailed", reasons[^1].Code);

            var message = conflict.Message;
            Assert.Contains($"PartitionKey={partitionKey}", message);
            var dataStart = message.IndexOf(
                $"TransactWriteItems[0] Operation={dataOperation} Role=Data RowKey={dataRowKey}",
                StringComparison.Ordinal);
            var keyIndex = hasSecondDataOperation ? 2 : 1;
            var keyStart = message.IndexOf(
                $"TransactWriteItems[{keyIndex}] Operation=Put Role=KeySynchronizer RowKey=key",
                StringComparison.Ordinal);
            Assert.True(dataStart >= 0, message);
            Assert.True(keyStart > dataStart, message);

            var secondDataStart = hasSecondDataOperation
                ? message.IndexOf(
                    "TransactWriteItems[1] Operation=Put Role=Data RowKey=state_0000000000000002",
                    StringComparison.Ordinal)
                : keyStart;
            Assert.True(secondDataStart > dataStart, message);

            var dataDiagnostic = message[dataStart..secondDataStart];
            var keyDiagnostic = message[keyStart..];
            Assert.Contains("CancellationReasonCode=ConditionalCheckFailed", dataDiagnostic);
            Assert.Contains($"CancellationReasonMessage={reasons[0].Message ?? "Unavailable"}", dataDiagnostic);
            Assert.Contains("CancellationReasonCode=ConditionalCheckFailed", keyDiagnostic);
            Assert.Contains($"CancellationReasonMessage={reasons[^1].Message ?? "Unavailable"}", keyDiagnostic);

            if (hasSecondDataOperation)
            {
                var secondDataDiagnostic = message[secondDataStart..keyStart];
                Assert.Equal("None", reasons[1].Code);
                Assert.Contains("Condition=attribute_not_exists(PartitionKey) AND attribute_not_exists(RowKey)", secondDataDiagnostic);
                Assert.Contains("CancellationReasonCode=None", secondDataDiagnostic);
                Assert.Contains($"CancellationReasonMessage={reasons[1].Message ?? "Unavailable"}", secondDataDiagnostic);
            }

            if (expectedDataETag is not null)
            {
                Assert.Contains("Condition=ETag = :currentETag", dataDiagnostic);
                Assert.Contains($"ExpectedETag={expectedDataETag}", dataDiagnostic);
            }
            else
            {
                Assert.Contains("Condition=attribute_not_exists(PartitionKey) AND attribute_not_exists(RowKey)", dataDiagnostic);
            }

            if (expectedKeyETag is not null)
            {
                Assert.Contains("Condition=ETag = :currentETag", keyDiagnostic);
                Assert.Contains($"ExpectedETag={expectedKeyETag}", keyDiagnostic);
            }
            else
            {
                Assert.Contains("Condition=attribute_not_exists(PartitionKey) AND attribute_not_exists(RowKey)", keyDiagnostic);
            }

            if (payload.Length > 0)
            {
                Assert.DoesNotContain(payload, conflict.ToString());
            }
        }

        private static void AssertConflictWarning(RecordingLogger logger, InconsistentStateException conflict, string payload)
        {
            var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Equal(conflict.Message, warning.Message);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
            if (payload.Length > 0)
            {
                Assert.DoesNotContain(payload, warning.Message);
            }
        }

        private static async Task<DynamoDBStorage> InitTableAsync(ILogger logger)
        {
            try
            {
                var storage = GetDynamoDBStorage(logger);
                await storage.InitializeTable(tableName,
                    new List<KeySchemaElement>
                    {
                        new KeySchemaElement { AttributeName = DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME, KeyType = KeyType.HASH },
                        new KeySchemaElement { AttributeName = DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME, KeyType = KeyType.RANGE }
                    },
                    new List<AttributeDefinition>
                    {
                        new AttributeDefinition { AttributeName = DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                        new AttributeDefinition { AttributeName = DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
                    },
                    secondaryIndexes: null,
                    null);
                return storage;
            }
            catch (Exception exc)
            {
                logger.LogError(exc, "Could not initialize connection to storage table {TableName}", tableName);
                throw;
            }
        }

        private static DynamoDBStorage GetDynamoDBStorage(ILogger logger)
        {
            try
            {
                var storage = new DynamoDBStorage(
                    logger,
                    service: AWSTestConstants.DynamoDbService,
                    accessKey: AWSTestConstants.DynamoDbAccessKey,
                    secretKey: AWSTestConstants.DynamoDbSecretKey);
                return storage;
            }
            catch (Exception exc)
            {
                logger.LogError(exc, "Error creating CloudTableCreationClient");
                throw;
            }
        }

        private static AmazonDynamoDBClient CreateDynamoDBClient()
        {
            var service = AWSTestConstants.DynamoDbService;
            if (service.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || service.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new AmazonDynamoDBClient(
                    new BasicAWSCredentials("dummy", "dummyKey"),
                    new AmazonDynamoDBConfig { ServiceURL = service });
            }

            var config = new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(service) };
            return string.IsNullOrEmpty(AWSTestConstants.DynamoDbAccessKey)
                || string.IsNullOrEmpty(AWSTestConstants.DynamoDbSecretKey)
                    ? new AmazonDynamoDBClient(config)
                    : new AmazonDynamoDBClient(
                        new BasicAWSCredentials(AWSTestConstants.DynamoDbAccessKey, AWSTestConstants.DynamoDbSecretKey),
                        config);
        }

        private sealed class RecordingLogger : ILogger<DynamoDBTransactionalStateStorage<TestState>>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                this.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
