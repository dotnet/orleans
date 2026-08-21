using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.Persistence.FileStorage.Tests;

[TestProvider("None"), TestSuite("BVT"), TestCategory("FileStorage"), TestCategory("Persistence")]
public sealed class FileGrainStorageFactoryTests
{
    [Fact]
    public async Task NamedFactoryResolution_UsesRequestedNamedOptions()
    {
        using var alphaDirectory = new TemporaryDirectory();
        using var betaDirectory = new TemporaryDirectory();
        byte[] alphaBytes = [0xA1, 0xA2, 0xA3];
        byte[] betaBytes = [0xB1, 0xB2, 0xB3, 0xB4];
        var alphaSerializer = new RecordingGrainStorageSerializer(alphaBytes, new object());
        var betaSerializer = new RecordingGrainStorageSerializer(betaBytes, new object());
        var services = FileStorageRegistrationTestContext.CreateServices(
            FileGrainStorageTestContext.CreateJsonSerializer());
        services.AddFileGrainStorage(
            "alpha",
            options =>
            {
                options.RootDirectory = alphaDirectory.RootDirectory;
                options.GrainStorageSerializer = alphaSerializer;
            });
        services.AddFileGrainStorage(
            "beta",
            options =>
            {
                options.RootDirectory = betaDirectory.RootDirectory;
                options.GrainStorageSerializer = betaSerializer;
            });
        using var serviceProvider = services.BuildServiceProvider();
        var alpha = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredKeyedService<IGrainStorage>("alpha"));
        var beta = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredKeyedService<IGrainStorage>("beta"));
        var alphaValue = new FileStorageTestState { Value = "alpha-value", Revision = 31 };
        var betaValue = new FileStorageTestState { Value = "beta-value", Revision = 32 };
        var alphaState = new GrainState<FileStorageTestState>(alphaValue);
        var betaState = new GrainState<FileStorageTestState>(betaValue);

        await alpha.WriteStateAsync("state", GrainId.Create("factory", "alpha"), alphaState);
        await beta.WriteStateAsync("state", GrainId.Create("factory", "beta"), betaState);

        Assert.NotSame(alpha, beta);
        Assert.Same(alphaValue, alphaSerializer.SerializedValue);
        Assert.Same(betaValue, betaSerializer.SerializedValue);
        Assert.Equal(1, alphaSerializer.SerializeCallCount);
        Assert.Equal(1, betaSerializer.SerializeCallCount);
        Assert.Equal(alphaBytes, FileStorageRegistrationTestContext.GetStoredPayload(alphaDirectory.RootDirectory));
        Assert.Equal(betaBytes, FileStorageRegistrationTestContext.GetStoredPayload(betaDirectory.RootDirectory));
        Assert.True(alphaState.RecordExists);
        Assert.True(betaState.RecordExists);
        Assert.NotEqual(alphaState.ETag, betaState.ETag);
    }

    [Fact]
    public async Task ExplicitSerializer_OverridesNamedAndGlobalSerializers()
    {
        const string ProviderName = "explicit";
        using var directory = new TemporaryDirectory();
        byte[] explicitBytes = [0xE1, 0xE2, 0xE3];
        var explicitSerializer = new RecordingGrainStorageSerializer(explicitBytes, new object());
        var keyedSerializer = new RecordingGrainStorageSerializer([0xA0], new object());
        var globalSerializer = new RecordingGrainStorageSerializer([0xB0], new object());
        var services = FileStorageRegistrationTestContext.CreateServices(globalSerializer);
        services.AddKeyedSingleton<IGrainStorageSerializer>(ProviderName, keyedSerializer);
        services.AddFileGrainStorage(
            ProviderName,
            options =>
            {
                options.RootDirectory = directory.RootDirectory;
                options.GrainStorageSerializer = explicitSerializer;
            });
        using var serviceProvider = services.BuildServiceProvider();
        var storage = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredKeyedService<IGrainStorage>(ProviderName));
        var value = new FileStorageTestState { Value = "explicit-wins", Revision = 33 };
        var state = new GrainState<FileStorageTestState>(value);

        await storage.WriteStateAsync("state", GrainId.Create("serializer", ProviderName), state);

        Assert.Equal(1, explicitSerializer.SerializeCallCount);
        Assert.Same(value, explicitSerializer.SerializedValue);
        Assert.Equal(0, keyedSerializer.SerializeCallCount);
        Assert.Equal(0, globalSerializer.SerializeCallCount);
        Assert.Equal(explicitBytes, FileStorageRegistrationTestContext.GetStoredPayload(directory.RootDirectory));
        Assert.True(state.RecordExists);
    }

    [Fact]
    public async Task NamedKeyedSerializer_IsSelectedForMatchingProvider()
    {
        const string ProviderName = "keyed";
        using var directory = new TemporaryDirectory();
        byte[] keyedBytes = [0xC1, 0xC2, 0xC3];
        var keyedSerializer = new RecordingGrainStorageSerializer(keyedBytes, new object());
        var globalSerializer = new RecordingGrainStorageSerializer([0xD0], new object());
        var services = FileStorageRegistrationTestContext.CreateServices(globalSerializer);
        services.AddKeyedSingleton<IGrainStorageSerializer>(ProviderName, keyedSerializer);
        services.AddFileGrainStorage(
            ProviderName,
            options => options.RootDirectory = directory.RootDirectory);
        using var serviceProvider = services.BuildServiceProvider();
        var storage = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredKeyedService<IGrainStorage>(ProviderName));
        var value = new FileStorageTestState { Value = "keyed-wins", Revision = 34 };
        var state = new GrainState<FileStorageTestState>(value);

        await storage.WriteStateAsync("state", GrainId.Create("serializer", ProviderName), state);

        Assert.Equal(1, keyedSerializer.SerializeCallCount);
        Assert.Same(value, keyedSerializer.SerializedValue);
        Assert.Equal(0, globalSerializer.SerializeCallCount);
        Assert.Equal(keyedBytes, FileStorageRegistrationTestContext.GetStoredPayload(directory.RootDirectory));
        Assert.True(state.RecordExists);
    }

    [Fact]
    public async Task GlobalSerializer_IsUsedWhenNoExplicitOrNamedSerializerExists()
    {
        const string ProviderName = "global";
        using var directory = new TemporaryDirectory();
        byte[] globalBytes = [0xF1, 0xF2, 0xF3];
        var globalSerializer = new RecordingGrainStorageSerializer(globalBytes, new object());
        var services = FileStorageRegistrationTestContext.CreateServices(globalSerializer);
        services.AddFileGrainStorage(
            ProviderName,
            options => options.RootDirectory = directory.RootDirectory);
        using var serviceProvider = services.BuildServiceProvider();
        var storage = Assert.IsType<FileGrainStorage>(
            serviceProvider.GetRequiredKeyedService<IGrainStorage>(ProviderName));
        var value = new FileStorageTestState { Value = "global-wins", Revision = 35 };
        var state = new GrainState<FileStorageTestState>(value);

        await storage.WriteStateAsync("state", GrainId.Create("serializer", ProviderName), state);

        Assert.Equal(1, globalSerializer.SerializeCallCount);
        Assert.Same(value, globalSerializer.SerializedValue);
        Assert.Equal(globalBytes, FileStorageRegistrationTestContext.GetStoredPayload(directory.RootDirectory));
        Assert.True(state.RecordExists);
    }
}
