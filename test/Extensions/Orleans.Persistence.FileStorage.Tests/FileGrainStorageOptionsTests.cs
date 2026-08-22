using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Persistence.FileStorage.Tests;

[TestProvider("None"), TestSuite("BVT"), TestCategory("FileStorage"), TestCategory("Persistence")]
public sealed class FileGrainStorageOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingRootDirectory_IsRejected(string? rootDirectory)
    {
        var services = FileStorageRegistrationTestContext.CreateServices(
            FileGrainStorageTestContext.CreateJsonSerializer());
        services.AddFileGrainStorage(
            "File",
            options => options.RootDirectory = rootDirectory!);
        using var serviceProvider = services.BuildServiceProvider();

        var validator = Assert.Single(serviceProvider.GetServices<IConfigurationValidator>());

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Fact]
    public void RootDirectoryCannotBeAnExistingFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.OwnedDirectory, "state.file");
        File.WriteAllText(path, "state");
        var services = FileStorageRegistrationTestContext.CreateServices(
            FileGrainStorageTestContext.CreateJsonSerializer());
        services.AddFileGrainStorage(
            "File",
            options => options.RootDirectory = path);
        using var serviceProvider = services.BuildServiceProvider();

        var validator = Assert.Single(serviceProvider.GetServices<IConfigurationValidator>());

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
        Assert.Equal("state", File.ReadAllText(path));
    }
}
