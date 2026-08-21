using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Xunit;

namespace Orleans.Persistence.FileStorage.Tests;

[TestProvider("None"), TestSuite("BVT"), TestCategory("FileStorage"), TestCategory("Persistence")]
public sealed class FileSiloBuilderExtensionsTests
{
    [Fact]
    public async Task NamedServiceCollectionRegistration_ResolvesNamedProvider()
    {
        const string ProviderName = "named-services";
        using var directory = new TemporaryDirectory();
        var services = FileStorageRegistrationTestContext.CreateServices(
            FileGrainStorageTestContext.CreateJsonSerializer());

        services.AddFileGrainStorage(
            ProviderName,
            options => options.RootDirectory = directory.RootDirectory);
        using var serviceProvider = services.BuildServiceProvider();
        var storage = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredKeyedService<IGrainStorage>(ProviderName));
        Assert.Contains(
            storage,
            serviceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>());
        var result = await FileStorageRegistrationTestContext.WriteReadAsync(
            storage,
            directory.RootDirectory,
            "named-services",
            41);

        Assert.Null(serviceProvider.GetKeyedService<IGrainStorage>("unknown-provider"));
        Assert.Equal(new FileStorageTestState { Value = "named-services", Revision = 41 }, result.Read.State);
        Assert.Equal(result.Written.ETag, result.Read.ETag);
        Assert.True(result.Read.RecordExists);
        Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
    }

    [Fact]
    public async Task NamedSiloBuilderRegistration_ResolvesNamedProvider()
    {
        const string ProviderName = "named-builder";
        using var directory = new TemporaryDirectory();
        var builder = new TestSiloBuilder();
        FileStorageRegistrationTestContext.AddRequiredServices(
            builder.Services,
            FileGrainStorageTestContext.CreateJsonSerializer());

        builder.AddFileGrainStorage(
            ProviderName,
            options => options.RootDirectory = directory.RootDirectory);
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var storage = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredKeyedService<IGrainStorage>(ProviderName));
        var result = await FileStorageRegistrationTestContext.WriteReadAsync(
            storage,
            directory.RootDirectory,
            "named-builder",
            42);

        Assert.Null(serviceProvider.GetKeyedService<IGrainStorage>("unknown-provider"));
        Assert.Equal(new FileStorageTestState { Value = "named-builder", Revision = 42 }, result.Read.State);
        Assert.Equal(result.Written.ETag, result.Read.ETag);
        Assert.True(result.Read.RecordExists);
        Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
    }

    [Fact]
    public async Task DefaultServiceCollectionRegistration_ResolvesKeyedAndUnkeyedProvider()
    {
        using var directory = new TemporaryDirectory();
        var services = FileStorageRegistrationTestContext.CreateServices(
            FileGrainStorageTestContext.CreateJsonSerializer());

        services.AddFileGrainStorage(
            options => options.RootDirectory = directory.RootDirectory);
        using var serviceProvider = services.BuildServiceProvider();
        var keyed = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredKeyedService<IGrainStorage>(
                ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        var unkeyed = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredService<IGrainStorage>());
        var result = await FileStorageRegistrationTestContext.WriteReadAsync(
            keyed,
            directory.RootDirectory,
            "default-services",
            43);

        Assert.Same(keyed, unkeyed);
        Assert.Same(keyed, serviceProvider.GetRequiredKeyedService<IGrainStorage>(
            ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        Assert.Equal(new FileStorageTestState { Value = "default-services", Revision = 43 }, result.Read.State);
        Assert.Equal(result.Written.ETag, result.Read.ETag);
        Assert.True(result.Read.RecordExists);
        Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
    }

    [Fact]
    public async Task DefaultSiloBuilderRegistration_ResolvesKeyedAndUnkeyedProvider()
    {
        using var directory = new TemporaryDirectory();
        var builder = new TestSiloBuilder();
        FileStorageRegistrationTestContext.AddRequiredServices(
            builder.Services,
            FileGrainStorageTestContext.CreateJsonSerializer());

        builder.AddFileGrainStorage(
            options => options.RootDirectory = directory.RootDirectory);
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var keyed = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredKeyedService<IGrainStorage>(
                ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        var unkeyed = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredService<IGrainStorage>());
        var result = await FileStorageRegistrationTestContext.WriteReadAsync(
            unkeyed,
            directory.RootDirectory,
            "default-builder",
            44);

        Assert.Same(keyed, unkeyed);
        Assert.Same(keyed, serviceProvider.GetRequiredKeyedService<IGrainStorage>(
            ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        Assert.Equal(new FileStorageTestState { Value = "default-builder", Revision = 44 }, result.Read.State);
        Assert.Equal(result.Written.ETag, result.Read.ETag);
        Assert.True(result.Read.RecordExists);
        Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

internal static class FileStorageRegistrationTestContext
{
    private const int RecordHeaderLength = 24;

    public static ServiceCollection CreateServices(IGrainStorageSerializer serializer)
    {
        var services = new ServiceCollection();
        AddRequiredServices(services, serializer);
        return services;
    }

    public static void AddRequiredServices(
        IServiceCollection services,
        IGrainStorageSerializer serializer)
    {
        services.AddSerializer();
        services.AddOptions();
        services.Configure<ClusterOptions>(
            options =>
            {
                options.ClusterId = "file-storage-registration-cluster";
                options.ServiceId = "file-storage-registration-service";
            });
        services.AddSingleton(serializer);
    }

    public static byte[] GetStoredPayload(string rootDirectory)
    {
        var path = Assert.Single(FileGrainStorageTestContext.GetRecordFiles(rootDirectory));
        return File.ReadAllBytes(path).AsSpan(RecordHeaderLength).ToArray();
    }

    public static async Task<(
        GrainState<FileStorageTestState> Written,
        GrainState<FileStorageTestState> Read)> WriteReadAsync(
        IGrainStorage storage,
        string rootDirectory,
        string value,
        int revision)
    {
        var grainId = GrainId.Create("registration-test", value);
        var written = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = value, Revision = revision });
        await storage.WriteStateAsync("state", grainId, written);
        var read = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await storage.ReadStateAsync("state", grainId, read);
        Assert.Single(FileGrainStorageTestContext.GetRecordFiles(rootDirectory));
        return (written, read);
    }
}
