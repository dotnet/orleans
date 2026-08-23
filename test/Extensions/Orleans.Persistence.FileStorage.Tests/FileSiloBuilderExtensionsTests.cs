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
    public void NamedProvider_IsRegistered()
    {
        using var directory = new TemporaryDirectory();
        var services = FileStorageRegistrationTestContext.CreateServices(
            FileGrainStorageTestContext.CreateJsonSerializer());

        services.AddFileGrainStorage(
            "File",
            options => options.RootDirectory = directory.RootDirectory);
        using var serviceProvider = services.BuildServiceProvider();

        var storage = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredKeyedService<IGrainStorage>("File"));
        Assert.Contains(
            storage,
            serviceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>());
    }

    [Fact]
    public void DefaultProvider_IsRegisteredAsKeyedAndUnkeyed()
    {
        using var directory = new TemporaryDirectory();
        var builder = new TestSiloBuilder();
        FileStorageRegistrationTestContext.AddRequiredServices(
            builder.Services,
            FileGrainStorageTestContext.CreateJsonSerializer());

        builder.AddFileGrainStorage(
            options => options.RootDirectory = directory.RootDirectory);
        using var serviceProvider = builder.Services.BuildServiceProvider();

        var keyed = serviceProvider.GetRequiredKeyedService<IGrainStorage>(
            ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        Assert.Same(keyed, serviceProvider.GetRequiredService<IGrainStorage>());
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

internal static class FileStorageRegistrationTestContext
{
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
                options.ClusterId = "file-storage-tests";
                options.ServiceId = "file-storage-tests";
            });
        services.AddSingleton(serializer);
    }
}
