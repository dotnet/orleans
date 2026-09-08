using System.Buffers;
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
public sealed class JournalStorageCatalogPagingTests
{
    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ReadPageAsync_StableTraversal_PreservesPrefixMembershipAndExactIdentities(string providerKind)
    {
        JournalId[] identities =
        [
            new("tenant/one"), new("tenant2"), new("Tenant/child"), new("tenant"),
            new("tenant/child"), new("tenantish/child"), new("tenant/one/deep"),
            JournalId.Create("tenant/one"),
            JournalId.Create("tenant/one", "hash#query?", "\u00E9"),
            new("tenant%2Foneish/child"), new("TENANT"), new(" surrounding spaces "),
            new("\u00E9/\u03A9"), new(@"back\slash"), new("percent%2f%25"), new("hash#query?"),
            new("control\u0001inside"), new("nul\0inside"), new("surrogate\uD800"), new("surrogate\uD801"),
            new("."), new(".."), new("trail/"), new("tenant/"),
        ];
        (JournalId Prefix, string[] Expected)[] scenarios =
        [
            (default,
            [
                "tenant/one", "tenant2", "Tenant/child", "tenant", "tenant/child",
                "tenantish/child", "tenant/one/deep", "tenant%2Fone",
                "tenant%2Fone/hash%23query%3F/%C3%A9", "tenant%2Foneish/child", "TENANT",
                " surrounding spaces ", "\u00E9/\u03A9", @"back\slash", "percent%2f%25", "hash#query?",
                "control\u0001inside", "nul\0inside", "surrogate\uD800", "surrogate\uD801", ".", "..", "trail/", "tenant/",
            ]),
            (new("tenant"), ["tenant", "tenant/one", "tenant/child", "tenant/one/deep", "tenant/"]),
            (JournalId.Create("tenant/one"), ["tenant%2Fone", "tenant%2Fone/hash%23query%3F/%C3%A9"]),
        ];

        foreach (var (prefix, expected) in scenarios)
        {
            await using var context = await CreateAsync(providerKind, identities);

            var actual = await SweepAsync(context, prefix, [2, 3, 1]);

            AssertExactMembership(expected, actual);
            Assert.All(actual, value => Assert.Contains(value, expected));
            if (context.IsCloud)
            {
                Assert.True(context.Native.Attempts > 1, $"{providerKind}: fixture must span native pages.");
                Assert.Equal(context.Native.Attempts, context.Native.Successes);
            }
        }
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ReadPageAsync_Continuation_AllowsSmallerAndLargerPageSizes(string providerKind)
    {
        await using var context = await CreateAsync(
            providerKind, Ids("z", "B", "a", "m", "C", "y", "d", "x", "e"), serverPageSize: 5);
        var first = await context.ReadAsync(default, 3);
        AssertValues(providerKind == "Volatile" ? ["B", "C", "a"] : ["z", "B", "a"], first);
        var token = Assert.IsType<string>(first.ContinuationToken);
        var nativeCursor = context.IsCloud ? context.Native.Fetches[^1].NextCursor : null;
        string[] expectedRemaining = providerKind == "Volatile"
            ? ["d", "e", "m", "x", "y", "z"]
            : ["m", "C", "y", "d", "x", "e"];

        foreach (var sizes in new int[][] { [1, 4, 2], [5, 1, 3] })
        {
            var start = context.Native.Fetches.Count;
            var remaining = await SweepAsync(context, default, sizes, token);

            AssertExactMembership(expectedRemaining, remaining);
            AssertExactMembership(
                ["z", "B", "a", "m", "C", "y", "d", "x", "e"],
                first.JournalIds.Select(id => id.Value).Concat(remaining).ToArray());
            if (context.IsCloud)
            {
                Assert.Equal(expectedRemaining, remaining);
                Assert.Equal(nativeCursor, context.Native.Fetches[start].Cursor);
                Assert.Equal(sizes[0], context.Native.Fetches[start].Maximum);
                Assert.Equal(sizes[0], context.Native.Fetches[start].ResultCount);
            }
        }
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ReadPageAsync_InvalidPageSize_ThrowsWithoutCloudRequests(string providerKind)
    {
        await using var context = await CreateAsync(providerKind, Ids("tenant/a", "tenant/b"));
        foreach (var size in new[] { 0, -1 })
        {
            var before = context.Native.Counts;

            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => context.ReadAsync(new("tenant"), size).AsTask());

            Assert.Equal("pageSize", exception.ParamName);
            Assert.Equal(before, context.Native.Counts);
        }
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ReadPageAsync_InvalidOrMisboundToken_ThrowsWithoutCloudRequests(string providerKind)
    {
        var identities = Ids("tenant/z", "tenant/B", "tenant/a", "other/q", "tenant\uD800/a", "tenant\uD800/b");
        await using var context = await CreateAsync(providerKind, identities);
        await using var other = await CreateAsync(providerKind, identities);
        var prefix = new JournalId("tenant");
        var first = await context.ReadAsync(default, 1);
        var defaultToken = Assert.IsType<string>(first.ContinuationToken);
        var explicitToken = Assert.IsType<string>((await context.ReadAsync(prefix, 1)).ContinuationToken);
        var foreignToken = Assert.IsType<string>((await other.ReadAsync(prefix, 1)).ContinuationToken);
        // The string constructor preserves arbitrary UTF-16. These distinct prefixes must not
        // collapse to the same replacement character when authenticating the scope.
        var exactUtf16Token = Assert.IsType<string>((await context.ReadAsync(new("tenant\uD800"), 1)).ContinuationToken);
        (JournalId Prefix, string Token)[] rejected =
        [
            (default, ""), (default, " \t "), (default, "not-a-catalog-token"),
            (default, defaultToken[..(defaultToken.Length / 2)]),
            (default, ModifyMiddleCharacter(defaultToken)),
            (new("other"), explicitToken), (prefix, defaultToken), (default, explicitToken),
            (prefix, foreignToken),
            (new("tenant\uD801"), exactUtf16Token),
        ];

        foreach (var (requestedPrefix, invalidToken) in rejected)
        {
            var before = context.Native.Counts;

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => context.ReadAsync(requestedPrefix, 2, invalidToken).AsTask());

            Assert.Equal("continuationToken", exception.ParamName);
            Assert.Equal(before, context.Native.Counts);
        }

        var fresh = await context.ReadAsync(default, 1);
        AssertValues(providerKind == "Volatile" ? ["other/q"] : ["tenant/z"], fresh);
        Assert.NotNull(fresh.ContinuationToken);
        Assert.Equal(first.JournalIds, fresh.JournalIds);
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ReadPageAsync_PreCanceled_ThrowsWithCallerTokenWithoutCloudRequests(string providerKind)
    {
        await using var context = await CreateAsync(providerKind, Ids("tenant/a", "tenant/b"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var before = context.Native.Counts;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.ReadAsync(new("tenant"), 2, cancellationToken: cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(before, context.Native.Counts);
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_PreservesOrdinalOrder_WhenNativePagingIsUnsorted(string providerKind)
    {
        await using var context = await CreateAsync(providerKind, Ids("z", "B", "a"), serverPageSize: 2);

        var actual = await context.ListAsync();

        Assert.Equal(["B", "a", "z"], actual);
        Assert.Equal(3, actual.Distinct(StringComparer.Ordinal).Count());
        if (context.IsCloud)
        {
            Assert.Equal(new NativeCounts(providerKind == "S3" ? 2 : 1, providerKind == "S3" ? 0 : 1, 2, 2),
                context.Native.Counts);
            Assert.Null(context.Native.Fetches[0].Cursor);
            Assert.Equal(context.Native.Fetches[0].NextCursor, context.Native.Fetches[1].Cursor);
            Assert.Equal(2, context.Native.Fetches[0].ResultCount);
            Assert.Equal(1, context.Native.Fetches[1].ResultCount);
            Assert.Null(context.Native.Fetches[1].NextCursor);
        }
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ReadPageAsync_CloudPage_IsSingleNativePage_AndEmptyContinuationCanResume(string providerKind)
    {
        foreach (var emptyNativePage in new[] { false, true })
        {
            await using var context = await CreateAsync(
                providerKind,
                emptyNativePage ? Ids("tenant/valid") : Ids("tenant2", "tenantish/child", "tenant/valid"),
                serverPageSize: 2, emptyFirstPage: emptyNativePage);

            var first = await context.ReadAsync(new("tenant"), 2);

            AssertValues([], first);
            var token = Assert.IsType<string>(first.ContinuationToken);
            Assert.Equal(new NativeCounts(1, providerKind == "S3" ? 0 : 1, 1, 1), context.Native.Counts);
            var native = Assert.Single(context.Native.Fetches);
            Assert.Equal(emptyNativePage ? 0 : 2, native.ResultCount);
            Assert.NotNull(native.NextCursor);

            var second = await context.ReadAsync(new("tenant"), 1, token);

            AssertValues(["tenant/valid"], second);
            Assert.Null(second.ContinuationToken);
            Assert.Equal(native.NextCursor, context.Native.Fetches[1].Cursor);
            Assert.Equal(new NativeCounts(2, providerKind == "S3" ? 0 : 2, 2, 2), context.Native.Counts);
        }
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ReadPageAsync_CloudRequest_UsesNativeCursorAndProviderCap(string providerKind)
    {
        var cap = providerKind == "AzureBlob" ? 5000 : 1000;
        foreach (var prefix in new JournalId[] { default, new(" tenant ") })
        {
            await using var context = await CreateAsync(
                providerKind, Ids(" tenant /z", " tenant /B", " tenant /a"), serverPageSize: 2);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            var first = await context.ReadAsync(prefix, cap + 1, cancellationToken: cancellation.Token);

            AssertValues([" tenant /z", " tenant /B"], first);
            var token = Assert.IsType<string>(first.ContinuationToken);
            AssertNativeRequest(context, 0, prefix, cap, null, cancellation.Token);
            var nextNativeCursor = Assert.IsType<string>(context.Native.Fetches[0].NextCursor);

            var second = await context.ReadAsync(prefix, 1, token, cancellation.Token);

            AssertValues([" tenant /a"], second);
            Assert.Null(second.ContinuationToken);
            AssertNativeRequest(context, 1, prefix, 1, nextNativeCursor, cancellation.Token);
            Assert.Equal(new NativeCounts(2, providerKind == "S3" ? 0 : 2, 2, 2), context.Native.Counts);
        }
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ReadPageAsync_CloudResponseCanceled_ThrowsWithCallerTokenAfterOneNativePage(string providerKind)
    {
        foreach (var emptyResponse in new[] { false, true })
        {
            await using var context = await CreateAsync(providerKind,
                emptyResponse ? [] : Ids("tenant/a", "tenant/b", "tenant/c"));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            context.Native.BeforeSuccessfulResponse = cancellation.Cancel;

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => context.ReadAsync(new("tenant"), 2, cancellationToken: cancellation.Token).AsTask());

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(new NativeCounts(1, providerKind == "S3" ? 0 : 1, 1, 1), context.Native.Counts);
            var native = Assert.Single(context.Native.Fetches);
            Assert.Equal(emptyResponse ? 0 : 2, native.ResultCount);
            Assert.Equal(cancellation.Token, native.CancellationToken);
            Assert.Equal(emptyResponse, native.NextCursor is null);
            Assert.Null(context.Native.BeforeSuccessfulResponse);
        }
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ReadPageAsync_CloudNativePageFailure_PropagatesSameException(string providerKind)
    {
        await using var context = await CreateAsync(providerKind, Ids("tenant/a", "tenant/b"));
        Exception expected = providerKind == "S3"
            ? new AmazonS3Exception("scripted native page failure")
            : new RequestFailedException(503, "scripted native page failure");
        context.Native.Failure = expected;

        var actual = providerKind == "S3"
            ? (Exception)await Assert.ThrowsAsync<AmazonS3Exception>(() => context.ReadAsync(default, 1).AsTask())
            : await Assert.ThrowsAsync<RequestFailedException>(() => context.ReadAsync(default, 1).AsTask());

        Assert.Same(expected, actual);
        Assert.Equal(new NativeCounts(1, providerKind == "S3" ? 0 : 1, 1, 0), context.Native.Counts);
        Assert.Null(Assert.Single(context.Native.Fetches).ResultCount);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ReadPageAsync_CloudInitialization_RejectsOldTokensAndRequiresInitializedState(string providerKind)
    {
        await using var context = await CreateAsync(
            providerKind, Ids("z", "B", "a"), initialize: false, serverPageSize: 1);
        var before = context.Native.Counts;

        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() => context.ReadAsync(default, 1).AsTask());

        Assert.Contains("has not been initialized", unavailable.Message);
        Assert.Equal(before, context.Native.Counts);
        Assert.Equal(0, context.Native.SetupCalls);
        await context.InitializeAsync();
        var first = await context.ReadAsync(default, 1);
        AssertValues(["z"], first);
        var oldToken = Assert.IsType<string>(first.ContinuationToken);
        before = context.Native.Counts;

        if (providerKind == "S3")
        {
            await context.CloseAsync();
            unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() => context.ReadAsync(default, 1).AsTask());
            Assert.Contains("has not been initialized", unavailable.Message);
            Assert.Equal(before, context.Native.Counts);
            context.S3Client!.DidNotReceive().Dispose();
        }

        await context.InitializeAsync();
        Assert.Equal(providerKind == "AzureBlob" ? 4 : 2, context.Native.SetupCalls);
        Assert.Equal(before, context.Native.Counts);

        var rejected = await Assert.ThrowsAsync<ArgumentException>(() => context.ReadAsync(default, 1, oldToken).AsTask());

        Assert.Equal("continuationToken", rejected.ParamName);
        Assert.Equal(before, context.Native.Counts);
        var fresh = await SweepAsync(context, default, [1, 2]);
        Assert.Equal(["z", "B", "a"], fresh);
        AssertExactMembership(["z", "B", "a"], fresh);
        Assert.Equal(before.Attempts + 3, context.Native.Attempts);
    }

    [Theory]
    [InlineData("CreateIfNotExistsAsync")]
    [InlineData("AppendAsync")]
    [InlineData("ReplaceAsync")]
    public async Task VolatileReadPageAsync_ExistenceChanges_UpdateCatalogWithoutDuplicates(string materialization)
    {
        await using var context = await CreateAsync("Volatile", []);
        var storage = context.Provider.CreateStorage(new("tenant/active"));
        Assert.Empty(await SweepAsync(context, default, [1]));

        await MaterializeAsync(storage, materialization, expectedCreated: true);
        AssertExactMembership(["tenant/active"], await SweepAsync(context, default, [1]));

        await MaterializeAsync(storage, materialization, expectedCreated: false);
        await WriteExistingAsync(storage);
        AssertExactMembership(["tenant/active"], await SweepAsync(context, default, [1]));
        Assert.Empty(await SweepAsync(context, new("tenant2"), [1]));

        await storage.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await SweepAsync(context, default, [1]));

        await MaterializeAsync(storage, materialization, expectedCreated: true);
        AssertExactMembership(["tenant/active"], await SweepAsync(context, new("tenant"), [1]));
        AssertExactMembership(["tenant/active"], await SweepAsync(context, default, [2]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AzureBlobReadPageAsync_FailedInitialization_KeepsCatalogUnavailable(int failingSetupCall)
    {
        await using var context = await CreateAsync("AzureBlob", Ids("tenant/a"), initialize: false);
        var expected = new RequestFailedException(503, "container initialization failed");
        context.Native.FailingSetupCall = failingSetupCall;
        context.Native.SetupFailure = expected;

        var actual = await Assert.ThrowsAsync<RequestFailedException>(() => context.InitializeAsync());

        Assert.Same(expected, actual);
        Assert.Equal(failingSetupCall, context.Native.SetupCalls);
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.ReadAsync(default, 1).AsTask());
        Assert.Contains("has not been initialized", unavailable.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.ListAsync());
        Assert.Equal(new NativeCounts(0, 0, 0, 0), context.Native.Counts);

        context.Native.SetupFailure = null;
        await context.InitializeAsync();

        Assert.Equal(failingSetupCall + 2, context.Native.SetupCalls);
        AssertValues(["tenant/a"], await context.ReadAsync(default, 1));
    }

    [Theory]
    [InlineData("CreateIfNotExistsAsync")]
    [InlineData("AppendAsync")]
    [InlineData("ReplaceAsync")]
    public async Task VolatileStorage_ExistenceCallback_RunsUnderStoreLock(string materialization)
    {
        var store = new VolatileJournalStorage.Store("tenant/callback");
        var transitions = new List<bool>();
        var storage = new VolatileJournalStorage(store, journalFormatKey: null, onExistenceChanged: (identity, exists) =>
        {
            Assert.True(Monitor.IsEntered(store.SyncRoot), $"{materialization}: callback must run under the store lock.");
            Assert.Equal("tenant/callback", identity);
            Assert.Equal(store.Exists, exists);
            transitions.Add(exists);
        });

        await MaterializeAsync(storage, materialization, expectedCreated: true);
        Assert.Equal([true], transitions);
        await MaterializeAsync(storage, materialization, expectedCreated: false);
        await WriteExistingAsync(storage);
        Assert.Equal([true], transitions);

        await storage.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.False(store.Exists);
        Assert.Equal([true, false], transitions);

        await MaterializeAsync(storage, materialization, expectedCreated: true);
        Assert.True(store.Exists);
        Assert.Equal([true, false, true], transitions);
    }

    [Fact]
    public async Task VolatileReadPageAsync_PrefixRange_SeeksPastUnrelatedIdsAndBoundsFiltering()
    {
        await using var context = await CreateAsync("Volatile", Ids("a", "b", "tenant", "tenant!", "tenant.", "tenant/child", "tenant0", "z"));
        var prefix = new JournalId("tenant");

        var exact = await context.ReadAsync(prefix, 1);
        AssertValues(["tenant"], exact);
        var token = Assert.IsType<string>(exact.ContinuationToken);

        var filtered = await context.ReadAsync(prefix, 2, token);

        AssertValues([], filtered);
        token = Assert.IsType<string>(filtered.ContinuationToken);
        var child = await context.ReadAsync(prefix, 1, token);
        AssertValues(["tenant/child"], child);
        // An exactly-full final page may require a terminal empty probe.
        if (child.ContinuationToken is { } lastToken)
        {
            var terminal = await context.ReadAsync(prefix, 1, lastToken);
            AssertValues([], terminal);
            Assert.Null(terminal.ContinuationToken);
        }

        var absent = await context.ReadAsync(new("missing"), 1);
        AssertValues([], absent);
        Assert.Null(absent.ContinuationToken);
    }

    [Fact]
    public async Task VolatileReadPageAsync_Continuation_UsesExclusiveIdentityCursorDuringMutation()
    {
        await using var context = await CreateAsync("Volatile", Ids("b", "d", "f", "h"));
        var d = context.Provider.CreateStorage(new("d"));
        var f = context.Provider.CreateStorage(new("f"));
        var first = await context.ReadAsync(default, 2);
        AssertValues(["b", "d"], first);
        var token = Assert.IsType<string>(first.ContinuationToken);

        foreach (var identity in new[] { "a", "c" })
        {
            Assert.True(await context.Provider.CreateStorage(new(identity))
                .CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        await d.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.True(await d.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await context.Provider.CreateStorage(new("e"))
            .CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        await f.DeleteAsync(TestContext.Current.CancellationToken);

        var resumed = await context.ReadAsync(default, 2, token);

        AssertValues(["e", "h"], resumed);
        if (resumed.ContinuationToken is { } finalToken)
        {
            var terminal = await context.ReadAsync(default, 2, finalToken);
            AssertValues([], terminal);
            Assert.Null(terminal.ContinuationToken);
        }

        var current = await SweepAsync(context, default, [2, 1]);
        Assert.Equal(["a", "b", "c", "d", "e", "h"], current);
        AssertExactMembership(["a", "b", "c", "d", "e", "h"], current);
    }

    [Fact]
    public async Task AzureBlobReadPageAsync_FiltersNativeItemsWithoutNormalizingIdentities()
    {
        BlobItem[] items =
        [
            Blob("tenant/z/wal"), Blob("tenant/block/wal", BlobType.Block),
            Blob("tenant/page/wal", BlobType.Page), Blob("tenant/chk.123"),
            Blob("tenant/child/wal"), Blob("tenant/bad/WAL"), Blob("tenant/bad/wal/extra"),
            Blob("/wal"), Blob(" \t /wal"), Blob("tenant2/wal"), Blob("tenantish/child/wal"),
            Blob("tenant/wal"), Blob("tenant//wal"), Blob(" tenant /raw /wal"),
        ];
        (JournalId Prefix, string[] Expected, int NativeCount)[] scenarios =
        [
            (default, ["tenant/z", "tenant/child", "tenant2", "tenantish/child", "tenant", "tenant/", " tenant /raw "], 14),
            (new("tenant"), ["tenant/z", "tenant/child", "tenant", "tenant/"], 11),
        ];
        foreach (var (prefix, expected, nativeCount) in scenarios)
        {
            await using var context = await CreateAsync("AzureBlob", [], blobItems: items, serverPageSize: 3);

            var actual = await SweepAsync(context, prefix, [3]);

            Assert.Equal(expected, actual);
            AssertExactMembership(expected, actual);
            Assert.Equal(nativeCount, context.Native.Fetches.Sum(fetch => fetch.ResultCount ?? 0));
            Assert.Equal((nativeCount + 2) / 3, context.Native.Attempts);
            Assert.All(context.Native.Listings, request => Assert.Equal(prefix.IsDefault ? null : prefix.Value, request.Prefix));
        }
    }

    [Theory]
    [InlineData("CustomContainerFactory")]
    [InlineData("EquivalentCustomWalDelegate")]
    public async Task AzureBlobReadPageAsync_EquivalentLayout_PreservesListAsyncCatalog(string layout)
    {
        await using var context = await CreateAsync(
            "AzureBlob", Ids("jobs/shards/z", "jobs/shards2", "jobs/shards/a", "jobs/shards/child"),
            serverPageSize: 2, blobLayout: layout);
        var prefix = new JournalId("jobs/shards");
        var listed = await context.ListAsync(prefix);

        Assert.Equal(["jobs/shards/a", "jobs/shards/child", "jobs/shards/z"], listed);
        Assert.Equal(2, context.Native.SetupCalls);
        var before = context.Native.Counts;

        var paged = await SweepAsync(context, prefix, [2, 1]);

        Assert.Equal(["jobs/shards/z", "jobs/shards/a", "jobs/shards/child"], paged);
        AssertExactMembership(listed.ToArray(), paged);
        Assert.Equal(before.Attempts + 3, context.Native.Attempts);
        Assert.All(context.Native.Listings, listing => Assert.Equal(prefix.Value, listing.Prefix));
    }

    [Fact]
    public async Task AzureTableReadPageAsync_UsesCanonicalIdentityAndOnlyReversibleLegacyFallback()
    {
        TableEntity[] entities =
        [
            Header("opaque!17", "tenant/custom"),
            Header("wrong%2Fidentity", "tenant/canonical"),
            Header("legacy%2F%C3%A9"),
            Header("legacy%2fchild"),
            Header("malformed%ZZ"),
            Header("%41"),
            Header("%20%09"),
            Header("fallback%2Fempty", ""),
            Header("fallback%2Fspace", " \t "),
            new("ignored", "data.0001") { [AzureTableJournalStorage.JournalIdPropertyName] = "not/a/header" },
        ];
        entities[0]["Unselected"] = "not part of the catalog projection";
        await using var context = await CreateAsync("AzureTable", [], tableEntities: entities, serverPageSize: 2);

        var actual = await SweepAsync(context, default, [2]);

        Assert.Equal(["tenant/custom", "tenant/canonical", "legacy/\u00E9"], actual);
        AssertExactMembership(["tenant/custom", "tenant/canonical", "legacy/\u00E9"], actual);
        Assert.Equal(9, context.Native.Fetches.Sum(fetch => fetch.ResultCount ?? 0));
        Assert.Equal(new NativeCounts(5, 5, 5, 5), context.Native.Counts);
        for (var i = 0; i < context.Native.Listings.Count; i++)
        {
            AssertNativeRequest(context, i, default, 2, i == 0 ? null : context.Native.Fetches[i - 1].NextCursor,
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task S3ReadPageAsync_UsesCanonicalWalMappingInNativeUnsortedOrder()
    {
        var parserInputs = new List<string>();
        var mappedIdentities = new List<string>();
        string[] keys =
        [
            "current/tenant/z/wal", "legacy/tenant/dangling/wal", "current/tenant/z/chk.1",
            "current/tenant/B/wal", "parser-null/wal", "parser-default/wal",
            "current/tenant/a/wal", "current/   /wal", "current/tenant/a/WAL",
            "current/other/q/wal", "legacy/tenant/a/wal", "current//wal",
        ];
        await using var context = await CreateAsync("S3", [], s3Keys: keys, serverPageSize: 3, configureS3: options =>
        {
            Assert.True(options.UseS3ExpressAppend);
            Assert.Equal(S3StorageClass.ExpressOnezone, options.StorageClass);
            options.GetObjectKey = id =>
            {
                mappedIdentities.Add(id.Value);
                return $"current/{id.Value}";
            };
            options.TryParseJournalId = key =>
            {
                parserInputs.Add(key);
                if (key == "parser-null")
                {
                    return null;
                }

                if (key == "parser-default")
                {
                    return default(JournalId);
                }

                var value = key.StartsWith("current/", StringComparison.Ordinal) ? key["current/".Length..]
                    : key.StartsWith("legacy/", StringComparison.Ordinal) ? key["legacy/".Length..] : null;
                return string.IsNullOrWhiteSpace(value) ? null : new JournalId(value);
            };
        });
        var prefix = new JournalId("tenant");
        string[][] expectedPages = [["tenant/z"], ["tenant/B"], ["tenant/a"], []];
        var all = new List<string>();
        string? token = null;
        for (var i = 0; i < expectedPages.Length; i++)
        {
            var page = await context.ReadAsync(prefix, 3, token);

            AssertValues(expectedPages[i], page);
            AssertNativeRequest(context, i, prefix, 3, i == 0 ? null : context.Native.Fetches[i - 1].NextCursor,
                TestContext.Current.CancellationToken);
            all.AddRange(page.JournalIds.Select(id => id.Value));
            token = page.ContinuationToken;
            if (i < expectedPages.Length - 1)
            {
                Assert.NotNull(token);
            }
        }

        Assert.Null(token);
        Assert.Equal(["tenant/z", "tenant/B", "tenant/a"], all);
        AssertExactMembership(["tenant/z", "tenant/B", "tenant/a"], all);
        Assert.Equal(
            [
                "current/tenant/z", "legacy/tenant/dangling", "current/tenant/B", "parser-null",
                "parser-default", "current/tenant/a", "current/   ", "current/other/q",
                "legacy/tenant/a", "current/",
            ],
            parserInputs);
        string[] expectedMappedIdentities = ["tenant/z", "tenant/dangling", "tenant/B", "tenant/a", "tenant/a"];
        // Parsed but out-of-prefix IDs must never reach the custom key mapper.
        Assert.Equal(expectedMappedIdentities, mappedIdentities);
        Assert.Equal(new NativeCounts(4, 0, 4, 4), context.Native.Counts);

        // The shared canonical-WAL helper also serves the original catalog operation.
        Assert.Equal(["tenant/B", "tenant/a", "tenant/z"], await context.ListAsync(prefix));
        Assert.Equal(expectedMappedIdentities.Concat(expectedMappedIdentities), mappedIdentities);
        Assert.Equal(new NativeCounts(8, 0, 8, 8), context.Native.Counts);
    }

    private static JournalId[] Ids(params string[] values) => values.Select(value => new JournalId(value)).ToArray();

    private static void AssertValues(string[] expected, JournalStorageCatalogPage page)
        => Assert.Equal(expected, page.JournalIds.Select(id => id.Value));

    private static void AssertExactMembership(string[] expected, IReadOnlyCollection<string> actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        Assert.Equal(expected.Length, actual.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal), actual.OrderBy(value => value, StringComparer.Ordinal));
    }

    private static string ModifyMiddleCharacter(string token)
    {
        var characters = token.ToCharArray();
        var index = characters.Length / 2;
        while (!char.IsAsciiLetterOrDigit(characters[index]))
        {
            index--;
        }

        characters[index] = characters[index] == 'A' ? 'B' : 'A';
        return new string(characters);
    }

    private static async Task<List<string>> SweepAsync(
        ProviderContext context, JournalId prefix, int[] sizes, string? token = null)
    {
        var result = new List<string>();
        // At least one record is consumed per ordinary native/index page. Allow an initial empty
        // native page, an exactly-full Volatile terminal probe, and the bounded Volatile mutations.
        var maximumPages = context.InputCount + 8;
        string? expectedNativeCursor = null;
        for (var index = 0; index < maximumPages; index++)
        {
            var page = await context.ReadAsync(prefix, sizes[index % sizes.Length], token);
            if (context.IsCloud && (index > 0 || token is null))
            {
                Assert.Equal(expectedNativeCursor, context.Native.Fetches[^1].Cursor);
            }

            result.AddRange(page.JournalIds.Select(id => id.Value));
            token = page.ContinuationToken;
            if (token is null)
            {
                return result;
            }

            if (context.IsCloud)
            {
                expectedNativeCursor = Assert.IsType<string>(context.Native.Fetches[^1].NextCursor);
            }
        }

        Assert.Fail($"{context.Kind}: traversal did not finish within {maximumPages} pages; prefix='{prefix.Value}', token='{token}'.");
        return result;
    }

    private static async Task MaterializeAsync(IJournalStorage storage, string operation, bool expectedCreated)
    {
        switch (operation)
        {
            case "CreateIfNotExistsAsync":
                Assert.Equal(expectedCreated, await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
                break;
            case "AppendAsync":
                await storage.AppendAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), TestContext.Current.CancellationToken);
                break;
            case "ReplaceAsync":
                await storage.ReplaceAsync(new ReadOnlySequence<byte>(new byte[] { 2 }), TestContext.Current.CancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown materialization operation.");
        }
    }

    private static async Task WriteExistingAsync(IJournalStorage storage)
    {
        Assert.False(await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        await storage.AppendAsync(new ReadOnlySequence<byte>(new byte[] { 3 }), TestContext.Current.CancellationToken);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>(new byte[] { 4 }), TestContext.Current.CancellationToken);
    }

    private static void AssertNativeRequest(
        ProviderContext context, int index, JournalId prefix, int maximum, string? cursor, CancellationToken cancellationToken)
    {
        var listing = context.Native.Listings[index];
        var fetch = context.Native.Fetches[index];
        Assert.Equal(cancellationToken, listing.CancellationToken);
        Assert.Equal(cancellationToken, fetch.CancellationToken);
        Assert.Equal(cursor, fetch.Cursor);
        Assert.Equal(maximum, fetch.Maximum);
        if (context.Kind == "S3")
        {
            Assert.Equal("journaling-tests", listing.Bucket);
            Assert.Null(listing.Prefix);
            Assert.Null(listing.StartAfter);
            Assert.Equal(cursor, listing.Cursor);
            Assert.Equal(maximum, listing.Maximum);
        }
        else
        {
            var pages = context.Native.PageEnumerations[index];
            Assert.Equal(cursor, pages.Cursor);
            Assert.Equal(maximum, pages.SizeHint);
            Assert.Equal(cancellationToken, pages.CancellationToken);
            if (context.Kind == "AzureBlob")
            {
                Assert.Equal(BlobTraits.None, listing.Traits);
                Assert.Equal(BlobStates.None, listing.States);
                Assert.Equal(prefix.IsDefault ? null : prefix.Value, listing.Prefix);
            }
            else
            {
                Assert.Equal(TableClient.CreateQueryFilter($"RowKey eq {AzureTableJournalStorage.HeaderRowKey}"), listing.Filter);
                Assert.NotNull(listing.Select);
                Assert.Equal([AzureTableJournalStorage.JournalIdPropertyName], listing.Select);
                Assert.Equal(maximum, listing.Maximum);
            }
        }
    }

    private static BlobItem Blob(string name, BlobType type = BlobType.Append)
        => BlobsModelFactory.BlobItem(
            name: name, deleted: false,
            properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false, blobType: type));

    private static TableEntity Header(string partitionKey, string? identity = null)
    {
        var entity = new TableEntity(partitionKey, AzureTableJournalStorage.HeaderRowKey);
        if (identity is not null)
        {
            entity[AzureTableJournalStorage.JournalIdPropertyName] = identity;
        }

        return entity;
    }

    private static async Task<ProviderContext> CreateAsync(
        string kind,
        JournalId[] identities,
        bool initialize = true,
        int serverPageSize = 3,
        bool emptyFirstPage = false,
        BlobItem[]? blobItems = null,
        TableEntity[]? tableEntities = null,
        string[]? s3Keys = null,
        string? blobLayout = null,
        Action<S3JournalStorageOptions>? configureS3 = null)
    {
        var context = new ProviderContext(kind, identities, serverPageSize, emptyFirstPage, blobItems, tableEntities, s3Keys, blobLayout, configureS3);
        try
        {
            if (initialize)
            {
                await context.InitializeAsync();
            }

            if (kind == "Volatile")
            {
                foreach (var identity in identities)
                {
                    Assert.True(await context.Provider.CreateStorage(identity)
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
        private bool _s3Initialized;

        public ProviderContext(
            string kind, JournalId[] identities, int serverPageSize, bool emptyFirstPage,
            BlobItem[]? blobItems, TableEntity[]? tableEntities, string[]? s3Keys,
            string? blobLayout, Action<S3JournalStorageOptions>? configureS3)
        {
            Kind = kind;
            InputCount = blobItems?.Length ?? tableEntities?.Length ?? s3Keys?.Length ?? identities.Length;
            Native = new NativeRecorder(serverPageSize, emptyFirstPage);
            var services = new ServiceCollection();
            services.AddKeyedSingleton<IJournalFormat>("test-format", new TestFormat());
            _services = services.BuildServiceProvider();
            var managerOptions = Options.Create(new JournaledStateManagerOptions { JournalFormatKey = "test-format" });
            try
            {
                switch (kind)
                {
                    case "Volatile":
                        Provider = new VolatileJournalStorageProvider();
                        break;
                    case "AzureBlob":
                        var container = new FakeBlobContainer(Native, blobItems ?? identities.Select(id => Blob($"{id.Value}/wal")).ToArray());
                        var blobOptions = new AzureBlobJournalStorageOptions { ContainerName = "journaling-tests" };
                        blobOptions.ConfigureBlobServiceClient(_ => Task.FromResult<BlobServiceClient>(new FakeBlobService(container)));
                        if (blobLayout == "CustomContainerFactory")
                        {
                            blobOptions.BuildContainerFactory = (_, _) => new CustomContainerFactory(container);
                        }
                        else if (blobLayout == "EquivalentCustomWalDelegate")
                        {
                            blobOptions.GetWalBlobName = id => $"{id.Value}/wal";
                        }

                        var blobProvider = new AzureBlobJournalStorageProvider(
                            Options.Create(blobOptions), managerOptions, _services, NullLogger<AzureBlobJournalStorage>.Instance);
                        blobProvider.Participate(_lifecycle);
                        Provider = blobProvider;
                        break;
                    case "AzureTable":
                        var table = new FakeTable(Native, tableEntities ?? identities.Select((id, index) => Header($"opaque!{index}", id.Value)).ToArray());
                        var tableOptions = new AzureTableJournalStorageOptions { TableName = "journalingTests" };
                        tableOptions.ConfigureTableServiceClient(_ => Task.FromResult<TableServiceClient>(new FakeTableService(table)));
                        var tableProvider = new AzureTableJournalStorageProvider(
                            Options.Create(tableOptions), managerOptions, _services, NullLogger<AzureTableJournalStorage>.Instance);
                        tableProvider.Participate(_lifecycle);
                        Provider = tableProvider;
                        break;
                    case "S3":
                        var s3Client = Substitute.For<IAmazonS3>();
                        S3Client = s3Client;
                        s3Client.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
                        {
                            Assert.Equal("journaling-tests", call.Arg<HeadBucketRequest>().BucketName);
                            Native.SetupCalls++;
                            return Task.FromResult(new HeadBucketResponse());
                        });
                        var objects = (s3Keys ?? identities.Select(id => $"{id.Value}/wal").ToArray())
                            .Select(key => new S3Object { Key = key }).ToArray();
                        s3Client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).Returns(call =>
                        {
                            var request = call.Arg<ListObjectsV2Request>();
                            var cancellationToken = call.Arg<CancellationToken>();
                            Native.Listings.Add(new()
                            {
                                Bucket = request.BucketName,
                                Prefix = request.Prefix,
                                Cursor = request.ContinuationToken,
                                Maximum = request.MaxKeys,
                                StartAfter = request.StartAfter,
                                CancellationToken = cancellationToken,
                            });
                            try
                            {
                                var page = Native.Fetch(objects, request.ContinuationToken, request.MaxKeys, cancellationToken);
                                return Task.FromResult(new ListObjectsV2Response
                                {
                                    S3Objects = page.Values.ToList(),
                                    IsTruncated = page.NextCursor is not null,
                                    NextContinuationToken = page.NextCursor,
                                });
                            }
                            catch (Exception exception)
                            {
                                return Task.FromException<ListObjectsV2Response>(exception);
                            }
                        });
                        var s3Options = new S3JournalStorageOptions
                        {
                            BucketName = "journaling-tests", CreateBucketIfNotExists = false, S3Client = s3Client,
                        };
                        configureS3?.Invoke(s3Options);
                        Provider = new S3JournalStorageProvider(
                            Options.Create(s3Options), managerOptions, _services, NullLogger<S3JournalStorage>.Instance);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown provider.");
                }

                Catalog = Assert.IsAssignableFrom<IJournalStorageCatalog>(Provider);
                Paged = Assert.IsAssignableFrom<IPagedJournalStorageCatalog>(Catalog);
            }
            catch
            {
                S3Client?.Dispose();
                _services.Dispose();
                throw;
            }
        }

        public string Kind { get; }
        public bool IsCloud => Kind != "Volatile";
        public int InputCount { get; }
        public IJournalStorageProvider Provider { get; }
        public IJournalStorageCatalog Catalog { get; }
        private IPagedJournalStorageCatalog Paged { get; }
        public NativeRecorder Native { get; }
        public IAmazonS3? S3Client { get; }

        public async Task InitializeAsync()
        {
            if (Provider is S3JournalStorageProvider s3)
            {
                await s3.InitializeAsync(TestContext.Current.CancellationToken);
                _s3Initialized = true;
            }
            else if (IsCloud)
            {
                Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, _lifecycle.Stage);
                Assert.Equal(Provider.GetType().Name, _lifecycle.ObserverName);
                await _lifecycle.StartAsync(TestContext.Current.CancellationToken);
            }
        }

        public async ValueTask<JournalStorageCatalogPage> ReadAsync(
            JournalId prefix, int size, string? token = null, CancellationToken? cancellationToken = null)
        {
            var callerToken = cancellationToken ?? TestContext.Current.CancellationToken;
            var before = Native.Counts;
            Native.BeginRead();
            try
            {
                var page = await Paged.ReadPageAsync(prefix, size, token, callerToken);
                Assert.InRange(page.JournalIds.Count, 0, size);
                if (IsCloud)
                {
                    Assert.Equal(new NativeCounts(before.Listings + 1,
                        before.PageEnumerations + (Kind == "S3" ? 0 : 1), before.Attempts + 1, before.Successes + 1),
                        Native.Counts);
                    var native = Native.Fetches[^1];
                    Assert.Equal(callerToken, native.CancellationToken);
                    Assert.Equal(Math.Min(size, Kind == "AzureBlob" ? 5000 : 1000), native.Maximum);
                    Assert.Equal(native.NextCursor is null, page.ContinuationToken is null);
                }

                return page;
            }
            finally
            {
                Native.EndOperation();
            }
        }

        public async Task<List<string>> ListAsync(JournalId prefix = default)
        {
            Native.BeginDrain(InputCount + 2);
            try
            {
                var result = new List<string>();
                await foreach (var identity in Catalog.ListAsync(prefix, cancellationToken: TestContext.Current.CancellationToken))
                {
                    result.Add(identity.Value);
                }

                return result;
            }
            finally
            {
                Native.EndOperation();
            }
        }

        public async Task CloseAsync()
        {
            if (_s3Initialized)
            {
                await ((S3JournalStorageProvider)Provider).CloseAsync(TestContext.Current.CancellationToken);
                _s3Initialized = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await CloseAsync();
            }
            finally
            {
                S3Client?.Dispose();
                await _services.DisposeAsync();
            }
        }
    }

    private sealed record NativeCounts(int Listings, int PageEnumerations, int Attempts, int Successes);
    private sealed record PageEnumeration(string? Cursor, int? SizeHint, CancellationToken CancellationToken);
    private sealed record NativePage<T>(IReadOnlyList<T> Values, string? NextCursor);

    private sealed record Listing
    {
        public string? Bucket { get; init; }
        public string? Prefix { get; init; }
        public string? Cursor { get; init; }
        public int? Maximum { get; init; }
        public string? StartAfter { get; init; }
        public string? Filter { get; init; }
        public string[]? Select { get; init; }
        public BlobTraits? Traits { get; init; }
        public BlobStates? States { get; init; }
        public CancellationToken CancellationToken { get; init; }
    }

    private sealed class NativeFetch(string? cursor, int? maximum, CancellationToken cancellationToken)
    {
        public string? Cursor { get; } = cursor;
        public int? Maximum { get; } = maximum;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public int? ResultCount { get; set; }
        public string? NextCursor { get; set; }
    }

    private sealed class NativeRecorder(int serverPageSize, bool emptyFirstPage)
    {
        private readonly Dictionary<string, int> _positions = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _cursors = [];
        private int _remainingFetches;

        public int SetupCalls { get; set; }
        public int FailingSetupCall { get; set; }
        public Exception? SetupFailure { get; set; }
        public List<Listing> Listings { get; } = [];
        public List<PageEnumeration> PageEnumerations { get; } = [];
        public List<NativeFetch> Fetches { get; } = [];
        public int Attempts => Fetches.Count;
        public int Successes { get; private set; }
        public Exception? Failure { get; set; }
        public Action? BeforeSuccessfulResponse { get; set; }
        public NativeCounts Counts => new(Listings.Count, PageEnumerations.Count, Attempts, Successes);

        public void BeginRead() => _remainingFetches = 1;
        public void BeginDrain(int maximumFetches) => _remainingFetches = maximumFetches;
        public void EndOperation() => _remainingFetches = 0;

        public NativePage<T> Fetch<T>(IReadOnlyList<T> records, string? cursor, int? maximum, CancellationToken cancellationToken)
        {
            var fetch = new NativeFetch(cursor, maximum, cancellationToken);
            Fetches.Add(fetch);
            Assert.True(_remainingFetches-- > 0,
                $"Unexpected native retrieval #{Attempts}: a public paging call may fetch exactly one native page (cursor='{cursor}').");
            if (Failure is { } failure)
            {
                throw failure;
            }

            var offset = 0;
            if (cursor is not null)
            {
                Assert.True(_positions.TryGetValue(cursor, out offset), $"Unknown native cursor '{cursor}': do not derive it from a key.");
            }

            var count = cursor is null && emptyFirstPage
                ? 0 : Math.Min(Math.Min(maximum ?? serverPageSize, serverPageSize), records.Count - offset);
            var values = records.Skip(offset).Take(count).ToArray();
            var next = offset + count < records.Count ? CursorAt(offset + count) : null;
            fetch.ResultCount = values.Length;
            fetch.NextCursor = next;
            Successes++;

            // Cancellation here is deliberately followed by a SUCCESSFUL response. Do not throw
            // or check the token afterwards: the provider must observe post-response cancellation.
            var beforeResponse = BeforeSuccessfulResponse;
            BeforeSuccessfulResponse = null;
            beforeResponse?.Invoke();
            return new(values, next);
        }

        private string CursorAt(int position)
        {
            if (!_cursors.TryGetValue(position, out var cursor))
            {
                // Opaque labels map to consumed RECORD offsets, never page numbers or last keys.
                cursor = $"native!resume/{_cursors.Count}:opaque?";
                _cursors.Add(position, cursor);
                _positions.Add(cursor, position);
            }

            return cursor;
        }
    }

    private sealed class RecordingPageable<T>(
        NativeRecorder recorder, IReadOnlyList<T> records, int? queryMaximum, CancellationToken cancellationToken) : AsyncPageable<T>
        where T : notnull
    {
        public override IAsyncEnumerable<Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
        {
            recorder.PageEnumerations.Add(new(continuationToken, pageSizeHint, cancellationToken));
            return EnumeratePages(continuationToken, pageSizeHint);
        }

        private async IAsyncEnumerable<Page<T>> EnumeratePages(string? cursor, int? sizeHint)
        {
            do
            {
                await Task.CompletedTask;
                var maximum = queryMaximum is { } queryCap && sizeHint is { } hint
                    ? Math.Min(queryCap, hint) : sizeHint ?? queryMaximum;
                var page = recorder.Fetch(records, cursor, maximum, cancellationToken);
                yield return Page<T>.FromValues(page.Values, page.NextCursor, new FakeResponse());
                cursor = page.NextCursor;
                // This is intentionally a multi-page iterator. A provider which drains the next
                // page before returning will advance into Fetch again and trip the per-call guard.
            }
            while (cursor is not null);
        }
    }

    private sealed class FakeBlobService(FakeBlobContainer container) : BlobServiceClient
    {
        public override BlobContainerClient GetBlobContainerClient(string blobContainerName)
        {
            Assert.Equal("journaling-tests", blobContainerName);
            return container;
        }
    }

    private sealed class FakeBlobContainer(NativeRecorder recorder, BlobItem[] records) : BlobContainerClient
    {
        public override Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(
            PublicAccessType publicAccessType = PublicAccessType.None,
            IDictionary<string, string>? metadata = null,
            BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null,
            CancellationToken cancellationToken = default)
        {
            recorder.SetupCalls++;
            if (recorder.SetupCalls == recorder.FailingSetupCall && recorder.SetupFailure is { } failure)
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
            recorder.Listings.Add(new() { Traits = traits, States = states, Prefix = prefix, CancellationToken = cancellationToken });
            // Azure's raw prefix filter happens before paging; logical JournalId filtering does not.
            var values = records.Where(item => prefix is null || item.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            return new RecordingPageable<BlobItem>(recorder, values, queryMaximum: null, cancellationToken);
        }
    }

    private sealed class CustomContainerFactory(FakeBlobContainer container) : IBlobContainerFactory
    {
        public BlobContainerClient GetBlobContainerClient(JournalId journalId) => container;

        public async Task InitializeAsync(BlobServiceClient client, CancellationToken cancellationToken)
            => await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    private sealed class FakeTableService(FakeTable table) : TableServiceClient
    {
        public override TableClient GetTableClient(string tableName)
        {
            Assert.Equal("journalingTests", tableName);
            return table;
        }
    }

    private sealed class FakeTable(NativeRecorder recorder, TableEntity[] records) : TableClient
    {
        public override Task<Response<TableItem>> CreateIfNotExistsAsync(CancellationToken cancellationToken = default)
        {
            recorder.SetupCalls++;
            return Task.FromResult(Response.FromValue(new TableItem("journalingTests"), new FakeResponse()));
        }

        public override AsyncPageable<T> QueryAsync<T>(
            string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
        {
            var selected = select?.ToArray();
            recorder.Listings.Add(new() { Filter = filter, Maximum = maxPerPage, Select = selected, CancellationToken = cancellationToken });
            Assert.Equal(TableClient.CreateQueryFilter($"RowKey eq {AzureTableJournalStorage.HeaderRowKey}"), filter);
            Assert.NotNull(selected);
            Assert.Equal([AzureTableJournalStorage.JournalIdPropertyName], selected);
            var values = records.Where(entity => entity.RowKey == AzureTableJournalStorage.HeaderRowKey).Select(entity =>
            {
                var projected = new TableEntity(entity.PartitionKey, entity.RowKey);
                foreach (var property in selected ?? [])
                {
                    if (entity.TryGetValue(property, out var value))
                    {
                        projected[property] = value;
                    }
                }

                return (T)(ITableEntity)projected;
            }).ToArray();
            return new RecordingPageable<T>(recorder, values, maxPerPage, cancellationToken);
        }
    }

    private sealed class TestFormat : IJournalFormat
    {
        public string FormatKey => "test-format";
        public string? MimeType => "application/test";
        public JournalBufferWriter CreateWriter() => throw new NotSupportedException("Catalog paging must not create a journal writer.");
        public void Replay(JournalBufferReader input, JournalReplayContext context)
            => throw new NotSupportedException("Catalog paging must not replay journal contents.");
    }

    private sealed class RecordingLifecycle : ISiloLifecycle
    {
        private ILifecycleObserver? _observer;
        public string? ObserverName { get; private set; }
        public int Stage { get; private set; }
        public int HighestCompletedStage => Stage;
        public int LowestStoppedStage => Stage;

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            Assert.Null(_observer);
            ObserverName = observerName;
            Stage = stage;
            _observer = observer;
            return new NoopDisposable();
        }

        public Task StartAsync(CancellationToken cancellationToken)
            => Assert.IsAssignableFrom<ILifecycleObserver>(_observer).OnStart(cancellationToken);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class FakeResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = string.Empty;
        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value)
        {
            value = string.Empty;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = [];
            return false;
        }
    }
}
