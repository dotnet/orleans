using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class AzureTableJournalStorageProviderTests
{
    private const string FormatKey = "test-format";

    [Fact]
    public void CreateStorage_DefaultJournalId_Throws()
    {
        using var context = CreateProvider();

        var exception = Assert.Throws<ArgumentException>(() => context.Provider.CreateStorage(default));

        Assert.Equal("journalId", exception.ParamName);
        Assert.StartsWith("The journal id must not be the default value.", exception.Message);
    }

    [Fact]
    public async Task CreateStorage_UsesConfiguredPartitionMapping()
    {
        var table = new FakeTableClient();
        var mappedJournalId = default(JournalId);
        var options = CreateOptions(table);
        options.GetPartitionKey = journalId =>
        {
            mappedJournalId = journalId;
            return $"tenant!{Uri.EscapeDataString(journalId.Value)}";
        };
        using var context = CreateProvider(options);
        await StartAsync(context.Provider, TestContext.Current.CancellationToken);
        var journalId = new JournalId("orders/42");

        var created = await context.Provider.CreateStorage(journalId)
            .CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(created);
        Assert.Equal(journalId, mappedJournalId);
        var added = Assert.Single(table.AddedEntities);
        Assert.Equal("tenant!orders%2F42", added.PartitionKey);
        Assert.Equal(AzureTableJournalStorage.HeaderRowKey, added.RowKey);
        Assert.Equal(journalId.Value, added[AzureTableJournalStorage.JournalIdPropertyName]);
    }

    [Fact]
    public void Participate_RegistersRuntimeInitializeStartupObserver()
    {
        using var context = CreateProvider();
        var lifecycle = new RecordingSiloLifecycle();

        context.Provider.Participate(lifecycle);

        Assert.Equal(nameof(AzureTableJournalStorageProvider), lifecycle.ObserverName);
        Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, lifecycle.Stage);
        Assert.NotNull(lifecycle.Observer);
    }

    [Fact]
    public async Task LifecycleStart_CreatesConfiguredTableAndMakesCatalogAvailable()
    {
        var table = new FakeTableClient();
        var service = new FakeTableServiceClient(table);
        var options = new AzureTableJournalStorageOptions { TableName = "tenantJournal" };
        CancellationToken receivedToken = default;
        options.ConfigureTableServiceClient(token =>
        {
            receivedToken = token;
            return Task.FromResult<TableServiceClient>(service);
        });
        using var context = CreateProvider(options);
        var lifecycle = new RecordingSiloLifecycle();
        context.Provider.Participate(lifecycle);
        using var cts = new CancellationTokenSource();

        await lifecycle.StartAsync(cts.Token);
        var listed = await ToListAsync(context.Provider.ListAsync(cancellationToken: cts.Token), cts.Token);

        Assert.Equal(cts.Token, receivedToken);
        Assert.Equal("tenantJournal", service.RequestedTableName);
        Assert.Equal(1, table.CreateIfNotExistsCalls);
        Assert.Equal(cts.Token, table.CreateCancellationToken);
        Assert.Empty(listed);
        Assert.Single(table.QueryCalls);
    }

    [Fact]
    public async Task LifecycleStart_WhenClientFactoryIsCanceled_PropagatesCancellationAndRemainsUninitialized()
    {
        var options = new AzureTableJournalStorageOptions();
        CancellationToken receivedToken = default;
        options.ConfigureTableServiceClient(token =>
        {
            receivedToken = token;
            return Task.FromCanceled<TableServiceClient>(token);
        });
        using var context = CreateProvider(options);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StartAsync(context.Provider, cts.Token));
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToListAsync(
                context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));

        Assert.Equal(cts.Token, receivedToken);
        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Equal(
            "AzureTableJournalStorageProvider has not been initialized. Ensure the silo lifecycle has started before using journal storage.",
            unavailable.Message);
    }

    [Fact]
    public async Task LifecycleStart_WhenClientFactoryFails_PropagatesOriginalFailure()
    {
        var expected = new InvalidOperationException("client factory failed");
        var options = new AzureTableJournalStorageOptions();
        options.ConfigureTableServiceClient(_ => Task.FromException<TableServiceClient>(expected));
        using var context = CreateProvider(options);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(context.Provider, TestContext.Current.CancellationToken));
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToListAsync(
                context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Contains("has not been initialized", unavailable.Message);
    }

    [Fact]
    public async Task LifecycleStart_WithoutClientConfiguration_ThrowsClearConfigurationError()
    {
        using var context = CreateProvider(new AzureTableJournalStorageOptions());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(context.Provider, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(AzureTableJournalStorageOptions.TableServiceClient), exception.Message);
        Assert.Contains(nameof(AzureTableJournalStorageOptions.ConfigureTableServiceClient), exception.Message);
    }

    [Fact]
    public async Task LifecycleStart_WhenClientFactoryReturnsNull_ThrowsClearConfigurationError()
    {
        var options = new AzureTableJournalStorageOptions();
        options.ConfigureTableServiceClient(_ => Task.FromResult<TableServiceClient>(null!));
        using var context = CreateProvider(options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(context.Provider, TestContext.Current.CancellationToken));

        Assert.Equal("The configured Azure Table service client factory returned null.", exception.Message);
    }

    [Fact]
    public async Task LifecycleStart_WhenTableCreationFails_PropagatesOriginalFailureAndRemainsUninitialized()
    {
        var expected = new RequestFailedException(503, "table unavailable");
        var table = new FakeTableClient { CreateException = expected };
        using var context = CreateProvider(CreateOptions(table));

        var actual = await Assert.ThrowsAsync<RequestFailedException>(
            () => StartAsync(context.Provider, TestContext.Current.CancellationToken));
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToListAsync(
                context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Equal(1, table.CreateIfNotExistsCalls);
        Assert.Contains("has not been initialized", unavailable.Message);
        Assert.Empty(table.QueryCalls);
    }

    [Fact]
    public async Task LifecycleStart_WhenTableCreationIsCanceled_PropagatesCancellationToken()
    {
        var table = new FakeTableClient();
        using var context = CreateProvider(CreateOptions(table));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StartAsync(context.Provider, cts.Token));

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Equal(cts.Token, table.CreateCancellationToken);
        Assert.Equal(1, table.CreateIfNotExistsCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Constructor_EmptyJournalFormatKey_ThrowsClearConfigurationError(string? configuredKey)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        using (services)
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => CreateProviderInstance(CreateOptions(new FakeTableClient()), configuredKey!, services));

            Assert.Equal("The configured journal format key must be non-empty.", exception.Message);
        }
    }

    [Fact]
    public void Constructor_MissingKeyedJournalFormat_IdentifiesKeyAndService()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateProviderInstance(CreateOptions(new FakeTableClient()), "missing-format", services));

        Assert.Equal(
            $"Journal format key 'missing-format' requires keyed service '{typeof(IJournalFormat).FullName}', but none was registered.",
            exception.Message);
    }

    [Fact]
    public void Constructor_MismatchedJournalFormat_IdentifiesRegistrationAndReportedKeys()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IJournalFormat>(FormatKey, new TestJournalFormat("reported-format"));
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateProviderInstance(CreateOptions(new FakeTableClient()), FormatKey, serviceProvider));

        Assert.Equal(
            $"Journal format key '{FormatKey}' resolved format '{typeof(TestJournalFormat).FullName}', but its FormatKey is 'reported-format'. " +
            "Register the journal format using the same key it reports.",
            exception.Message);
    }

    [Fact]
    public async Task Constructor_MatchingKeyedJournalFormat_CreatesUsableProvider()
    {
        var table = new FakeTableClient();
        using var context = CreateProvider(CreateOptions(table));
        await StartAsync(context.Provider, TestContext.Current.CancellationToken);

        var storage = context.Provider.CreateStorage(new JournalId("matching-format"));

        Assert.IsType<AzureTableJournalStorage>(storage);
        Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        var header = Assert.Single(table.AddedEntities);
        Assert.Equal(FormatKey, header[AzureTableJournalStorage.FormatPropertyName]);
        Assert.Equal(0L, header[AzureTableJournalStorage.RowCountPropertyName]);
        Assert.Equal(0L, header[AzureTableJournalStorage.LengthPropertyName]);
    }

    [Fact]
    public async Task ListAsync_EmptyTable_ReturnsEmptyAndSelectsOnlyCanonicalJournalId()
    {
        var table = new FakeTableClient();
        using var context = await CreateStartedProviderAsync(table, TestContext.Current.CancellationToken);

        var result = await ToListAsync(
            context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
        var query = Assert.Single(table.QueryCalls);
        Assert.Equal(
            TableClient.CreateQueryFilter($"RowKey eq {AzureTableJournalStorage.HeaderRowKey}"),
            query.Filter);
        Assert.Equal([AzureTableJournalStorage.JournalIdPropertyName], query.Select);
    }

    [Fact]
    public async Task ListAsync_MultipleHeaders_ReturnsOrdinallySortedJournalIds()
    {
        var table = new FakeTableClient();
        var lower = new JournalId("catalog/a");
        var upper = new JournalId("catalog/B");
        var last = new JournalId("catalog/z");
        table.AddHeader(last);
        table.AddHeader(lower);
        table.AddHeader(upper);
        using var context = await CreateStartedProviderAsync(table, TestContext.Current.CancellationToken);

        var result = await ToListAsync(
            context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal([upper, lower, last], result);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ListAsync_WithPrefix_ReturnsExactIdAndDescendantsOnly()
    {
        var table = new FakeTableClient();
        var prefix = new JournalId("tenant/orders");
        var exact = prefix;
        var child = new JournalId("tenant/orders/2026");
        table.AddHeader(new JournalId("tenant/order"));
        table.AddHeader(new JournalId("tenant/orders-archive"));
        table.AddHeader(new JournalId("tenant/payments"));
        table.AddHeader(child);
        table.AddHeader(exact);
        using var context = await CreateStartedProviderAsync(table, TestContext.Current.CancellationToken);

        var result = await ToListAsync(
            context.Provider.ListAsync(prefix, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal([exact, child], result);
        var query = Assert.Single(table.QueryCalls);
        Assert.Equal(
            TableClient.CreateQueryFilter($"RowKey eq {AzureTableJournalStorage.HeaderRowKey}"),
            query.Filter);
    }

    [Fact]
    public async Task ListAsync_DeletedHeader_IsNotReturned()
    {
        var table = new FakeTableClient();
        var retained = new JournalId("retained");
        var deleted = new JournalId("deleted");
        table.AddHeader(retained);
        table.AddHeader(deleted);
        table.RemoveHeader(deleted);
        using var context = await CreateStartedProviderAsync(table, TestContext.Current.CancellationToken);

        var result = await ToListAsync(
            context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal([retained], result);
        Assert.DoesNotContain(deleted, result);
    }

    [Fact]
    public async Task ListAsync_OrphanDataRowsAndMalformedHeader_AreIgnored()
    {
        var table = new FakeTableClient();
        var valid = new JournalId("valid");
        var orphan = new JournalId("orphan");
        table.AddHeader(valid);
        table.AddEntity(
            AzureTableJournalStorageOptions.GetDefaultPartitionKey(orphan),
            "g00000000000000000001-r0000000000");
        table.AddEntity("%20", AzureTableJournalStorage.HeaderRowKey);
        using var context = await CreateStartedProviderAsync(table, TestContext.Current.CancellationToken);

        var result = await ToListAsync(
            context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal([valid], result);
        Assert.DoesNotContain(orphan, result);
    }

    [Fact]
    public async Task ListAsync_EscapedJournalId_RoundTripsExactValue()
    {
        var table = new FakeTableClient();
        var escaped = new JournalId("tenant/slash\\hash#query?control\u0001");
        table.AddHeader(escaped);
        using var context = await CreateStartedProviderAsync(table, TestContext.Current.CancellationToken);

        var result = await ToListAsync(
            context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        var listed = Assert.Single(result);
        Assert.Equal(escaped, listed);
        Assert.Equal(
            "tenant%2Fslash%5Chash%23query%3Fcontrol%01",
            table.Entities.Single().PartitionKey);
    }

    [Fact]
    public async Task ListAsync_CustomPartitionMapping_UsesCanonicalJournalIdAndAppliesPrefix()
    {
        var table = new FakeTableClient();
        var included = new JournalId("tenant/orders/42");
        var excluded = new JournalId("other/orders/42");
        table.AddHeader(included, "hash-a");
        table.AddHeader(excluded, "hash-b");
        var options = CreateOptions(table);
        options.GetPartitionKey = static journalId => journalId.Value == "tenant/orders/42" ? "hash-a" : "hash-b";
        using var context = CreateProvider(options);
        await StartAsync(context.Provider, TestContext.Current.CancellationToken);

        var result = await ToListAsync(
            context.Provider.ListAsync(new JournalId("tenant/orders"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal([included], result);
        Assert.Equal(
            TableClient.CreateQueryFilter($"RowKey eq {AzureTableJournalStorage.HeaderRowKey}"),
            Assert.Single(table.QueryCalls).Filter);
    }

    [Fact]
    public async Task ListAsync_LegacyHeaderUsingDefaultPartitionMapping_IsStillReturned()
    {
        var table = new FakeTableClient();
        var journalId = new JournalId("legacy/orders/42");
        table.AddLegacyHeader(journalId);
        using var context = await CreateStartedProviderAsync(table, TestContext.Current.CancellationToken);

        var result = await ToListAsync(
            context.Provider.ListAsync(new JournalId("legacy/orders"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal([journalId], result);
    }

    [Fact]
    public async Task ListAsync_WhenCanceledBeforeQuery_ThrowsWithoutQuerying()
    {
        var table = new FakeTableClient();
        table.AddHeader(new JournalId("never-returned"));
        using var context = await CreateStartedProviderAsync(table, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToListAsync(context.Provider.ListAsync(cancellationToken: cts.Token), cts.Token));

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Empty(table.QueryCalls);
    }

    [Fact]
    public async Task ListAsync_WhenCanceledAfterQuery_ThrowsBeforeYieldingBufferedIds()
    {
        using var cts = new CancellationTokenSource();
        var table = new FakeTableClient { AfterQuery = cts.Cancel };
        table.AddHeader(new JournalId("buffered"));
        using var context = await CreateStartedProviderAsync(table, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToListAsync(context.Provider.ListAsync(cancellationToken: cts.Token), cts.Token));

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Single(table.QueryCalls);
    }

    private static AzureTableJournalStorageOptions CreateOptions(FakeTableClient table)
    {
        var options = new AzureTableJournalStorageOptions();
        options.ConfigureTableServiceClient(
            _ => Task.FromResult<TableServiceClient>(new FakeTableServiceClient(table)));
        return options;
    }

    private static ProviderContext CreateProvider(
        AzureTableJournalStorageOptions? options = null,
        string configuredKey = FormatKey)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IJournalFormat>(configuredKey, new TestJournalFormat(configuredKey));
        var serviceProvider = services.BuildServiceProvider();
        return new(
            CreateProviderInstance(
                options ?? CreateOptions(new FakeTableClient()),
                configuredKey,
                serviceProvider),
            serviceProvider);
    }

    private static AzureTableJournalStorageProvider CreateProviderInstance(
        AzureTableJournalStorageOptions options,
        string configuredKey,
        IServiceProvider serviceProvider)
        => new(
            Options.Create(options),
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = configuredKey }),
            serviceProvider,
            NullLogger<AzureTableJournalStorage>.Instance);

    private static async Task<ProviderContext> CreateStartedProviderAsync(
        FakeTableClient table,
        CancellationToken cancellationToken)
    {
        var context = CreateProvider(CreateOptions(table));
        try
        {
            await StartAsync(context.Provider, cancellationToken);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static Task StartAsync(
        AzureTableJournalStorageProvider provider,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = new RecordingSiloLifecycle();
        provider.Participate(lifecycle);
        return lifecycle.StartAsync(cancellationToken);
    }

    private static async Task<List<T>> ToListAsync<T>(
        IAsyncEnumerable<T> source,
        CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class ProviderContext(
        AzureTableJournalStorageProvider provider,
        ServiceProvider services) : IDisposable
    {
        public AzureTableJournalStorageProvider Provider { get; } = provider;

        public void Dispose() => services.Dispose();
    }

    private sealed class TestJournalFormat(string formatKey) : IJournalFormat
    {
        public string FormatKey { get; } = formatKey;

        public string? MimeType => "application/test";

        public JournalBufferWriter CreateWriter() => throw new NotSupportedException();

        public void Replay(JournalBufferReader input, JournalReplayContext context) => throw new NotSupportedException();
    }

    private sealed class RecordingSiloLifecycle : ISiloLifecycle
    {
        public string? ObserverName { get; private set; }

        public int? Stage { get; private set; }

        public ILifecycleObserver? Observer { get; private set; }

        public int HighestCompletedStage => Stage ?? 0;

        public int LowestStoppedStage => Stage ?? 0;

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            Assert.Null(Observer);
            ObserverName = observerName;
            Stage = stage;
            Observer = observer;
            return NoopDisposable.Instance;
        }

        public Task StartAsync(CancellationToken cancellationToken)
            => Assert.IsAssignableFrom<ILifecycleObserver>(Observer).OnStart(cancellationToken);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class FakeTableServiceClient(FakeTableClient table) : TableServiceClient
    {
        public string? RequestedTableName { get; private set; }

        public override TableClient GetTableClient(string tableName)
        {
            RequestedTableName = tableName;
            return table;
        }
    }

    private sealed class FakeTableClient : TableClient
    {
        private readonly List<TableEntity> _entities = [];

        public override string Name => "journal";

        public Exception? CreateException { get; set; }

        public int CreateIfNotExistsCalls { get; private set; }

        public CancellationToken CreateCancellationToken { get; private set; }

        public Action? AfterQuery { get; set; }

        public IReadOnlyList<TableEntity> Entities => _entities;

        public List<TableEntity> AddedEntities { get; } = [];

        public List<QueryCall> QueryCalls { get; } = [];

        public void AddHeader(JournalId journalId, string? partitionKey = null)
            => AddEntity(
                partitionKey ?? AzureTableJournalStorageOptions.GetDefaultPartitionKey(journalId),
                AzureTableJournalStorage.HeaderRowKey,
                new Dictionary<string, object>
                {
                    [AzureTableJournalStorage.JournalIdPropertyName] = journalId.Value,
                });

        public void AddLegacyHeader(JournalId journalId)
            => AddEntity(
                AzureTableJournalStorageOptions.GetDefaultPartitionKey(journalId),
                AzureTableJournalStorage.HeaderRowKey);

        public void RemoveHeader(JournalId journalId)
            => _entities.RemoveAll(entity =>
                entity.PartitionKey == AzureTableJournalStorageOptions.GetDefaultPartitionKey(journalId)
                && entity.RowKey == AzureTableJournalStorage.HeaderRowKey);

        public void AddEntity(
            string partitionKey,
            string rowKey,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            var entity = new TableEntity(partitionKey, rowKey);
            if (properties is not null)
            {
                foreach (var (key, value) in properties)
                {
                    entity[key] = value;
                }
            }

            _entities.Add(entity);
        }

        public override Task<Response<TableItem>> CreateIfNotExistsAsync(CancellationToken cancellationToken = default)
        {
            CreateIfNotExistsCalls++;
            CreateCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (CreateException is not null)
            {
                throw CreateException;
            }

            return Task.FromResult(
                Response.FromValue(new TableItem(Name), new FakeResponse(status: 201, eTag: default)));
        }

        public override Task<Response> AddEntityAsync<T>(
            T entity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var added = new TableEntity(entity.PartitionKey, entity.RowKey) { ETag = new ETag("created") };
            foreach (var property in (TableEntity)(ITableEntity)entity)
            {
                added[property.Key] = property.Value;
            }

            _entities.Add(added);
            AddedEntities.Add(added);
            return Task.FromResult<Response>(new FakeResponse(204, added.ETag));
        }

        public override AsyncPageable<T> QueryAsync<T>(
            string? filter = null,
            int? maxPerPage = null,
            IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCalls.Add(new(filter, select?.ToArray()));
            var values = _entities
                .Where(entity => entity.RowKey == AzureTableJournalStorage.HeaderRowKey)
                .Select(entity =>
                {
                    var projected = new TableEntity(entity.PartitionKey, entity.RowKey);
                    foreach (var propertyName in select ?? [])
                    {
                        if (entity.TryGetValue(propertyName, out var value))
                        {
                            projected[propertyName] = value;
                        }
                    }

                    return (T)(ITableEntity)projected;
                })
                .ToList();
            var page = Page<T>.FromValues(values, continuationToken: null, new FakeResponse(200, default));
            AfterQuery?.Invoke();
            return AsyncPageable<T>.FromPages([page]);
        }

    }

    private sealed record QueryCall(string? Filter, IReadOnlyList<string>? Select);

    private sealed class FakeResponse(int status, ETag eTag) : Response
    {
        public override int Status => status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => TryGetHeader(name, out _);

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
            => eTag == default ? [] : [new HttpHeader("ETag", eTag.ToString("H"))];

        protected override bool TryGetHeader(string name, out string value)
        {
            if (eTag != default && string.Equals(name, "ETag", StringComparison.OrdinalIgnoreCase))
            {
                value = eTag.ToString("H");
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
