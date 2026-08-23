using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Persistence.TestKit;
using Orleans.Storage;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
[TestCategory("Persistence")]
[TestArea("Journaling")]
public sealed class S3JournalStoragePersistenceTestKitTests
    : GrainStorageTestRunner, IClassFixture<S3JournalStoragePersistenceTestKitFixture>
{
    public S3JournalStoragePersistenceTestKitTests(S3JournalStoragePersistenceTestKitFixture fixture)
        : base(fixture.GetStorageOrSkip())
    {
    }

    [Fact]
    public override Task PersistenceStorage_WriteReadIdCyrillic() =>
        base.PersistenceStorage_WriteReadIdCyrillic();

    [Fact]
    public override Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException() =>
        base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();

    [Fact]
    public override Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException() =>
        base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();

    [Fact]
    public override Task PersistenceStorage_WriteReadWriteReadStatesInParallel() =>
        RunPersistenceStorage_WriteReadWriteReadStatesInParallel("S3Journal", 10);

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentState() =>
        base.PersistenceStorage_ReadNonExistentState();

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentStateHasNonNullState() =>
        base.PersistenceStorage_ReadNonExistentStateHasNonNullState();

    [Fact]
    public override Task PersistenceStorage_WriteClearWrite() =>
        base.PersistenceStorage_WriteClearWrite();

    [Fact]
    public override Task PersistenceStorage_WriteClearRead() =>
        base.PersistenceStorage_WriteClearRead();

    [Fact]
    public override Task PersistenceStorage_WriteReadClearReadCycle() =>
        base.PersistenceStorage_WriteReadClearReadCycle();

    [Fact]
    public override Task PersistenceStorage_WriteRead_StringKey() =>
        base.PersistenceStorage_WriteRead_StringKey();

    [Fact]
    public override Task PersistenceStorage_WriteRead_IntegerKey() =>
        base.PersistenceStorage_WriteRead_IntegerKey();

    [Fact]
    public override Task PersistenceStorage_ETagChangesOnWrite() =>
        base.PersistenceStorage_ETagChangesOnWrite();

    [Fact]
    public override Task PersistenceStorage_ClearBeforeWrite() =>
        base.PersistenceStorage_ClearBeforeWrite();

    [Fact]
    public override Task PersistenceStorage_ClearStateDoesNotNullifyState() =>
        base.PersistenceStorage_ClearStateDoesNotNullifyState();

    [Fact]
    public override Task PersistenceStorage_ClearUpdatesETag() =>
        base.PersistenceStorage_ClearUpdatesETag();

    [Fact]
    public override Task PersistenceStorage_ReadAfterClear() =>
        base.PersistenceStorage_ReadAfterClear();

    [Fact]
    public override Task PersistenceStorage_MultipleClearOperations() =>
        base.PersistenceStorage_MultipleClearOperations();

    [Fact]
    public override Task PersistenceStorage_WriteWithSameValuesUpdatesETag() =>
        base.PersistenceStorage_WriteWithSameValuesUpdatesETag();

    [Fact]
    public override Task PersistenceStorage_StateNamesUseIndependentRecords() =>
        base.PersistenceStorage_StateNamesUseIndependentRecords();

    [Fact]
    public override Task PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException() =>
        base.PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException();

    [Fact, TestCategory("ModelBased")]
    public Task PersistenceStorage_ModelBasedGeneratedConformance()
    {
        var runner = new GrainStorageModelBasedTestRunner(
            Storage,
            new GrainStorageModelBasedConformanceOptions
            {
                ProviderName = "S3JournalStorage",
                MaxDepth = 3,
                MaxSequenceLength = 3,
            });
        return runner.RunGeneratedConformanceTests();
    }

    [Fact]
    public async Task PersistenceStorage_ConcurrentSameKeyWrites_PreservesWinningState()
    {
        var grainId = GrainId.Create("S3JournalConcurrency", Guid.NewGuid().ToString("N"));
        var initial = new GrainState<TestState1>
        {
            State = new TestState1 { A = "initial", B = 1, C = 2 },
        };
        await Storage.WriteStateAsync("state", grainId, initial);
        var first = new GrainState<TestState1>
        {
            State = new TestState1 { A = "first", B = 3, C = 4 },
            ETag = initial.ETag,
            RecordExists = true,
        };
        var second = new GrainState<TestState1>
        {
            State = new TestState1 { A = "second", B = 5, C = 6 },
            ETag = initial.ETag,
            RecordExists = true,
        };

        var errors = await Task.WhenAll(TryWriteAsync(first), TryWriteAsync(second));

        var successIndex = Array.FindIndex(errors, static error => error is null);
        Assert.InRange(successIndex, 0, 1);
        Assert.Single(errors, static error => error is InconsistentStateException);
        var read = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync("state", grainId, read);
        Assert.Equal(successIndex == 0 ? first.State : second.State, read.State);
        Assert.Equal(successIndex == 0 ? first.ETag : second.ETag, read.ETag);

        async Task<Exception?> TryWriteAsync(GrainState<TestState1> state)
        {
            try
            {
                await Storage.WriteStateAsync("state", grainId, state);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }
}

public sealed class S3JournalStoragePersistenceTestKitFixture : IAsyncLifetime
{
    private const int MinioPort = 9000;
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private static readonly Lazy<string?> DockerSkipReason = new(GetDockerSkipReason);
    private readonly IContainer? _container;
    private AmazonS3Client? _client;
    private IGrainStorage? _storage;

    public S3JournalStoragePersistenceTestKitFixture()
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

    public IGrainStorage GetStorageOrSkip()
    {
        EnsurePreconditionsMet();
        return _storage ?? throw new InvalidOperationException("The S3 journal persistence test-kit fixture has not been initialized.");
    }

    public void EnsurePreconditionsMet()
    {
        if (DockerSkipReason.Value is { } reason)
        {
            throw Xunit.Sdk.SkipException.ForSkip(reason);
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
        var bucketName = $"journaling-test-kit-{Guid.NewGuid():N}";
        await _client.PutBucketAsync(new PutBucketRequest { BucketName = bucketName });
        var options = new S3JournalStorageOptions
        {
            BucketName = bucketName,
            S3Client = _client,
            UseS3ExpressAppend = false,
            UseConditionalDelete = false,
            StorageClass = null,
            MetadataOnlyConflictInitialBackoff = TimeSpan.Zero,
        };
        var shared = new S3JournalStorage.S3JournalStorageShared(
            NullLogger<S3JournalStorage>.Instance,
            Options.Create(options),
            S3JournalStorageInstruments.CreateForDirectConstruction(),
            mimeType: "application/json",
            journalFormatKey: "persistence-test-kit");
        _storage = new JournalGrainStorageAdapter(
            journalId => new S3JournalStorage(shared, _client, journalId));
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static string? GetDockerSkipReason()
    {
        try
        {
            var endpointAuthConfig = TestcontainersSettings.OS?.DockerEndpointAuthConfig;
            if (endpointAuthConfig is null)
            {
                return "Docker is unavailable, so S3 journal persistence test-kit tests are skipped.";
            }

            using var dockerClient = endpointAuthConfig
                .GetDockerClientConfiguration(Guid.NewGuid())
                .CreateClient();
            var dockerInfo = dockerClient.System.GetSystemInfoAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(dockerInfo.OSType))
            {
                return "Docker is unavailable, so S3 journal persistence test-kit tests are skipped.";
            }

            if (string.Equals(dockerInfo.OSType, "windows", StringComparison.OrdinalIgnoreCase))
            {
                return "Docker is running in Windows container mode, so S3 journal persistence test-kit tests are skipped.";
            }

            return null;
        }
        catch (HttpRequestException)
        {
            return "Docker is unavailable, so S3 journal persistence test-kit tests are skipped.";
        }
        catch (OperationCanceledException)
        {
            return "Docker is unavailable, so S3 journal persistence test-kit tests are skipped.";
        }
        catch (DockerApiException)
        {
            return "Docker is unavailable, so S3 journal persistence test-kit tests are skipped.";
        }
        catch (InvalidOperationException)
        {
            return "Docker is unavailable, so S3 journal persistence test-kit tests are skipped.";
        }
    }
}

internal sealed class JournalGrainStorageAdapter(Func<JournalId, IJournalStorage> createStorage) : IGrainStorage
{
    private readonly ConcurrentDictionary<JournalId, SemaphoreSlim> _locks = new();

    public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var journalId = CreateJournalId(grainType, grainId);
        var gate = _locks.GetOrAdd(journalId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var current = await ReadAsync<T>(createStorage(journalId));
            if (current.Metadata is null)
            {
                grainState.State = CreateDefaultState<T>();
                grainState.ETag = null;
                grainState.RecordExists = false;
                return;
            }

            grainState.State = current.Envelope.RecordExists
                ? current.Envelope.State!
                : CreateDefaultState<T>();
            grainState.ETag = current.Metadata.ETag;
            grainState.RecordExists = current.Envelope.RecordExists;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var journalId = CreateJournalId(grainType, grainId);
        var gate = _locks.GetOrAdd(journalId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var storage = createStorage(journalId);
            var current = await ReadAsync<T>(storage);
            if (grainState.ETag is null)
            {
                if (current.Metadata is not null
                    || !await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None))
                {
                    throw new InconsistentStateException("The S3 journal storage record already exists.");
                }
            }
            else if (!string.Equals(grainState.ETag, current.Metadata?.ETag, StringComparison.Ordinal))
            {
                throw new InconsistentStateException("The S3 journal storage ETag does not match the current record.");
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new JournalStorageEnvelope<T> { RecordExists = true, State = grainState.State });
            await storage.ReplaceAsync(new ReadOnlySequence<byte>(payload), CancellationToken.None);
            var metadata = await storage.GetMetadataAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("The S3 journal storage record was not visible after a successful write.");
            grainState.ETag = metadata.ETag;
            grainState.RecordExists = true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var journalId = CreateJournalId(grainType, grainId);
        var gate = _locks.GetOrAdd(journalId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var storage = createStorage(journalId);
            var current = await ReadAsync<T>(storage);
            if (current.Metadata is null)
            {
                if (grainState.ETag is not null)
                {
                    throw new InconsistentStateException("The S3 journal storage record no longer exists.");
                }

                grainState.State = CreateDefaultState<T>();
                grainState.ETag = null;
                grainState.RecordExists = false;
                return;
            }

            if (!string.Equals(grainState.ETag, current.Metadata.ETag, StringComparison.Ordinal))
            {
                throw new InconsistentStateException("The S3 journal storage ETag does not match the current record.");
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new JournalStorageEnvelope<T> { RecordExists = false, State = CreateDefaultState<T>() });
            await storage.ReplaceAsync(new ReadOnlySequence<byte>(payload), CancellationToken.None);
            var metadata = await storage.GetMetadataAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("The S3 journal storage tombstone was not visible after a successful clear.");
            grainState.State = CreateDefaultState<T>();
            grainState.ETag = metadata.ETag;
            grainState.RecordExists = false;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<JournalStorageReadResult<T>> ReadAsync<T>(IJournalStorage storage)
    {
        var consumer = new BufferingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);
        var metadata = await storage.GetMetadataAsync(CancellationToken.None);
        if (metadata is null)
        {
            return new(default!, null);
        }

        var envelope = JsonSerializer.Deserialize<JournalStorageEnvelope<T>>(consumer.Bytes.ToArray())
            ?? throw new InvalidOperationException("The S3 journal storage record payload is invalid.");
        return new(envelope, metadata);
    }

    private static JournalId CreateJournalId(string grainType, GrainId grainId)
    {
        var key = Encoding.UTF8.GetBytes($"{grainType}\0{grainId}");
        return new JournalId($"persistence-test-kit/{Convert.ToHexString(SHA256.HashData(key))}");
    }

    private static T CreateDefaultState<T>() =>
        Activator.CreateInstance<T>()
        ?? throw new InvalidOperationException($"The persistence test state type '{typeof(T)}' cannot be initialized.");

    private sealed class BufferingJournalStorageConsumer : IJournalStorageConsumer
    {
        public MemoryStream Bytes { get; } = new();

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            while (buffer.Length > 0)
            {
                var chunk = new byte[buffer.Length];
                buffer.Read(chunk);
                Bytes.Write(chunk);
            }
        }
    }

    private sealed class JournalStorageEnvelope<T>
    {
        public bool RecordExists { get; set; }

        public T? State { get; set; }
    }

    private readonly record struct JournalStorageReadResult<T>(
        JournalStorageEnvelope<T> Envelope,
        IJournalMetadata? Metadata);
}
