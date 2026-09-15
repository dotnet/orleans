using System.Buffers;
using System.Diagnostics.Metrics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class S3JournalStorageInstrumentsTests
{
    [Fact]
    public void CreateForDirectConstruction_ReturnsCachedInstance()
    {
        var first = S3JournalStorageInstruments.CreateForDirectConstruction();
        var second = S3JournalStorageInstruments.CreateForDirectConstruction();

        Assert.Same(first, second);
    }

    [Fact]
    public void OnOperationCompleted_RecordsNamesTypesUnitsValuesAndOutcomeTags()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var orleansInstruments = new OrleansInstruments(meterFactory);
        var instruments = new S3JournalStorageInstruments(orleansInstruments);
        using var operations = new MetricCollector<long>(
            orleansInstruments.Meter,
            "orleans-journaling-s3-operations");
        using var bytes = new MetricCollector<long>(
            orleansInstruments.Meter,
            "orleans-journaling-s3-operation-bytes");
        using var duration = new MetricCollector<double>(
            orleansInstruments.Meter,
            "orleans-journaling-s3-operation-duration");

        instruments.OnOperationCompleted(
            S3JournalStorageInstruments.OperationAppend,
            TimeSpan.FromMilliseconds(8),
            bytes: 12,
            succeeded: true);
        instruments.OnOperationCompleted(
            S3JournalStorageInstruments.OperationRead,
            TimeSpan.FromMilliseconds(-3),
            bytes: 99,
            succeeded: false);
        instruments.OnOperationCompleted(
            S3JournalStorageInstruments.OperationDelete,
            TimeSpan.FromMilliseconds(5),
            bytes: 0,
            succeeded: true);

        Assert.Equal("orleans-journaling-s3-operations", operations.Instrument?.Name);
        Assert.IsType<Counter<long>>(operations.Instrument);
        Assert.Null(operations.Instrument.Unit);
        Assert.Equal("orleans-journaling-s3-operation-bytes", bytes.Instrument?.Name);
        Assert.IsType<Counter<long>>(bytes.Instrument);
        Assert.Equal("bytes", bytes.Instrument.Unit);
        Assert.Equal("orleans-journaling-s3-operation-duration", duration.Instrument?.Name);
        Assert.IsType<Histogram<double>>(duration.Instrument);
        Assert.Equal("ms", duration.Instrument.Unit);

        var operationMeasurements = operations.GetMeasurementSnapshot();
        Assert.Equal([1L, 1L, 1L], operationMeasurements.Select(static measurement => measurement.Value));
        Assert.True(operationMeasurements[0].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "append"), new("status", "ok") }));
        Assert.True(operationMeasurements[1].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "read"), new("status", "error") }));
        Assert.True(operationMeasurements[2].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "delete"), new("status", "ok") }));

        var bytesMeasurement = Assert.Single(bytes.GetMeasurementSnapshot());
        Assert.Equal(12, bytesMeasurement.Value);
        Assert.True(bytesMeasurement.MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "append") }));

        var durationMeasurements = duration.GetMeasurementSnapshot();
        Assert.Equal([8d, 0d, 5d], durationMeasurements.Select(static measurement => measurement.Value));
        Assert.True(durationMeasurements[0].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "append"), new("status", "ok") }));
        Assert.True(durationMeasurements[1].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "read"), new("status", "error") }));
        Assert.True(durationMeasurements[2].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "delete"), new("status", "ok") }));
    }

    [Fact]
    public async Task ProviderLifecycle_RecordsBucketRequestsAndHandledConflict()
    {
        using var context = new MetricsContext();
        context.Options.CreateBucketIfNotExists = true;
        var headCalls = 0;
        context.Client.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++headCalls == 1
                ? context.Fail<HeadBucketResponse>(S3Failure(HttpStatusCode.NotFound))
                : context.Complete(new HeadBucketResponse()));
        context.Client.PutBucketAsync(Arg.Any<PutBucketRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => context.Fail<PutBucketResponse>(S3Failure(HttpStatusCode.Conflict)));

        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        await context.Provider.CloseAsync(TestContext.Current.CancellationToken);

        context.AssertCalls(("head_bucket", "not_found"), ("create_bucket", "conflict"), ("head_bucket", "ok"));
        context.AssertOperations(("initialize", "ok"), ("close", "ok"));
        Assert.Equal([15d, 0d], context.OperationDuration.GetMeasurementSnapshot().Select(value => value.Value));
        Assert.Empty(context.LegacyOperations.GetMeasurementSnapshot());
        Assert.Empty(context.Retries.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task ListAsync_RecordsEmptyAndContinuedPagesWithoutConsumerIdleTime()
    {
        using var context = new MetricsContext();
        var requests = new List<ListObjectsV2Request>();
        context.Client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ListObjectsV2Request>();
                requests.Add(request);
                return context.Complete(request.ContinuationToken is null
                    ? new ListObjectsV2Response { S3Objects = null, IsTruncated = true, NextContinuationToken = "opaque-next" }
                    : new ListObjectsV2Response
                    {
                        S3Objects = [new S3Object { Key = "wal/journals/a" }, new S3Object { Key = "wal/other/filtered" }],
                        IsTruncated = false,
                    });
            });
        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        var sequence = context.Provider.ListAsync(
            new() { Prefix = new("journals/"), IncludeMetadata = true }, TestContext.Current.CancellationToken);
        Assert.Empty(requests);
        await using var enumerator = sequence.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("journals/a", enumerator.Current.Id.Value);
        Assert.Null(enumerator.Current.Metadata);
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await enumerator.MoveNextAsync());

        Assert.Equal(new string?[] { null, "opaque-next" }, requests.Select(request => request.ContinuationToken));
        context.AssertCalls(("head_bucket", "ok"), ("list_objects_v2", "ok"), ("list_objects_v2", "ok"));
        context.AssertOperations(("initialize", "ok"), ("list", "ok"));
        Assert.Equal([5d, 10d], context.OperationDuration.GetMeasurementSnapshot().Select(value => value.Value));
        var items = Assert.Single(context.ApiItems.GetMeasurementSnapshot());
        Assert.Equal(2, items.Value);
        Assert.True(items.MatchesTags(ApiTags("list_objects_v2", "ok")));
        Assert.Equal(1, Assert.Single(context.CatalogEntries.GetMeasurementSnapshot()).Value);
        await context.Client.DidNotReceive().GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAsync_RecordsContinuationFailureOrCancellation(bool canceled)
    {
        using var context = new MetricsContext();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Exception failure = canceled
            ? new OperationCanceledException(cancellation.Token)
            : S3Failure(HttpStatusCode.ServiceUnavailable);
        context.Client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.Arg<ListObjectsV2Request>().ContinuationToken is null)
                {
                    return context.Complete(new ListObjectsV2Response
                    {
                        S3Objects = [],
                        IsTruncated = true,
                        NextContinuationToken = "opaque-failure",
                    });
                }

                if (canceled)
                {
                    cancellation.Cancel();
                }

                return context.Fail<ListObjectsV2Response>(failure);
            });
        await context.Provider.InitializeAsync(cancellation.Token);
        await using var enumerator = context.Provider.ListAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var actual = await Record.ExceptionAsync(() => enumerator.MoveNextAsync().AsTask());

        Assert.Same(failure, actual);
        context.AssertCalls(("head_bucket", "ok"), ("list_objects_v2", "ok"),
            ("list_objects_v2", canceled ? "canceled" : "unavailable"));
        context.AssertOperations(("initialize", "ok"), ("list", canceled ? "canceled" : "error"));
        Assert.Equal([5d, 10d], context.OperationDuration.GetMeasurementSnapshot().Select(value => value.Value));
        Assert.Empty(context.ApiItems.GetMeasurementSnapshot());
        Assert.Empty(context.CatalogEntries.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task ListAsync_EarlyDisposalRecordsOneLogicalOutcome()
    {
        using var context = new MetricsContext();
        context.Client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(_ => context.Complete(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = "wal/a" }, new S3Object { Key = "wal/b" }],
                IsTruncated = true,
                NextContinuationToken = "unused",
            }));
        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        var enumerator = context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        context.Clock.Advance(TimeSpan.FromHours(1));
        await enumerator.DisposeAsync();
        await enumerator.DisposeAsync();

        context.AssertCalls(("head_bucket", "ok"), ("list_objects_v2", "ok"));
        context.AssertOperations(("initialize", "ok"), ("list", "disposed"));
        Assert.Equal([5d, 5d], context.OperationDuration.GetMeasurementSnapshot().Select(value => value.Value));
        Assert.Equal(1, Assert.Single(context.CatalogEntries.GetMeasurementSnapshot()).Value);
        Assert.Equal(2, Assert.Single(context.ApiItems.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public async Task ListAsync_PreCanceledEnumerationRecordsNoNativeCall()
    {
        using var context = new MetricsContext();
        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await using var enumerator = context.Provider.ListAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());

        context.AssertCalls(("head_bucket", "ok"));
        context.AssertOperations(("initialize", "ok"), ("list", "canceled"));
        Assert.Equal([5d, 0d], context.OperationDuration.GetMeasurementSnapshot().Select(value => value.Value));
    }

    [Fact]
    public async Task StorageOperations_RecordLogicalAndNativeWorkIncludingPartHeaders()
    {
        using var context = new MetricsContext();
        context.ConfigureObjectStorage();
        var cancellationToken = TestContext.Current.CancellationToken;
        await context.Provider.InitializeAsync(cancellationToken);
        var storage = context.Provider.CreateStorage(new("journals/private-id"));
        Assert.IsType<InstrumentedJournalStorage>(storage);

        Assert.True(await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["catalog"] = "open" }, cancellationToken));
        Assert.False(await storage.CreateIfNotExistsAsync(cancellationToken: cancellationToken));
        Assert.Equal("open", (await storage.GetMetadataAsync(cancellationToken))!.Properties["catalog"]);
        Assert.Equal("closed", (await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["catalog"] = "closed" }, cancellationToken: cancellationToken))!.Properties["catalog"]);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2, 3]), cancellationToken);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([4, 5]), cancellationToken);
        var consumer = new CapturingConsumer();
        await storage.ReadAsync(consumer, cancellationToken);
        Assert.Equal(new byte[] { 4, 5 }, consumer.Bytes);
        await storage.DeleteAsync(cancellationToken);
        Assert.Null(await storage.GetMetadataAsync(cancellationToken));
        await context.Provider.CloseAsync(cancellationToken);

        context.AssertOperations(
            ("initialize", "ok"), ("create", "ok"), ("create", "already_exists"), ("get_metadata", "ok"),
            ("update_metadata", "ok"), ("append", "ok"), ("replace", "ok"), ("read", "ok"),
            ("delete", "ok"), ("get_metadata", "not_found"), ("close", "ok"));
        context.AssertCalls(
            ("head_bucket", "ok"), ("put_object", "ok"), ("put_object", "conflict"), ("head_object", "ok"),
            ("head_object", "ok"), ("get_object", "ok"), ("put_object", "ok"), ("put_object", "ok"),
            ("head_object", "ok"), ("head_object", "ok"), ("put_object", "ok"), ("put_object", "ok"),
            ("head_object", "ok"), ("head_object", "ok"), ("get_object", "ok"), ("get_object", "ok"),
            ("head_object", "ok"), ("head_object", "ok"), ("delete_object", "ok"), ("delete_object", "ok"),
            ("head_object", "not_found"));
        Assert.Equal(new int?[] { null, null, null, 1, null, 1, null, 1, null },
            context.HeadRequests.Select(request => request.PartNumber));
        Assert.Equal([5d, 5d, 5d, 5d, 15d, 5d, 20d, 20d, 20d, 5d, 0d],
            context.OperationDuration.GetMeasurementSnapshot().Select(value => value.Value));
        Assert.Equal([3L, 2L, 2L], context.OperationBytes.GetMeasurementSnapshot().Select(value => value.Value));
        string[] legacyNames = ["create", "create", "get_metadata", "update_metadata", "append", "replace", "read", "delete", "get_metadata"];
        var legacy = context.LegacyOperations.GetMeasurementSnapshot();
        Assert.Equal(legacyNames.Length, legacy.Count);
        for (var index = 0; index < legacyNames.Length; index++)
        {
            Assert.Equal(1, legacy[index].Value);
            Assert.True(legacy[index].MatchesTags(new KeyValuePair<string, object?>[]
            {
                new("operation", legacyNames[index]), new("status", "ok"),
            }));
        }
        Assert.Empty(context.Retries.GetMeasurementSnapshot());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, null, "not_found", "not_found")]
    [InlineData(HttpStatusCode.Conflict, null, "conflict", "error")]
    [InlineData(HttpStatusCode.PreconditionFailed, null, "conflict", "error")]
    [InlineData(HttpStatusCode.TooManyRequests, null, "throttled", "error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, "unavailable", "error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown", "throttled", "error")]
    public async Task GetMetadataAsync_DistinguishesNativeAndLogicalOutcomes(
        HttpStatusCode statusCode, string? errorCode, string nativeStatus, string logicalStatus)
    {
        using var context = new MetricsContext();
        var failure = S3Failure(statusCode);
        failure.ErrorCode = errorCode;
        context.Client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => context.Fail<GetObjectMetadataResponse>(failure));
        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        var storage = context.Provider.CreateStorage(new("journals/private-id"));
        if (statusCode == HttpStatusCode.NotFound)
        {
            Assert.Null(await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Same(failure, await Assert.ThrowsAsync<AmazonS3Exception>(
                () => storage.GetMetadataAsync(TestContext.Current.CancellationToken).AsTask()));
        }

        context.AssertCalls(("head_bucket", "ok"), ("head_object", nativeStatus));
        context.AssertOperations(("initialize", "ok"), ("get_metadata", logicalStatus));
        Assert.Empty(context.Retries.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task ReplaceAsync_RecordsCheckpointCollisionRetryWithoutDuplicateLogicalOperation()
    {
        using var context = new MetricsContext();
        context.ConfigureObjectStorage();
        var rejected = false;
        context.PutFailure = request => request.Key.StartsWith("checkpoints/", StringComparison.Ordinal) && !rejected
            ? Reject() : null;
        Exception Reject()
        {
            rejected = true;
            return S3Failure(HttpStatusCode.PreconditionFailed);
        }

        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        var storage = context.Provider.CreateStorage(new("journals/a"));
        await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);

        context.AssertCalls(("head_bucket", "ok"), ("put_object", "ok"), ("head_object", "ok"), ("head_object", "ok"),
            ("put_object", "conflict"), ("put_object", "ok"), ("put_object", "ok"));
        context.AssertOperations(("initialize", "ok"), ("create", "ok"), ("replace", "ok"));
        var retry = Assert.Single(context.Retries.GetMeasurementSnapshot());
        Assert.Equal(1, retry.Value);
        Assert.True(retry.MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "s3"), new("reason", "checkpoint_collision") }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task UpdateMetadataAsync_CountsOnlyActualConflictRetries(int failureCount)
    {
        using var context = new MetricsContext();
        context.ConfigureObjectStorage();
        var failures = failureCount;
        context.PutFailure = request => request.IfMatch is not null && failures-- > 0
            ? S3Failure(HttpStatusCode.PreconditionFailed) : null;
        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        var storage = context.Provider.CreateStorage(new("journals/a"));
        await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var updated = await storage.UpdateMetadataAsync(new Dictionary<string, string> { ["catalog"] = "closed" },
            cancellationToken: TestContext.Current.CancellationToken);

        if (failureCount == 1)
        {
            Assert.Equal("closed", updated!.Properties["catalog"]);
        }
        else
        {
            Assert.Null(updated);
        }

        var attempts = Math.Min(failureCount + 1, 3);
        var calls = new List<(string, string)> { ("head_bucket", "ok"), ("put_object", "ok") };
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            calls.AddRange([("head_object", "ok"), ("get_object", "ok"),
                ("put_object", attempt < failureCount ? "conflict" : "ok")]);
        }

        context.AssertCalls(calls.ToArray());
        context.AssertOperations(("initialize", "ok"), ("create", "ok"),
            ("update_metadata", failureCount == 1 ? "ok" : "not_applied"));
        var retries = context.Retries.GetMeasurementSnapshot();
        Assert.Equal(attempts - 1, retries.Count);
        Assert.All(retries, retry =>
        {
            Assert.Equal(1, retry.Value);
            Assert.True(retry.MatchesTags(
                new KeyValuePair<string, object?>[] { new("provider", "s3"), new("reason", "metadata_conflict") }));
        });
    }

    [Fact]
    public async Task AppendAsync_RecordsRecoveredMetadataConflictWithoutDuplicateLogicalOperation()
    {
        using var context = new MetricsContext();
        context.ConfigureObjectStorage();
        var failed = false;
        context.PutFailure = request =>
        {
            if (request.WriteOffsetBytes is not null && !failed)
            {
                failed = true;
                return S3Failure(HttpStatusCode.PreconditionFailed);
            }

            return null;
        };
        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        var storage = context.Provider.CreateStorage(new("journals/a"));
        await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);

        context.AssertCalls(("head_bucket", "ok"), ("put_object", "ok"), ("put_object", "conflict"),
            ("head_object", "ok"), ("head_object", "ok"), ("put_object", "ok"));
        context.AssertOperations(("initialize", "ok"), ("create", "ok"), ("append", "ok"));
        var retry = Assert.Single(context.Retries.GetMeasurementSnapshot());
        Assert.Equal(1, retry.Value);
        Assert.True(retry.MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "s3"), new("reason", "metadata_conflict") }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_RecordsNativeConflictOrCancellationWithoutRetry(bool canceled)
    {
        using var context = new MetricsContext();
        context.Options.MaxMetadataOnlyConflictRetries = 0;
        context.ConfigureObjectStorage();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Exception failure = canceled ? new OperationCanceledException(cancellation.Token) : S3Failure(HttpStatusCode.PreconditionFailed);
        context.DeleteFailure = _ =>
        {
            if (canceled)
            {
                cancellation.Cancel();
            }

            return failure;
        };
        await context.Provider.InitializeAsync(cancellation.Token);
        var storage = context.Provider.CreateStorage(new("journals/a"));
        await storage.CreateIfNotExistsAsync(cancellationToken: cancellation.Token);

        var actual = await Record.ExceptionAsync(() => storage.DeleteAsync(cancellation.Token).AsTask());

        if (canceled)
        {
            Assert.Same(failure, actual);
        }
        else
        {
            Assert.IsType<InconsistentStateException>(actual);
        }

        var status = canceled ? "canceled" : "conflict";
        context.AssertCalls(("head_bucket", "ok"), ("put_object", "ok"),
            ("head_object", "ok"), ("head_object", "ok"), ("delete_object", status));
        context.AssertOperations(("initialize", "ok"), ("create", "ok"), ("delete", status));
        Assert.Empty(context.Retries.GetMeasurementSnapshot());
        Assert.True(context.LegacyOperations.GetMeasurementSnapshot()[1].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "delete"), new("status", "error") }));
    }

    [Fact]
    public async Task DirectStorage_PreservesLegacyMetricsWithoutGenericLogicalDoubleCounting()
    {
        using var context = new MetricsContext();
        context.ConfigureObjectStorage();
        var storage = new S3JournalStorage(new(
            NullLogger<S3JournalStorage>.Instance, Options.Create(context.Options), context.Instruments,
            mimeType: "application/octet-stream", journalFormatKey: null), context.Client, new("journals/a"));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2]), TestContext.Current.CancellationToken);

        context.AssertCalls(("put_object", "ok"), ("put_object", "ok"));
        Assert.Empty(context.Operations.GetMeasurementSnapshot());
        var legacy = Assert.Single(context.LegacyOperations.GetMeasurementSnapshot());
        Assert.Equal(1, legacy.Value);
        Assert.True(legacy.MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "append"), new("status", "ok") }));
    }

    private static AmazonS3Exception S3Failure(HttpStatusCode statusCode)
        => new("Private service diagnostic which must not become a metric tag.") { StatusCode = statusCode };

    private static KeyValuePair<string, object?>[] ApiTags(string api, string status)
        => [new("provider", "s3"), new("api", api), new("status", status)];

    private sealed class MetricsContext : IDisposable
    {
        private readonly ServiceProvider _services;
        private int _version;

        public MetricsContext()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSerializer();
            services.AddLogging();
            services.AddSingleton<OrleansBinaryJournalFormat>();
            services.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey,
                static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
            _services = services.BuildServiceProvider();
            var meter = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
            Instruments = new(meter, Clock);
            Operations = new(meter.Meter, "orleans-journaling-provider-operations");
            OperationDuration = new(meter.Meter, "orleans-journaling-provider-operation-duration");
            OperationBytes = new(meter.Meter, "orleans-journaling-provider-operation-bytes");
            ApiCalls = new(meter.Meter, "orleans-journaling-provider-api-calls");
            ApiDuration = new(meter.Meter, "orleans-journaling-provider-api-call-duration");
            ApiItems = new(meter.Meter, "orleans-journaling-provider-api-items");
            CatalogEntries = new(meter.Meter, "orleans-journaling-provider-catalog-entries");
            Retries = new(meter.Meter, "orleans-journaling-provider-retries");
            LegacyOperations = new(meter.Meter, "orleans-journaling-s3-operations");
            Options = new() { BucketName = "private-bucket", S3Client = Client, MetadataOnlyConflictInitialBackoff = TimeSpan.Zero };
            Provider = new(Microsoft.Extensions.Options.Options.Create(Options),
                Microsoft.Extensions.Options.Options.Create(new JournaledStateManagerOptions
                {
                    JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey,
                }),
                _services, NullLogger<S3JournalStorage>.Instance, Instruments);
            Client.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => Complete(new HeadBucketResponse()));
        }

        public FakeTimeProvider Clock { get; } = new();
        public IAmazonS3 Client { get; } = Substitute.For<IAmazonS3>();
        public S3JournalStorageOptions Options { get; }
        public S3JournalStorageProvider Provider { get; }
        public S3JournalStorageInstruments Instruments { get; }
        public MetricCollector<long> Operations { get; }
        public MetricCollector<double> OperationDuration { get; }
        public MetricCollector<long> OperationBytes { get; }
        public MetricCollector<long> ApiCalls { get; }
        public MetricCollector<double> ApiDuration { get; }
        public MetricCollector<long> ApiItems { get; }
        public MetricCollector<long> CatalogEntries { get; }
        public MetricCollector<long> Retries { get; }
        public MetricCollector<long> LegacyOperations { get; }
        public List<GetObjectMetadataRequest> HeadRequests { get; } = [];
        public Func<PutObjectRequest, Exception?>? PutFailure { get; set; }
        public Func<DeleteObjectRequest, Exception?>? DeleteFailure { get; set; }

        public Task<T> Complete<T>(T response)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(5));
            return Task.FromResult(response);
        }

        public Task<T> Fail<T>(Exception exception)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(5));
            return Task.FromException<T>(exception);
        }

        public void AssertCalls(params (string Api, string Status)[] expected)
        {
            var calls = ApiCalls.GetMeasurementSnapshot();
            Assert.Equal(expected.Length, calls.Count);
            var durations = ApiDuration.GetMeasurementSnapshot();
            Assert.Equal(expected.Length, durations.Count);
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(1, calls[index].Value);
                Assert.True(calls[index].MatchesTags(ApiTags(expected[index].Api, expected[index].Status)));
                Assert.Equal(5, durations[index].Value);
                Assert.True(durations[index].MatchesTags(ApiTags(expected[index].Api, expected[index].Status)));
            }
        }

        public void AssertOperations(params (string Operation, string Status)[] expected)
        {
            var values = Operations.GetMeasurementSnapshot();
            Assert.Equal(expected.Length, values.Count);
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(1, values[index].Value);
                Assert.True(values[index].MatchesTags(new KeyValuePair<string, object?>[]
                {
                    new("provider", "s3"), new("operation", expected[index].Operation), new("status", expected[index].Status),
                }));
            }
        }

        public void ConfigureObjectStorage()
        {
            var objects = new Dictionary<string, StoredObject>();
            Client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<PutObjectRequest>();
                    if (PutFailure?.Invoke(request) is { } failure)
                    {
                        return Fail<PutObjectResponse>(failure);
                    }

                    objects.TryGetValue(request.Key, out var current);
                    if (request.IfNoneMatch == "*" && current is not null
                        || request.IfMatch is not null && request.IfMatch != current?.ETag)
                    {
                        return Fail<PutObjectResponse>(S3Failure(HttpStatusCode.PreconditionFailed));
                    }

                    using var payload = new MemoryStream();
                    request.InputStream.CopyTo(payload);
                    var data = payload.ToArray();
                    var metadata = request.Metadata.Keys.ToDictionary(key => key, key => request.Metadata[key]);
                    if (request.WriteOffsetBytes is not null)
                    {
                        Assert.NotNull(current);
                        Assert.Equal(current.Data.Length, request.WriteOffsetBytes);
                        data = [.. current.Data, .. data];
                        metadata = current.Metadata;
                    }

                    var stored = new StoredObject(data, metadata, $"etag-{++_version}");
                    objects[request.Key] = stored;
                    return Complete(new PutObjectResponse { ETag = stored.ETag });
                });
            Client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<GetObjectMetadataRequest>();
                    HeadRequests.Add(request);
                    if (!objects.TryGetValue(request.Key, out var stored))
                    {
                        return Fail<GetObjectMetadataResponse>(S3Failure(HttpStatusCode.NotFound));
                    }

                    Assert.True(request.EtagToMatch is null || request.EtagToMatch == stored.ETag);
                    var result = new GetObjectMetadataResponse
                    {
                        ETag = stored.ETag,
                        ContentLength = stored.Data.Length,
                        PartsCount = 1,
                        LastModified = Clock.GetUtcNow().UtcDateTime,
                    };
                    foreach (var (key, value) in stored.Metadata)
                    {
                        result.Metadata.Add(key, value);
                    }

                    return Complete(result);
                });
            Client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<GetObjectRequest>();
                    var stored = objects[request.Key];
                    var result = new GetObjectResponse
                    {
                        ETag = stored.ETag,
                        ContentLength = stored.Data.Length,
                        PartsCount = 1,
                        ResponseStream = new MemoryStream(stored.Data, writable: false),
                    };
                    foreach (var (key, value) in stored.Metadata)
                    {
                        result.Metadata.Add(key, value);
                    }

                    return Complete(result);
                });
            Client.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<DeleteObjectRequest>();
                    if (DeleteFailure?.Invoke(request) is { } failure)
                    {
                        return Fail<DeleteObjectResponse>(failure);
                    }

                    Assert.True(objects.Remove(request.Key));
                    return Complete(new DeleteObjectResponse());
                });
        }

        public void Dispose()
        {
            LegacyOperations.Dispose();
            Retries.Dispose();
            CatalogEntries.Dispose();
            ApiItems.Dispose();
            ApiDuration.Dispose();
            ApiCalls.Dispose();
            OperationBytes.Dispose();
            OperationDuration.Dispose();
            Operations.Dispose();
            _services.Dispose();
            Client.Dispose();
        }
    }

    private sealed record StoredObject(byte[] Data, Dictionary<string, string> Metadata, string ETag);

    private sealed class CapturingConsumer : IJournalStorageConsumer
    {
        public List<byte> Bytes { get; } = [];

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            var bytes = new byte[buffer.Length];
            buffer.Read(bytes);
            Bytes.AddRange(bytes);
        }
    }
}
