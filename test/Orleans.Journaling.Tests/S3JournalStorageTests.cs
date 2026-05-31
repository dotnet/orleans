using System.Buffers;
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Storage;
using Xunit;

namespace Orleans.Journaling.Tests;

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

    public async Task InitializeAsync()
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

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
    public async Task ListAsync_ReturnsSortedJournalIdsMatchingPrefix()
    {
        EnsureDockerAvailable();
        var provider = CreateProvider();
        await CreateStorage("journals/zeta").AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await CreateStorage("journals/alpha").AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await CreateStorage("other/beta").AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var listed = new List<JournalId>();
        await foreach (var journalId in provider.ListAsync(new JournalId("journals/"), CancellationToken.None))
        {
            listed.Add(journalId);
        }

        Assert.Equal(["journals/alpha", "journals/zeta"], listed.Select(static id => id.Value));
    }

    [SkippableFact]
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

    private S3JournalStorage CreateStorage(string journalId, string? journalFormatKey = null)
    {
        var options = CreateOptions(journalFormatKey);
        return new S3JournalStorage(
            new S3JournalStorage.S3JournalStorageShared(
                NullLogger<S3JournalStorage>.Instance,
                Options.Create(options),
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
        Skip.If(DockerSkipReason.Value is not null, DockerSkipReason.Value);
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
        catch (NullReferenceException)
        {
            return "Docker is unavailable, so MinIO S3 journal storage tests are skipped.";
        }
    }

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
