using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.TestKit;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Xunit;

namespace Orleans.Persistence.FileStorage.Tests;

public sealed class FileGrainStorageTestFixture : GrainStorageTestFixture, IAsyncLifetime
{
    private readonly TemporaryDirectory _temporaryDirectory = new();

    protected override string StorageProviderName { get; } = $"FileStorage-{Guid.NewGuid():N}";

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _temporaryDirectory.Dispose();
        }
    }

    protected override void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddFileGrainStorage(
            StorageProviderName,
            options => options.RootDirectory = _temporaryDirectory.RootDirectory);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        OwnedDirectory = Path.Combine(
            Path.GetTempPath(),
            "Orleans.Persistence.FileStorage.Tests",
            Guid.NewGuid().ToString("N"));
        RootDirectory = Path.Combine(OwnedDirectory, "nested", "root");
        Directory.CreateDirectory(OwnedDirectory);
    }

    public string OwnedDirectory { get; }

    public string RootDirectory { get; }

    public void Dispose()
    {
        if (Directory.Exists(OwnedDirectory))
        {
            Directory.Delete(OwnedDirectory, recursive: true);
        }
    }
}

internal static class FileGrainStorageTestContext
{
    private static readonly IServiceProvider ActivatorServices =
        new ServiceCollection().AddSerializer().BuildServiceProvider();
    private static readonly IActivatorProvider ActivatorProvider =
        ActivatorServices.GetRequiredService<IActivatorProvider>();

    public static FileGrainStorage CreateStorage(
        string rootDirectory,
        string serviceId = "file-storage-test-service",
        IGrainStorageSerializer? serializer = null) =>
        new(
            "FileStore",
            new FileGrainStorageOptions
            {
                RootDirectory = rootDirectory,
                GrainStorageSerializer = serializer ?? CreateJsonSerializer(),
            },
            Options.Create(new ClusterOptions { ServiceId = serviceId }),
            ActivatorProvider);

    public static IGrainStorageSerializer CreateJsonSerializer() =>
        new SystemTextJsonGrainStorageSerializer(Options.Create(new SystemTextJsonGrainStorageSerializerOptions()));

    public static string[] GetRecordFiles(string rootDirectory) =>
        Directory.Exists(rootDirectory)
            ? Directory.GetFiles(rootDirectory, "*.grain", SearchOption.AllDirectories)
            : [];
}

internal sealed record FileStorageTestState
{
    public string? Value { get; set; }

    public int Revision { get; set; }
}
