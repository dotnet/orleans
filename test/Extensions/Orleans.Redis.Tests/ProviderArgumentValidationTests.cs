using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Journaling;
using Orleans.Persistence;
using Orleans.Storage;
using Xunit;

namespace Tester.Redis;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class ProviderArgumentValidationTests
{
    [Fact]
    public void AddRedisJournalStorage_NullBuilder_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => RedisJournalStorageHostingExtensions.AddRedisJournalStorage(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public async Task RedisStorageOptions_DefaultCreateMultiplexer_NullOptions_Throws()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => RedisStorageOptions.DefaultCreateMultiplexer(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task RedisReminderTableOptions_DefaultCreateMultiplexer_NullOptions_Throws()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => RedisReminderTableOptions.DefaultCreateMultiplexer(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task RedisStreamingOptions_DefaultCreateMultiplexer_NullOptions_Throws()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => RedisStreamingOptions.DefaultCreateMultiplexer(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void UseGetRedisKeyIgnoringGrainType_NullOptionsBuilder_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => RedisStorageOptionsExtensions.UseGetRedisKeyIgnoringGrainType(null!));

        Assert.Equal("optionsBuilder", exception.ParamName);
    }

    [Theory]
    [InlineData(nameof(RedisGrainStorage.ReadStateAsync))]
    [InlineData(nameof(RedisGrainStorage.WriteStateAsync))]
    [InlineData(nameof(RedisGrainStorage.ClearStateAsync))]
    public async Task StorageOperation_NullGrainType_ThrowsBeforeMutatingState(string operation)
    {
        var storage = CreateStorage();
        var state = new GrainState<string> { State = "value", ETag = "etag", RecordExists = true };

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => Invoke(storage, operation, null!, state));

        Assert.Equal("grainType", exception.ParamName);
        Assert.Equal("value", state.State);
        Assert.Equal("etag", state.ETag);
        Assert.True(state.RecordExists);
    }

    [Theory]
    [InlineData(nameof(RedisGrainStorage.ReadStateAsync))]
    [InlineData(nameof(RedisGrainStorage.WriteStateAsync))]
    [InlineData(nameof(RedisGrainStorage.ClearStateAsync))]
    public async Task StorageOperation_NullGrainState_Throws(string operation)
    {
        var storage = CreateStorage();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => Invoke(storage, operation, "grain-type", null!));

        Assert.Equal("grainState", exception.ParamName);
    }

    private static RedisGrainStorage CreateStorage() =>
        new(
            "argument-tests",
            new RedisStorageOptions(),
            null!,
            Options.Create(new ClusterOptions { ServiceId = "service" }),
            null!,
            NullLogger<RedisGrainStorage>.Instance);

    private static Task Invoke(RedisGrainStorage storage, string operation, string grainType, IGrainState<string> grainState)
    {
        var grainId = GrainId.Create("grain-type", "grain-key");
        return operation switch
        {
            nameof(RedisGrainStorage.ReadStateAsync) => storage.ReadStateAsync(grainType, grainId, grainState),
            nameof(RedisGrainStorage.WriteStateAsync) => storage.WriteStateAsync(grainType, grainId, grainState),
            nameof(RedisGrainStorage.ClearStateAsync) => storage.ClearStateAsync(grainType, grainId, grainState),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }
}
