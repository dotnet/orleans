using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;
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
    public async Task ListAsync_PrefixUsesRawOrdinalStartsWith(string kind)
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
            ["tenant", "tenant/z", "tenant/a", "tenant/a/child", "tenant/", "tenant2", "tenantish/child", "tenant%2Fone", "tenant%2Fone/child"],
            await DrainAsync(context.Catalog.ListAsync(new() { Prefix = new("tenant") }, TestContext.Current.CancellationToken)));
        AssertMembership(
            ["tenant%2Fone", "tenant%2Fone/child"],
            await DrainAsync(context.Catalog.ListAsync(
                new() { Prefix = JournalId.Create("tenant/one") }, TestContext.Current.CancellationToken)));
        AssertMembership(["tenant/a", "tenant/a/child"], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant/a") }, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_OptionsAreReadAtEnumerationStartAndRemainStable(string kind)
    {
        await using var context = await CreateAsync(kind, ["tenant/z", "tenant/a", "tenant/b", "other/q"]);
        var options = new ListOptions { Prefix = new("other"), MinId = new("other"), MaxId = new("other") };
        var listing = context.Catalog.ListAsync(options, TestContext.Current.CancellationToken);
        options.Prefix = new("tenant");
        options.MinId = new("tenant/a");
        options.MaxId = new("tenant/b");
        await using var enumerator = listing.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        var result = new List<string> { enumerator.Current.Value };

        options.Prefix = new("other");
        options.MinId = new("other/q");
        options.MaxId = default;
        while (await enumerator.MoveNextAsync())
        {
            result.Add(enumerator.Current.Value);
        }

        AssertMembership(["tenant/a", "tenant/b"], result);
        AssertMembership(["other/q"], await DrainAsync(listing));
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_MinAndMaxAreInclusiveAndIntersectRawPrefix(string kind)
    {
        string[] ids = ["tenant/z", "tenant/a", "tenant/b", "tenant/b-extra", "tenant/c", "tenant2/a", "other"];
        await using var context = await CreateAsync(kind, ids);

        AssertMembership(["tenant/b", "tenant/b-extra", "tenant/c"], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MinId = new("tenant/b"), MaxId = new("tenant/c") }, TestContext.Current.CancellationToken)));
        AssertMembership(["tenant/b", "tenant/b-extra", "tenant/c"], await DrainAsync(context.Catalog.ListAsync(
            new() { MinId = new("tenant/b"), MaxId = new("tenant/c") }, TestContext.Current.CancellationToken)));
        AssertMembership(["tenant/b", "tenant/b-extra"], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant/b"), MinId = new("tenant/a"), MaxId = new("tenant/c") }, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_EmptyRangeDoesNotRequestStorage(string kind)
    {
        await using var context = await CreateAsync(kind, ["tenant/a"]);
        Assert.Empty(await DrainAsync(context.Catalog.ListAsync(
            new() { MinId = new("z"), MaxId = new("a") }, TestContext.Current.CancellationToken)));
        Assert.Empty(await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MinId = new("z") }, TestContext.Current.CancellationToken)));
        Assert.Empty(await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MaxId = new("a") }, TestContext.Current.CancellationToken)));
        Assert.Empty(context.Native.Requests);
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_MaxIdIsInclusiveOrdinalAndCombinedWithPrefix(string kind)
    {
        string[] ids = ["tenant/z", "tenant/a", "tenant/B", "tenant", "tenant/a/child", "other", "tenant2"];
        await using var context = await CreateAsync(kind, ids);

        AssertMembership(ids, await DrainAsync(context.Catalog.ListAsync(
            new() { MaxId = default }, TestContext.Current.CancellationToken)));
        AssertMembership(["other", "tenant", "tenant/B", "tenant/a"], await DrainAsync(context.Catalog.ListAsync(
            new() { MaxId = new("tenant/a") }, TestContext.Current.CancellationToken)));
        AssertMembership(["tenant", "tenant/B", "tenant/a"], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MaxId = new("tenant/a") }, TestContext.Current.CancellationToken)));
        Assert.Empty(await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MaxId = new("other") }, TestContext.Current.CancellationToken)));
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
        Assert.Equal(kind == "AzureBlob" ? "tenant/a" : "tenant/z", enumerator.Current.Value);
        Assert.Single(context.Native.Requests);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(kind == "AzureBlob" ? "tenant/b" : "tenant/a", enumerator.Current.Value);
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
        AssertMembership(
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
            Assert.Equal(kind == "AzureBlob" ? "a" : "z", enumerator.Current.Value);
            Assert.Single(context.Native.Requests);
        }

        Assert.Single(context.Native.Requests);
        Assert.Equal(kind == "S3" ? 0 : 1, context.Native.DisposedEnumerators);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_AdvanceCrossesEmptyNativePages(string kind)
    {
        await using var context = await CreateAsync(kind, ["tenant2", "tenantish/child", "tenant/valid"]);
        context.Native.EmptyFirstPage = true;
        await using var enumerator = context.Catalog.ListAsync(
            new() { Prefix = new("tenant/") }, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("tenant/valid", enumerator.Current.Value);
        Assert.Equal([0, 1], context.Native.Requests.Select(request => request.ResultCount));
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(2, context.Native.Requests.Count);
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
            await using var enumerator = context.Catalog.ListAsync(
                new() { MaxId = new("0") }, cancellation.Token).GetAsyncEnumerator(cancellation.Token);

            var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
            Assert.Equal(cancellation.Token, actual.CancellationToken);
            var request = Assert.Single(context.Native.Requests);
            Assert.Equal(empty || kind == "AzureTable" ? 0 : 2, request.ResultCount);
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
        AssertMembership(
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
            Assert.Contains($"PartitionKey ge '{AzureTableJournalStorageOptions.GetDefaultPartitionKey(new("tenant"))}'", context.Native.Filter);
            Assert.DoesNotContain(" or ", context.Native.Filter);
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

        Assert.Equal(["jobs/shards/a", "jobs/shards/child", "jobs/shards/z", "jobs/shards2"], result);
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
            ["tenant/child", "tenant", "tenant/z", "tenant2"],
            await DrainAsync(context.Catalog.ListAsync(new() { Prefix = new("tenant") }, TestContext.Current.CancellationToken)));
        Assert.Equal(8, context.Native.Requests.Sum(request => request.ResultCount));
    }

    [Fact]
    public async Task AzureBlobListAsync_MaxIdStopsBeforeFetchingFutureTail()
    {
        await using var context = await CreateAsync("AzureBlob", ["a", "b", "z", "zz", "zzz"]);
        context.Native.Failure = new InvalidOperationException("future tail must not be requested");
        context.Native.FailureAtRequest = 3;
        context.Native.EmptyFirstPage = true;

        Assert.Equal(["a"], await DrainAsync(context.Catalog.ListAsync(
            new() { MaxId = new("a") }, TestContext.Current.CancellationToken)));
        Assert.Equal([0, 2], context.Native.Requests.Select(request => request.ResultCount));
        Assert.Equal(1, context.Native.DisposedEnumerators);
    }

    [Fact]
    public async Task AzureBlobListAsync_MaxIdIncludesExactJournalWalAfterCheckpoint()
    {
        await using var context = await CreateAsync("AzureBlob", [],
            blobs: [Blob("a/chk.1", BlobType.Block), Blob("a/wal"), Blob("b/chk.1", BlobType.Block), Blob("b/wal"), Blob("z/wal")]);
        context.Native.Failure = new InvalidOperationException("future tail must not be requested");
        context.Native.FailureAtRequest = 3;

        Assert.Equal(["a"], await DrainAsync(context.Catalog.ListAsync(
            new() { MaxId = new("a") }, TestContext.Current.CancellationToken)));
        Assert.Equal([2, 2], context.Native.Requests.Select(request => request.ResultCount));
        Assert.Equal(1, context.Native.DisposedEnumerators);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("S3")]
    public async Task OrderedListAsync_TimePrefixedNamespaceStopsBeforeFuturePages(string kind)
    {
        const string prefix = "jobs/shards/";
        const string overdue = prefix + "20250101T0000000000000Z-11111111111111111111111111111111";
        const string due = prefix + "20260909T2100000000000Z-22222222222222222222222222222222";
        const string maximum = prefix + "20260909T2100000000000Z~";
        var ids = new List<string> { overdue, due, maximum };
        ids.AddRange(Enumerable.Range(0, 256).Select(index =>
            $"{prefix}20260909T2100010000000Z-{index.ToString("x32", CultureInfo.InvariantCulture)}"));
        ids.Add("jobs/shards");

        await using var context = await CreateAsync(kind, ids.ToArray(), configureS3: options => options.UseOrderedListing = true);
        context.Native.Failure = new InvalidOperationException("future tail must not be requested");
        context.Native.FailureAtRequest = 3;

        var result = await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new(prefix), MaxId = new(maximum) }, TestContext.Current.CancellationToken));

        Assert.Equal([overdue, due, maximum], result);
        Assert.Equal(2, context.Native.Requests.Count);
        Assert.All(context.Native.Requests, request =>
        {
            Assert.Equal(prefix, request.Prefix);
            Assert.Equal(prefix, request.LowerStart);
            Assert.Equal(kind == "AzureBlob" ? 5000 : 1000, request.Maximum);
            Assert.Equal(2, request.ResultCount);
        });
        Assert.Equal(kind == "AzureBlob" ? 1 : 0, context.Native.DisposedEnumerators);
    }

    [Theory]
    [InlineData("Append")]
    [InlineData("Block")]
    [InlineData("Missing")]
    public async Task AzureBlobListAsync_RawPrefixFiltersWalTypeAndDoesNotDuplicateIds(string exactWal)
    {
        var blobs = new List<BlobItem> { Blob("tenant/wal-child/wal"), Blob("tenant/z/wal") };
        if (exactWal != "Missing")
        {
            blobs.Add(Blob("tenant/wal", exactWal == "Append" ? BlobType.Append : BlobType.Block));
        }

        await using var context = await CreateAsync("AzureBlob", [], blobs: blobs.ToArray());
        var result = await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MaxId = new("tenant/z") }, TestContext.Current.CancellationToken));

        Assert.Equal(exactWal == "Append" ? ["tenant", "tenant/wal-child", "tenant/z"] : new[] { "tenant/wal-child", "tenant/z" }, result);
        Assert.All(context.Native.Requests, request => Assert.Equal("tenant", request.Prefix));
    }

    [Theory]
    [InlineData("a!")]
    [InlineData("a/0")]
    [InlineData("a/w")]
    [InlineData("a/wal!")]
    [InlineData("\u00e9")]
    public async Task AzureBlobListAsync_BoundedPrefixPreservesShorterDescendants(string suffix)
    {
        string[] ids = ["tenant", "tenant/a", "tenant/a!", "tenant/a/0", "tenant/a/w", "tenant/a/wal!", "tenant/\u00e9", "tenant/\uffff"];
        var maximum = "tenant/" + suffix;
        await using var context = await CreateAsync("AzureBlob", ids);

        AssertMembership(ids.Where(id => string.CompareOrdinal(id, maximum) <= 0).ToArray(),
            await DrainAsync(context.Catalog.ListAsync(
                new() { Prefix = new("tenant"), MaxId = new(maximum) }, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureBlobListAsync_BoundedPrefixDisposalOrCancellationStopsListing(bool cancel)
    {
        await using var context = await CreateAsync("AzureBlob", ["tenant", "tenant/a"]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using (var enumerator = context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MaxId = new("tenant/z") }, cancellation.Token).GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("tenant/a", enumerator.Current.Value);
            if (cancel)
            {
                cancellation.Cancel();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
                Assert.Equal(cancellation.Token, exception.CancellationToken);
            }
        }

        Assert.Equal("tenant", Assert.Single(context.Native.Requests).Prefix);
        Assert.Equal(1, context.Native.DisposedEnumerators);
    }

    [Fact]
    public async Task AzureBlobListAsync_BoundedPrefixCancellationAfterEmptyPagePropagates()
    {
        await using var context = await CreateAsync("AzureBlob", ["tenant/a"]);
        context.Native.EmptyFirstPage = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Native.BeforeResponse = cancellation.Cancel;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MaxId = new("tenant/z") }, cancellation.Token)));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var request = Assert.Single(context.Native.Requests);
        Assert.Equal("tenant", request.Prefix);
        Assert.Equal(0, request.ResultCount);
    }

    [Fact]
    public async Task AzureBlobListAsync_BoundedPrefixCrossesEmptyAndFilteredPages()
    {
        await using var context = await CreateAsync("AzureBlob", ["tenant", "tenant/a", "tenant/b"]);
        context.Native.EmptyFirstPage = true;

        Assert.Equal(["tenant/a", "tenant"], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MaxId = new("tenant/a") }, TestContext.Current.CancellationToken)));
        Assert.Equal([0, 2, 1], context.Native.Requests.Select(request => request.ResultCount));
        Assert.Equal(1, context.Native.DisposedEnumerators);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("tenant!")]
    public async Task AzureBlobListAsync_BoundedPrefixMaximumPreservesExactId(string maximum)
    {
        await using var context = await CreateAsync("AzureBlob", ["tenant", "tenant/a"]);

        Assert.Equal(["tenant"], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MaxId = new(maximum) }, TestContext.Current.CancellationToken)));
        Assert.Equal("tenant", Assert.Single(context.Native.Requests).Prefix);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AzureBlobListAsync_BoundedPrefixListingErrorPropagates(int failedRequest)
    {
        await using var context = await CreateAsync("AzureBlob", ["tenant/a", "tenant/b", "tenant/c"]);
        var failure = new RequestFailedException(503, "listing failed");
        context.Native.Failure = failure;
        context.Native.FailureAtRequest = failedRequest;

        Assert.Same(failure, await Assert.ThrowsAsync<RequestFailedException>(() => DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MaxId = new("tenant/z") }, TestContext.Current.CancellationToken))));
        Assert.Equal(failedRequest, context.Native.Requests.Count);
    }

    [Theory]
    [InlineData("AzureBlob", false)]
    [InlineData("AzureBlob", true)]
    [InlineData("S3", false)]
    [InlineData("S3", true)]
    public async Task OrderedListAsync_SeeksMinimumBeforeFetchingAnyPageAndIncludesBothEndpoints(string kind, bool includePrefix)
    {
        const string day = "jobs/shards/20260909";
        const string common = day + "T1200000000000Z-";
        const string minimum = common + "80000000000000000000000000000000";
        const string maximum = common + "c0000000000000000000000000000000";
        var ids = Enumerable.Range(0, 256).Select(index => common + index.ToString("x32", CultureInfo.InvariantCulture))
            .Concat([minimum, maximum, common + "f0000000000000000000000000000000"]).ToArray();
        await using var context = await CreateAsync(kind, ids, configureS3: options => options.UseOrderedListing = true);

        Assert.Equal([minimum, maximum], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = includePrefix ? new(day) : default, MinId = new(minimum), MaxId = new(maximum) }, TestContext.Current.CancellationToken)));
        Assert.Equal(2, context.Native.Requests.Count);
        Assert.Equal([2, 1], context.Native.Requests.Select(request => request.ResultCount));
        Assert.All(context.Native.Requests, request =>
        {
            Assert.Equal(common, request.Prefix);
            Assert.Equal(minimum, request.LowerStart);
        });
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("S3")]
    public async Task OrderedListAsync_PushesPartialDayPrefixWithoutAddingSeparator(string kind)
    {
        const string prefix = "jobs/shards/202609";
        string[] ids = ["jobs/shards/20260831-a", prefix + "01-a", prefix + "09-b", "jobs/shards/20261001-a"];
        await using var context = await CreateAsync(kind, ids, configureS3: options => options.UseOrderedListing = true);

        Assert.Equal(ids[1..3], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new(prefix) }, TestContext.Current.CancellationToken)));
        var request = Assert.Single(context.Native.Requests);
        Assert.Equal(prefix, request.Prefix);
        Assert.Equal(prefix, request.LowerStart);
        Assert.Equal(2, request.ResultCount);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("S3")]
    public async Task OrderedListAsync_UnicodeBoundsDoNotUseUnsafeNativeOrdering(string kind)
    {
        string[] ids = ["a", "\ud800\udc00", "\ue000", "\uffff"];
        await using var context = await CreateAsync(kind, ids, configureS3: options => options.UseOrderedListing = true);

        AssertMembership(ids[1..3], await DrainAsync(context.Catalog.ListAsync(
            new() { MinId = new(ids[1]), MaxId = new(ids[2]) }, TestContext.Current.CancellationToken)));
        Assert.Equal(2, context.Native.Requests.Count);
        Assert.Equal(4, context.Native.Requests.Sum(request => request.ResultCount));
        Assert.All(context.Native.Requests, request => Assert.Null(request.LowerStart));
    }

    [Theory]
    [InlineData("a!")]
    [InlineData("a/0")]
    [InlineData("a/w")]
    [InlineData("a/wal!")]
    public async Task AzureBlobListAsync_MaxIdPreservesShorterIdsWhoseWalSortsLater(string maximum)
    {
        string[] ids = ["a", "a!", "a/0", "a/w", "a/wal!", "z"];
        await using var context = await CreateAsync("AzureBlob", ids);
        AssertMembership(ids.Where(id => string.CompareOrdinal(id, maximum) <= 0).ToArray(),
            await DrainAsync(context.Catalog.ListAsync(
                new() { MaxId = new(maximum) }, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task AzureBlobListAsync_NonAsciiMaxIdFiltersWithoutLexicalCutoff()
    {
        await using var context = await CreateAsync("AzureBlob", ["a", "\u00e9", "\uffff"]);
        Assert.Equal(["a", "\u00e9"], await DrainAsync(context.Catalog.ListAsync(
            new() { MaxId = new("\u00e9") }, TestContext.Current.CancellationToken)));
        Assert.Equal(2, context.Native.Requests.Count);
        Assert.Equal(3, context.Native.Requests.Sum(request => request.ResultCount));
    }

    [Fact]
    public async Task AzureTableListAsync_PushesBoundsIntoIndexedPartitionKeyQuery()
    {
        const string prefix = "jobs/shards/202609";
        const string due = prefix + "09-a";
        const string maximum = prefix + "09~";
        const string future = prefix + "10-a";
        await using var context = await CreateAsync("AzureTable", [prefix + "08-a", due, maximum, future, "other/id"]);

        Assert.Equal([due, maximum], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new(prefix), MinId = new(due), MaxId = new(maximum) }, TestContext.Current.CancellationToken)));
        Assert.Equal(2, Assert.Single(context.Native.Requests).ResultCount);
        Assert.Contains($"PartitionKey ge '{AzureTableJournalStorageOptions.GetDefaultPartitionKey(new(due))}'", context.Native.Filter);
        Assert.Contains($"PartitionKey le '{AzureTableJournalStorageOptions.GetDefaultPartitionKey(new(maximum))}'", context.Native.Filter);
        Assert.Contains($"PartitionKey lt '{AzureTableJournalStorageOptions.GetDefaultPartitionKey(new(prefix))}G'", context.Native.Filter);
        Assert.DoesNotContain("JournalId ", context.Native.Filter);
        Assert.DoesNotContain(" or ", context.Native.Filter);
    }

    [Fact]
    public async Task AzureTableListAsync_CustomMappingFiltersCanonicalIdsAndEscapesQueryLiterals()
    {
        const string prefix = "tenant'one";
        string[] ids = [prefix, prefix + "/a", prefix + "/a/child", prefix + "/z"];
        await using var context = await CreateAsync("AzureTable", [],
            headers: ids.Select((id, index) => Header($"opaque-{index}", id)).ToArray(),
            configureTable: options => options.GetPartitionKey = id => $"opaque-{Array.IndexOf(ids, id.Value)}");

        Assert.Equal(ids[..3], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new(prefix), MaxId = new(prefix + "/a/child") }, TestContext.Current.CancellationToken)));
        Assert.Contains("JournalId ge 'tenant''one'", context.Native.Filter);
        Assert.Contains("JournalId le 'tenant''one/a/child'", context.Native.Filter);
        Assert.DoesNotContain("PartitionKey ", context.Native.Filter);
        Assert.Equal(3, context.Native.Requests.Sum(request => request.ResultCount));
    }

    [Fact]
    public async Task AzureTableListAsync_OnlyCanonicalHeaderIdsAreReturned()
    {
        TableEntity[] headers =
        [
            Header(AzureTableJournalStorageOptions.GetDefaultPartitionKey(new("tenant/z")), "tenant/z"),
            Header(AzureTableJournalStorageOptions.GetDefaultPartitionKey(new("tenant/a")), "tenant/a"),
            Header(AzureTableJournalStorageOptions.GetDefaultPartitionKey(new("missing/id"))),
            Header("invalid", ""), Header("whitespace", " \t "),
            new("orphan", "data") { [AzureTableJournalStorage.JournalIdPropertyName] = "ignored" },
        ];
        await using var context = await CreateAsync("AzureTable", [], headers: headers);
        Assert.Equal(
            ["tenant/z", "tenant/a"],
            await DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(5, context.Native.Requests.Sum(request => request.ResultCount));
    }

    [Fact]
    public async Task S3ListAsync_UsesCanonicalMappingAfterPrefixFilter()
    {
        var mapped = new List<string>();
        string[] keys =
        [
            "current/tenant/z/wal", "current/tenant/alias/wal", "current/tenant/a/chk.1",
            "current/tenant/a/wal", "current/other/x/wal", "invalid/wal", "default/wal",
        ];
        await using var context = await CreateAsync("S3", [], keys: keys, configureS3: options =>
        {
            options.GetObjectKey = id =>
            {
                mapped.Add(id.Value);
                return $"current/{id.Value}";
            };
            options.GetObjectKeyPrefix = id => $"current/{id.Value}";
            options.TryParseJournalId = value => value switch
            {
                "current/tenant/alias" => new JournalId("tenant/z"),
                "invalid" => null,
                "default" => default(JournalId),
                _ => new JournalId(value[(value.IndexOf('/') + 1)..]),
            };
        });

        Assert.Equal(
            ["tenant/z", "tenant/a"],
            await DrainAsync(context.Catalog.ListAsync(new() { Prefix = new("tenant") }, TestContext.Current.CancellationToken)));
        Assert.Equal(["tenant/z", "tenant/z", "tenant/a"], mapped);
        Assert.All(context.Native.Requests, request => Assert.Equal("current/", request.Prefix));
    }

    [Fact]
    public async Task S3ListAsync_MaxIdDoesNotStopUnorderedDirectoryBucketTraversal()
    {
        await using var context = await CreateAsync("S3", ["tenant/z", "tenant/y", "tenant/a", "tenant/b"]);
        context.Native.EmptyFirstPage = true;

        Assert.Equal(["tenant/a", "tenant/b"], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MinId = new("tenant/a"), MaxId = new("tenant/b") }, TestContext.Current.CancellationToken)));
        Assert.Equal([0, 2, 2], context.Native.Requests.Select(request => request.ResultCount));
        Assert.All(context.Native.Requests, request => Assert.Null(request.LowerStart));
    }

    [Theory]
    [InlineData("tenant", "tenant-a", "tenant-b", null)]
    [InlineData("jobs/shards/202609", "jobs/shards/20260909-a", "jobs/shards/20260909-b", "jobs/shards/")]
    public async Task S3ListAsync_DirectoryModeWidensPartialPrefixWithoutLosingBoundedIds(
        string prefix, string minimum, string maximum, string? nativePrefix)
    {
        string[] ids = [maximum + "-future", minimum[..^1] + "0", maximum, minimum, minimum + "-child"];
        await using var context = await CreateAsync("S3", ids, configureS3: options => options.UseOrderedListing = false);

        Assert.Equal([maximum, minimum, minimum + "-child"], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new(prefix), MinId = new(minimum), MaxId = new(maximum) }, TestContext.Current.CancellationToken)));
        Assert.Equal(5, context.Native.Requests.Sum(request => request.ResultCount));
        Assert.All(context.Native.Requests, request =>
        {
            Assert.Equal(nativePrefix, request.Prefix);
            Assert.Null(request.LowerStart);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3ListAsync_CustomMappingNeverUsesNativeIdentityBounds(bool ordered)
    {
        string[] ids = ["tenant/z", "tenant/a", "tenant/b", "tenant/0"];
        var mapping = ids.Select((id, index) => (id, key: $"current/{index}/{id}")).ToDictionary(item => item.id, item => item.key);
        await using var context = await CreateAsync("S3", [], keys: ids.Select(id => mapping[id] + "/wal").ToArray(), configureS3: options =>
        {
            options.UseOrderedListing = ordered;
            options.GetObjectKey = id => mapping[id.Value];
            options.GetObjectKeyPrefix = _ => "current/";
            options.TryParseJournalId = key => new JournalId(key["current/0/".Length..]);
        });

        Assert.Equal(["tenant/a", "tenant/b"], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant/"), MinId = new("tenant/a"), MaxId = new("tenant/b") }, TestContext.Current.CancellationToken)));
        Assert.Equal(4, context.Native.Requests.Sum(request => request.ResultCount));
        Assert.All(context.Native.Requests, request =>
        {
            Assert.Equal("current/", request.Prefix);
            Assert.Null(request.LowerStart);
        });
    }

    [Theory]
    [InlineData(false, "current/tenant/")]
    [InlineData(true, "current/tenant/a")]
    public async Task S3ListAsync_CustomMapperAcceptsRawPartialPrefixes(bool ordered, string nativePrefix)
    {
        await using var context = await CreateAsync("S3", [], keys:
            ["current/tenant/aa/wal", "current/tenant/ab/wal", "current/tenant/ac/wal"], configureS3: options =>
        {
            options.UseOrderedListing = ordered;
            options.GetObjectKey = id => "current/" + id.Value;
            options.GetObjectKeyPrefix = prefix => "current/" + prefix.Value;
            options.TryParseJournalId = key => new JournalId(key["current/".Length..]);
        });

        Assert.Equal(["tenant/aa", "tenant/ab"], await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant/a"), MinId = new("tenant/aa"), MaxId = new("tenant/ab") }, TestContext.Current.CancellationToken)));
        Assert.Equal(3, context.Native.Requests.Sum(request => request.ResultCount));
        Assert.All(context.Native.Requests, request =>
        {
            Assert.Equal(nativePrefix, request.Prefix);
            Assert.Null(request.LowerStart);
        });
    }

    [Fact]
    public async Task S3ListAsync_CustomMappingWithBoundsOnlyDoesNotRequirePrefixMapper()
    {
        await using var context = await CreateAsync("S3", [], keys: ["current/tenant/a/wal", "current/tenant/z/wal"], configureS3: options =>
        {
            options.UseOrderedListing = true;
            options.GetObjectKey = id => "current/" + id.Value;
            options.TryParseJournalId = key => new JournalId(key["current/".Length..]);
        });

        Assert.Equal(["tenant/a"], await DrainAsync(context.Catalog.ListAsync(
            new() { MinId = new("tenant/a"), MaxId = new("tenant/b") }, TestContext.Current.CancellationToken)));
        var request = Assert.Single(context.Native.Requests);
        Assert.Null(request.Prefix);
        Assert.Null(request.LowerStart);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task S3ListAsync_CustomMappingRequiresValidExplicitPrefix(string? mappedPrefix)
    {
        await using var context = await CreateAsync("S3", [], keys: ["current/tenant/wal"], configureS3: options =>
        {
            options.GetObjectKey = id => "current/" + id.Value;
            options.TryParseJournalId = key => new JournalId(key["current/".Length..]);
            if (mappedPrefix is not null)
            {
                options.GetObjectKeyPrefix = _ => mappedPrefix;
            }
        });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant") }, TestContext.Current.CancellationToken)));
        Assert.Contains(nameof(S3JournalStorageOptions.GetObjectKeyPrefix), failure.Message);
        Assert.Empty(context.Native.Requests);
        Assert.Equal(["tenant"], await DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Null(Assert.Single(context.Native.Requests).Prefix);
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
        Action<S3JournalStorageOptions>? configureS3 = null,
        Action<AzureTableJournalStorageOptions>? configureTable = null)
    {
        var context = new ProviderContext(kind, ids, blobLayout, blobs, headers, keys, configureS3, configureTable);
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
            TableEntity[]? headers, string[]? keys, Action<S3JournalStorageOptions>? configureS3,
            Action<AzureTableJournalStorageOptions>? configureTable)
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
                    var table = new FakeTable(Native, headers ?? ids.Select(id => Header(AzureTableJournalStorageOptions.GetDefaultPartitionKey(new(id)), id)).ToArray());
                    var tableOptions = new AzureTableJournalStorageOptions { TableName = "journals" };
                    configureTable?.Invoke(tableOptions);
                    tableOptions.ConfigureTableServiceClient(_ => Task.FromResult<TableServiceClient>(new FakeTableService(table)));
                    Provider = new AzureTableJournalStorageProvider(
                        Options.Create(tableOptions), manager, _services, NullLogger<AzureTableJournalStorage>.Instance);
                    break;
                case "S3":
                    _client = Substitute.For<IAmazonS3>();
                    _client.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(new HeadBucketResponse()));
                    var s3Options = new S3JournalStorageOptions { BucketName = "journals", S3Client = _client, UseOrderedListing = false };
                    configureS3?.Invoke(s3Options);
                    var objects = (keys ?? ids.Select(id => $"{id}/wal").ToArray()).Select(key => new S3Object { Key = key }).ToArray();
                    _client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).Returns(call =>
                    {
                        var request = call.Arg<ListObjectsV2Request>();
                        Assert.Equal("journals", request.BucketName);
                        var matching = objects.Where(item => (request.Prefix is null
                            || item.Key.StartsWith(request.Prefix, StringComparison.Ordinal))
                            && (request.StartAfter is null || string.CompareOrdinal(item.Key, request.StartAfter) > 0));
                        if (s3Options.UseOrderedListing)
                        {
                            matching = matching.OrderBy(item => item.Key, StringComparer.Ordinal);
                        }

                        var page = Native.Fetch(matching.ToArray(), request.ContinuationToken, request.MaxKeys, request.Prefix,
                            call.Arg<CancellationToken>(), request.StartAfter);
                        return Task.FromResult(new ListObjectsV2Response
                        {
                            S3Objects = page.Values.ToList(),
                            IsTruncated = page.NextCursor is not null,
                            NextContinuationToken = page.NextCursor,
                        });
                    });
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
        string? Cursor, int? Maximum, string? Prefix, CancellationToken CancellationToken, int ResultCount, string? NextCursor, string? LowerStart);
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

        public NativePage<T> Fetch<T>(T[] records, string? cursor, int? maximum, string? prefix, CancellationToken cancellationToken, string? lowerStart = null)
        {
            var offset = cursor is null ? 0 : int.Parse(cursor.AsSpan("native:".Length), CultureInfo.InvariantCulture);
            var count = cursor is null && EmptyFirstPage ? 0 : Math.Min(Math.Min(2, maximum ?? 2), records.Length - offset);
            var next = offset + count < records.Length ? $"native:{offset + count}" : null;
            Requests.Add(new(cursor, maximum, prefix, cancellationToken, count, next, lowerStart));
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

    private sealed class FakePageable<T>(NativeState state, T[] records, int? maximum, string? prefix, CancellationToken token, string? lowerStart = null) : AsyncPageable<T>
        where T : notnull
    {
        public override async IAsyncEnumerable<Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
        {
            try
            {
                do
                {
                    await Task.CompletedTask;
                    var page = state.Fetch(records, continuationToken, pageSizeHint ?? maximum, prefix, token, lowerStart);
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

        public override AsyncPageable<BlobItem> GetBlobsAsync(GetBlobsOptions options, CancellationToken cancellationToken = default)
        {
            Assert.Equal(BlobTraits.None, options.Traits);
            Assert.Equal(BlobStates.None, options.States);
            return new FakePageable<BlobItem>(
                state, records.Where(item => (options.Prefix is null || item.Name.StartsWith(options.Prefix, StringComparison.Ordinal))
                        && (options.StartFrom is null || string.CompareOrdinal(item.Name, options.StartFrom) >= 0))
                    .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
                null, options.Prefix, cancellationToken, options.StartFrom);
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
            Assert.NotNull(filter);
            Assert.Equal([AzureTableJournalStorage.JournalIdPropertyName], Assert.IsType<string[]>(state.Select));
            var values = records.Where(entity => MatchesFilter(entity, filter)).Select(entity =>
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

        private static bool MatchesFilter(TableEntity entity, string filter)
        {
            var tokens = Regex.Matches(filter, "'(?:[^']|'')*'|[()]|[^\\s()]+").Select(match => match.Value).ToArray();
            var index = 0;
            var result = ParseOr();
            Assert.Equal(tokens.Length, index);
            return result;

            bool ParseOr()
            {
                var value = ParseAnd();
                while (index < tokens.Length && tokens[index] == "or")
                {
                    index++;
                    value |= ParseAnd();
                }

                return value;
            }

            bool ParseAnd()
            {
                var value = ParseComparison();
                while (index < tokens.Length && tokens[index] == "and")
                {
                    index++;
                    value &= ParseComparison();
                }

                return value;
            }

            bool ParseComparison()
            {
                if (tokens[index] == "(")
                {
                    index++;
                    var value = ParseOr();
                    Assert.Equal(")", tokens[index++]);
                    return value;
                }

                var name = tokens[index++];
                var operation = tokens[index++];
                var expected = tokens[index++][1..^1].Replace("''", "'", StringComparison.Ordinal);
                var actual = name switch
                {
                    "PartitionKey" => entity.PartitionKey,
                    "RowKey" => entity.RowKey,
                    _ => entity.GetString(name),
                };
                if (actual is null)
                {
                    return false;
                }

                var comparison = string.CompareOrdinal(actual, expected);
                return operation switch
                {
                    "eq" => comparison == 0,
                    "ge" => comparison >= 0,
                    "le" => comparison <= 0,
                    "lt" => comparison < 0,
                    _ => throw new InvalidOperationException($"Unexpected filter operator: {operation}"),
                };
            }
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
