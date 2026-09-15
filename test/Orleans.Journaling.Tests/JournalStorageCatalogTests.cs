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
using Azure.Storage.Blobs.Specialized;
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
    [InlineData("AzureBlob", "azure_blob", 2)]
    [InlineData("AzureTable", "azure_table", 1)]
    public async Task AzureInitialize_TelemetryPreservesAlreadyExistingResourceResponses(
        string kind, string provider, int expectedCalls)
    {
        using var metrics = new AzureJournalStorageMetricsFixture(provider);
        await using var context = await CreateAsync(kind, ["a"], initialize: false, metrics: metrics);
        context.Native.ResourceAlreadyExists = true;
        context.Native.BeforeSetup = () => metrics.Clock.Advance(TimeSpan.FromMilliseconds(5));

        await context.InitializeAsync();

        var calls = metrics.Calls.GetMeasurementSnapshot();
        Assert.Equal(expectedCalls, calls.Count);
        Assert.Equal(expectedCalls, context.Native.SetupCalls);
        Assert.All(calls, call =>
        {
            Assert.Equal(provider, call.Tags["provider"]);
            Assert.Equal("CreateIfNotExistsAsync", call.Tags["api"]);
            Assert.Equal("already_exists", call.Tags["status"]);
            Assert.Equal(1, call.Value);
        });
        Assert.All(metrics.ApiDuration.GetMeasurementSnapshot(), duration => Assert.Equal(5, duration.Value));
        var operation = Assert.Single(metrics.Operations.GetMeasurementSnapshot());
        Assert.Equal("initialize", operation.Tags["operation"]);
        Assert.Equal("ok", operation.Tags["status"]);
        Assert.Equal(1, operation.Value);
        Assert.Equal(expectedCalls * 5, Assert.Single(metrics.OperationDuration.GetMeasurementSnapshot()).Value);
        Assert.Equal(["a"], await DrainAsync(context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("AzureBlob", "azure_blob", "GetBlobsAsync")]
    [InlineData("AzureTable", "azure_table", "QueryAsync")]
    public async Task AzureListAsync_TelemetryCountsEmptyAndContinuationPagesExcludingConsumerPauses(
        string kind, string provider, string api)
    {
        using var metrics = new AzureJournalStorageMetricsFixture(provider);
        await using var context = await CreateAsync(kind, ["a", "b", "c"], metrics: metrics);
        context.Native.EmptyFirstPage = true;
        context.Native.BeforeRequest = () => metrics.Clock.Advance(TimeSpan.FromMilliseconds(5));

        var entries = new List<string>();
        await foreach (var entry in context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            entries.Add(entry.Id.Value);
            metrics.Clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(["a", "b", "c"], entries);
        Assert.Equal([0, 2, 1], context.Native.Requests.Select(request => request.ResultCount));
        var calls = metrics.Calls.GetMeasurementSnapshot();
        Assert.Equal(kind == "AzureBlob" ? 5 : 4, calls.Count);
        Assert.All(calls, call =>
        {
            Assert.Equal(3, call.Tags.Count);
            Assert.Equal(provider, call.Tags["provider"]);
            Assert.Equal("ok", call.Tags["status"]);
            Assert.Equal(1, call.Value);
        });
        Assert.Equal(3, calls.Count(call => Equals(call.Tags["api"], api)));
        Assert.Equal([5d, 5d, 5d], metrics.ApiDuration.GetMeasurementSnapshot()
            .Where(measurement => Equals(measurement.Tags["api"], api)).Select(measurement => measurement.Value));
        Assert.Equal([2L, 1L], metrics.Items.GetMeasurementSnapshot().Select(measurement => measurement.Value));
        Assert.Equal(3, metrics.Entries.GetMeasurementSnapshot().Sum(measurement => measurement.Value));
        var operation = Assert.Single(metrics.Operations.GetMeasurementSnapshot(),
            measurement => Equals(measurement.Tags["operation"], "list"));
        Assert.Equal("ok", operation.Tags["status"]);
        Assert.Equal(provider, operation.Tags["provider"]);
        Assert.Equal(1, operation.Value);
        var duration = Assert.Single(metrics.OperationDuration.GetMeasurementSnapshot(),
            measurement => Equals(measurement.Tags["operation"], "list"));
        Assert.Equal(15, duration.Value);
    }

    [Theory]
    [InlineData("AzureBlob", "azure_blob", "GetBlobsAsync", false)]
    [InlineData("AzureBlob", "azure_blob", "GetBlobsAsync", true)]
    [InlineData("AzureTable", "azure_table", "QueryAsync", false)]
    [InlineData("AzureTable", "azure_table", "QueryAsync", true)]
    public async Task AzureListAsync_TelemetryRecordsFailedPageAndEarlyDisposalOnce(
        string kind, string provider, string api, bool fail)
    {
        using var metrics = new AzureJournalStorageMetricsFixture(provider);
        await using var context = await CreateAsync(kind, ["a", "b", "c"], metrics: metrics);
        context.Native.BeforeRequest = () => metrics.Clock.Advance(TimeSpan.FromMilliseconds(5));
        context.Native.Failure = new RequestFailedException(503, "unavailable");
        context.Native.FailureAtRequest = 2;
        await using (var enumerator = context.Catalog.ListAsync(
            cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await enumerator.MoveNextAsync());
            metrics.Clock.Advance(TimeSpan.FromHours(1));
            if (fail)
            {
                Assert.True(await enumerator.MoveNextAsync());
                await Assert.ThrowsAsync<RequestFailedException>(() => enumerator.MoveNextAsync().AsTask());
            }
        }

        var calls = metrics.Calls.GetMeasurementSnapshot().Where(call => Equals(call.Tags["api"], api)).ToArray();
        Assert.Equal(fail ? new[] { "ok", "unavailable" } : ["ok"], calls.Select(call => call.Tags["status"]));
        Assert.Equal(fail ? 2 : 1, calls.Length);
        var operation = Assert.Single(metrics.Operations.GetMeasurementSnapshot(),
            measurement => Equals(measurement.Tags["operation"], "list"));
        Assert.Equal(fail ? "error" : "disposed", operation.Tags["status"]);
        Assert.Equal(1, operation.Value);
        var duration = Assert.Single(metrics.OperationDuration.GetMeasurementSnapshot(),
            measurement => Equals(measurement.Tags["operation"], "list"));
        Assert.Equal(fail ? 10 : 5, duration.Value);
        Assert.Equal(fail ? 2 : 1, metrics.Entries.GetMeasurementSnapshot().Sum(measurement => measurement.Value));
    }

    [Theory]
    [InlineData("AzureBlob", "azure_blob", "GetBlobsAsync", 404, "not_found")]
    [InlineData("AzureBlob", "azure_blob", "GetBlobsAsync", 409, "conflict")]
    [InlineData("AzureBlob", "azure_blob", "GetBlobsAsync", 412, "conflict")]
    [InlineData("AzureBlob", "azure_blob", "GetBlobsAsync", 429, "throttled")]
    [InlineData("AzureBlob", "azure_blob", "GetBlobsAsync", 500, "error")]
    [InlineData("AzureBlob", "azure_blob", "GetBlobsAsync", 503, "unavailable")]
    [InlineData("AzureBlob", "azure_blob", "GetBlobsAsync", 0, "canceled")]
    [InlineData("AzureTable", "azure_table", "QueryAsync", 404, "not_found")]
    [InlineData("AzureTable", "azure_table", "QueryAsync", 409, "conflict")]
    [InlineData("AzureTable", "azure_table", "QueryAsync", 412, "conflict")]
    [InlineData("AzureTable", "azure_table", "QueryAsync", 429, "throttled")]
    [InlineData("AzureTable", "azure_table", "QueryAsync", 500, "error")]
    [InlineData("AzureTable", "azure_table", "QueryAsync", 503, "unavailable")]
    [InlineData("AzureTable", "azure_table", "QueryAsync", 0, "canceled")]
    public async Task AzureListAsync_TelemetryClassifiesSdkPageFailures(
        string kind, string provider, string api, int status, string expectedStatus)
    {
        using var metrics = new AzureJournalStorageMetricsFixture(provider);
        await using var context = await CreateAsync(kind, [], metrics: metrics);
        context.Native.BeforeRequest = () => metrics.Clock.Advance(TimeSpan.FromMilliseconds(7));
        context.Native.Failure = status == 0
            ? new OperationCanceledException(TestContext.Current.CancellationToken)
            : new RequestFailedException(status, "native failure");
        context.Native.FailureAtRequest = 1;

        var exception = await Record.ExceptionAsync(() => DrainAsync(
            context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));

        Assert.Same(context.Native.Failure, exception);
        var call = Assert.Single(metrics.Calls.GetMeasurementSnapshot(), call => Equals(call.Tags["api"], api));
        Assert.Equal(expectedStatus, call.Tags["status"]);
        Assert.Equal(provider, call.Tags["provider"]);
        Assert.Equal(1, call.Value);
        Assert.Equal(7, Assert.Single(metrics.ApiDuration.GetMeasurementSnapshot(),
            call => Equals(call.Tags["api"], api)).Value);
        Assert.Empty(metrics.Items.GetMeasurementSnapshot());
        Assert.Empty(metrics.Entries.GetMeasurementSnapshot());
        var operation = Assert.Single(metrics.Operations.GetMeasurementSnapshot(),
            measurement => Equals(measurement.Tags["operation"], "list"));
        Assert.Equal(status == 0 ? "canceled" : "error", operation.Tags["status"]);
    }

    [Theory]
    [InlineData("AzureBlob", "azure_blob", "GetPropertiesAsync")]
    [InlineData("AzureTable", "azure_table", "GetEntityAsync")]
    public async Task AzureStorage_TelemetrySeparatesLogicalConditionalOutcomeFromNativeCalls(
        string kind, string provider, string api)
    {
        using var metrics = new AzureJournalStorageMetricsFixture(provider);
        await using var context = await CreateAsync(kind, ["a"], metrics: metrics);
        var storage = context.Provider.CreateStorage(new("a"));
        var metadata = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        context.Native.CurrentETag = new ETag("changed");

        Assert.Null(await storage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["owner"] = "bob" }, expectedETag: metadata.ETag,
            cancellationToken: TestContext.Current.CancellationToken));

        var calls = metrics.Calls.GetMeasurementSnapshot().Where(call => Equals(call.Tags["api"], api)).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal("ok", calls[0].Tags["status"]);
        Assert.Equal(kind == "AzureBlob" ? "conflict" : "ok", calls[1].Tags["status"]);
        var get = Assert.Single(metrics.Operations.GetMeasurementSnapshot(),
            operation => Equals(operation.Tags["operation"], "get_metadata"));
        Assert.Equal(provider, get.Tags["provider"]);
        Assert.Equal("ok", get.Tags["status"]);
        var update = Assert.Single(metrics.Operations.GetMeasurementSnapshot(),
            operation => Equals(operation.Tags["operation"], "update_metadata"));
        Assert.Equal(provider, update.Tags["provider"]);
        Assert.Equal("not_applied", update.Tags["status"]);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    public async Task AzureListAsync_IncludeMetadataProjectsCompleteSnapshotWithoutPerJournalRequests(string kind)
    {
        await using var context = await CreateAsync(kind, ["tenant/a", "tenant/b", "tenant/c"]);
        var entries = await DrainEntriesAsync(context.Catalog.ListAsync(
            new() { IncludeMetadata = true }, TestContext.Current.CancellationToken));

        Assert.Equal(["tenant/a", "tenant/b", "tenant/c"], entries.Select(entry => entry.Id.Value));
        Assert.Equal(2, context.Native.Requests.Count);
        Assert.Equal(0, context.Native.MetadataRequests);
        AssertMetadataProjection(context.Native, kind, includeMetadata: true);
        foreach (var entry in entries)
        {
            var metadata = Assert.IsAssignableFrom<IJournalMetadata>(entry.Metadata);
            Assert.Equal("test", metadata.Format);
            Assert.Equal(new ETag("listed").ToString(), metadata.ETag);
            Assert.Equal(new Dictionary<string, string> { ["owner"] = "alice" }, metadata.Properties);

            var current = await context.Provider.CreateStorage(entry.Id).GetMetadataAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(current);
            Assert.Equal(current.Format, metadata.Format);
            Assert.Equal(current.ETag, metadata.ETag);
            Assert.Equal(current.Properties, metadata.Properties);
        }

        Assert.Equal(entries.Count, context.Native.MetadataRequests);
    }

    [Theory]
    [InlineData("AzureBlob", false)]
    [InlineData("AzureBlob", true)]
    [InlineData("AzureTable", false)]
    [InlineData("AzureTable", true)]
    public async Task AzureListAsync_IncludeMetadataIsSnapshottedAcrossPages(string kind, bool includeMetadata)
    {
        await using var context = await CreateAsync(kind, ["tenant/a", "tenant/b", "tenant/c"]);
        var options = new ListOptions { IncludeMetadata = !includeMetadata };
        var listing = context.Catalog.ListAsync(options, TestContext.Current.CancellationToken);
        options.IncludeMetadata = includeMetadata;
        await using (var enumerator = listing.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(includeMetadata, enumerator.Current.Metadata is not null);
            options.IncludeMetadata = !includeMetadata;
            var count = 1;
            while (await enumerator.MoveNextAsync())
            {
                Assert.Equal(includeMetadata, enumerator.Current.Metadata is not null);
                count++;
            }

            Assert.Equal(3, count);
        }

        Assert.Equal(2, context.Native.Requests.Count);
        Assert.Equal(0, context.Native.MetadataRequests);
        AssertMetadataProjection(context.Native, kind, includeMetadata);
        var next = await DrainEntriesAsync(listing);
        Assert.Equal(3, next.Count);
        Assert.All(next, entry => Assert.Equal(!includeMetadata, entry.Metadata is not null));
        Assert.Equal(0, context.Native.MetadataRequests);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    public async Task AzureListAsync_ProjectedETagRejectsStaleMetadataUpdate(string kind)
    {
        await using var context = await CreateAsync(kind, ["tenant/a"]);
        var entry = Assert.Single(await DrainEntriesAsync(context.Catalog.ListAsync(
            new() { IncludeMetadata = true }, TestContext.Current.CancellationToken)));
        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(entry.Metadata);
        Assert.Equal(0, context.Native.MetadataRequests);
        context.Native.CurrentETag = new ETag("changed");

        var updated = await context.Provider.CreateStorage(entry.Id).UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["owner"] = "bob" },
            expectedETag: metadata.ETag,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(updated);
        Assert.Equal(1, context.Native.MetadataRequests);
        Assert.Equal(new ETag("listed").ToString(), metadata.ETag);
        Assert.Equal("alice", metadata.Properties["owner"]);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    public async Task AzureListAsync_EmptyMetadataStillReturnsCompleteVersionedSnapshot(string kind)
    {
        var blob = Blob("wal/tenant/a");
        blob.Metadata.Clear();
        var header = Header(AzureTableJournalStorageOptions.GetDefaultPartitionKey(new("tenant/a")), "tenant/a");
        header[AzureTableJournalStorage.FormatPropertyName] = string.Empty;
        header[AzureTableJournalStorage.MetadataPropertyName] = "{}";
        await using var context = await CreateAsync(kind, [], blobs: [blob], headers: [header]);

        var entry = Assert.Single(await DrainEntriesAsync(context.Catalog.ListAsync(
            new() { IncludeMetadata = true }, TestContext.Current.CancellationToken)));

        Assert.Equal(new JournalId("tenant/a"), entry.Id);
        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(entry.Metadata);
        Assert.Null(metadata.Format);
        Assert.Equal(new ETag("listed").ToString(), metadata.ETag);
        Assert.Empty(metadata.Properties);
        Assert.Equal(0, context.Native.MetadataRequests);
    }

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    public async Task AzureListAsync_MetadataSnapshotRetainsObservedPropertiesAfterStorageChanges(string kind)
    {
        var blob = Blob("wal/tenant/a");
        var header = Header(AzureTableJournalStorageOptions.GetDefaultPartitionKey(new("tenant/a")), "tenant/a");
        await using var context = await CreateAsync(kind, [], blobs: [blob], headers: [header]);
        var entry = Assert.Single(await DrainEntriesAsync(context.Catalog.ListAsync(
            new() { IncludeMetadata = true }, TestContext.Current.CancellationToken)));
        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(entry.Metadata);

        blob.Metadata["owner"] = "bob";
        header[AzureTableJournalStorage.MetadataPropertyName] = """{"owner":"bob"}""";
        context.Native.CurrentETag = new ETag("changed");
        var current = await context.Provider.CreateStorage(entry.Id).GetMetadataAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(current);
        Assert.Equal("bob", current.Properties["owner"]);
        Assert.Equal(new ETag("changed").ToString(), current.ETag);
        Assert.Equal("test", metadata.Format);
        Assert.Equal(new ETag("listed").ToString(), metadata.ETag);
        Assert.Equal(new Dictionary<string, string> { ["owner"] = "alice" }, metadata.Properties);
        Assert.Equal(1, context.Native.MetadataRequests);
    }

    [Theory]
    [InlineData("Volatile")]
    [InlineData("AzureBlob")]
    [InlineData("AzureTable")]
    [InlineData("S3")]
    public async Task ListAsync_DefaultProjectionReturnsIdsWithoutMetadata(string kind)
    {
        await using var context = await CreateAsync(kind, ["tenant/a"]);
        var entry = Assert.Single(await DrainEntriesAsync(context.Catalog.ListAsync(
            cancellationToken: TestContext.Current.CancellationToken)));

        Assert.Equal(new JournalId("tenant/a"), entry.Id);
        Assert.Null(entry.Metadata);
        Assert.Equal(0, context.Native.MetadataRequests);
        if (kind is "AzureBlob" or "AzureTable")
        {
            AssertMetadataProjection(context.Native, kind, includeMetadata: false);
        }
    }

    [Fact]
    public async Task S3ListAsync_IncludeMetadataRetainsIdentityOnlyProjection()
    {
        await using var context = await CreateAsync("S3", ["tenant/a"]);
        var entry = Assert.Single(await DrainEntriesAsync(context.Catalog.ListAsync(
            new() { IncludeMetadata = true }, TestContext.Current.CancellationToken)));

        Assert.Equal(new JournalId("tenant/a"), entry.Id);
        Assert.Null(entry.Metadata);
        context.AssertNoS3MetadataRequests();
    }

    [Theory]
    [InlineData("{", typeof(InvalidOperationException))]
    [InlineData("{\"owner\":null}", typeof(ArgumentNullException))]
    public async Task AzureTableListAsync_MalformedProjectedMetadataPropagates(string json, Type exceptionType)
    {
        var header = Header(AzureTableJournalStorageOptions.GetDefaultPartitionKey(new("tenant/a")), "tenant/a");
        header[AzureTableJournalStorage.MetadataPropertyName] = json;
        await using var context = await CreateAsync("AzureTable", [], headers: [header]);

        var exception = await Record.ExceptionAsync(() => DrainEntriesAsync(context.Catalog.ListAsync(
            new() { IncludeMetadata = true }, TestContext.Current.CancellationToken)));

        Assert.IsType(exceptionType, exception);
        Assert.Equal(0, context.Native.MetadataRequests);
        Assert.Equal(["tenant/a"], await DrainAsync(context.Catalog.ListAsync(
            cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task AzureBlobListAsync_MalformedProjectedMetadataPropagates()
    {
        var blob = Blob("wal/tenant/a");
        blob.Metadata["owner"] = null!;
        await using var context = await CreateAsync("AzureBlob", [], blobs: [blob]);

        await Assert.ThrowsAsync<ArgumentNullException>(() => DrainEntriesAsync(context.Catalog.ListAsync(
            new() { IncludeMetadata = true }, TestContext.Current.CancellationToken)));

        Assert.Equal(0, context.Native.MetadataRequests);
        Assert.Equal(["tenant/a"], await DrainAsync(context.Catalog.ListAsync(
            cancellationToken: TestContext.Current.CancellationToken)));
    }

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
            "tenant%2Fone", "tenant%2Fone/child", " leading space ",
            "percent%2f", @"back\slash", "tenant/",
        ];
        if (kind != "AzureTable")
        {
            ids = [.. ids, "raw\uD800", "raw\uD801", "unicode/\u00E9"];
        }
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
        var result = new List<string> { enumerator.Current.Id.Value };

        options.Prefix = new("other");
        options.MinId = new("other/q");
        options.MaxId = default;
        while (await enumerator.MoveNextAsync())
        {
            result.Add(enumerator.Current.Id.Value);
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
        Assert.Equal(kind == "AzureBlob" ? "tenant/a" : "tenant/z", enumerator.Current.Id.Value);
        Assert.Single(context.Native.Requests);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(kind == "AzureBlob" ? "tenant/b" : "tenant/a", enumerator.Current.Id.Value);
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
            Assert.Equal(kind == "AzureBlob" ? "a" : "z", enumerator.Current.Id.Value);
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
        Assert.Equal("tenant/valid", enumerator.Current.Id.Value);
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
            Assert.Equal(kind == "AzureBlob" ? "wal/tenant" : kind == "S3" ? "wal/" : null, request.Prefix);
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
        Assert.All(context.Native.Requests, request => Assert.Equal("wal/jobs/shards", request.Prefix));
    }

    [Fact]
    public async Task AzureBlobListAsync_FiltersAppendWalEntries()
    {
        BlobItem[] blobs =
        [
            Blob("wal/tenant/z"), Blob("wal/tenant/block", BlobType.Block),
            Blob("wal/tenant/page", BlobType.Page), Blob("checkpoints/tenant/snapshot", BlobType.Block),
            Blob("wal/tenant/child"), Blob("WAL/tenant/case"), Blob("wal/"),
            Blob("wal/ \t "), Blob("wal/tenant2"), Blob("wal/tenant"),
        ];
        await using var context = await CreateAsync("AzureBlob", [], blobs: blobs);
        Assert.Equal(
            ["tenant", "tenant/child", "tenant/z", "tenant2"],
            await DrainAsync(context.Catalog.ListAsync(new() { Prefix = new("tenant") }, TestContext.Current.CancellationToken)));
        Assert.Equal(6, context.Native.Requests.Sum(request => request.ResultCount));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tenant/")]
    public async Task AzureBlobListAsync_SeparatePrefixExcludesRetainedCheckpointsFromEveryPage(string? prefix)
    {
        string[] ids = ["tenant", "tenant/a", "tenant/b"];
        var blobs = ids.Select(id => Blob($"wal/{id}"))
            .Concat(ids.SelectMany(id => Enumerable.Range(0, 256)
                .Select(index => Blob($"checkpoints/{id}/{index}", BlobType.Block))))
            .Append(Blob("unrelated/tenant"))
            .ToArray();
        await using var context = await CreateAsync("AzureBlob", [], blobs: blobs);
        var expected = prefix is null ? ids : ids[1..];

        Assert.Equal(expected, await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = prefix is null ? default : new(prefix) }, TestContext.Current.CancellationToken)));
        Assert.Equal(expected.Length, context.Native.Requests.Sum(request => request.ResultCount));
        Assert.Equal(prefix is null ? 2 : 1, context.Native.Requests.Count);
        Assert.All(context.Native.Requests, request => Assert.Equal("wal/" + prefix, request.Prefix));
    }

    [Fact]
    public async Task AzureBlobListAsync_WalPrefixRequiresANonWhitespaceJournalId()
    {
        await using var context = await CreateAsync("AzureBlob", [],
            blobs: [Blob("wal/"), Blob("wal/ \t "), Blob("wal/wal/tenant"), Blob("wal/tenant/wal")]);

        Assert.Equal(["tenant/wal", "wal/tenant"], await DrainAsync(
            context.Catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken)));
        Assert.All(context.Native.Requests, request => Assert.Equal("wal/", request.Prefix));
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
    public async Task AzureBlobListAsync_MaxIdIncludesExactJournalAndExcludesCheckpointsBeforePagination()
    {
        await using var context = await CreateAsync("AzureBlob", [],
            blobs: [Blob("checkpoints/a/1", BlobType.Block), Blob("wal/a"), Blob("checkpoints/b/1", BlobType.Block), Blob("wal/b"), Blob("wal/z")]);
        context.Native.Failure = new InvalidOperationException("future tail must not be requested");
        context.Native.FailureAtRequest = 3;

        Assert.Equal(["a"], await DrainAsync(context.Catalog.ListAsync(
            new() { MaxId = new("a") }, TestContext.Current.CancellationToken)));
        Assert.Equal(2, Assert.Single(context.Native.Requests).ResultCount);
        Assert.Equal("wal/", context.Native.Requests[0].Prefix);
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
            Assert.Equal("wal/" + prefix, request.Prefix);
            Assert.Equal(kind == "AzureBlob" ? "wal/" + prefix : null, request.LowerStart);
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
        var blobs = new List<BlobItem> { Blob("wal/tenant/wal-child"), Blob("wal/tenant/z") };
        if (exactWal != "Missing")
        {
            blobs.Add(Blob("wal/tenant", exactWal == "Append" ? BlobType.Append : BlobType.Block));
        }

        await using var context = await CreateAsync("AzureBlob", [], blobs: blobs.ToArray());
        var result = await DrainAsync(context.Catalog.ListAsync(
            new() { Prefix = new("tenant"), MaxId = new("tenant/z") }, TestContext.Current.CancellationToken));

        Assert.Equal(exactWal == "Append" ? ["tenant", "tenant/wal-child", "tenant/z"] : new[] { "tenant/wal-child", "tenant/z" }, result);
        Assert.All(context.Native.Requests, request => Assert.Equal("wal/tenant", request.Prefix));
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
            Assert.Equal("tenant", enumerator.Current.Id.Value);
            if (cancel)
            {
                cancellation.Cancel();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
                Assert.Equal(cancellation.Token, exception.CancellationToken);
            }
        }

        Assert.Equal("wal/tenant", Assert.Single(context.Native.Requests).Prefix);
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
        Assert.Equal("wal/tenant", request.Prefix);
        Assert.Equal(0, request.ResultCount);
    }

    [Fact]
    public async Task AzureBlobListAsync_BoundedPrefixCrossesEmptyAndFilteredPages()
    {
        await using var context = await CreateAsync("AzureBlob", ["tenant", "tenant/a", "tenant/b"]);
        context.Native.EmptyFirstPage = true;

        Assert.Equal(["tenant", "tenant/a"], await DrainAsync(context.Catalog.ListAsync(
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
        Assert.Equal("wal/tenant", Assert.Single(context.Native.Requests).Prefix);
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
            Assert.Equal("wal/" + common, request.Prefix);
            Assert.Equal(kind == "AzureBlob" ? "wal/" + minimum : request.Cursor is null ? "wal/" + minimum[..^1] : null, request.LowerStart);
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
        Assert.Equal("wal/" + prefix, request.Prefix);
        Assert.Equal(kind == "AzureBlob" ? "wal/" + prefix : null, request.LowerStart);
        Assert.Equal(2, request.ResultCount);
    }

    [Theory]
    [InlineData("journals", null, null, "wal/journals", null)]
    [InlineData("journals", "journal", "journals/zeta", "wal/journals", null)]
    [InlineData("journals", "journals", "journals/zeta", "wal/journals", null)]
    [InlineData("journals", "journals/alpha", "journals/alpha", "wal/journals/alpha", null)]
    [InlineData("journals/", "journals/alpha", "journals/zeta", "wal/journals/", "wal/journals/alph")]
    public async Task S3ListAsync_OrderedSeekOnlyNarrowsBeyondNativePrefix(
        string prefix, string? minId, string? maxId, string nativePrefix, string? startAfter)
    {
        string[] ids = ["before/a", "journals/alpha", "journals/zeta", "other/beta"];
        await using var context = await CreateAsync("S3", ids, configureS3: options => options.UseOrderedListing = true);

        var result = await DrainAsync(context.Catalog.ListAsync(
            new()
            {
                Prefix = new(prefix),
                MinId = minId is null ? default : new(minId),
                MaxId = maxId is null ? default : new(maxId)
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(maxId == "journals/alpha" ? new[] { "journals/alpha" } : ["journals/alpha", "journals/zeta"], result);
        Assert.All(context.Native.Requests, request =>
        {
            Assert.Equal(nativePrefix, request.Prefix);
            Assert.Equal(request.Cursor is null ? startAfter : null, request.LowerStart);
        });
        Assert.Equal(result.Count, context.Native.Requests.Sum(request => request.ResultCount));
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
    public async Task AzureBlobListAsync_MaxIdPreservesShorterIdsInJournalOrder(string maximum)
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureTableListAsync_CustomMappingFiltersCanonicalIdsAndEscapesQueryLiterals(bool includeMetadata)
    {
        const string prefix = "tenant'one";
        string[] ids = [prefix, prefix + "/a", prefix + "/a/child", prefix + "/z"];
        await using var context = await CreateAsync("AzureTable", [],
            headers: ids.Select((id, index) => Header($"opaque-{index}", id)).ToArray(),
            configureTable: options => options.GetPartitionKey = id => $"opaque-{Array.IndexOf(ids, id.Value)}");

        var entries = await DrainEntriesAsync(context.Catalog.ListAsync(
            new() { Prefix = new(prefix), MaxId = new(prefix + "/a/child"), IncludeMetadata = includeMetadata },
            TestContext.Current.CancellationToken));

        Assert.Equal(ids[..3], entries.Select(entry => entry.Id.Value));
        Assert.All(entries, entry => Assert.Equal(includeMetadata, entry.Metadata is not null));
        AssertMetadataProjection(context.Native, "AzureTable", includeMetadata);
        Assert.Equal(0, context.Native.MetadataRequests);
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
            "wal/current/tenant/z", "wal/current/tenant/alias", "checkpoints/current/tenant/a/1",
            "wal/current/tenant/a", "wal/current/other/x", "wal/invalid", "wal/default",
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
        Assert.All(context.Native.Requests, request => Assert.Equal("wal/current/", request.Prefix));
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
    [InlineData("tenant", "tenant-a", "tenant-b", "wal/")]
    [InlineData("jobs/shards/202609", "jobs/shards/20260909-a", "jobs/shards/20260909-b", "wal/jobs/shards/")]
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
        await using var context = await CreateAsync("S3", [], keys: ids.Select(id => "wal/" + mapping[id]).ToArray(), configureS3: options =>
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
            Assert.Equal("wal/current/", request.Prefix);
            Assert.Null(request.LowerStart);
        });
    }

    [Theory]
    [InlineData(false, "wal/current/tenant/")]
    [InlineData(true, "wal/current/tenant/a")]
    public async Task S3ListAsync_CustomMapperAcceptsRawPartialPrefixes(bool ordered, string nativePrefix)
    {
        await using var context = await CreateAsync("S3", [], keys:
            ["wal/current/tenant/aa", "wal/current/tenant/ab", "wal/current/tenant/ac"], configureS3: options =>
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
        await using var context = await CreateAsync("S3", [], keys: ["wal/current/tenant/a", "wal/current/tenant/z"], configureS3: options =>
        {
            options.UseOrderedListing = true;
            options.GetObjectKey = id => "current/" + id.Value;
            options.TryParseJournalId = key => new JournalId(key["current/".Length..]);
        });

        Assert.Equal(["tenant/a"], await DrainAsync(context.Catalog.ListAsync(
            new() { MinId = new("tenant/a"), MaxId = new("tenant/b") }, TestContext.Current.CancellationToken)));
        var request = Assert.Single(context.Native.Requests);
        Assert.Equal("wal/", request.Prefix);
        Assert.Null(request.LowerStart);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task S3ListAsync_CustomMappingRequiresValidExplicitPrefix(string? mappedPrefix)
    {
        await using var context = await CreateAsync("S3", [], keys: ["wal/current/tenant"], configureS3: options =>
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
        Assert.Equal("wal/", Assert.Single(context.Native.Requests).Prefix);
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

    private static async Task<List<string>> DrainAsync(IAsyncEnumerable<JournalCatalogEntry> source)
    {
        var result = new List<string>();
        await foreach (var entry in source)
        {
            result.Add(entry.Id.Value);
        }

        return result;
    }

    private static async Task<List<JournalCatalogEntry>> DrainEntriesAsync(IAsyncEnumerable<JournalCatalogEntry> source)
    {
        var result = new List<JournalCatalogEntry>();
        await foreach (var entry in source)
        {
            result.Add(entry);
        }

        return result;
    }

    private static void AssertMetadataProjection(NativeState state, string kind, bool includeMetadata)
    {
        if (kind == "AzureBlob")
        {
            Assert.Equal(includeMetadata ? BlobTraits.Metadata : BlobTraits.None, Assert.Single(state.BlobTraits));
        }
        else
        {
            Assert.Equal(includeMetadata
                ? [AzureTableJournalStorage.JournalIdPropertyName, AzureTableJournalStorage.FormatPropertyName,
                    AzureTableJournalStorage.MetadataPropertyName, nameof(TableEntity.Timestamp)]
                : new[] { AzureTableJournalStorage.JournalIdPropertyName }, state.Select);
        }
    }

    private static BlobItem Blob(string name, BlobType type = BlobType.Append)
        => BlobsModelFactory.BlobItem(
            name: name, deleted: false,
            properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false, blobType: type, eTag: new ETag("listed")),
            metadata: new Dictionary<string, string>
            {
                [AzureBlobJournalStorage.FormatMetadataKey] = "test",
                [AzureBlobJournalStorage.CheckpointMetadataKey] = "checkpoints/previous/snapshot",
                [AzureBlobJournalStorage.CheckpointOffsetMetadataKey] = "0",
                [AzureBlobJournalStorage.WalGenerationMetadataKey] = "generation",
                ["owner"] = "alice",
            });

    private static TableEntity Header(string partition, string? id = null)
    {
        var result = new TableEntity(partition, AzureTableJournalStorage.HeaderRowKey)
        {
            ETag = new ETag("listed"),
            Timestamp = DateTimeOffset.UnixEpoch,
            [AzureTableJournalStorage.FormatPropertyName] = "test",
            [AzureTableJournalStorage.MetadataPropertyName] = """{"owner":"alice"}""",
        };
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
        Action<AzureTableJournalStorageOptions>? configureTable = null,
        AzureJournalStorageMetricsFixture? metrics = null)
    {
        var context = new ProviderContext(kind, ids, blobLayout, blobs, headers, keys, configureS3, configureTable, metrics);
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
            Action<AzureTableJournalStorageOptions>? configureTable, AzureJournalStorageMetricsFixture? metrics)
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
                    var container = new FakeContainer(Native, blobs ?? ids.Select(id => Blob($"wal/{id}")).ToArray());
                    var blobOptions = new AzureBlobJournalStorageOptions { ContainerName = "journals" };
                    blobOptions.ConfigureBlobServiceClient(_ => Task.FromResult<BlobServiceClient>(new FakeBlobService(container)));
                    if (blobLayout == "EquivalentDelegate")
                    {
                        blobOptions.GetWalBlobName = id => $"wal/{id.Value}";
                    }
                    else if (blobLayout == "WrapperFactory")
                    {
                        blobOptions.BuildContainerFactory = (_, _) => new WrapperFactory(container);
                    }

                    Provider = new AzureBlobJournalStorageProvider(
                        Options.Create(blobOptions), manager, _services, NullLogger<AzureBlobJournalStorage>.Instance, metrics?.Blob);
                    break;
                case "AzureTable":
                    var table = new FakeTable(Native, headers ?? ids.Select(id => Header(AzureTableJournalStorageOptions.GetDefaultPartitionKey(new(id)), id)).ToArray());
                    var tableOptions = new AzureTableJournalStorageOptions { TableName = "journals" };
                    configureTable?.Invoke(tableOptions);
                    tableOptions.ConfigureTableServiceClient(_ => Task.FromResult<TableServiceClient>(new FakeTableService(table)));
                    Provider = new AzureTableJournalStorageProvider(
                        Options.Create(tableOptions), manager, _services, NullLogger<AzureTableJournalStorage>.Instance, metrics?.Table);
                    break;
                case "S3":
                    _client = Substitute.For<IAmazonS3>();
                    _client.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(new HeadBucketResponse()));
                    var s3Options = new S3JournalStorageOptions { BucketName = "journals", S3Client = _client, UseOrderedListing = false };
                    configureS3?.Invoke(s3Options);
                    var objects = (keys ?? ids.Select(id => $"wal/{id}").ToArray()).Select(key => new S3Object { Key = key }).ToArray();
                    var continuationRecords = new Dictionary<string, S3Object[]>();
                    _client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).Returns(call =>
                    {
                        var request = call.Arg<ListObjectsV2Request>();
                        Assert.Equal("journals", request.BucketName);
                        S3Object[] records;
                        if (request.ContinuationToken is { } token)
                        {
                            Assert.Null(request.StartAfter);
                            records = continuationRecords[token];
                        }
                        else
                        {
                            var matching = objects.Where(item => (request.Prefix is null
                                || item.Key.StartsWith(request.Prefix, StringComparison.Ordinal))
                                && (request.StartAfter is null || string.CompareOrdinal(item.Key, request.StartAfter) > 0));
                            if (s3Options.UseOrderedListing)
                            {
                                matching = matching.OrderBy(item => item.Key, StringComparer.Ordinal);
                            }

                            records = matching.ToArray();
                        }

                        var page = Native.Fetch(records, request.ContinuationToken, request.MaxKeys, request.Prefix,
                            call.Arg<CancellationToken>(), request.StartAfter);
                        if (page.NextCursor is { } nextToken)
                        {
                            continuationRecords[nextToken] = records;
                        }

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

        public void AssertNoS3MetadataRequests()
            => Assert.DoesNotContain(_client!.ReceivedCalls(), call => call.GetMethodInfo().Name
                is nameof(IAmazonS3.GetObjectMetadataAsync) or nameof(IAmazonS3.GetObjectAsync));

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
        public Action? BeforeRequest { get; set; }
        public int DisposedEnumerators { get; set; }
        public int SetupCalls { get; set; }
        public bool ResourceAlreadyExists { get; set; }
        public Action? BeforeSetup { get; set; }
        public int FailingSetup { get; set; }
        public Exception? SetupFailure { get; set; }
        public string? Filter { get; set; }
        public string[]? Select { get; set; }
        public List<BlobTraits> BlobTraits { get; } = [];
        public int MetadataRequests { get; set; }
        public ETag? CurrentETag { get; set; }

        public NativePage<T> Fetch<T>(T[] records, string? cursor, int? maximum, string? prefix, CancellationToken cancellationToken, string? lowerStart = null)
        {
            var offset = cursor is null ? 0 : int.Parse(cursor.AsSpan("native:".Length), CultureInfo.InvariantCulture);
            var count = cursor is null && EmptyFirstPage ? 0 : Math.Min(Math.Min(2, maximum ?? 2), records.Length - offset);
            var next = offset + count < records.Length ? $"native:{offset + count}" : null;
            Requests.Add(new(cursor, maximum, prefix, cancellationToken, count, next, lowerStart));
            BeforeRequest?.Invoke();
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
        protected override AppendBlobClient GetAppendBlobClientCore(string blobName)
            => new FakeAppendBlob(state, records, blobName);

        public override Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(
            PublicAccessType publicAccessType = PublicAccessType.None,
            IDictionary<string, string>? metadata = null,
            BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null,
            CancellationToken cancellationToken = default)
        {
            state.BeforeSetup?.Invoke();
            if (++state.SetupCalls == state.FailingSetup && state.SetupFailure is { } failure)
            {
                return Task.FromException<Response<BlobContainerInfo>>(failure);
            }

            if (state.ResourceAlreadyExists)
            {
                return Task.FromResult<Response<BlobContainerInfo>>(null!);
            }

            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContainerInfo(new ETag("created"), DateTimeOffset.UnixEpoch), new FakeResponse()));
        }

        public override AsyncPageable<BlobItem> GetBlobsAsync(GetBlobsOptions options, CancellationToken cancellationToken = default)
        {
            state.BlobTraits.Add(options.Traits);
            Assert.Equal(BlobStates.None, options.States);
            return new FakePageable<BlobItem>(
                state, records.Where(item => (options.Prefix is null || item.Name.StartsWith(options.Prefix, StringComparison.Ordinal))
                        && (options.StartFrom is null || string.CompareOrdinal(item.Name, options.StartFrom) >= 0))
                    .OrderBy(item => item.Name, StringComparer.Ordinal)
                    .Select(item => BlobsModelFactory.BlobItem(
                        name: item.Name, deleted: false, properties: item.Properties,
                        metadata: options.Traits.HasFlag(BlobTraits.Metadata)
                            ? new Dictionary<string, string>(item.Metadata) : null))
                    .ToArray(),
                null, options.Prefix, cancellationToken, options.StartFrom);
        }
    }

    private sealed class FakeAppendBlob(NativeState state, BlobItem[] records, string name) : AppendBlobClient
    {
        public override Task<Response<BlobProperties>> GetPropertiesAsync(
            BlobRequestConditions conditions = default!, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.MetadataRequests++;
            var item = Assert.Single(records, item => item.Name == name);
            var eTag = state.CurrentETag ?? item.Properties.ETag!.Value;
            if (conditions?.IfMatch is { } expected && expected != eTag)
            {
                throw new RequestFailedException(412, "The metadata snapshot is stale.");
            }

            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobProperties(eTag: eTag, blobType: BlobType.Append, metadata: item.Metadata),
                new FakeResponse()));
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
        public override Task<Response<T>> GetEntityAsync<T>(
            string partitionKey, string rowKey, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.MetadataRequests++;
            var entity = Assert.Single(records, entity => entity.PartitionKey == partitionKey && entity.RowKey == rowKey);
            var snapshot = new TableEntity(new Dictionary<string, object>(entity))
            {
                PartitionKey = partitionKey,
                RowKey = rowKey,
                ETag = state.CurrentETag ?? entity.ETag,
                Timestamp = entity.Timestamp,
            };
            return Task.FromResult(Response.FromValue((T)(ITableEntity)snapshot, new FakeResponse()));
        }

        public override Task<Response<TableItem>> CreateIfNotExistsAsync(CancellationToken cancellationToken = default)
        {
            state.SetupCalls++;
            state.BeforeSetup?.Invoke();
            return Task.FromResult(Response.FromValue(
                new TableItem("journals"), new FakeResponse(state.ResourceAlreadyExists ? 409 : 200)));
        }

        public override AsyncPageable<T> QueryAsync<T>(
            string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
        {
            state.Filter = filter;
            state.Select = select?.ToArray();
            Assert.Equal(1000, maxPerPage);
            Assert.NotNull(filter);
            Assert.NotNull(state.Select);
            var values = records.Where(entity => MatchesFilter(entity, filter)).Select(entity =>
            {
                var projected = new TableEntity(entity.PartitionKey, entity.RowKey);
                foreach (var property in state.Select)
                {
                    if (property == nameof(TableEntity.Timestamp))
                    {
                        projected.Timestamp = entity.Timestamp;
                        projected.ETag = entity.ETag;
                    }
                    else if (entity.TryGetValue(property, out var value))
                    {
                        projected[property] = value;
                    }
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

    private sealed class FakeResponse(int status = 200) : Response
    {
        public override int Status => status;
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
