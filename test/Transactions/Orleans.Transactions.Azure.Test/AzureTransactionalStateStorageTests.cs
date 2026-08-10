using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage;
using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.TestKit.xUnit;
using Tester.AzureUtils;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Azure.Tests
{
    public class TestState : IEquatable<TestState>
    {
        public int State { get; set; }

        public string? Payload { get; set; }

        public bool Equals(TestState? other)
        {
            return other is not null && State.Equals(other.State) && Payload == other.Payload;
        }
    }

    /// <summary>
    /// Tests for Azure Table Storage implementation of transactional state storage.
    /// </summary>
    [TestCategory("AzureStorage"), TestCategory("Transactions"), TestCategory("Functional")]
    public class AzureTransactionalStateStorageTests : TransactionalStateStorageTestRunnerxUnit<TestState>, IClassFixture<TestFixture>
    {
        private const string tableName = "StateStorageTests";
        private const string partition = "testpartition";
        private readonly TestFixture _fixture;

        public AzureTransactionalStateStorageTests(TestFixture fixture, ITestOutputHelper testOutput)
            : base(() => StateStorageFactory(fixture), (seed) => new TestState() { State = seed }, fixture.GrainFactory, testOutput)
        {
            _fixture = fixture;
        }

        private static async Task<ITransactionalStateStorage<TestState>> StateStorageFactory(TestFixture fixture)
        {
            var table = await InitTableAsync(NullLogger.Instance);
            var jsonSettings = TransactionalStateFactory.GetJsonSerializerSettings(fixture.HostedCluster.ServiceProvider);
            return CreateStorage(table, $"{partition}{DateTime.UtcNow.Ticks}", jsonSettings);
        }

        [Fact]
        public async Task NoChangeStoreUsesStrictETag()
        {
            var (writer, staleWriter, writerLoad, staleLoad) = await CreateInitializedWriters();

            var updatedETag = await writer.Store(writerLoad.ETag, writerLoad.Metadata, [], null, null);

            Assert.NotEqual(writerLoad.ETag, updatedETag);
            await Assert.ThrowsAsync<InconsistentStateException>(
                () => staleWriter.Store(staleLoad.ETag, staleLoad.Metadata, [], null, null));
        }

        [Fact]
        public async Task StaleWriterConvergesAfterLoad()
        {
            var (writer, staleWriter, writerLoad, staleLoad) = await CreateInitializedWriters();
            var writerTimestamp = DateTime.UtcNow;
            var staleWriterTimestamp = writerTimestamp.AddSeconds(1);

            await writer.Store(
                writerLoad.ETag,
                new TransactionalStateMetaData { TimeStamp = writerTimestamp },
                [],
                null,
                null);
            await Assert.ThrowsAsync<InconsistentStateException>(
                () => staleWriter.Store(
                    staleLoad.ETag,
                    new TransactionalStateMetaData { TimeStamp = staleWriterTimestamp },
                    [],
                    null,
                    null));

            var refreshed = await staleWriter.Load();
            var storedETag = await staleWriter.Store(
                refreshed.ETag,
                new TransactionalStateMetaData { TimeStamp = staleWriterTimestamp },
                [],
                null,
                null);
            var converged = await staleWriter.Load();

            Assert.Equal(storedETag, converged.ETag);
            Assert.Equal(staleWriterTimestamp, converged.Metadata.TimeStamp);
        }

        [Fact]
        public async Task FailedStoreRequiresSuccessfulLoadBeforeReuse()
        {
            var (writer, staleWriter, writerLoad, staleLoad) = await CreateInitializedWriters();

            await writer.Store(writerLoad.ETag, writerLoad.Metadata, [], null, null);
            await Assert.ThrowsAsync<InconsistentStateException>(
                () => staleWriter.Store(staleLoad.ETag, staleLoad.Metadata, [], null, null));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => staleWriter.Store(staleLoad.ETag, staleLoad.Metadata, [], null, null));
            Assert.Contains("Load must complete successfully", exception.Message);

            var refreshed = await staleWriter.Load();
            await staleWriter.Store(refreshed.ETag, refreshed.Metadata, [], null, null);
        }

        [Fact]
        public async Task ConflictDiagnosticsExcludePayloads()
        {
            var table = await InitTableAsync(NullLogger.Instance);
            var jsonSettings = TransactionalStateFactory.GetJsonSerializerSettings(_fixture.HostedCluster.ServiceProvider);
            var testPartition = $"{partition}{Guid.NewGuid():N}";
            var logger = new CapturingLogger<AzureTableTransactionalStateStorage<TestState>>();
            var writer = CreateStorage(table, testPartition, jsonSettings);
            var staleWriter = CreateStorage(table, testPartition, jsonSettings, logger);
            var statePayload = $"state-payload-{Guid.NewGuid():N}";
            var transactionId = $"transaction-id-{Guid.NewGuid():N}";
            var metadataId = Guid.NewGuid();
            var pendingState = new PendingTransactionState<TestState>
            {
                SequenceId = 1,
                State = new TestState { State = 1, Payload = statePayload },
                TimeStamp = DateTime.UtcNow,
                TransactionId = transactionId,
            };
            var metadata = new TransactionalStateMetaData
            {
                CommitRecords =
                {
                    [metadataId] = new CommitRecord
                    {
                        Timestamp = DateTime.UtcNow,
                        WriteParticipants = [],
                    },
                },
            };

            var writerLoad = await writer.Load();
            var staleLoad = await staleWriter.Load();
            await writer.Store(writerLoad.ETag, metadata, [pendingState], null, null);
            var exception = await Assert.ThrowsAsync<InconsistentStateException>(
                () => staleWriter.Store(staleLoad.ETag, metadata, [pendingState], null, null));
            var diagnostics = $"{exception}{Environment.NewLine}{string.Join(Environment.NewLine, logger.Messages)}";

            Assert.Contains($"Partition={testPartition}", diagnostics);
            Assert.Contains("ActionIndex=0", diagnostics);
            Assert.Contains("ActionType=Add", diagnostics);
            Assert.Contains("RowKey=s_0000000000000001", diagnostics);
            Assert.Contains("HttpStatus=409", diagnostics);
            Assert.Contains("ErrorCode=EntityAlreadyExists", diagnostics);
            Assert.Contains("FailedOperationIndex=0", diagnostics);
            Assert.DoesNotContain(statePayload, diagnostics);
            Assert.DoesNotContain(transactionId, diagnostics);
            Assert.DoesNotContain(metadataId.ToString(), diagnostics, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<(
            AzureTableTransactionalStateStorage<TestState> Writer,
            AzureTableTransactionalStateStorage<TestState> StaleWriter,
            TransactionalStorageLoadResponse<TestState> WriterLoad,
            TransactionalStorageLoadResponse<TestState> StaleLoad)> CreateInitializedWriters()
        {
            var table = await InitTableAsync(NullLogger.Instance);
            var jsonSettings = TransactionalStateFactory.GetJsonSerializerSettings(_fixture.HostedCluster.ServiceProvider);
            var testPartition = $"{partition}{Guid.NewGuid():N}";
            var initializer = CreateStorage(table, testPartition, jsonSettings);
            var initialLoad = await initializer.Load();
            await initializer.Store(initialLoad.ETag, initialLoad.Metadata, [], null, null);

            var writer = CreateStorage(table, testPartition, jsonSettings);
            var staleWriter = CreateStorage(table, testPartition, jsonSettings);
            return (writer, staleWriter, await writer.Load(), await staleWriter.Load());
        }

        private static AzureTableTransactionalStateStorage<TestState> CreateStorage(
            TableClient table,
            string partitionKey,
            JsonSerializerSettings jsonSettings,
            ILogger<AzureTableTransactionalStateStorage<TestState>>? logger = null)
        {
            return new AzureTableTransactionalStateStorage<TestState>(
                table,
                partitionKey,
                jsonSettings,
                logger ?? NullLoggerFactory.Instance.CreateLogger<AzureTableTransactionalStateStorage<TestState>>());
        }

        private static async Task<TableClient> InitTableAsync(ILogger logger)
        {
            try
            {
                var tableCreationClient = GetCloudTableCreationClient(logger);
                TableClient tableRef = tableCreationClient.GetTableClient(tableName);
                var tableItem = await tableRef.CreateIfNotExistsAsync();
                var didCreate = tableItem is not null;

                logger.LogInformation("{Verb} Azure storage table {TableName}", didCreate ? "Created" : "Attached to", tableName);
                return tableRef;
            }
            catch (Exception exc)
            {
                logger.LogError(exc, "Could not initialize connection to storage table {TableName}", tableName);
                throw;
            }
        }

        private static TableServiceClient GetCloudTableCreationClient(ILogger logger)
        {
            try
            {
                var creationClient = AzureStorageOperationOptionsExtensions.GetTableServiceClient();
                return creationClient;
            }
            catch (Exception exc)
            {
                logger.LogError(exc, "Error creating CloudTableCreationClient");
                throw;
            }
        }

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<string> Messages { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Messages.Add(exception is null
                    ? formatter(state, exception)
                    : $"{formatter(state, exception)}{Environment.NewLine}{exception}");
            }
        }
    }

    [TestCategory("BVT")]
    public class AzureTransactionalStateStorageSnapshotTests
    {
        private const string Partition = "test-partition";
        private const string LowerBoundaryRowKey = "!";
        private const string UpperBoundaryRowKey = "~";
        private const string BoundaryVersionPropertyName = "SnapshotVersion";
        private static readonly string MetadataJson = JsonConvert.SerializeObject(new TransactionalStateMetaData());

        [Fact]
        public async Task Load_WhenBoundaryChangesDuringStateRead_RetriesAndReturnsCoherentSnapshot()
        {
            var table = new ScriptedTableClient { StorageVersion = "old" };
            table.Enqueue(PagedSnapshotQuery(
                "snapshot-crossing-mutation",
                EntityPage(
                    "page:snapshot-crossing-mutation:1",
                    () => Assert.Equal("old", table.StorageVersion),
                    BoundaryEntity(LowerBoundaryRowKey, "old"),
                    KeyEntity(sequenceId: 1, etag: "old"),
                    StateEntity(sequenceId: 1, value: 111)),
                EntityPage(
                    "page:snapshot-crossing-mutation:2",
                    () =>
                    {
                        table.StorageVersion = "new";
                        table.Events.Add("mutation:key-old-to-new");
                    },
                    StateEntity(sequenceId: 2, value: 222),
                    BoundaryEntity(UpperBoundaryRowKey, "new"))));
            table.Enqueue(PagedSnapshotQuery(
                "snapshot-new",
                EntityPage(
                    "page:snapshot-new:1",
                    () => Assert.Equal("new", table.StorageVersion),
                    BoundaryEntity(LowerBoundaryRowKey, "new"),
                    KeyEntity(sequenceId: 2, etag: "new")),
                EntityPage(
                    "page:snapshot-new:2",
                    null,
                    StateEntity(sequenceId: 2, value: 222),
                    BoundaryEntity(UpperBoundaryRowKey, "new"))));
            var storage = CreateStorage(table);

            var result = await storage.Load();

            Assert.Equal(new ETag("new").ToString(), result.ETag);
            Assert.Equal(2, result.CommittedSequenceId);
            Assert.Equal(222, result.CommittedState.State);
            Assert.Empty(result.PendingStates);
            Assert.Empty(result.Metadata.CommitRecords);
            Assert.Equal(
                [
                    "query:snapshot-crossing-mutation",
                    "page:snapshot-crossing-mutation:1",
                    "page:snapshot-crossing-mutation:2",
                    "mutation:key-old-to-new",
                    "query:snapshot-new",
                    "page:snapshot-new:1",
                    "page:snapshot-new:2",
                ],
                table.Events);
            Assert.Equal(0, table.RemainingQueryResponses);
        }

        [Fact]
        public async Task Load_WhenSnapshotFitsOnePage_UsesSingleQuery()
        {
            var table = new ScriptedTableClient();
            table.Enqueue(FencedSnapshotQuery(
                "snapshot",
                "stable",
                KeyEntity(sequenceId: 2, etag: "stable"),
                StateEntity(sequenceId: 1, value: 111),
                StateEntity(sequenceId: 2, value: 222)));
            var storage = CreateStorage(table);

            var result = await storage.Load();

            Assert.Equal(new ETag("stable").ToString(), result.ETag);
            Assert.Equal(2, result.CommittedSequenceId);
            Assert.Equal(222, result.CommittedState.State);
            Assert.Equal(["snapshot"], table.QueryLabels);
            Assert.Equal(["query:snapshot"], table.Events);
            Assert.Equal(0, table.RemainingQueryResponses);
        }

        [Fact]
        public async Task Load_WhenOnlyOneBoundaryPersists_ThrowsWithSafeVersionContext()
        {
            var table = new ScriptedTableClient();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                table.Enqueue(PagedSnapshotQuery(
                    $"snapshot-{attempt}",
                    EntityPage(
                        $"page:snapshot-{attempt}:1",
                        null,
                        BoundaryEntity(LowerBoundaryRowKey, $"boundary-{attempt}")),
                    EntityPage($"page:snapshot-{attempt}:2", null)));
            }

            var exception = await Assert.ThrowsAsync<InconsistentStateException>(
                () => CreateStorage(table).Load());

            Assert.Equal("boundary-4", exception.StoredEtag);
            Assert.Equal("null", exception.CurrentEtag);
            Assert.Equal(5, table.QueryLabels.Count);
        }

        [Fact]
        public async Task Load_WhenPaginatedBoundaryVersionIsStable_UsesSingleQuery()
        {
            var table = new ScriptedTableClient();
            table.Enqueue(PagedSnapshotQuery(
                "snapshot",
                EntityPage(
                    "page:snapshot:1",
                    null,
                    BoundaryEntity(LowerBoundaryRowKey, "stable"),
                    KeyEntity(sequenceId: 2, etag: "stable"),
                    StateEntity(sequenceId: 1, value: 111)),
                EntityPage(
                    "page:snapshot:2",
                    null,
                    StateEntity(sequenceId: 2, value: 222),
                    BoundaryEntity(UpperBoundaryRowKey, "stable"))));
            var storage = CreateStorage(table);

            var result = await storage.Load();

            Assert.Equal(new ETag("stable").ToString(), result.ETag);
            Assert.Equal(2, result.CommittedSequenceId);
            Assert.Equal(222, result.CommittedState.State);
            Assert.Equal(["snapshot"], table.QueryLabels);
            Assert.Equal(
                [
                    "query:snapshot",
                    "page:snapshot:1",
                    "page:snapshot:2",
                ],
                table.Events);
            Assert.Equal(0, table.RemainingQueryResponses);
        }

        [Fact]
        public async Task Load_WhenLegacyPaginatedSnapshotHasNoBoundaries_AcceptsWithoutFence()
        {
            var table = new ScriptedTableClient();
            table.Enqueue(PagedSnapshotQuery(
                "snapshot",
                EntityPage(
                    "page:snapshot:1",
                    null,
                    KeyEntity(sequenceId: 2, etag: "stable"),
                    StateEntity(sequenceId: 1, value: 111)),
                EntityPage("page:snapshot:2", null, StateEntity(sequenceId: 2, value: 222))));
            var storage = CreateStorage(table);

            var result = await storage.Load();

            Assert.Equal(new ETag("stable").ToString(), result.ETag);
            Assert.Equal(2, result.CommittedSequenceId);
            Assert.Equal(222, result.CommittedState.State);
            Assert.Equal(["snapshot"], table.QueryLabels);
            Assert.Equal(0, table.RemainingQueryResponses);
        }

        [Fact]
        public async Task Load_WhenBoundaryVersionRemainsUnstable_ThrowsAfterBoundedAttempts()
        {
            var table = new ScriptedTableClient();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                table.Enqueue(PagedSnapshotQuery(
                    $"snapshot-{attempt}",
                    EntityPage(
                        $"page:snapshot-{attempt}:1",
                        null,
                        BoundaryEntity(LowerBoundaryRowKey, $"boundary-{attempt}")),
                    EntityPage(
                        $"page:snapshot-{attempt}:2",
                        null,
                        BoundaryEntity(UpperBoundaryRowKey, $"boundary-{attempt + 1}"))));
            }

            var storage = CreateStorage(table);

            var exception = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.Load());

            Assert.Equal("Could not load a consistent Azure Table transactional state snapshot.", exception.Message);
            Assert.Equal("boundary-4", exception.StoredEtag);
            Assert.Equal("boundary-5", exception.CurrentEtag);
            Assert.Null(exception.InnerException);
            Assert.Equal(5, table.QueryLabels.Count);
            Assert.Equal(5, table.QueryLabels.Count(label => label.StartsWith("snapshot-", StringComparison.Ordinal)));
            Assert.Equal(0, table.RemainingQueryResponses);
        }

        [Fact]
        public async Task Load_WhenSinglePageKeyIsAbsent_AcceptsFreshSnapshot()
        {
            var table = new ScriptedTableClient();
            table.Enqueue(SnapshotQuery("snapshot-empty", null));
            var storage = CreateStorage(table);

            var result = await storage.Load();

            Assert.Null(result.ETag);
            Assert.Equal(0, result.CommittedSequenceId);
            Assert.Equal(0, result.CommittedState.State);
            Assert.Empty(result.PendingStates);
            Assert.Equal(["snapshot-empty"], table.QueryLabels);
            Assert.Equal(0, table.RemainingQueryResponses);
        }

        [Fact]
        public async Task Store_WhenFinalPhysicalBatchIsPartial_IncludesKeyFenceInEveryBatch()
        {
            var table = new ScriptedTableClient();
            table.Enqueue(SnapshotQuery(
                "snapshot",
                KeyEntity(sequenceId: 1, etag: "loaded"),
                Enumerable.Range(1, 101).Select(sequenceId => StateEntity(sequenceId, sequenceId)).ToArray()));
            var storage = CreateStorage(table);
            var loaded = await storage.Load();

            var storedEtag = await storage.Store(
                loaded.ETag,
                new TransactionalStateMetaData(),
                statesToPrepare: null,
                commitUpTo: 101,
                abortAfter: null);

            Assert.Equal(new ETag("batch-2").ToString(), storedEtag);
            Assert.Equal([100, 6], table.SubmittedTransactions.Select(batch => batch.Count));
            Assert.All(
                table.SubmittedTransactions,
                batch =>
                {
                    Assert.Single(batch, action => action.RowKey == "k");
                    var lower = Assert.Single(batch, action => action.RowKey == LowerBoundaryRowKey);
                    var upper = Assert.Single(batch, action => action.RowKey == UpperBoundaryRowKey);
                    Assert.NotNull(lower.BoundaryVersion);
                    Assert.Equal(lower.BoundaryVersion, upper.BoundaryVersion);
                });
            Assert.Equal(
                table.SubmittedTransactions.Count,
                table.SubmittedTransactions
                    .Select(batch => batch.Single(action => action.RowKey == LowerBoundaryRowKey).BoundaryVersion)
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            var finalBatch = table.SubmittedTransactions[1];
            Assert.Contains(
                finalBatch,
                action => action.ActionType == TableTransactionActionType.Delete
                    && action.RowKey == StateRowKey(100));
            var finalKeyFence = Assert.Single(finalBatch, action => action.RowKey == "k");
            Assert.Equal(TableTransactionActionType.UpdateReplace, finalKeyFence.ActionType);
            Assert.Equal(new ETag("batch-1").ToString(), finalKeyFence.ETag.ToString());
            Assert.Equal(0, table.RemainingQueryResponses);
        }

        [Fact]
        public async Task Store_WhenFreshSplitFailsAfterFirstBatch_IntermediateStateIsRecoverable()
        {
            var table = new ScriptedTableClient
            {
                PersistTransactions = true,
                FailTransactionNumber = 2,
            };
            var storage = CreateStorage(table);
            var initial = await storage.Load();
            var commitRecordId = Guid.NewGuid();
            var incomingMetadata = new TransactionalStateMetaData
            {
                TimeStamp = DateTime.UtcNow,
                CommitRecords =
                {
                    [commitRecordId] = new CommitRecord
                    {
                        Timestamp = DateTime.UtcNow,
                        WriteParticipants = [],
                    },
                },
            };
            var statesToPrepare = Enumerable.Range(101, 101)
                .Select(sequenceId => new PendingTransactionState<TestState>
                {
                    SequenceId = sequenceId,
                    State = new TestState { State = sequenceId },
                    TimeStamp = DateTime.UtcNow,
                    TransactionId = $"transaction-{sequenceId}",
                    TransactionManager = new ParticipantId(
                        "tm",
                        null!,
                        ParticipantId.Role.Resource | ParticipantId.Role.Manager),
                })
                .ToList();

            await Assert.ThrowsAsync<RequestFailedException>(
                () => storage.Store(
                    initial.ETag,
                    incomingMetadata,
                    statesToPrepare,
                    commitUpTo: 101,
                    abortAfter: null));

            Assert.Equal([100, 7], table.SubmittedTransactions.Select(batch => batch.Count));
            var firstBatchKey = Assert.Single(table.SubmittedTransactions[0], action => action.RowKey == "k");
            Assert.Equal(TableTransactionActionType.Add, firstBatchKey.ActionType);
            Assert.All(
                table.SubmittedTransactions,
                batch =>
                {
                    var lower = Assert.Single(batch, action => action.RowKey == LowerBoundaryRowKey);
                    var upper = Assert.Single(batch, action => action.RowKey == UpperBoundaryRowKey);
                    Assert.Equal(lower.BoundaryVersion, upper.BoundaryVersion);
                });

            var recoveryStorage = CreateStorage(table);
            var intermediate = await recoveryStorage.Load();

            Assert.Equal(new ETag("batch-1").ToString(), intermediate.ETag);
            Assert.Equal(0, intermediate.CommittedSequenceId);
            Assert.Equal(0, intermediate.CommittedState.State);
            Assert.Equal(default, intermediate.Metadata.TimeStamp);
            Assert.Empty(intermediate.Metadata.CommitRecords);
            Assert.Equal(97, intermediate.PendingStates.Count);
            Assert.Equal(Enumerable.Range(101, 97).Select(value => (long)value), intermediate.PendingStates.Select(state => state.SequenceId));

            table.FailTransactionNumber = null;
            await recoveryStorage.Store(
                intermediate.ETag,
                incomingMetadata,
                statesToPrepare,
                commitUpTo: 101,
                abortAfter: null);

            var loadedAfterRetry = await CreateStorage(table).Load();
            Assert.Equal(101, loadedAfterRetry.CommittedSequenceId);
            Assert.Equal(101, loadedAfterRetry.CommittedState.State);
            Assert.Equal(incomingMetadata.TimeStamp, loadedAfterRetry.Metadata.TimeStamp);
            Assert.True(loadedAfterRetry.Metadata.CommitRecords.ContainsKey(commitRecordId));
            Assert.Equal(100, loadedAfterRetry.PendingStates.Count);
            Assert.Equal(Enumerable.Range(102, 100).Select(value => (long)value), loadedAfterRetry.PendingStates.Select(state => state.SequenceId));
        }

        private static AzureTableTransactionalStateStorage<TestState> CreateStorage(TableClient table)
            => new(
                table,
                Partition,
                new JsonSerializerSettings(),
                NullLoggerFactory.Instance.CreateLogger<AzureTableTransactionalStateStorage<TestState>>());

        private static QueryResponse SnapshotQuery(string label, ScriptedEntity? key, params ScriptedEntity[] states)
            => new(label, nameof(TableEntity), [SnapshotPage(null, null, key, states)]);

        private static QueryResponse FencedSnapshotQuery(
            string label,
            string version,
            ScriptedEntity key,
            params ScriptedEntity[] states)
            => new(
                label,
                nameof(TableEntity),
                [
                    EntityPage(
                        null,
                        null,
                        [
                            BoundaryEntity(LowerBoundaryRowKey, version),
                            key,
                            .. states,
                            BoundaryEntity(UpperBoundaryRowKey, version)
                        ])
                ]);

        private static QueryResponse PagedSnapshotQuery(string label, params QueryPage[] pages)
            => new(label, nameof(TableEntity), pages);

        private static QueryPage SnapshotPage(
            string? eventLabel,
            Action? onRead,
            ScriptedEntity? key,
            params ScriptedEntity[] states)
        {
            IReadOnlyList<ScriptedEntity> entities = key is null ? states : [key, .. states];
            return new QueryPage(entities, eventLabel, onRead);
        }

        private static QueryPage EntityPage(string? eventLabel, Action? onRead, params ScriptedEntity[] entities)
            => new(entities, eventLabel, onRead);

        private static ScriptedEntity BoundaryEntity(string rowKey, string version)
            => new(
                Partition,
                rowKey,
                new ETag($"boundary-{rowKey}-{version}"),
                new Dictionary<string, object>
                {
                    [BoundaryVersionPropertyName] = version,
                });

        private static ScriptedEntity KeyEntity(long sequenceId, string etag)
            => new(
                Partition,
                "k",
                new ETag(etag),
                new Dictionary<string, object>
                {
                    ["CommittedSequenceId"] = sequenceId,
                    ["Metadata"] = MetadataJson,
                });

        private static ScriptedEntity StateEntity(long sequenceId, int value)
            => new(
                Partition,
                StateRowKey(sequenceId),
                new ETag($"state-{sequenceId}"),
                new Dictionary<string, object>
                {
                    ["StateJson"] = JsonConvert.SerializeObject(new TestState { State = value }),
                });

        private static string StateRowKey(long sequenceId) => $"s_{sequenceId:x16}";

        private sealed record ScriptedEntity(
            string PartitionKey,
            string RowKey,
            ETag ETag,
            IReadOnlyDictionary<string, object> Properties);

        private sealed record QueryResponse(
            string Label,
            string EntityTypeName,
            IReadOnlyList<QueryPage> Pages,
            Action? OnQuery = null);

        private sealed record QueryPage(
            IReadOnlyList<ScriptedEntity> Entities,
            string? EventLabel = null,
            Action? OnRead = null);

        private sealed record SubmittedAction(
            TableTransactionActionType ActionType,
            string PartitionKey,
            string RowKey,
            ETag ETag,
            string? BoundaryVersion);

        private sealed class ScriptedTableClient : TableClient
        {
            private readonly Queue<QueryResponse> _queryResponses = new();
            private readonly Dictionary<(string PartitionKey, string RowKey), ScriptedEntity> _durableEntities = [];

            public override string Name => "scripted";

            public string? StorageVersion { get; set; }

            public bool PersistTransactions { get; set; }

            public int? FailTransactionNumber { get; set; }

            public List<string> Events { get; } = [];

            public List<string> QueryLabels { get; } = [];

            public List<IReadOnlyList<SubmittedAction>> SubmittedTransactions { get; } = [];

            public int RemainingQueryResponses => _queryResponses.Count;

            public void Enqueue(QueryResponse response) => _queryResponses.Enqueue(response);

            public override AsyncPageable<T> QueryAsync<T>(
                string? filter = null,
                int? maxPerPage = null,
                IEnumerable<string>? select = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_queryResponses.TryDequeue(out var response))
                {
                    Assert.True(PersistTransactions, $"Unexpected {typeof(T).Name} query: {filter}");
                    return QueryDurableEntities<T>(cancellationToken);
                }

                Assert.Equal(response.EntityTypeName, typeof(T).Name);
                QueryLabels.Add(response.Label);
                Events.Add($"query:{response.Label}");
                response.OnQuery?.Invoke();

                return AsyncPageable<T>.FromPages(EnumeratePages<T>(response, cancellationToken));
            }

            public override Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(
                IEnumerable<TableTransactionAction> transactionActions,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var actions = transactionActions.ToList();
                var transactionNumber = SubmittedTransactions.Count + 1;
                SubmittedTransactions.Add(
                    actions.Select(action => new SubmittedAction(
                        action.ActionType,
                        action.Entity.PartitionKey,
                        action.Entity.RowKey,
                        action.ETag,
                        action.Entity is TableEntity tableEntity
                            ? tableEntity.GetString(BoundaryVersionPropertyName)
                            : null)).ToList());

                if (FailTransactionNumber == transactionNumber)
                {
                    throw new RequestFailedException(500, $"Injected failure for batch {transactionNumber}.");
                }

                var responseEtag = new ETag($"batch-{transactionNumber}");
                if (PersistTransactions)
                {
                    foreach (var action in actions)
                    {
                        var entityKey = (action.Entity.PartitionKey, action.Entity.RowKey);
                        if (action.ActionType == TableTransactionActionType.Delete)
                        {
                            _durableEntities.Remove(entityKey);
                        }
                        else
                        {
                            _durableEntities[entityKey] = CaptureEntity(action.Entity, responseEtag);
                        }
                    }
                }

                IReadOnlyList<Response> responses = actions.Select(_ => (Response)new StubResponse(responseEtag)).ToList();
                return Task.FromResult(Response.FromValue(responses, new StubResponse(default)));
            }

            private AsyncPageable<T> QueryDurableEntities<T>(CancellationToken cancellationToken)
                where T : class, ITableEntity
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isKeyQuery = typeof(T).Name == "KeyEntity";
                var label = $"durable:{typeof(T).Name}";
                QueryLabels.Add(label);
                Events.Add($"query:{label}");
                var values = _durableEntities.Values
                    .Where(entity => isKeyQuery
                        ? entity.RowKey == "k"
                        : true)
                    .OrderBy(entity => entity.RowKey, StringComparer.Ordinal)
                    .Select(entity => Materialize<T>(entity))
                    .ToList();
                var page = Page<T>.FromValues(values, null, new StubResponse(default));
                return AsyncPageable<T>.FromPages([page]);
            }

            private static ScriptedEntity CaptureEntity(ITableEntity source, ETag etag)
            {
                Dictionary<string, object> properties;
                if (source is TableEntity tableEntity)
                {
                    properties = tableEntity.ToDictionary(entry => entry.Key, entry => entry.Value);
                }
                else
                {
                    properties = source.GetType()
                        .GetProperties()
                        .Where(property => property.CanRead
                            && property.Name is not (nameof(ITableEntity.PartitionKey)
                                or nameof(ITableEntity.RowKey)
                                or nameof(ITableEntity.Timestamp)
                                or nameof(ITableEntity.ETag)))
                        .Select(property => (property.Name, Value: property.GetValue(source)))
                        .Where(entry => entry.Value is not null)
                        .ToDictionary(entry => entry.Name, entry => entry.Value!);
                }

                return new ScriptedEntity(source.PartitionKey, source.RowKey, etag, properties);
            }

            private IEnumerable<Page<T>> EnumeratePages<T>(QueryResponse response, CancellationToken cancellationToken)
                where T : class, ITableEntity
            {
                for (var i = 0; i < response.Pages.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scriptedPage = response.Pages[i];
                    if (scriptedPage.EventLabel is { } eventLabel)
                    {
                        Events.Add(eventLabel);
                    }

                    scriptedPage.OnRead?.Invoke();
                    var values = scriptedPage.Entities.Select(entity => Materialize<T>(entity)).ToList();
                    var continuationToken = i < response.Pages.Count - 1 ? $"token-{i + 1}" : null;
                    yield return Page<T>.FromValues(values, continuationToken, new StubResponse(default));
                }
            }

            private static T Materialize<T>(ScriptedEntity source)
                where T : class, ITableEntity
            {
                var entity = Activator.CreateInstance<T>();
                entity.PartitionKey = source.PartitionKey;
                entity.RowKey = source.RowKey;
                entity.ETag = source.ETag;

                foreach (var (name, value) in source.Properties)
                {
                    if (entity is TableEntity tableEntity)
                    {
                        tableEntity[name] = value;
                    }
                    else
                    {
                        var property = typeof(T).GetProperty(name);
                        Assert.NotNull(property);
                        property.SetValue(entity, value);
                    }
                }

                return entity;
            }
        }

        private sealed class StubResponse(ETag etag) : Response
        {
            public override int Status => 204;

            public override string ReasonPhrase => "No Content";

            public override Stream? ContentStream { get; set; }

            public override string ClientRequestId { get; set; } = string.Empty;

            public override void Dispose()
            {
            }

            protected override bool ContainsHeader(string name) => TryGetHeader(name, out _);

            protected override IEnumerable<HttpHeader> EnumerateHeaders()
                => etag == default ? [] : [new HttpHeader("ETag", etag.ToString("H"))];

            protected override bool TryGetHeader(string name, out string value)
            {
                if (etag != default && string.Equals(name, "ETag", StringComparison.OrdinalIgnoreCase))
                {
                    value = etag.ToString("H");
                    return true;
                }

                value = string.Empty;
                return false;
            }

            protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
            {
                if (TryGetHeader(name, out var value))
                {
                    values = [value];
                    return true;
                }

                values = [];
                return false;
            }
        }
    }
}
