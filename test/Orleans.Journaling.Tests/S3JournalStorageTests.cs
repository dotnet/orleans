using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class S3JournalStorageTests : IAsyncLifetime
{
    private const int MinioPort = 9000;
    private const string BucketName = "journaling-tests";
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private static readonly Lazy<string?> DockerSkipReason = new(GetDockerSkipReason);
    private readonly IContainer? _container;
    private AmazonS3Client? _client;
    private string? _bucketName;

    public S3JournalStorageTests()
    {
        if (DockerSkipReason.Value is null)
        {
            _container = new ContainerBuilder("minio/minio:RELEASE.2025-09-07T16-13-09Z")
                .WithEnvironment("MINIO_ROOT_USER", AccessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
                .WithCommand("server", "/data")
                .WithPortBinding(MinioPort, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                    .ForPort(MinioPort)
                    .ForPath("/minio/health/ready")))
                .Build();
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestCategory("BVT")]
    public sealed class S3JournalStorageProviderTests
    {
        [Fact]
        public async Task CloseAsync_DisposesProviderOwnedClient()
        {
            var client = CreateTrackingClient();
            var options = CreateOptions();
            options.ConfigureS3Client(_ => Task.FromResult(client));
            var provider = CreateProvider(options);

            await provider.InitializeAsync(CancellationToken.None);
            await provider.CloseAsync(CancellationToken.None);

            client.Received(1).Dispose();
        }

        [Fact]
        public async Task CloseAsync_PreservesCallerOwnedClient()
        {
            var client = CreateTrackingClient();
            var options = CreateOptions();
            options.S3Client = client;
            var provider = CreateProvider(options);

            await provider.InitializeAsync(CancellationToken.None);
            await provider.CloseAsync(CancellationToken.None);

            client.DidNotReceive().Dispose();
            client.Dispose();
        }

        [Fact]
        public async Task CloseAsync_WhenCancellationRequested_DisposesProviderOwnedClient()
        {
            var client = CreateTrackingClient();
            var options = CreateOptions();
            options.ConfigureS3Client(_ => Task.FromResult(client));
            var provider = CreateProvider(options);
            await provider.InitializeAsync(CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provider.CloseAsync(cancellation.Token));

            client.Received(1).Dispose();
        }

        [Fact]
        public async Task InitializeAsync_WhenBucketCreationRaces_RechecksBucket()
        {
            var client = CreateTrackingClient();
            var headCalls = 0;
            client.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    headCalls++;
                    return headCalls == 1
                        ? Task.FromException<HeadBucketResponse>(CreateS3Exception(HttpStatusCode.NotFound))
                        : Task.FromResult(new HeadBucketResponse());
                });
            client.PutBucketAsync(Arg.Any<PutBucketRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<PutBucketResponse>(CreateS3Exception(HttpStatusCode.Conflict)));
            var options = CreateOptions();
            options.S3Client = client;
            options.CreateBucketIfNotExists = true;
            var provider = CreateProvider(options);

            await provider.InitializeAsync(CancellationToken.None);

            await client.Received(2).HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>());
            await client.Received(1).PutBucketAsync(Arg.Any<PutBucketRequest>(), Arg.Any<CancellationToken>());
            await provider.CloseAsync(CancellationToken.None);
        }

        [Fact]
        public void Constructor_WhenRetryBackoffExceedsTaskDelayLimit_Throws()
        {
            var options = CreateOptions();
            options.MetadataOnlyConflictMaxBackoff = TimeSpan.FromDays(50);

            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => CreateProvider(options));

            Assert.Contains(nameof(S3JournalStorageOptions.MetadataOnlyConflictMaxBackoff), exception.Message);
        }

        [Fact]
        public void AddS3JournalStorage_RegistersDiscoverableProviderAliasesOnce()
        {
            var builder = new TestSiloBuilder();
            builder.Services.AddLogging();
            builder.Services.AddMetrics();
            builder.Services.AddSingleton<OrleansInstruments>();

            builder.AddS3JournalStorage(options => options.BucketName = "first");
            builder.AddS3JournalStorage(options => options.BucketName = "second");

            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(S3JournalStorageProvider));
            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(S3JournalStorageInstruments));
            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IJournalStorageProvider));
            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IJournalStorageCatalog));
            Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(ILifecycleParticipant<ISiloLifecycle>));
            using var services = builder.Services.BuildServiceProvider();
            var provider = services.GetRequiredService<S3JournalStorageProvider>();
            Assert.Same(provider, services.GetRequiredService<IJournalStorageProvider>());
            Assert.Same(provider, services.GetRequiredService<IJournalStorageCatalog>());
            Assert.Same(provider, services.GetRequiredService<ILifecycleParticipant<ISiloLifecycle>>());
            Assert.Equal("second", services.GetRequiredService<IOptions<S3JournalStorageOptions>>().Value.BucketName);
        }

        [Fact]
        public async Task ListAsync_CustomObjectKeyMapping_FiltersParsedJournalIds()
        {
            var client = CreateTrackingClient();
            var response = new ListObjectsV2Response
            {
                IsTruncated = false,
                S3Objects =
                [
                    new S3Object { Key = "tenant/journals/alpha/wal" },
                    new S3Object { Key = "tenant/other/beta/wal" },
                ],
            };
            client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(response));
            var options = CreateOptions();
            options.S3Client = client;
            options.GetObjectKey = static id => $"tenant/{id.Value}";
            options.TryParseJournalId = static key => key.StartsWith("tenant/", StringComparison.Ordinal)
                ? new JournalId(key["tenant/".Length..])
                : null;
            var provider = CreateProvider(options);
            await provider.InitializeAsync(CancellationToken.None);

            var listed = new List<JournalId>();
            await foreach (var journalId in provider.ListAsync(new JournalId("journals"), CancellationToken.None))
            {
                listed.Add(journalId);
            }

            Assert.Equal(["journals/alpha"], listed.Select(static id => id.Value));
            await client.Received(1).ListObjectsV2Async(
                Arg.Is<ListObjectsV2Request>(request => request.Prefix == null),
                Arg.Any<CancellationToken>());
            await provider.CloseAsync(CancellationToken.None);
        }

        [Fact]
        public async Task ListAsync_WhenPhysicalKeysMapToSameJournalId_ReturnsIdentityOnce()
        {
            var client = CreateTrackingClient();
            client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ListObjectsV2Response
                {
                    IsTruncated = false,
                    S3Objects =
                    [
                        new S3Object { Key = "current/journals/alpha/wal" },
                        new S3Object { Key = "legacy/journals/alpha/wal" },
                    ],
                }));
            var options = CreateOptions();
            options.S3Client = client;
            options.TryParseJournalId = static key =>
                key.EndsWith("journals/alpha", StringComparison.Ordinal)
                    ? new JournalId("journals/alpha")
                    : null;
            var provider = CreateProvider(options);
            await provider.InitializeAsync(CancellationToken.None);

            var listed = new List<JournalId>();
            await foreach (var journalId in provider.ListAsync(new JournalId("journals"), CancellationToken.None))
            {
                listed.Add(journalId);
            }

            Assert.Equal(["journals/alpha"], listed.Select(static id => id.Value));
            await provider.CloseAsync(CancellationToken.None);
        }

        private static S3JournalStorageOptions CreateOptions() => new()
        {
            BucketName = "journaling-tests",
            CreateBucketIfNotExists = false,
        };

        private static S3JournalStorageProvider CreateProvider(S3JournalStorageOptions options)
        {
            var services = new ServiceCollection();
            services.AddSerializer();
            services.AddLogging();
            services.AddSingleton<OrleansBinaryJournalFormat>();
            services.AddKeyedSingleton<IJournalFormat>(
                OrleansBinaryJournalFormat.JournalFormatKey,
                static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
            using var serviceProvider = services.BuildServiceProvider();
            return new S3JournalStorageProvider(
                Options.Create(options),
                Options.Create(new JournaledStateManagerOptions { JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey }),
                serviceProvider,
                NullLogger<S3JournalStorage>.Instance);
        }

        private static IAmazonS3 CreateTrackingClient()
        {
            var client = Substitute.For<IAmazonS3>();
            client.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new HeadBucketResponse()));
            return client;
        }

        private sealed class TestSiloBuilder : ISiloBuilder
        {
            public IServiceCollection Services { get; } = new ServiceCollection();

            public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestCategory("BVT")]
    public sealed class S3JournalStorageRequestTests
    {
        [Fact]
        public async Task AppendAsync_DefaultS3ExpressPath_UsesWriteOffsetAndAdvancesState()
        {
            var client = Substitute.For<IAmazonS3>();
            var requests = new List<PutObjectRequest>();
            client.PutObjectAsync(
                    Arg.Do<PutObjectRequest>(requests.Add),
                    Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new PutObjectResponse { ETag = $"etag-{requests.Count}" }));
            var storage = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });

            await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2, 3]), CancellationToken.None);
            await storage.AppendAsync(new ReadOnlySequence<byte>([4, 5]), CancellationToken.None);

            Assert.Equal(3, requests.Count);
            Assert.Equal("etag-1", requests[1].IfMatch);
            Assert.Equal(16, requests[1].WriteOffsetBytes);
            Assert.False(requests[1].UseChunkEncoding);
            Assert.Equal("etag-2", requests[2].IfMatch);
            Assert.Equal(19, requests[2].WriteOffsetBytes);
            Assert.False(requests[2].UseChunkEncoding);
        }

        [Fact]
        public async Task AppendAsync_TwoStorageInstancesWithSameETag_OneConditionalWriteWins()
        {
            var client = Substitute.For<IAmazonS3>();
            var winnerCommitted = false;
            var appendRequests = new ConcurrentBag<PutObjectRequest>();
            var bothAppendsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var winnerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var appendCalls = 0;
            client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(CreateWalResponse()));
            client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(CreateWalProperties(
                    winnerCommitted ? "etag-2" : "etag-1",
                    winnerCommitted ? 17 : 16,
                    winnerCommitted ? 2 : 1)));
            client.PutObjectAsync(
                    Arg.Do<PutObjectRequest>(appendRequests.Add),
                    Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    var call = Interlocked.Increment(ref appendCalls);
                    if (call == 2)
                    {
                        bothAppendsEntered.SetResult();
                    }

                    await bothAppendsEntered.Task;
                    if (call == 1)
                    {
                        winnerCommitted = true;
                        winnerCompleted.SetResult();
                        return new PutObjectResponse { ETag = "etag-2" };
                    }

                    await winnerCompleted.Task;
                    throw CreateS3Exception(HttpStatusCode.PreconditionFailed);
                });
            var first = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });
            var second = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });
            await first.ReadAsync(new CapturingJournalStorageConsumer(), CancellationToken.None);
            await second.ReadAsync(new CapturingJournalStorageConsumer(), CancellationToken.None);

            var errors = await Task.WhenAll(
                CaptureExceptionAsync(() => first.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None)),
                CaptureExceptionAsync(() => second.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None)));

            Assert.Single(errors, static error => error is null);
            Assert.Single(errors, static error => error is InconsistentStateException);
            Assert.Equal(2, appendRequests.Count);
            Assert.All(appendRequests, request =>
            {
                Assert.Equal("etag-1", request.IfMatch);
                Assert.Equal(16, request.WriteOffsetBytes);
            });

            static GetObjectResponse CreateWalResponse()
            {
                var response = new GetObjectResponse
                {
                    ETag = "etag-1",
                    ContentLength = 16,
                    PartsCount = 1,
                    ResponseStream = new MemoryStream(new byte[16], writable: false),
                };
                AddWalMetadata(response.Metadata);
                return response;
            }

            static GetObjectMetadataResponse CreateWalProperties(string eTag, long contentLength, int partsCount)
            {
                var response = new GetObjectMetadataResponse
                {
                    ETag = eTag,
                    ContentLength = contentLength,
                    PartsCount = partsCount,
                    LastModified = DateTime.UtcNow,
                };
                AddWalMetadata(response.Metadata);
                return response;
            }

            static void AddWalMetadata(MetadataCollection metadata)
            {
                metadata.Add(S3JournalStorage.WalGenerationMetadataKey, "generation");
                metadata.Add(S3JournalStorage.MetadataVersionMetadataKey, "version");
                metadata.Add(S3JournalStorage.CheckpointOffsetMetadataKey, "16");
            }

            static async Task<Exception?> CaptureExceptionAsync(Func<ValueTask> operation)
            {
                try
                {
                    await operation();
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }
        }

        [Fact]
        public async Task ReadAsync_RecoversS3ExpressPartCount()
        {
            var client = Substitute.For<IAmazonS3>();
            client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new GetObjectResponse
                {
                    ETag = "etag-1",
                    ContentLength = 1,
                    ResponseStream = new MemoryStream([1], writable: false),
                }));
            GetObjectMetadataRequest? partRequest = null;
            client.GetObjectMetadataAsync(
                    Arg.Do<GetObjectMetadataRequest>(request => partRequest = request),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new GetObjectMetadataResponse
                {
                    ETag = "etag-1",
                    PartsCount = 9_801,
                }));
            var storage = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });

            await storage.ReadAsync(new CapturingJournalStorageConsumer(), CancellationToken.None);

            Assert.True(storage.IsCompactionRequested);
            Assert.NotNull(partRequest);
            Assert.Equal(1, partRequest.PartNumber);
            Assert.Equal("etag-1", partRequest.EtagToMatch);
        }

        [Fact]
        public async Task ReadAsync_WhenPartCountSnapshotConflicts_RequestsCompaction()
        {
            var client = Substitute.For<IAmazonS3>();
            client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new GetObjectResponse
                {
                    ETag = "etag-1",
                    ContentLength = 1,
                    ResponseStream = new MemoryStream([1], writable: false),
                }));
            client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<GetObjectMetadataResponse>(CreateS3Exception(HttpStatusCode.PreconditionFailed)));
            var storage = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });

            await storage.ReadAsync(new CapturingJournalStorageConsumer(), CancellationToken.None);

            Assert.True(storage.IsCompactionRequested);
        }

        [Fact]
        public async Task AppendAsync_WhenAppendExceedsSinglePutLimit_Throws()
        {
            var client = Substitute.For<IAmazonS3>();
            var storage = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });
            var first = new SparseSequenceSegment(new byte[] { 0 }, runningIndex: 0);
            var last = first.Append(new byte[] { 0 }, nextRunningIndex: 5_000_000_000);
            var oversized = new ReadOnlySequence<byte>(first, 0, last, 1);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => storage.AppendAsync(oversized, CancellationToken.None).AsTask());

            Assert.Contains("appends larger than 5 GB", exception.Message);
            await client.DidNotReceive().PutObjectAsync(
                Arg.Any<PutObjectRequest>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task AppendAsync_WhenConditionalRewriteExceedsSinglePutLimit_ThrowsBeforeDownload()
        {
            var client = Substitute.For<IAmazonS3>();
            client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PutObjectResponse { ETag = "etag-1" }));
            var storage = CreateStorage(
                client,
                new S3JournalStorageOptions
                {
                    BucketName = BucketName,
                    UseS3ExpressAppend = false,
                });
            var first = new SparseSequenceSegment(new byte[] { 0 }, runningIndex: 0);
            var last = first.Append(new byte[] { 0 }, nextRunningIndex: 4_999_999_984);
            var oversizedCombinedValue = new ReadOnlySequence<byte>(first, 0, last, 1);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => storage.AppendAsync(oversizedCombinedValue, CancellationToken.None).AsTask());

            Assert.Contains("cannot produce an object larger than 5 GB", exception.Message);
            await client.DidNotReceive().GetObjectAsync(
                Arg.Any<GetObjectRequest>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task AppendAsync_ConditionalRewrite_StreamsViaTemporaryFile()
        {
            var client = Substitute.For<IAmazonS3>();
            PutObjectRequest? createRequest = null;
            Type? rewriteStreamType = null;
            var putCount = 0;
            client.PutObjectAsync(
                    Arg.Do<PutObjectRequest>(request =>
                    {
                        putCount++;
                        if (putCount == 1)
                        {
                            createRequest = request;
                        }
                        else
                        {
                            rewriteStreamType = request.InputStream.GetType();
                        }
                    }),
                    Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new PutObjectResponse { ETag = $"etag-{putCount}" }));
            client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var response = new GetObjectResponse
                    {
                        ETag = "etag-1",
                        ContentLength = 16,
                        PartsCount = 1,
                        ResponseStream = new MemoryStream(new byte[16], writable: false),
                    };
                    foreach (var key in createRequest!.Metadata.Keys)
                    {
                        response.Metadata.Add(key, createRequest.Metadata[key]);
                    }

                    return Task.FromResult(response);
                });
            var storage = CreateStorage(
                client,
                new S3JournalStorageOptions
                {
                    BucketName = BucketName,
                    UseS3ExpressAppend = false,
                });

            await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2, 3]), CancellationToken.None);

            Assert.Equal(typeof(FileStream), rewriteStreamType);
        }

        [Fact]
        public async Task CreateIfNotExistsAsync_WhenConditionalRequestConflicts_Retries()
        {
            var client = Substitute.For<IAmazonS3>();
            var putCount = 0;
            client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    putCount++;
                    return putCount == 1
                        ? Task.FromException<PutObjectResponse>(CreateS3Exception(HttpStatusCode.Conflict))
                        : Task.FromResult(new PutObjectResponse { ETag = "etag-1" });
                });
            client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<GetObjectMetadataResponse>(CreateS3Exception(HttpStatusCode.NotFound)));
            var storage = CreateStorage(
                client,
                new S3JournalStorageOptions
                {
                    BucketName = BucketName,
                    MetadataOnlyConflictInitialBackoff = TimeSpan.Zero,
                });

            var created = await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);

            Assert.True(created);
            await client.Received(2).PutObjectAsync(
                Arg.Any<PutObjectRequest>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task DeleteAsync_WhenMetadataChanged_RetriesWithRefreshedETag()
        {
            var client = Substitute.For<IAmazonS3>();
            PutObjectRequest? createRequest = null;
            client.PutObjectAsync(
                    Arg.Do<PutObjectRequest>(request => createRequest = request),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PutObjectResponse { ETag = "etag-1" }));
            var options = new S3JournalStorageOptions
            {
                BucketName = BucketName,
                UseS3ExpressAppend = false,
                MetadataOnlyConflictInitialBackoff = TimeSpan.Zero,
            };
            var storage = CreateStorage(client, options);
            await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);
            var refreshedProperties = new GetObjectMetadataResponse
            {
                ETag = "etag-2",
                ContentLength = 16,
                LastModified = DateTime.UtcNow,
                PartsCount = 1,
            };
            foreach (var key in createRequest!.Metadata.Keys)
            {
                refreshedProperties.Metadata.Add(key, createRequest.Metadata[key]);
            }

            client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(
                    call => call.Arg<GetObjectMetadataRequest>().EtagToMatch == "etag-1"
                        ? Task.FromException<GetObjectMetadataResponse>(CreateS3Exception(HttpStatusCode.PreconditionFailed))
                        : Task.FromResult(refreshedProperties));
            DeleteObjectRequest? deleteRequest = null;
            client.DeleteObjectAsync(
                    Arg.Do<DeleteObjectRequest>(request => deleteRequest = request),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new DeleteObjectResponse()));

            await storage.DeleteAsync(CancellationToken.None);

            Assert.NotNull(deleteRequest);
            Assert.Equal("etag-2", deleteRequest.IfMatch);
        }

        [Fact]
        public async Task DeleteAsync_WhenUncachedMetadataChangesDuringDelete_RetriesWithRefreshedState()
        {
            var client = Substitute.For<IAmazonS3>();
            var metadataCalls = 0;
            client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    metadataCalls++;
                    var properties = new GetObjectMetadataResponse
                    {
                        ETag = metadataCalls == 1 ? "etag-1" : "etag-2",
                        ContentLength = 16,
                        LastModified = DateTime.UtcNow,
                        PartsCount = 1,
                    };
                    properties.Metadata.Add(S3JournalStorage.WalGenerationMetadataKey, "generation");
                    properties.Metadata.Add(
                        S3JournalStorage.MetadataVersionMetadataKey,
                        metadataCalls == 1 ? "version-1" : "version-2");
                    return Task.FromResult(properties);
                });
            var deleteCalls = 0;
            DeleteObjectRequest? successfulDelete = null;
            client.DeleteObjectAsync(
                    Arg.Do<DeleteObjectRequest>(request =>
                    {
                        deleteCalls++;
                        if (deleteCalls == 2)
                        {
                            successfulDelete = request;
                        }
                    }),
                    Arg.Any<CancellationToken>())
                .Returns(_ => deleteCalls == 1
                    ? Task.FromException<DeleteObjectResponse>(CreateS3Exception(HttpStatusCode.PreconditionFailed))
                    : Task.FromResult(new DeleteObjectResponse()));
            var storage = CreateStorage(
                client,
                new S3JournalStorageOptions
                {
                    BucketName = BucketName,
                    UseS3ExpressAppend = false,
                    MetadataOnlyConflictInitialBackoff = TimeSpan.Zero,
                });

            await storage.DeleteAsync(CancellationToken.None);

            Assert.NotNull(successfulDelete);
            Assert.Equal("etag-2", successfulDelete.IfMatch);
        }

        [Fact]
        public void GetRetryDelay_WhenMultiplicationWouldOverflow_ReturnsMaximum()
        {
            var initial = TimeSpan.FromDays(30);
            var maximum = TimeSpan.FromDays(40);

            var delay = S3JournalStorage.GetRetryDelay(initial, maximum, attempt: 16);

            Assert.Equal(maximum, delay);
        }

        [Fact]
        public async Task UpdateMetadataAsync_DefaultPath_RewritesWalWithConditionalPut()
        {
            var client = Substitute.For<IAmazonS3>();
            var requests = new List<PutObjectRequest>();
            var payloads = new List<byte[]>();
            Type? rewriteStreamType = null;
            client.PutObjectAsync(
                    Arg.Do<PutObjectRequest>(request =>
                    {
                        requests.Add(request);
                        if (requests.Count == 3)
                        {
                            rewriteStreamType = request.InputStream.GetType();
                        }

                        using var payload = new MemoryStream();
                        request.InputStream.CopyTo(payload);
                        payloads.Add(payload.ToArray());
                    }),
                    Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new PutObjectResponse { ETag = $"etag-{requests.Count}" }));
            var storage = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });
            await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2, 3]), CancellationToken.None);

            var properties = new GetObjectMetadataResponse
            {
                ETag = "etag-2",
                ContentLength = 19,
                LastModified = DateTime.UtcNow,
                PartsCount = 2,
            };
            foreach (var key in requests[0].Metadata.Keys)
            {
                properties.Metadata.Add(key, requests[0].Metadata[key]);
            }

            client.GetObjectMetadataAsync(
                    Arg.Any<GetObjectMetadataRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(properties));
            var walResponse = new GetObjectResponse
            {
                ETag = "etag-2",
                ContentLength = 19,
                LastModified = properties.LastModified,
                PartsCount = 2,
                ResponseStream = new MemoryStream([.. payloads[0], 1, 2, 3], writable: false),
            };
            foreach (var key in properties.Metadata.Keys)
            {
                walResponse.Metadata.Add(key, properties.Metadata[key]);
            }

            client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(walResponse));

            var updated = await storage.UpdateMetadataAsync(
                set: new Dictionary<string, string> { ["catalog"] = "closed" },
                cancellationToken: CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal(3, requests.Count);
            Assert.Equal("etag-2", requests[2].IfMatch);
            Assert.Equal("closed", requests[2].Metadata["catalog"]);
            Assert.Equal(16 + 3, payloads[2].Length);
            Assert.Equal([1, 2, 3], payloads[2][16..]);
            Assert.Equal(typeof(FileStream), rewriteStreamType);
            await client.DidNotReceive().CopyObjectAsync(
                Arg.Any<CopyObjectRequest>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task UpdateMetadataAsync_NoOpRecoversS3ExpressPartCount()
        {
            var client = Substitute.For<IAmazonS3>();
            var properties = new GetObjectMetadataResponse
            {
                ETag = "etag-1",
                ContentLength = 16,
                LastModified = DateTime.UtcNow,
            };
            properties.Metadata.Add(S3JournalStorage.WalGenerationMetadataKey, "generation");
            properties.Metadata.Add(S3JournalStorage.MetadataVersionMetadataKey, "version");
            client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<GetObjectMetadataRequest>();
                    return Task.FromResult(request.PartNumber == 1
                        ? new GetObjectMetadataResponse { ETag = "etag-1", PartsCount = 9_801 }
                        : properties);
                });
            var storage = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });

            var metadata = await storage.UpdateMetadataAsync(cancellationToken: CancellationToken.None);

            Assert.NotNull(metadata);
            Assert.True(storage.IsCompactionRequested);
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.PreconditionFailed)]
        [InlineData(HttpStatusCode.Conflict)]
        public async Task UpdateMetadataAsync_WithExpectedETag_WhenWalMutationConflicts_ReturnsNull(
            HttpStatusCode statusCode)
        {
            var client = Substitute.For<IAmazonS3>();
            var properties = new GetObjectMetadataResponse
            {
                ETag = "etag-1",
                ContentLength = 16,
                LastModified = DateTime.UtcNow,
                PartsCount = 1,
            };
            properties.Metadata.Add(S3JournalStorage.WalGenerationMetadataKey, "generation");
            properties.Metadata.Add(S3JournalStorage.MetadataVersionMetadataKey, "version");
            client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(properties));
            client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<GetObjectResponse>(CreateS3Exception(statusCode)));
            var storage = CreateStorage(
                client,
                new S3JournalStorageOptions
                {
                    BucketName = BucketName,
                    UseS3ExpressAppend = false,
                });

            var updated = await storage.UpdateMetadataAsync(
                set: new Dictionary<string, string> { ["catalog"] = "closed" },
                expectedETag: "etag-1:version",
                cancellationToken: CancellationToken.None);

            Assert.Null(updated);
        }

        [Fact]
        public async Task UpdateMetadataAsync_WhenPayloadOffsetExceedsWalLength_Throws()
        {
            var client = Substitute.For<IAmazonS3>();
            var properties = new GetObjectMetadataResponse
            {
                ETag = "etag-1",
                ContentLength = 3,
                LastModified = DateTime.UtcNow,
                PartsCount = 1,
            };
            properties.Metadata.Add(S3JournalStorage.WalGenerationMetadataKey, "generation");
            properties.Metadata.Add(S3JournalStorage.MetadataVersionMetadataKey, "version");
            properties.Metadata.Add(S3JournalStorage.CheckpointOffsetMetadataKey, "10");
            client.GetObjectMetadataAsync(
                    Arg.Any<GetObjectMetadataRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(properties));
            var storage = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => storage.UpdateMetadataAsync(
                    set: new Dictionary<string, string> { ["catalog"] = "closed" },
                    cancellationToken: CancellationToken.None).AsTask());

            Assert.Contains("payload offset 10", exception.Message);
            await client.DidNotReceive().GetObjectAsync(
                Arg.Any<GetObjectRequest>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task UpdateMetadataAsync_WhenReplacementExceedsSinglePutLimit_Throws()
        {
            var client = Substitute.For<IAmazonS3>();
            var properties = new GetObjectMetadataResponse
            {
                ETag = "etag-1",
                ContentLength = 5_000_000_017,
                LastModified = DateTime.UtcNow,
                PartsCount = 2,
            };
            properties.Metadata.Add(S3JournalStorage.WalGenerationMetadataKey, "generation");
            properties.Metadata.Add(S3JournalStorage.MetadataVersionMetadataKey, "version");
            properties.Metadata.Add(S3JournalStorage.CheckpointOffsetMetadataKey, "16");
            client.GetObjectMetadataAsync(
                    Arg.Any<GetObjectMetadataRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(properties));
            var storage = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => storage.UpdateMetadataAsync(
                    set: new Dictionary<string, string> { ["catalog"] = "closed" },
                    cancellationToken: CancellationToken.None).AsTask());

            Assert.Contains("larger than 5 GB", exception.Message);
            Assert.Contains("checkpoint", exception.Message, StringComparison.OrdinalIgnoreCase);
            await client.DidNotReceive().GetObjectAsync(
                Arg.Any<GetObjectRequest>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ReplaceAsync_WhenCheckpointExceedsSinglePutLimit_Throws()
        {
            var client = Substitute.For<IAmazonS3>();
            var storage = CreateStorage(client, new S3JournalStorageOptions { BucketName = BucketName });
            var first = new SparseSequenceSegment(new byte[] { 0 }, runningIndex: 0);
            var last = first.Append(new byte[] { 0 }, nextRunningIndex: 5_000_000_000);
            var oversized = new ReadOnlySequence<byte>(first, 0, last, 1);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => storage.ReplaceAsync(oversized, CancellationToken.None).AsTask());

            Assert.Contains("checkpoints larger than 5 GB", exception.Message);
            await client.DidNotReceive().PutObjectAsync(
                Arg.Any<PutObjectRequest>(),
                Arg.Any<CancellationToken>());
        }

        [Theory]
        [InlineData(HttpStatusCode.PreconditionFailed)]
        [InlineData(HttpStatusCode.Conflict)]
        public async Task ReplaceAsync_WhenWalPublicationIsDefinitivelyRejected_DeletesUnreferencedCheckpoint(
            HttpStatusCode statusCode)
        {
            var client = Substitute.For<IAmazonS3>();
            PutObjectRequest? createRequest = null;
            string? checkpointName = null;
            client.PutObjectAsync(
                    Arg.Do<PutObjectRequest>(request =>
                    {
                        if (request.IfNoneMatch == "*" && request.Key.EndsWith("/wal", StringComparison.Ordinal))
                        {
                            createRequest = request;
                        }
                        else if (request.Key.Contains("/chk.", StringComparison.Ordinal))
                        {
                            checkpointName = request.Key;
                        }
                    }),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<PutObjectRequest>();
                    if (request.IfNoneMatch == "*" && request.Key.EndsWith("/wal", StringComparison.Ordinal))
                    {
                        return Task.FromResult(new PutObjectResponse { ETag = "etag-1" });
                    }

                    if (request.Key.Contains("/chk.", StringComparison.Ordinal))
                    {
                        return Task.FromResult(new PutObjectResponse { ETag = "checkpoint-etag" });
                    }

                    return Task.FromException<PutObjectResponse>(CreateS3Exception(statusCode));
                });
            client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(CreateWalProperties(createRequest!, "etag-1")));
            DeleteObjectRequest? deleteRequest = null;
            client.DeleteObjectAsync(
                    Arg.Do<DeleteObjectRequest>(request => deleteRequest = request),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new DeleteObjectResponse()));
            var storage = CreateStorage(
                client,
                new S3JournalStorageOptions
                {
                    BucketName = BucketName,
                    UseS3ExpressAppend = false,
                    MaxMetadataOnlyConflictRetries = 0,
                });
            await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);

            await Assert.ThrowsAsync<InconsistentStateException>(
                () => storage.ReplaceAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None).AsTask());

            Assert.NotNull(checkpointName);
            Assert.NotNull(deleteRequest);
            Assert.Equal(checkpointName, deleteRequest.Key);
        }

        [Fact]
        public async Task ReplaceAsync_WhenRejectedPublicationIsAlreadyVisible_PreservesReferencedCheckpoint()
        {
            var client = Substitute.For<IAmazonS3>();
            PutObjectRequest? createRequest = null;
            string? checkpointName = null;
            client.PutObjectAsync(
                    Arg.Do<PutObjectRequest>(request =>
                    {
                        if (request.IfNoneMatch == "*" && request.Key.EndsWith("/wal", StringComparison.Ordinal))
                        {
                            createRequest = request;
                        }
                        else if (request.Key.Contains("/chk.", StringComparison.Ordinal))
                        {
                            checkpointName = request.Key;
                        }
                    }),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<PutObjectRequest>();
                    if (request.IfNoneMatch == "*" && request.Key.EndsWith("/wal", StringComparison.Ordinal))
                    {
                        return Task.FromResult(new PutObjectResponse { ETag = "etag-1" });
                    }

                    if (request.Key.Contains("/chk.", StringComparison.Ordinal))
                    {
                        return Task.FromResult(new PutObjectResponse { ETag = "checkpoint-etag" });
                    }

                    return Task.FromException<PutObjectResponse>(CreateS3Exception(HttpStatusCode.PreconditionFailed));
                });
            var metadataCalls = 0;
            client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    metadataCalls++;
                    var properties = CreateWalProperties(createRequest!, metadataCalls == 1 ? "etag-1" : "etag-2");
                    if (metadataCalls > 1)
                    {
                        properties.Metadata[S3JournalStorage.CheckpointMetadataKey] = checkpointName;
                        properties.Metadata[S3JournalStorage.CheckpointOffsetMetadataKey] = "16";
                    }

                    return Task.FromResult(properties);
                });
            var storage = CreateStorage(
                client,
                new S3JournalStorageOptions
                {
                    BucketName = BucketName,
                    UseS3ExpressAppend = false,
                    MaxMetadataOnlyConflictRetries = 0,
                });
            await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);

            await Assert.ThrowsAsync<InconsistentStateException>(
                () => storage.ReplaceAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None).AsTask());

            Assert.NotNull(checkpointName);
            await client.DidNotReceive().DeleteObjectAsync(
                Arg.Is<DeleteObjectRequest>(request => request.Key == checkpointName),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ReplaceAsync_WhenWalPublicationIsAmbiguous_PreservesCheckpoint()
        {
            var client = Substitute.For<IAmazonS3>();
            PutObjectRequest? createRequest = null;
            string? checkpointName = null;
            client.PutObjectAsync(
                    Arg.Do<PutObjectRequest>(request =>
                    {
                        if (request.IfNoneMatch == "*" && request.Key.EndsWith("/wal", StringComparison.Ordinal))
                        {
                            createRequest = request;
                        }
                        else if (request.Key.Contains("/chk.", StringComparison.Ordinal))
                        {
                            checkpointName = request.Key;
                        }
                    }),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<PutObjectRequest>();
                    if (request.IfNoneMatch == "*" && request.Key.EndsWith("/wal", StringComparison.Ordinal))
                    {
                        return Task.FromResult(new PutObjectResponse { ETag = "etag-1" });
                    }

                    if (request.Key.Contains("/chk.", StringComparison.Ordinal))
                    {
                        return Task.FromResult(new PutObjectResponse { ETag = "checkpoint-etag" });
                    }

                    return Task.FromException<PutObjectResponse>(CreateS3Exception(HttpStatusCode.InternalServerError));
                });
            client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(CreateWalProperties(createRequest!, "etag-1")));
            var storage = CreateStorage(
                client,
                new S3JournalStorageOptions
                {
                    BucketName = BucketName,
                    UseS3ExpressAppend = false,
                    MaxMetadataOnlyConflictRetries = 0,
                });
            await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);

            await Assert.ThrowsAsync<AmazonS3Exception>(
                () => storage.ReplaceAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None).AsTask());

            Assert.NotNull(checkpointName);
            await client.DidNotReceive().DeleteObjectAsync(
                Arg.Is<DeleteObjectRequest>(request => request.Key == checkpointName),
                Arg.Any<CancellationToken>());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DeleteAsync_AlwaysUsesETagCondition(bool useConditionalDelete)
        {
            var client = Substitute.For<IAmazonS3>();
            PutObjectRequest? createRequest = null;
            client.PutObjectAsync(
                    Arg.Do<PutObjectRequest>(request => createRequest = request),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PutObjectResponse { ETag = "etag-1" }));
            var options = new S3JournalStorageOptions
            {
                BucketName = BucketName,
                UseConditionalDelete = useConditionalDelete,
            };
            var storage = CreateStorage(client, options);
            await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);

            var lastModified = DateTime.UtcNow;
            var properties = new GetObjectMetadataResponse
            {
                ETag = "etag-1",
                ContentLength = 16,
                LastModified = lastModified,
                PartsCount = 1,
            };
            foreach (var key in createRequest!.Metadata.Keys)
            {
                properties.Metadata.Add(key, createRequest.Metadata[key]);
            }

            client.GetObjectMetadataAsync(
                    Arg.Any<GetObjectMetadataRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(properties));
            DeleteObjectRequest? deleteRequest = null;
            client.DeleteObjectAsync(
                    Arg.Do<DeleteObjectRequest>(request => deleteRequest = request),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new DeleteObjectResponse()));

            await storage.DeleteAsync(CancellationToken.None);

            Assert.NotNull(deleteRequest);
            Assert.Equal("etag-1", deleteRequest.IfMatch);
            if (useConditionalDelete)
            {
                Assert.Equal(16, deleteRequest.IfMatchSize);
                Assert.Equal(lastModified, deleteRequest.IfMatchLastModifiedTime);
            }
            else
            {
                Assert.Null(deleteRequest.IfMatchSize);
                Assert.Null(deleteRequest.IfMatchLastModifiedTime);
            }
        }

        private static S3JournalStorage CreateStorage(IAmazonS3 client, S3JournalStorageOptions options)
            => new(
                new S3JournalStorage.S3JournalStorageShared(
                    NullLogger<S3JournalStorage>.Instance,
                    Options.Create(options),
                    S3JournalStorageInstruments.CreateForDirectConstruction(),
                    mimeType: "application/octet-stream",
                    journalFormatKey: null),
                client,
                new JournalId("journals/test"));

        private static GetObjectMetadataResponse CreateWalProperties(PutObjectRequest createRequest, string eTag)
        {
            var properties = new GetObjectMetadataResponse
            {
                ETag = eTag,
                ContentLength = 16,
                LastModified = DateTime.UtcNow,
                PartsCount = 1,
            };
            foreach (var key in createRequest.Metadata.Keys)
            {
                properties.Metadata.Add(key, createRequest.Metadata[key]);
            }

            return properties;
        }

        private sealed class SparseSequenceSegment : ReadOnlySequenceSegment<byte>
        {
            public SparseSequenceSegment(ReadOnlyMemory<byte> memory, long runningIndex)
            {
                Memory = memory;
                RunningIndex = runningIndex;
            }

            public SparseSequenceSegment Append(ReadOnlyMemory<byte> nextMemory, long nextRunningIndex)
            {
                var segment = new SparseSequenceSegment(nextMemory, nextRunningIndex);
                Next = segment;
                return segment;
            }
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (DockerSkipReason.Value is not null)
        {
            return;
        }

        await _container!.StartAsync();
        _client = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config
            {
                ServiceURL = $"http://127.0.0.1:{_container.GetMappedPublicPort(MinioPort)}",
                ForcePathStyle = true,
                AuthenticationRegion = RegionEndpoint.USEast1.SystemName,
            });
        _bucketName = $"{BucketName}-{Guid.NewGuid():N}";
        await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucketName });
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task AppendReadReplaceAndDelete_RoundTripsThroughMinio()
    {
        EnsureDockerAvailable();
        var storage = CreateStorage("journals/test", journalFormatKey: "json-lines");

        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["catalog"] = "open" },
            CancellationToken.None));
        Assert.False(await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None));

        var metadata = await storage.GetMetadataAsync(CancellationToken.None);
        Assert.NotNull(metadata);
        Assert.Equal("json-lines", metadata.Format);
        Assert.Equal("open", metadata.Properties["catalog"]);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);
        Assert.Equal("json-lines", consumer.JournalFormatKey);
        Assert.Equal([1, 2, 3], consumer.Bytes.ToArray());

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([4, 5]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([6]), CancellationToken.None);

        var reloaded = CreateStorage("journals/test", journalFormatKey: "json-lines");
        var reloadedConsumer = new CapturingJournalStorageConsumer();
        await reloaded.ReadAsync(reloadedConsumer, CancellationToken.None);
        Assert.Equal("json-lines", reloadedConsumer.JournalFormatKey);
        Assert.Equal([4, 5, 6], reloadedConsumer.Bytes.ToArray());

        await reloaded.DeleteAsync(CancellationToken.None);
        var emptyConsumer = new CapturingJournalStorageConsumer();
        await CreateStorage("journals/test", journalFormatKey: "json-lines").ReadAsync(emptyConsumer, CancellationToken.None);
        Assert.Empty(emptyConsumer.Bytes.ToArray());
    }

    [Fact]
    public async Task UpdateMetadataAsync_IsConditionalAndPreservesProviderMetadata()
    {
        EnsureDockerAvailable();
        var storage = CreateStorage("journals/metadata", journalFormatKey: "json-lines");
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var before = await storage.GetMetadataAsync(CancellationToken.None);
        Assert.NotNull(before);

        var updated = await storage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["catalog"] = "closed" },
            expectedETag: before.ETag,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("json-lines", updated.Format);
        Assert.Equal("closed", updated.Properties["catalog"]);
        Assert.NotEqual(before.ETag, updated.ETag);

        var stale = await storage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["catalog"] = "stale" },
            expectedETag: before.ETag,
            cancellationToken: CancellationToken.None);
        Assert.Null(stale);

        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage("journals/metadata", journalFormatKey: "json-lines").ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([1], consumer.Bytes.ToArray());
        Assert.Equal("json-lines", consumer.JournalFormatKey);
    }

    [Fact]
    public async Task ReplaceAsync_MigratesStoredFormatToConfiguredWriteFormat()
    {
        EnsureDockerAvailable();
        var original = CreateStorage("journals/format-migration", journalFormatKey: "old-format");
        await original.AppendAsync(new ReadOnlySequence<byte>([1, 2, 3]), CancellationToken.None);

        var replacement = CreateStorage("journals/format-migration", journalFormatKey: "new-format");
        await replacement.ReplaceAsync(new ReadOnlySequence<byte>([4, 5]), CancellationToken.None);

        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage("journals/format-migration", journalFormatKey: "new-format")
            .ReadAsync(consumer, CancellationToken.None);

        Assert.Equal("new-format", consumer.JournalFormatKey);
        Assert.Equal([4, 5], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ListAsync_ReturnsSortedJournalIdsMatchingPrefix()
    {
        EnsureDockerAvailable();
        var provider = CreateProvider();
        await CreateStorage("journals/zeta").AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await CreateStorage("journals/alpha").AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await CreateStorage("other/beta").AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var listed = new List<JournalId>();
        await foreach (var journalId in provider.ListAsync(new JournalId("journals"), CancellationToken.None))
        {
            listed.Add(journalId);
        }

        Assert.Equal(["journals/alpha", "journals/zeta"], listed.Select(static id => id.Value));
    }

    [Fact]
    public async Task AppendAsync_WhenWalChangedExternally_RequiresRecovery()
    {
        EnsureDockerAvailable();
        var storage = CreateStorage("journals/conflict");
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var other = CreateStorage("journals/conflict");
        await other.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None).AsTask());
        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_WhenMetadataChangedExternally_Retries()
    {
        EnsureDockerAvailable();
        var storage = CreateStorage("journals/delete-conflict");
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var other = CreateStorage("journals/delete-conflict");
        var metadata = await other.GetMetadataAsync(CancellationToken.None);
        Assert.NotNull(metadata);
        var updated = await other.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["catalog"] = "updated" },
            expectedETag: metadata.ETag,
            cancellationToken: CancellationToken.None);
        Assert.NotNull(updated);

        await storage.DeleteAsync(CancellationToken.None);

        Assert.Null(await other.GetMetadataAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_WhenJournalWasRecreatedWithSameLogicalBytes_RequiresRecovery()
    {
        EnsureDockerAvailable();
        var stale = CreateStorage("journals/recreated");
        await stale.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var replacement = CreateStorage("journals/recreated");
        await replacement.DeleteAsync(CancellationToken.None);
        await replacement.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => stale.DeleteAsync(CancellationToken.None).AsTask());
        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private S3JournalStorage CreateStorage(string journalId, string? journalFormatKey = null)
    {
        var options = CreateOptions(journalFormatKey);
        return new S3JournalStorage(
            new S3JournalStorage.S3JournalStorageShared(
                NullLogger<S3JournalStorage>.Instance,
                Options.Create(options),
                S3JournalStorageInstruments.CreateForDirectConstruction(),
                mimeType: "application/octet-stream",
                journalFormatKey),
            _client!,
            new JournalId(journalId));
    }

    private S3JournalStorageProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging();
        services.AddSingleton<OrleansBinaryJournalFormat>();
        services.AddKeyedSingleton<IJournalFormat>(
            OrleansBinaryJournalFormat.JournalFormatKey,
            static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        using var serviceProvider = services.BuildServiceProvider();
        var provider = new S3JournalStorageProvider(
            Options.Create(CreateOptions(journalFormatKey: null)),
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey }),
            serviceProvider,
            NullLogger<S3JournalStorage>.Instance);
        provider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        return provider;
    }

    private S3JournalStorageOptions CreateOptions(string? journalFormatKey)
    {
        return new S3JournalStorageOptions
        {
            BucketName = _bucketName!,
            S3Client = _client!,
            UseS3ExpressAppend = false,
            UseConditionalDelete = false,
            StorageClass = null,
            MetadataOnlyConflictInitialBackoff = TimeSpan.Zero,
            GetObjectKey = id => id.Value,
        };
    }

    private static void EnsureDockerAvailable()
    {
        if (DockerSkipReason.Value is { } reason)
        {
            throw Xunit.Sdk.SkipException.ForSkip(reason);
        }
    }

    private static string? GetDockerSkipReason()
    {
        try
        {
            var endpointAuthConfig = TestcontainersSettings.OS?.DockerEndpointAuthConfig;
            if (endpointAuthConfig is null)
            {
                return "Docker is unavailable, so MinIO S3 journal storage tests are skipped.";
            }

            using var dockerClient = endpointAuthConfig
                .GetDockerClientConfiguration(Guid.NewGuid())
                .CreateClient();
            var dockerInfo = dockerClient.System.GetSystemInfoAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(dockerInfo.OSType))
            {
                return "Docker is unavailable, so MinIO S3 journal storage tests are skipped.";
            }

            if (string.Equals(dockerInfo.OSType, "windows", StringComparison.OrdinalIgnoreCase))
            {
                return "Docker is running in Windows container mode, so MinIO S3 journal storage tests are skipped.";
            }

            return null;
        }
        catch (HttpRequestException)
        {
            return "Docker is unavailable, so MinIO S3 journal storage tests are skipped.";
        }
        catch (OperationCanceledException)
        {
            return "Docker is unavailable, so MinIO S3 journal storage tests are skipped.";
        }
        catch (DockerApiException)
        {
            return "Docker is unavailable, so MinIO S3 journal storage tests are skipped.";
        }
        catch (InvalidOperationException)
        {
            return "Docker is unavailable, so MinIO S3 journal storage tests are skipped.";
        }
    }

    private static AmazonS3Exception CreateS3Exception(HttpStatusCode statusCode)
        => new()
        {
            StatusCode = statusCode,
        };

    private sealed class CapturingJournalStorageConsumer : IJournalStorageConsumer
    {
        public string? JournalFormatKey { get; private set; }

        public MemoryStream Bytes { get; } = new();

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            JournalFormatKey = metadata?.Format;
            while (buffer.Length > 0)
            {
                var chunk = new byte[buffer.Length];
                buffer.Read(chunk);
                Bytes.Write(chunk);
            }
        }
    }
}
