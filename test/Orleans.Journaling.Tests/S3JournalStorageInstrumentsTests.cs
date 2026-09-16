using System.Buffers;
using System.Diagnostics.Metrics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAsync_CountsEmptyAndContinuedPagesAndFiltersCatalogEntries(bool nullFirstPage)
    {
        using var context = new MetricsContext();
        var requests = new List<ListObjectsV2Request>();
        context.Client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ListObjectsV2Request>();
                requests.Add(request);
                context.AssertListing(pages: requests.Count - 1);
                return Task.FromResult(request.ContinuationToken is null
                    ? new ListObjectsV2Response
                    {
                        S3Objects = nullFirstPage ? null : [],
                        IsTruncated = true,
                        NextContinuationToken = "opaque-next",
                    }
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
        context.AssertListing(pages: 2, entries: 1, items: [2]);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.False(await enumerator.MoveNextAsync());

        Assert.Equal(new string?[] { null, "opaque-next" }, requests.Select(request => request.ContinuationToken));
        context.AssertListing(pages: 2, entries: 1, items: [2]);
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
                    context.AssertListing();
                    return Task.FromResult(new ListObjectsV2Response
                    {
                        S3Objects = [],
                        IsTruncated = true,
                        NextContinuationToken = "opaque-failure",
                    });
                }

                context.AssertListing(pages: 1);
                if (canceled)
                {
                    cancellation.Cancel();
                }

                return Task.FromException<ListObjectsV2Response>(failure);
            });
        await context.Provider.InitializeAsync(cancellation.Token);
        await using var enumerator = context.Provider.ListAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var actual = await Record.ExceptionAsync(() => enumerator.MoveNextAsync().AsTask());

        Assert.Same(failure, actual);
        if (canceled)
        {
            Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken);
        }

        context.AssertListing(pages: 1);
        Assert.Empty(context.Retries.GetMeasurementSnapshot());
        await context.Client.Received(2).ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), cancellation.Token);
    }

    [Fact]
    public async Task ListAsync_EarlyDisposalCountsFetchedItemsAndOnlyYieldedEntries()
    {
        using var context = new MetricsContext();
        context.Client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = "wal/a" }, new S3Object { Key = "wal/b" }],
                IsTruncated = true,
                NextContinuationToken = "unused",
            }));
        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        var enumerator = context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("a", enumerator.Current.Id.Value);
        context.AssertListing(pages: 1, entries: 1, items: [2]);
        await enumerator.DisposeAsync();
        await enumerator.DisposeAsync();

        context.AssertListing(pages: 1, entries: 1, items: [2]);
        await context.Client.Received(1).ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_PreCanceledEnumerationPreservesCancellationWithoutFetchingPages()
    {
        using var context = new MetricsContext();
        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await using var enumerator = context.Provider.ListAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        context.AssertListing();
        await context.Client.DidNotReceive().ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceAsync_CountsActualCheckpointCollisionRetryAndPersistsReplacement()
    {
        using var context = new MetricsContext();
        context.ConfigureObjectStorage();
        var checkpointAttempts = 0;
        context.PutFailure = request =>
        {
            if (request.Key.StartsWith("checkpoints/", StringComparison.Ordinal) && ++checkpointAttempts == 1)
            {
                return S3Failure(HttpStatusCode.PreconditionFailed);
            }

            return null;
        };

        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        var storage = context.Provider.CreateStorage(new("journals/a"));
        Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var consumer = new CapturingConsumer();
        await storage.ReadAsync(consumer, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1 }, consumer.Bytes);
        Assert.Equal(2, checkpointAttempts);
        context.AssertListing();
        var retry = Assert.Single(context.Retries.GetMeasurementSnapshot());
        Assert.Equal(1, retry.Value);
        Assert.Equal(2, retry.Tags.Count);
        Assert.True(retry.MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", nameof(S3JournalStorageProvider)), new("reason", "checkpoint_collision") }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task UpdateMetadataAsync_CountsOnlyActualConflictRetries(int failureCount)
    {
        using var context = new MetricsContext();
        context.ConfigureObjectStorage();
        var attempts = 0;
        context.PutFailure = request => request.IfMatch is not null && ++attempts <= failureCount
            ? S3Failure(HttpStatusCode.PreconditionFailed) : null;
        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        var storage = context.Provider.CreateStorage(new("journals/a"));
        Assert.True(await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["catalog"] = "open" },
            TestContext.Current.CancellationToken));
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

        var expectedAttempts = Math.Min(failureCount + 1, 3);
        Assert.Equal(expectedAttempts, attempts);
        var persisted = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.Equal(failureCount == 1 ? "closed" : "open", persisted!.Properties["catalog"]);
        context.AssertListing();
        var retries = context.Retries.GetMeasurementSnapshot();
        Assert.Equal(expectedAttempts - 1, retries.Count);
        Assert.All(retries, retry =>
        {
            Assert.Equal(1, retry.Value);
            Assert.Equal(2, retry.Tags.Count);
            Assert.True(retry.MatchesTags(
                new KeyValuePair<string, object?>[] { new("provider", nameof(S3JournalStorageProvider)), new("reason", "metadata_conflict") }));
        });
    }

    [Fact]
    public async Task AppendAsync_CountsActualMetadataConflictRetryAndPersistsAppend()
    {
        using var context = new MetricsContext();
        context.ConfigureObjectStorage();
        var appendAttempts = 0;
        context.PutFailure = request =>
        {
            if (request.WriteOffsetBytes is not null && ++appendAttempts == 1)
            {
                return S3Failure(HttpStatusCode.PreconditionFailed);
            }

            return null;
        };
        await context.Provider.InitializeAsync(TestContext.Current.CancellationToken);
        var storage = context.Provider.CreateStorage(new("journals/a"));
        Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var consumer = new CapturingConsumer();
        await storage.ReadAsync(consumer, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1 }, consumer.Bytes);
        Assert.Equal(2, appendAttempts);
        context.AssertListing();
        var retry = Assert.Single(context.Retries.GetMeasurementSnapshot());
        Assert.Equal(1, retry.Value);
        Assert.Equal(2, retry.Tags.Count);
        Assert.True(retry.MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", nameof(S3JournalStorageProvider)), new("reason", "metadata_conflict") }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_PreservesConflictOrCancellationWithoutRetry(bool canceled)
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
            Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken);
        }
        else
        {
            Assert.Same(failure, Assert.IsType<InconsistentStateException>(actual).InnerException);
        }

        await context.Client.Received(1).DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), cancellation.Token);
        context.AssertListing();
        Assert.Empty(context.Retries.GetMeasurementSnapshot());
        Assert.True(context.LegacyOperations.GetMeasurementSnapshot()[1].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "delete"), new("status", "error") }));
    }

    private static AmazonS3Exception S3Failure(HttpStatusCode statusCode)
        => new("Private service diagnostic which must not become a metric tag.") { StatusCode = statusCode };

    private static KeyValuePair<string, object?>[] CatalogTags()
        => [new("provider", nameof(S3JournalStorageProvider))];

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
            var instruments = new S3JournalStorageInstruments(meter);
            CatalogPages = new(meter.Meter, "orleans-journaling-provider-catalog-pages");
            CatalogItems = new(meter.Meter, "orleans-journaling-provider-catalog-items");
            CatalogEntries = new(meter.Meter, "orleans-journaling-provider-catalog-entries");
            Retries = new(meter.Meter, "orleans-journaling-provider-retries");
            LegacyOperations = new(meter.Meter, "orleans-journaling-s3-operations");
            Options = new() { BucketName = "private-bucket", S3Client = Client, MetadataOnlyConflictInitialBackoff = TimeSpan.Zero };
            Provider = new(Microsoft.Extensions.Options.Options.Create(Options),
                Microsoft.Extensions.Options.Options.Create(new JournaledStateManagerOptions
                {
                    JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey,
                }),
                _services, NullLogger<S3JournalStorage>.Instance, instruments);
            Client.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new HeadBucketResponse()));
        }

        public IAmazonS3 Client { get; } = Substitute.For<IAmazonS3>();
        public S3JournalStorageOptions Options { get; }
        public S3JournalStorageProvider Provider { get; }
        public MetricCollector<long> CatalogPages { get; }
        public MetricCollector<long> CatalogItems { get; }
        public MetricCollector<long> CatalogEntries { get; }
        public MetricCollector<long> Retries { get; }
        public MetricCollector<long> LegacyOperations { get; }
        public Func<PutObjectRequest, Exception?>? PutFailure { get; set; }
        public Func<DeleteObjectRequest, Exception?>? DeleteFailure { get; set; }

        public void AssertListing(int pages = 0, int entries = 0, params long[] items)
        {
            var pageMeasurements = CatalogPages.GetMeasurementSnapshot();
            Assert.Equal(pages, pageMeasurements.Count);
            Assert.All(pageMeasurements, page =>
            {
                Assert.Equal(1, page.Value);
                Assert.Single(page.Tags);
                Assert.True(page.MatchesTags(CatalogTags()));
            });
            var itemMeasurements = CatalogItems.GetMeasurementSnapshot();
            Assert.Equal(items, itemMeasurements.Select(item => item.Value));
            Assert.All(itemMeasurements, item =>
            {
                Assert.Single(item.Tags);
                Assert.True(item.MatchesTags(CatalogTags()));
            });
            var entryMeasurements = CatalogEntries.GetMeasurementSnapshot();
            Assert.Equal(entries, entryMeasurements.Count);
            Assert.All(entryMeasurements, entry =>
            {
                Assert.Equal(1, entry.Value);
                Assert.Single(entry.Tags);
                Assert.True(entry.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", nameof(S3JournalStorageProvider)) }));
            });
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
                        return Task.FromException<PutObjectResponse>(failure);
                    }

                    objects.TryGetValue(request.Key, out var current);
                    if (request.IfNoneMatch == "*" && current is not null
                        || request.IfMatch is not null && request.IfMatch != current?.ETag)
                    {
                        return Task.FromException<PutObjectResponse>(S3Failure(HttpStatusCode.PreconditionFailed));
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
                    return Task.FromResult(new PutObjectResponse { ETag = stored.ETag });
                });
            Client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<GetObjectMetadataRequest>();
                    if (!objects.TryGetValue(request.Key, out var stored))
                    {
                        return Task.FromException<GetObjectMetadataResponse>(S3Failure(HttpStatusCode.NotFound));
                    }

                    Assert.True(request.EtagToMatch is null || request.EtagToMatch == stored.ETag);
                    var result = new GetObjectMetadataResponse
                    {
                        ETag = stored.ETag,
                        ContentLength = stored.Data.Length,
                        PartsCount = 1,
                        LastModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    };
                    foreach (var (key, value) in stored.Metadata)
                    {
                        result.Metadata.Add(key, value);
                    }

                    return Task.FromResult(result);
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

                    return Task.FromResult(result);
                });
            Client.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<DeleteObjectRequest>();
                    if (DeleteFailure?.Invoke(request) is { } failure)
                    {
                        return Task.FromException<DeleteObjectResponse>(failure);
                    }

                    Assert.True(objects.Remove(request.Key));
                    return Task.FromResult(new DeleteObjectResponse());
                });
        }

        public void Dispose()
        {
            LegacyOperations.Dispose();
            Retries.Dispose();
            CatalogEntries.Dispose();
            CatalogItems.Dispose();
            CatalogPages.Dispose();
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
