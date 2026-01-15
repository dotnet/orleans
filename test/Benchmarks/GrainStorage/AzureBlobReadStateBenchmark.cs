using Azure.Storage.Blobs;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using TestExtensions;

namespace Benchmarks.GrainStorage;

[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 5)]
[MemoryDiagnoser(true)]
public class AzureBlobReadStateBenchmark
{
    private const int ReadIterations = 50;
    private ServiceProvider _serviceProvider = null!;
    private AzureBlobGrainStorage _nonPooledStorage = null!;
    private AzureBlobGrainStorage _pooledStorage = null!;
    private IGrainState<BenchmarkState> _nonPooledState = null!;
    private IGrainState<BenchmarkState> _pooledState = null!;
    private GrainId _grainId;
    private string _grainType = null!;
    private BlobContainerClient _containerClient = null!;

    [Params(4 * 1024, 64 * 1024, 128 * 1024)]
    public int PayloadSize { get; set; }

    [Params(StorageSerializerKind.Orleans, StorageSerializerKind.NewtonsoftJson, StorageSerializerKind.SystemTextJson)]
    public StorageSerializerKind SerializerKind { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var client = CreateBlobServiceClient();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSerializer();
        services.AddOptions<OrleansJsonSerializerOptions>();
        services.AddSingleton<IPostConfigureOptions<OrleansJsonSerializerOptions>, ConfigureOrleansJsonSerializerOptions>();
        services.AddSingleton<OrleansJsonSerializer>();
        _serviceProvider = services.BuildServiceProvider();

        var activatorProvider = _serviceProvider.GetRequiredService<IActivatorProvider>();
        var serializer = CreateSerializer(SerializerKind);
        var nonStreamingSerializer = new NonStreamingGrainStorageSerializer(serializer);
        var containerName = $"bench-grainstate-{Guid.NewGuid():N}";
        _grainType = "bench-grain";
        _grainId = GrainId.Create("bench-grain", Guid.NewGuid().ToString("N"));

        var nonPooledOptions = CreateOptions(client, nonStreamingSerializer, containerName, usePooledReads: false);
        var pooledOptions = CreateOptions(client, nonStreamingSerializer, containerName, usePooledReads: true);

        (_nonPooledStorage, var nonPooledFactory) = CreateStorage("bench-nonpooled", nonPooledOptions, activatorProvider);
        (_pooledStorage, var pooledFactory) = CreateStorage("bench-pooled", pooledOptions, activatorProvider);

        await nonPooledFactory.InitializeAsync(client);
        await pooledFactory.InitializeAsync(client);

        var writeState = new GrainState<BenchmarkState>
        {
            State = BenchmarkState.Create(PayloadSize)
        };
        await _nonPooledStorage.WriteStateAsync(_grainType, _grainId, writeState);

        _nonPooledState = new GrainState<BenchmarkState>();
        _pooledState = new GrainState<BenchmarkState>();
        _containerClient = client.GetBlobContainerClient(containerName);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_containerClient is not null)
        {
            await _containerClient.DeleteIfExistsAsync();
        }

        _serviceProvider?.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = ReadIterations)]
    public async Task ReadStateNonPooledAsync()
    {
        for (var i = 0; i < ReadIterations; i++)
        {
            await _nonPooledStorage.ReadStateAsync(_grainType, _grainId, _nonPooledState);
        }
    }

    [Benchmark(OperationsPerInvoke = ReadIterations)]
    public async Task ReadStatePooledAsync()
    {
        for (var i = 0; i < ReadIterations; i++)
        {
            await _pooledStorage.ReadStateAsync(_grainType, _grainId, _pooledState);
        }
    }

    private static BlobServiceClient CreateBlobServiceClient()
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            if (!TestDefaultConfiguration.GetValue(nameof(TestDefaultConfiguration.DataBlobUri), out var blobUriValue) ||
                string.IsNullOrWhiteSpace(blobUriValue))
            {
                throw new InvalidOperationException("DataBlobUri is required when UseAadAuthentication is true.");
            }

            return new BlobServiceClient(new Uri(blobUriValue), TestDefaultConfiguration.TokenCredential);
        }

        if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
        {
            throw new InvalidOperationException("OrleansDataConnectionString must be set for Azure Blob benchmarks.");
        }

        return new BlobServiceClient(TestDefaultConfiguration.DataConnectionString);
    }

    private static AzureBlobStorageOptions CreateOptions(
        BlobServiceClient client,
        IGrainStorageSerializer serializer,
        string containerName,
        bool usePooledReads)
    {
        return new AzureBlobStorageOptions
        {
            BlobServiceClient = client,
            ContainerName = containerName,
            GrainStorageSerializer = serializer,
            UsePooledBufferForReads = usePooledReads
        };
    }

    private (AzureBlobGrainStorage Storage, IBlobContainerFactory Factory) CreateStorage(
        string name,
        AzureBlobStorageOptions options,
        IActivatorProvider activatorProvider)
    {
        var containerFactory = options.BuildContainerFactory(_serviceProvider, options);
        var logger = NullLogger<AzureBlobGrainStorage>.Instance;
        var storage = new AzureBlobGrainStorage(name, options, containerFactory, activatorProvider, logger);
        return (storage, containerFactory);
    }

    private IGrainStorageSerializer CreateSerializer(StorageSerializerKind kind)
    {
        return kind switch
        {
            StorageSerializerKind.Orleans => new OrleansGrainStorageSerializer(_serviceProvider.GetRequiredService<Serializer>()),
            StorageSerializerKind.NewtonsoftJson => new JsonGrainStorageSerializer(CreateJsonSerializer()),
            StorageSerializerKind.SystemTextJson => new SystemTextJsonGrainStorageSerializer(),
            _ => throw new InvalidOperationException($"Unknown serializer kind '{kind}'.")
        };
    }

    private static OrleansJsonSerializer CreateJsonSerializer()
        => new(Options.Create(new OrleansJsonSerializerOptions()));
}
