using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.Persistence.FileStorage.Tests;

[TestProvider("None"), TestSuite("BVT"), TestCategory("FileStorage"), TestCategory("Persistence")]
public sealed class FileGrainStorageOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void InvalidRootDirectory_ThrowsOrleansConfigurationException(string? rootDirectory)
    {
        const string ProviderName = "invalid-root-provider";
        var services = FileStorageRegistrationTestContext.CreateServices(
            FileGrainStorageTestContext.CreateJsonSerializer());
        services.AddFileGrainStorage(
            ProviderName,
            options => options.RootDirectory = rootDirectory!);
        using var serviceProvider = services.BuildServiceProvider();

        var validator = Assert.Single(serviceProvider.GetServices<IConfigurationValidator>());
        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(nameof(FileGrainStorageOptions.RootDirectory), exception.Message, StringComparison.Ordinal);
        Assert.Contains(ProviderName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RootDirectoryPointingToExistingFile_ThrowsOrleansConfigurationException()
    {
        const string ProviderName = "file-root-provider";
        const string Contents = "must remain untouched";
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.OwnedDirectory, "existing.file");
        File.WriteAllText(filePath, Contents);
        var services = FileStorageRegistrationTestContext.CreateServices(
            FileGrainStorageTestContext.CreateJsonSerializer());
        services.AddFileGrainStorage(
            ProviderName,
            options => options.RootDirectory = filePath);
        using var serviceProvider = services.BuildServiceProvider();

        var validator = Assert.Single(serviceProvider.GetServices<IConfigurationValidator>());
        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(nameof(FileGrainStorageOptions.RootDirectory), exception.Message, StringComparison.Ordinal);
        Assert.Contains(ProviderName, exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(filePath));
        Assert.Equal(Contents, File.ReadAllText(filePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLockAcquireTimeout_ThrowsOrleansConfigurationException(int milliseconds)
    {
        const string ProviderName = "invalid-timeout-provider";
        using var directory = new TemporaryDirectory();
        var services = FileStorageRegistrationTestContext.CreateServices(
            FileGrainStorageTestContext.CreateJsonSerializer());
        services.AddFileGrainStorage(
            ProviderName,
            options =>
            {
                options.RootDirectory = directory.RootDirectory;
                options.LockAcquireTimeout = TimeSpan.FromMilliseconds(milliseconds);
            });
        using var serviceProvider = services.BuildServiceProvider();

        var validator = Assert.Single(serviceProvider.GetServices<IConfigurationValidator>());
        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(nameof(FileGrainStorageOptions.LockAcquireTimeout), exception.Message, StringComparison.Ordinal);
        Assert.Contains(ProviderName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidRootDirectory_PreservesConfiguredRootAndSerializer()
    {
        const string ProviderName = "valid-root-provider";
        using var directory = new TemporaryDirectory();
        var explicitSerializer = new RecordingGrainStorageSerializer([0x31, 0x32], new object());
        var globalSerializer = new RecordingGrainStorageSerializer([0x41, 0x42], new object());
        var services = FileStorageRegistrationTestContext.CreateServices(globalSerializer);
        services.AddFileGrainStorage(
            ProviderName,
            options =>
            {
                options.RootDirectory = directory.RootDirectory;
                options.GrainStorageSerializer = explicitSerializer;
            });
        using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider
            .GetRequiredService<IOptionsMonitor<FileGrainStorageOptions>>()
            .Get(ProviderName);
        var validator = Assert.Single(serviceProvider.GetServices<IConfigurationValidator>());

        Assert.Equal(directory.RootDirectory, options.RootDirectory);
        Assert.Equal(TimeSpan.FromSeconds(30), options.LockAcquireTimeout);
        Assert.Same(explicitSerializer, options.GrainStorageSerializer);
        var serializerOptions = Assert.IsAssignableFrom<IStorageProviderSerializerOptions>(options);
        Assert.Same(explicitSerializer, serializerOptions.GrainStorageSerializer);
        Assert.Null(Record.Exception(validator.ValidateConfiguration));
    }
}
