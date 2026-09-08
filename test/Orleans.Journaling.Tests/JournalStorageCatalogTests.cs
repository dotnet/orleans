using System.Buffers;
using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JournalStorageCatalogTests
{
    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_OptionsSelectExactAndDescendantIdentities(string kind)
    {
        string[] ids =
        [
            "tenant/z", "tenant2", "tenant", "tenant/a", "tenant/a/child", "tenantish/child",
            "tenant%2Fone", "tenant%2Fone/child", " leading space ", "raw\uD800", "raw\uD801",
            "unicode/\u00E9", "percent%2f", @"back\slash", "tenant/",
        ];
        await using var context = await CreateAsync(kind, ids);

        AssertMembership(ids, await DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
        AssertMembership(ids, await DrainAsync(context.Catalog.ListAsync(new(), TestContext.Current.CancellationToken)));
        AssertMembership(
            ["tenant", "tenant/z", "tenant/a", "tenant/a/child", "tenant/"],
            await DrainAsync(context.Catalog.ListAsync(new() { Prefix = new("tenant") }, TestContext.Current.CancellationToken)));
        AssertMembership(
            ["tenant%2Fone", "tenant%2Fone/child"],
            await DrainAsync(context.Catalog.ListAsync(
                new() { Prefix = JournalId.Create("tenant/one") }, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_OptionsAreReadAtEnumerationStartAndRemainStable(string kind)
    {
        await using var context = await CreateAsync(kind, ["tenant/z", "tenant/a", "tenant/b", "other/q"]);
        var options = new ListOptions { Prefix = new("other") };
        var listing = context.Catalog.ListAsync(options, TestContext.Current.CancellationToken);
        options.Prefix = new("tenant");
        await using var enumerator = listing.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        var result = new List<string> { enumerator.Current.Value };

        options.Prefix = new("other");
        while (await enumerator.MoveNextAsync())
        {
            result.Add(enumerator.Current.Value);
        }

        AssertMembership(["tenant/z", "tenant/a", "tenant/b"], result);
        AssertMembership(["other/q"], await DrainAsync(listing));
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_YieldsCurrentPageBeforeFetchingFailingTail(string kind)
    {
        await using var context = await CreateAsync(kind, ["tenant/z", "tenant/a", "tenant/b"]);
        var failure = new InvalidOperationException("later page failed");
        context.Native.Failure = failure;
        context.Native.FailureAtRequest = 2;
        await using var enumerator = context.Catalog.ListAsync(
            new() { Prefix = new("tenant") }, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.Empty(context.Native.Requests);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("tenant/z", enumerator.Current.Value);
        Assert.Single(context.Native.Requests);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("tenant/a", enumerator.Current.Value);
        Assert.Single(context.Native.Requests);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());
        Assert.Same(failure, actual);
        Assert.Equal(2, context.Native.Requests.Count);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(2, context.Native.Requests.Count);
        if (kind != "S3")
        {
            Assert.Equal(1, context.Native.DisposedEnumerators);
        }

        context.Native.Failure = null;
        Assert.Equal(
            ["tenant/z", "tenant/a", "tenant/b"],
            await DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Null(context.Native.Requests[2].Cursor);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_EarlyDisposalStopsFetchingPages(string kind)
    {
        await using var context = await CreateAsync(kind, ["z", "a", "b"]);
        await using (var enumerator = context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("z", enumerator.Current.Value);
            Assert.Single(context.Native.Requests);
        }

        Assert.Single(context.Native.Requests);
        Assert.Equal(kind == "S3" ? 0 : 1, context.Native.DisposedEnumerators);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_AdvanceCrossesEmptyAndFilteredNativePages(string kind)
    {
        await using var context = await CreateAsync(kind, ["tenant2", "tenantish/child", "tenant/valid"]);
        context.Native.EmptyFirstPage = true;
        await using var enumerator = context.Catalog.ListAsync(
            new() { Prefix = new("tenant") }, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("tenant/valid", enumerator.Current.Value);
        Assert.Equal(3, context.Native.Requests.Count);
        Assert.Equal([0, 2, 1], context.Native.Requests.Select(request => request.ResultCount));
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(3, context.Native.Requests.Count);
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_CancellationBeforeAndBetweenResultsPropagates(string kind)
    {
        await using var context = await CreateAsync(kind, ["z", "a", "b"]);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await using (var enumerator = context.Catalog.ListAsync(cancellationToken: canceled.Token).GetAsyncEnumerator(canceled.Token))
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
            Assert.Equal(canceled.Token, exception.CancellationToken);
            Assert.Empty(context.Native.Requests);
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var active = context.Catalog.ListAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await active.MoveNextAsync());
        var requests = context.Native.Requests.Count;
        cancellation.Cancel();
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.MoveNextAsync().AsTask());
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(requests, context.Native.Requests.Count);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_CancellationAfterNativeResponseIncludesEmptyPages(string kind)
    {
        foreach (var empty in new[] { false, true })
        {
            await using var context = await CreateAsync(kind, empty ? [] : ["z", "a"]);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            context.Native.BeforeResponse = cancellation.Cancel;
            await using var enumerator = context.Catalog.ListAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token);

            var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
            Assert.Equal(cancellation.Token, actual.CancellationToken);
            var request = Assert.Single(context.Native.Requests);
            Assert.Equal(empty ? 0 : 2, request.ResultCount);
            Assert.Equal(cancellation.Token, request.CancellationToken);
        }
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_NativeRequestsCarryPrefixContinuationAndCancellation(string kind)
    {
        await using var context = await CreateAsync(kind, ["tenant/z", "tenant/a", "tenant/b"]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Assert.Equal(
            ["tenant/z", "tenant/a", "tenant/b"],
            await DrainAsync(context.Catalog.ListAsync(new() { Prefix = new("tenant") }, cancellation.Token)));

        Assert.Equal(2, context.Native.Requests.Count);
        Assert.Null(context.Native.Requests[0].Cursor);
        Assert.Equal(context.Native.Requests[0].NextCursor, context.Native.Requests[1].Cursor);
        Assert.Null(context.Native.Requests[1].NextCursor);
        Assert.All(context.Native.Requests, request =>
        {
            Assert.Equal(kind == "AzureBlob" ? 5000 : 1000, request.Maximum);
            Assert.Equal(cancellation.Token, request.CancellationToken);
            Assert.Equal(kind == "AzureBlob" ? "tenant" : null, request.Prefix);
        });
        if (kind == "AzureTable")
        {
            Assert.Equal(
                TableClient.CreateQueryFilter($"RowKey eq {AzureTableJournalStorage.HeaderRowKey}"),
                context.Native.Filter);
            Assert.Equal([AzureTableJournalStorage.JournalIdPropertyName], Assert.IsType<string[]>(context.Native.Select));
        }
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_RequiresInitializedProvider(string kind)
    {
        await using var context = await CreateAsync(kind, ["z"], initialize: false);
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Contains("has not been initialized", unavailable.Message);
        Assert.Empty(context.Native.Requests);
        await context.InitializeAsync();
        Assert.Equal(["z"], await DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
        if (context.Provider is S3JournalStorageProvider s3)
        {
            await s3.CloseAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AzureBlobListAsync_FailedInitializationKeepsCatalogUnavailable(int failedSetup)
    {
        await using var context = await CreateAsync("AzureBlob", ["z"], initialize: false);
        var failure = new RequestFailedException(503, "setup failed");
        context.Native.FailingSetup = failedSetup;
        context.Native.SetupFailure = failure;

        Assert.Same(failure, await Assert.ThrowsAsync<RequestFailedException>(() => context.InitializeAsync()));
        Assert.Equal(failedSetup, context.Native.SetupCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Empty(context.Native.Requests);

        context.Native.SetupFailure = null;
        await context.InitializeAsync();
        Assert.Equal(["z"], await DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("EquivalentDelegate")]
    [InlineData("WrapperFactory")]
    public async Task AzureBlobListAsync_EquivalentConfigurationStreamsSameCatalog(string layout)
    {
        await using var context = await CreateAsync(
            "AzureBlob", ["jobs/shards/z", "jobs/shards2", "jobs/shards/a", "jobs/shards/child"], blobLayout: layout);
        var result = await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("jobs/shards") }, TestContext.Current.CancellationToken));

        Assert.Equal(["jobs/shards/z", "jobs/shards/a", "jobs/shards/child"], result);
        Assert.Equal(2, context.Native.Requests.Count);
        Assert.All(context.Native.Requests, request => Assert.Equal("jobs/shards", request.Prefix));
    }

    [Fact]
    public async Task AzureBlobListAsync_FiltersAppendWalEntries()
    {
        BlobItem[] blobs =
        [
            Blob("tenant/z/wal"), Blob("tenant/block/wal", BlobType.Block),
            Blob("tenant/page/wal", BlobType.Page), Blob("tenant/chk.1"),
            Blob("tenant/child/wal"), Blob("tenant/case/WAL"), Blob("/wal"),
            Blob(" \t /wal"), Blob("tenant2/wal"), Blob("tenant/wal"),
        ];
        await using var context = await CreateAsync("AzureBlob", [], blobs: blobs);
        Assert.Equal(
            ["tenant/z", "tenant/child", "tenant"],
            await DrainAsync(context.Catalog.ListAsync(new() { Prefix = new("tenant") }, TestContext.Current.CancellationToken)));
        Assert.Equal(8, context.Native.Requests.Sum(request => request.ResultCount));
    }

    [Fact]
    public async Task AzureTableListAsync_PreservesCanonicalAndReversibleLegacyIds()
    {
        TableEntity[] headers =
        [
            Header("opaque!1", "tenant/z"), Header("other%2Fid", "tenant/a"),
            Header("legacy%2F%C3%A9"), Header("legacy%2fchild"), Header("%41"),
            Header("bad%ZZ"), Header("fallback%2Fid", ""), Header("%20%09"),
            new("orphan", "data") { [AzureTableJournalStorage.JournalIdPropertyName] = "ignored" },
        ];
        await using var context = await CreateAsync("AzureTable", [], headers: headers);
        Assert.Equal(
            ["tenant/z", "tenant/a", "legacy/\u00E9"],
            await DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(8, context.Native.Requests.Sum(request => request.ResultCount));
    }

    [Fact]
    public async Task S3ListAsync_UsesCanonicalMappingAfterPrefixFilter()
    {
        var mapped = new List<string>();
        string[] keys =
        [
            "current/tenant/z/wal", "legacy/tenant/z/wal", "current/tenant/a/chk.1",
            "current/tenant/a/wal", "current/other/x/wal", "invalid/wal", "default/wal",
        ];
        await using var context = await CreateAsync("S3", [], keys: keys, configureS3: options =>
        {
            options.GetObjectKey = id =>
            {
                mapped.Add(id.Value);
                return $"current/{id.Value}";
            };
            options.TryParseJournalId = value => value switch
            {
                "invalid" => null,
                "default" => default(JournalId),
                _ => new JournalId(value[(value.IndexOf('/') + 1)..]),
            };
        });

        Assert.Equal(
            ["tenant/z", "tenant/a"],
            await DrainAsync(context.Catalog.ListAsync(new() { Prefix = new("tenant") }, TestContext.Current.CancellationToken)));
        Assert.Equal(["tenant/z", "tenant/z", "tenant/a"], mapped);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Append")]
    [InlineData("Replace")]
    public async Task VolatileListAsync_TracksExistenceAcrossNewEnumerations(string operation)
    {
        var provider = new VolatileJournalStorageProvider();
        var storage = provider.CreateStorage(new("tenant/id"));
        var listing = provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(await DrainAsync(listing));
        await MaterializeAsync();
        Assert.Equal(["tenant/id"], await DrainAsync(listing));
        await MaterializeAsync();
        Assert.Equal(["tenant/id"], await DrainAsync(listing));
        await storage.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await DrainAsync(listing));
        await MaterializeAsync();
        Assert.Equal(["tenant/id"], await DrainAsync(listing));

        async Task MaterializeAsync()
        {
            switch (operation)
            {
                case "Create":
                    await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
                    break;
                case "Append":
                    await storage.AppendAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), TestContext.Current.CancellationToken);
                    break;
                case "Replace":
                    await storage.ReplaceAsync(new ReadOnlySequence<byte>(new byte[] { 2 }), TestContext.Current.CancellationToken);
                    break;
            }
        }
    }

    private static void AssertMembership(string[] expected, IReadOnlyCollection<string> actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        Assert.Equal(expected.Length, actual.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    private static async Task<List<string>> DrainAsync(IAsyncEnumerable<JournalId> source)
    {
        var result = new List<string>();
        await foreach (var id in source)
        {
            result.Add(id.Value);
        }

        return result;
    }

    private static BlobItem Blob(string name, BlobType type = BlobType.Append)
        => BlobsModelFactory.BlobItem(
            name: name, deleted: false,
            properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false, blobType: type));

    private static TableEntity Header(string partition, string? id = null)
    {
        var result = new TableEntity(partition, AzureTableJournalStorage.HeaderRowKey);
        if (id is not null)
        {
            result[AzureTableJournalStorage.JournalIdPropertyName] = id;
        }

        return result;
    }

    private static async Task<ProviderContext> CreateAsync(
        string kind, string[] ids, bool initialize = true, string? blobLayout = null,
        BlobItem[]? blobs = null, TableEntity[]? headers = null, string[]? keys = null,
        Action<S3JournalStorageOptions>? configureS3 = null)
    {
        var context = new ProviderContext(kind, ids, blobLayout, blobs, headers, keys, configureS3);
        try
        {
            if (initialize)
            {
                await context.InitializeAsync();
            }

            if (kind == "Volatile")
            {
                foreach (var id in ids)
                {
                    Assert.True(await context.Provider.CreateStorage(new(id))
                        .CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
                }
            }

            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    private sealed class ProviderContext : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly RecordingLifecycle _lifecycle = new();
        private readonly IAmazonS3? _client;

        public ProviderContext(
            string kind, string[] ids, string? blobLayout, BlobItem[]? blobs,
            TableEntity[]? headers, string[]? keys, Action<S3JournalStorageOptions>? configureS3)
        {
            var services = new ServiceCollection();
            services.AddKeyedSingleton<IJournalFormat>("test", new TestFormat());
            _services = services.BuildServiceProvider();
            var manager = Options.Create(new JournaledStateManagerOptions { JournalFormatKey = "test" });
            switch (kind)
            {
                case "Volatile":
                    Provider = new VolatileJournalStorageProvider();
                    break;
                case "AzureBlob":
                    var container = new FakeContainer(Native, blobs ?? ids.Select(id => Blob($"{id}/wal")).ToArray());
                    var blobOptions = new AzureBlobJournalStorageOptions { ContainerName = "journals" };
                    blobOptions.ConfigureBlobServiceClient(_ => Task.FromResult<BlobServiceClient>(new FakeBlobService(container)));
                    if (blobLayout == "EquivalentDelegate")
                    {
                        blobOptions.GetWalBlobName = id => $"{id.Value}/wal";
                    }
                    else if (blobLayout == "WrapperFactory")
                    {
                        blobOptions.BuildContainerFactory = (_, _) => new WrapperFactory(container);
                    }

                    Provider = new AzureBlobJournalStorageProvider(
                        Options.Create(blobOptions), manager, _services, NullLogger<AzureBlobJournalStorage>.Instance);
                    break;
                case "AzureTable":
                    var table = new FakeTable(Native, headers ?? ids.Select((id, index) => Header($"mapped-{index}", id)).ToArray());
                    var tableOptions = new AzureTableJournalStorageOptions { TableName = "journals" };
                    tableOptions.ConfigureTableServiceClient(_ => Task.FromResult<TableServiceClient>(new FakeTableService(table)));
                    Provider = new AzureTableJournalStorageProvider(
                        Options.Create(tableOptions), manager, _services, NullLogger<AzureTableJournalStorage>.Instance);
                    break;
                case "S3":
                    _client = Substitute.For<IAmazonS3>();
                    _client.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(new HeadBucketResponse()));
                    var objects = (keys ?? ids.Select(id => $"{id}/wal").ToArray()).Select(key => new S3Object { Key = key }).ToArray();
                    _client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).Returns(call =>
                    {
                        var request = call.Arg<ListObjectsV2Request>();
                        Assert.Equal("journals", request.BucketName);
                        Assert.Null(request.Prefix);
                        Assert.Null(request.StartAfter);
                        var page = Native.Fetch(objects, request.ContinuationToken, request.MaxKeys, request.Prefix, call.Arg<CancellationToken>());
                        return Task.FromResult(new ListObjectsV2Response
                        {
                            S3Objects = page.Values.ToList(),
                            IsTruncated = page.NextCursor is not null,
                            NextContinuationToken = page.NextCursor,
                        });
                    });
                    var s3Options = new S3JournalStorageOptions { BucketName = "journals", S3Client = _client };
                    configureS3?.Invoke(s3Options);
                    Provider = new S3JournalStorageProvider(
                        Options.Create(s3Options), manager, _services, NullLogger<S3JournalStorage>.Instance);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }

            Catalog = Assert.IsAssignableFrom<IJournalStorageCatalog>(Provider);
            if (Provider is ILifecycleParticipant<ISiloLifecycle> participant)
            {
                participant.Participate(_lifecycle);
            }
        }

        public NativeState Native { get; } = new();
        public IJournalStorageProvider Provider { get; }
        public IJournalStorageCatalog Catalog { get; }

        public Task InitializeAsync()
            => Provider is VolatileJournalStorageProvider
                ? Task.CompletedTask : _lifecycle.StartAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Provider is S3JournalStorageProvider s3)
            {
                await s3.CloseAsync(TestContext.Current.CancellationToken);
                _client!.Dispose();
            }

            await _services.DisposeAsync();
        }
    }

    private sealed record NativeRequest(
        string? Cursor, int? Maximum, string? Prefix, CancellationToken CancellationToken, int ResultCount, string? NextCursor);
    private sealed record NativePage<T>(IReadOnlyList<T> Values, string? NextCursor);

    private sealed class NativeState
    {
        public List<NativeRequest> Requests { get; } = [];
        public bool EmptyFirstPage { get; set; }
        public Exception? Failure { get; set; }
        public int FailureAtRequest { get; set; }
        public Action? BeforeResponse { get; set; }
        public int DisposedEnumerators { get; set; }
        public int SetupCalls { get; set; }
        public int FailingSetup { get; set; }
        public Exception? SetupFailure { get; set; }
        public string? Filter { get; set; }
        public string[]? Select { get; set; }

        public NativePage<T> Fetch<T>(T[] records, string? cursor, int? maximum, string? prefix, CancellationToken cancellationToken)
        {
            var offset = cursor is null ? 0 : int.Parse(cursor.AsSpan("native:".Length), CultureInfo.InvariantCulture);
            var count = cursor is null && EmptyFirstPage ? 0 : Math.Min(2, records.Length - offset);
            var next = offset + count < records.Length ? $"native:{offset + count}" : null;
            Requests.Add(new(cursor, maximum, prefix, cancellationToken, count, next));
            if (Requests.Count == FailureAtRequest && Failure is { } failure)
            {
                throw failure;
            }

            var callback = BeforeResponse;
            BeforeResponse = null;
            callback?.Invoke();
            return new(records.AsSpan(offset, count).ToArray(), next);
        }
    }

    private sealed class FakePageable<T>(NativeState state, T[] records, int? maximum, string? prefix, CancellationToken token) : AsyncPageable<T>
        where T : notnull
    {
        public override async IAsyncEnumerable<Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
        {
            try
            {
                do
                {
                    await Task.CompletedTask;
                    var page = state.Fetch(records, continuationToken, pageSizeHint ?? maximum, prefix, token);
                    yield return Page<T>.FromValues(page.Values, page.NextCursor, new FakeResponse());
                    continuationToken = page.NextCursor;
                }
                while (continuationToken is not null);
            }
            finally
            {
                state.DisposedEnumerators++;
            }
        }
    }

    private sealed class FakeBlobService(FakeContainer container) : BlobServiceClient
    {
        public override BlobContainerClient GetBlobContainerClient(string blobContainerName)
        {
            Assert.Equal("journals", blobContainerName);
            return container;
        }
    }

    private sealed class FakeContainer(NativeState state, BlobItem[] records) : BlobContainerClient
    {
        public override Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(
            PublicAccessType publicAccessType = PublicAccessType.None,
            IDictionary<string, string>? metadata = null,
            BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null,
            CancellationToken cancellationToken = default)
        {
            if (++state.SetupCalls == state.FailingSetup && state.SetupFailure is { } failure)
            {
                return Task.FromException<Response<BlobContainerInfo>>(failure);
            }

            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContainerInfo(new ETag("created"), DateTimeOffset.UnixEpoch), new FakeResponse()));
        }

        public override AsyncPageable<BlobItem> GetBlobsAsync(
            BlobTraits traits = BlobTraits.None, BlobStates states = BlobStates.None,
            string? prefix = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(BlobTraits.None, traits);
            Assert.Equal(BlobStates.None, states);
            return new FakePageable<BlobItem>(
                state, records.Where(item => prefix is null || item.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray(),
                null, prefix, cancellationToken);
        }
    }

    private sealed class WrapperFactory(FakeContainer container) : IBlobContainerFactory
    {
        public BlobContainerClient GetBlobContainerClient(JournalId journalId) => container;
        public async Task InitializeAsync(BlobServiceClient client, CancellationToken cancellationToken)
            => await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    private sealed class FakeTableService(FakeTable table) : TableServiceClient
    {
        public override TableClient GetTableClient(string tableName)
        {
            Assert.Equal("journals", tableName);
            return table;
        }
    }

    private sealed class FakeTable(NativeState state, TableEntity[] records) : TableClient
    {
        public override Task<Response<TableItem>> CreateIfNotExistsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Response.FromValue(new TableItem("journals"), new FakeResponse()));

        public override AsyncPageable<T> QueryAsync<T>(
            string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
        {
            state.Filter = filter;
            state.Select = select?.ToArray();
            Assert.Equal(1000, maxPerPage);
            Assert.Equal(TableClient.CreateQueryFilter($"RowKey eq {AzureTableJournalStorage.HeaderRowKey}"), filter);
            Assert.Equal([AzureTableJournalStorage.JournalIdPropertyName], Assert.IsType<string[]>(state.Select));
            var values = records.Where(entity => entity.RowKey == AzureTableJournalStorage.HeaderRowKey).Select(entity =>
            {
                var projected = new TableEntity(entity.PartitionKey, entity.RowKey);
                if (entity.TryGetValue(AzureTableJournalStorage.JournalIdPropertyName, out var value))
                {
                    projected[AzureTableJournalStorage.JournalIdPropertyName] = value;
                }

                return (T)(ITableEntity)projected;
            }).ToArray();
            return new FakePageable<T>(state, values, maxPerPage, null, cancellationToken);
        }
    }

    private sealed class TestFormat : IJournalFormat
    {
        public string FormatKey => "test";
        public string? MimeType => "application/test";
        public JournalBufferWriter CreateWriter() => throw new NotSupportedException();
        public void Replay(JournalBufferReader input, JournalReplayContext context) => throw new NotSupportedException();
    }

    private sealed class RecordingLifecycle : ISiloLifecycle
    {
        private ILifecycleObserver? _observer;
        public int HighestCompletedStage => ServiceLifecycleStage.RuntimeInitialize;
        public int LowestStoppedStage => ServiceLifecycleStage.RuntimeInitialize;
        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            Assert.Null(_observer);
            _observer = observer;
            return new NoopDisposable();
        }

        public Task StartAsync(CancellationToken token) => Assert.IsAssignableFrom<ILifecycleObserver>(_observer).OnStart(token);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class FakeResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = string.Empty;
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value) { value = string.Empty; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
    }
}
