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
public class AzureBlobWriteStateStreamingBenchmark
{
    private const int WriteIterations = 50;
    private ServiceProvider _serviceProvider = null!;
    private AzureBlobGrainStorage _binaryStorage = null!;
    private AzureBlobGrainStorage _streamStorage = null!;
    private GrainState<BenchmarkState> _binaryState = null!;
    private GrainState<BenchmarkState> _streamState = null!;
    private GrainId _binaryGrainId;
    private GrainId _streamGrainId;
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
        var containerName = $"bench-grainstate-{Guid.NewGuid():N}";
        _grainType = "bench-grain";
        _binaryGrainId = GrainId.Create("bench-grain", Guid.NewGuid().ToString("N"));
        _streamGrainId = GrainId.Create("bench-grain", Guid.NewGuid().ToString("N"));

        var binaryOptions = CreateOptions(client, new NonStreamingGrainStorageSerializer(serializer), containerName, AzureBlobStorageWriteMode.BinaryData);
        var streamOptions = CreateOptions(client, serializer, containerName, AzureBlobStorageWriteMode.BufferedStream);

        (_binaryStorage, var binaryFactory) = CreateStorage("bench-binary", binaryOptions, activatorProvider);
        (_streamStorage, var streamFactory) = CreateStorage("bench-stream", streamOptions, activatorProvider);

        await binaryFactory.InitializeAsync(client);
        await streamFactory.InitializeAsync(client);

        _binaryState = new GrainState<BenchmarkState> { State = BenchmarkState.Create(PayloadSize) };
        _streamState = new GrainState<BenchmarkState> { State = BenchmarkState.Create(PayloadSize) };
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

    [Benchmark(Baseline = true, OperationsPerInvoke = WriteIterations)]
    public async Task WriteStateBinaryAsync()
    {
        for (var i = 0; i < WriteIterations; i++)
        {
            await _binaryStorage.WriteStateAsync(_grainType, _binaryGrainId, _binaryState);
        }
    }

    [Benchmark(OperationsPerInvoke = WriteIterations)]
    public async Task WriteStateStreamAsync()
    {
        for (var i = 0; i < WriteIterations; i++)
        {
            await _streamStorage.WriteStateAsync(_grainType, _streamGrainId, _streamState);
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
        AzureBlobStorageWriteMode writeMode)
    {
        return new AzureBlobStorageOptions
        {
            BlobServiceClient = client,
            ContainerName = containerName,
            GrainStorageSerializer = serializer,
            UsePooledBufferForReads = false,
            WriteMode = writeMode
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
