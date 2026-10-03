using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class FaultInjectionAzureTableTransactionStateStorageTests
{
    private const string ProviderName = "azure-faults";
    private const string Partition = "test-partition";

    [Fact]
    public async Task Store_NullMetadata_ThrowsWithMetadataParamNameBeforeInjectorOrStorage()
    {
        var events = new List<string>();
        var table = new RecordingTableClient(events);
        var injector = new RecordingFaultInjector(events);
        var storage = CreateStorage(table, injector);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.Store(
                expectedETag: "unobserved-etag",
                metadata: null!,
                statesToPrepare: null,
                commitUpTo: null,
                abortAfter: null));

        Assert.Equal("metadata", exception.ParamName);
        Assert.Empty(events);
        Assert.Equal(0, table.QueryCallCount);
        Assert.Empty(table.SubmittedTransactions);
    }

    [Fact]
    public async Task Load_ForwardsFreshSnapshotWithoutInvokingInjector()
    {
        var events = new List<string>();
        var table = new RecordingTableClient(events);
        var injector = new RecordingFaultInjector(events);
        var storage = CreateStorage(table, injector);

        var result = await storage.Load();

        Assert.Null(result.ETag);
        Assert.Equal(0, result.CommittedSequenceId);
        Assert.NotNull(result.CommittedState);
        Assert.Empty(result.Metadata.CommitRecords);
        Assert.Empty(result.PendingStates);
        Assert.Equal(1, table.QueryCallCount);
        Assert.Empty(events);
        Assert.Empty(injector.BeforeTransactionIds);
        Assert.Empty(injector.AfterTransactionIds);
    }

    [Fact]
    public async Task Store_ValidMetadataAndETag_ForwardsStorageCallBetweenInjectorCallbacks()
    {
        var events = new List<string>();
        var table = new RecordingTableClient(events);
        var injector = new RecordingFaultInjector(events);
        var storage = CreateStorage(table, injector);
        var firstTransactionId = new Guid("26891220-BECA-48A2-89A6-950751110A20");
        var secondTransactionId = new Guid("AB6A8FF5-5D3F-4A8E-B11E-2E302057CE5C");

        var loaded = await storage.Load();
        var firstMetadata = CreateMetadata(firstTransactionId, 101);
        var firstETag = await storage.Store(
            loaded.ETag,
            firstMetadata,
            statesToPrepare: null,
            commitUpTo: null,
            abortAfter: null);
        var secondMetadata = CreateMetadata(secondTransactionId, 202);
        var secondETag = await storage.Store(
            firstETag,
            secondMetadata,
            statesToPrepare: null,
            commitUpTo: null,
            abortAfter: null);

        Assert.Equal("etag-1", firstETag);
        Assert.Equal("etag-2", secondETag);
        Assert.Equal(
            ["before", "storage", "after", "before", "storage", "after"],
            events);
        Assert.Collection(
            injector.BeforeTransactionIds,
            ids => Assert.Equal([firstTransactionId], ids),
            ids => Assert.Equal([secondTransactionId], ids));
        Assert.Collection(
            injector.AfterTransactionIds,
            ids => Assert.Equal([firstTransactionId], ids),
            ids => Assert.Equal([secondTransactionId], ids));

        Assert.Equal(2, table.SubmittedTransactions.Count);
        var firstKey = Assert.Single(
            table.SubmittedTransactions[0],
            action => action.RowKey == "k");
        Assert.Equal(TableTransactionActionType.Add, firstKey.ActionType);
        Assert.Contains(firstTransactionId.ToString(), firstKey.Metadata);
        Assert.Contains("101", firstKey.Metadata);

        var secondKey = Assert.Single(
            table.SubmittedTransactions[1],
            action => action.RowKey == "k");
        Assert.Equal(TableTransactionActionType.UpdateReplace, secondKey.ActionType);
        Assert.Equal(firstETag, secondKey.ETag.ToString());
        Assert.Contains(secondTransactionId.ToString(), secondKey.Metadata);
        Assert.Contains("202", secondKey.Metadata);
    }

    [Fact]
    public void FactoryCreate_RegisteredNamedOptions_ReturnsWrapperFactoryWithoutStorageAccess()
    {
        var table = new RecordingTableClient([]);
        var tableService = new RecordingTableServiceClient(table);
        using var services = CreateServices(options => options.TableServiceClient = tableService);

        var result = FaultInjectionAzureTableTransactionStateStorageFactory.Create(
            services,
            ProviderName);

        Assert.IsType<FaultInjectionAzureTableTransactionStateStorageFactory>(result);
        Assert.Equal(0, tableService.GetTableClientCallCount);
        Assert.Equal(0, table.CreateIfNotExistsCallCount);
        Assert.Equal(0, table.QueryCallCount);
        Assert.Empty(table.SubmittedTransactions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Participate_CancellationStopsTableInitialization(bool cancelBeforeStart)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var table = new RecordingTableClient([]) { OnCreate = cancellation.Cancel };
        var tableService = new RecordingTableServiceClient(table);
        using var services = CreateServices(options => options.TableServiceClient = tableService);
        var inner = new AzureTableTransactionalStateStorageFactory(
            ProviderName,
            services.GetRequiredService<IOptionsMonitor<AzureTableTransactionalStateOptions>>().Get(ProviderName),
            services.GetRequiredService<IOptions<ClusterOptions>>(),
            services,
            NullLoggerFactory.Instance);
        var factory = new FaultInjectionAzureTableTransactionStateStorageFactory(inner);
        var lifecycle = new RecordingSiloLifecycle();
        factory.Participate(lifecycle);
        if (cancelBeforeStart)
        {
            cancellation.Cancel();
        }

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifecycle.StartAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(AzureTableTransactionalStateOptions.DEFAULT_INIT_STAGE, lifecycle.Stage);
        var expectedCreateCalls = cancelBeforeStart ? 0 : 1;
        Assert.Equal(expectedCreateCalls, tableService.GetTableClientCallCount);
        Assert.Equal(expectedCreateCalls, table.CreateIfNotExistsCallCount);
        Assert.Equal(cancelBeforeStart ? default : cancellation.Token, table.CreateCancellationToken);
        Assert.Equal(0, table.QueryCallCount);
        Assert.Empty(table.SubmittedTransactions);
    }

    [Fact]
    public async Task Participate_Start_ForwardsCancellationTokenToStorage()
    {
        var table = new RecordingTableClient([]);
        var tableService = new RecordingTableServiceClient(table);
        using var services = CreateServices(options => options.TableServiceClient = tableService);
        var inner = new AzureTableTransactionalStateStorageFactory(
            ProviderName,
            services.GetRequiredService<IOptionsMonitor<AzureTableTransactionalStateOptions>>().Get(ProviderName),
            services.GetRequiredService<IOptions<ClusterOptions>>(),
            services,
            NullLoggerFactory.Instance);
        var factory = new FaultInjectionAzureTableTransactionStateStorageFactory(inner);
        var lifecycle = new RecordingSiloLifecycle();
        factory.Participate(lifecycle);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await lifecycle.StartAsync(cancellation.Token);

        Assert.Equal(AzureTableTransactionalStateOptions.DEFAULT_INIT_STAGE, lifecycle.Stage);
        Assert.Equal(1, tableService.GetTableClientCallCount);
        Assert.Equal(1, table.CreateIfNotExistsCallCount);
        Assert.Equal(cancellation.Token, table.CreateCancellationToken);
        Assert.Equal(0, table.QueryCallCount);
        Assert.Empty(table.SubmittedTransactions);
    }

    private static FaultInjectionAzureTableTransactionStateStorage<TestState> CreateStorage(
        RecordingTableClient table,
        ITransactionFaultInjector injector)
    {
        var inner = new AzureTableTransactionalStateStorage<TestState>(
            table,
            Partition,
            new JsonSerializerSettings(),
            NullLogger<AzureTableTransactionalStateStorage<TestState>>.Instance);
        return new FaultInjectionAzureTableTransactionStateStorage<TestState>(injector, inner);
    }

    private static TransactionalStateMetaData CreateMetadata(Guid transactionId, long ticks) => new()
    {
        TimeStamp = new DateTime(ticks),
        CommitRecords =
        {
            [transactionId] = new CommitRecord
            {
                Timestamp = new DateTime(ticks + 1),
                WriteParticipants = [],
            },
        },
    };

    private static ServiceProvider CreateServices(
        Action<AzureTableTransactionalStateOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSerializer();
        services.AddSingleton(
            serviceProvider => new GrainReferenceActivator(
                serviceProvider,
                []));
        services.Configure<ClusterOptions>(options => options.ServiceId = "service");
        services.Configure<AzureTableTransactionalStateOptions>(ProviderName, configure);
        return services.BuildServiceProvider();
    }

    private sealed class RecordingFaultInjector(List<string> events)
        : ITransactionFaultInjector, ITransactionScopedFaultInjector
    {
        public List<IReadOnlyList<Guid>> BeforeTransactionIds { get; } = [];

        public List<IReadOnlyList<Guid>> AfterTransactionIds { get; } = [];

        public void BeforeStore() => throw new InvalidOperationException(
            "The transaction-scoped injector path was expected.");

        public void AfterStore() => throw new InvalidOperationException(
            "The transaction-scoped injector path was expected.");

        public void Arm(
            Guid transactionId,
            FaultInjectionType injectionType,
            bool requireTransactionMatch) =>
            throw new NotSupportedException();

        public void BeforeStore(System.Collections.Immutable.ImmutableArray<Guid> transactionIds)
        {
            BeforeTransactionIds.Add(transactionIds);
            events.Add("before");
        }

        public void AfterStore(System.Collections.Immutable.ImmutableArray<Guid> transactionIds)
        {
            AfterTransactionIds.Add(transactionIds);
            events.Add("after");
        }
    }

    private sealed class RecordingTableServiceClient(RecordingTableClient table) : TableServiceClient
    {
        public int GetTableClientCallCount { get; private set; }

        public override TableClient GetTableClient(string tableName)
        {
            GetTableClientCallCount++;
            Assert.Equal("TransactionalState", tableName);
            return table;
        }
    }

    private sealed class RecordingTableClient(List<string> events) : TableClient
    {
        public override string Name => "transactional-state";

        public int QueryCallCount { get; private set; }

        public int CreateIfNotExistsCallCount { get; private set; }

        public CancellationToken CreateCancellationToken { get; private set; }

        public Action? OnCreate { get; init; }

        public List<IReadOnlyList<SubmittedAction>> SubmittedTransactions { get; } = [];

        public override AsyncPageable<T> QueryAsync<T>(
            string? filter = null,
            int? maxPerPage = null,
            IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCallCount++;
            var page = Page<T>.FromValues([], null, new StubResponse(default));
            return AsyncPageable<T>.FromPages([page]);
        }

        public override Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(
            IEnumerable<TableTransactionAction> transactionActions,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("storage");
            var etag = new ETag($"etag-{SubmittedTransactions.Count + 1}");
            var actions = transactionActions
                .Select(action => new SubmittedAction(
                    action.ActionType,
                    action.Entity.RowKey,
                    action.ETag,
                    action.Entity is TableEntity entity
                        ? entity.GetString("Metadata") ?? string.Empty
                        : action.Entity.GetType().GetProperty("Metadata")?.GetValue(action.Entity) as string
                            ?? string.Empty))
                .ToList();
            SubmittedTransactions.Add(actions);
            IReadOnlyList<Response> responses = actions
                .Select(_ => (Response)new StubResponse(etag))
                .ToList();
            return Task.FromResult(
                Response.FromValue(responses, new StubResponse(default)));
        }

        public override Task<Response<TableItem>> CreateIfNotExistsAsync(
            CancellationToken cancellationToken = default)
        {
            CreateIfNotExistsCallCount++;
            CreateCancellationToken = cancellationToken;
            OnCreate?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Response.FromValue(
                    new TableItem(Name),
                    new StubResponse(default)));
        }
    }

    private sealed record SubmittedAction(
        TableTransactionActionType ActionType,
        string RowKey,
        ETag ETag,
        string Metadata);

    private sealed class RecordingSiloLifecycle : ISiloLifecycle
    {
        private ILifecycleObserver? _observer;

        public int HighestCompletedStage => 0;

        public int LowestStoppedStage => 0;

        public int? Stage { get; private set; }

        public IDisposable Subscribe(
            string observerName,
            int stage,
            ILifecycleObserver observer)
        {
            Assert.Null(_observer);
            Assert.Contains(nameof(AzureTableTransactionalStateStorageFactory), observerName);
            Stage = stage;
            _observer = observer;
            return NoopDisposable.Instance;
        }

        public Task StartAsync(CancellationToken cancellationToken) =>
            Assert.IsAssignableFrom<ILifecycleObserver>(_observer).OnStart(cancellationToken);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
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

        protected override IEnumerable<HttpHeader> EnumerateHeaders() =>
            etag == default ? [] : [new HttpHeader("ETag", etag.ToString("H"))];

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

        protected override bool TryGetHeaderValues(
            string name,
            out IEnumerable<string> values)
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

    private sealed class TestState
    {
        public string Value { get; set; } = string.Empty;
    }
}
