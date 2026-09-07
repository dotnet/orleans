using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Storage;
using Xunit;

namespace UnitTests.Storage;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class GrainStorageSerializerTests
{
    [Fact]
    public async Task StorageSerializerEntryPoints_NullInput_ThrowBeforeCollaborators()
    {
        var json = new JsonGrainStorageSerializer(null!);
        var jsonException = Assert.Throws<ArgumentNullException>(() => json.Deserialize<object>(null!));
        Assert.Equal("input", jsonException.ParamName);

        var jsonDestinationException = Assert.Throws<ArgumentNullException>(
            () => json.SerializeAsync<object>(null, null!, TestContext.Current.CancellationToken));
        Assert.Equal("destination", jsonDestinationException.ParamName);

        var jsonStreamException = Assert.Throws<ArgumentNullException>(
            () => json.DeserializeAsync<object>(null!, TestContext.Current.CancellationToken));
        Assert.Equal("input", jsonStreamException.ParamName);

        var orleans = new OrleansGrainStorageSerializer(null!);
        var orleansException = Assert.Throws<ArgumentNullException>(() => orleans.Deserialize<object>(null!));
        Assert.Equal("input", orleansException.ParamName);

        var orleansDestinationException = Assert.Throws<ArgumentNullException>(
            () => orleans.SerializeAsync<object>(null, null!, TestContext.Current.CancellationToken));
        Assert.Equal("destination", orleansDestinationException.ParamName);

        var streamException = await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await orleans.DeserializeAsync<object>(null!, TestContext.Current.CancellationToken));
        Assert.Equal("input", streamException.ParamName);

        var systemTextJson = new SystemTextJsonGrainStorageSerializer(
            Options.Create(new SystemTextJsonGrainStorageSerializerOptions()));
        var systemTextJsonException = Assert.Throws<ArgumentNullException>(
            () => systemTextJson.Deserialize<object>(null!));
        Assert.Equal("input", systemTextJsonException.ParamName);

        var systemTextJsonDestinationException = Assert.Throws<ArgumentNullException>(
            () => systemTextJson.SerializeAsync<object>(null, null!, TestContext.Current.CancellationToken));
        Assert.Equal("destination", systemTextJsonDestinationException.ParamName);

        var systemTextJsonStreamException = await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await systemTextJson.DeserializeAsync<object>(null!, TestContext.Current.CancellationToken));
        Assert.Equal("input", systemTextJsonStreamException.ParamName);
    }

    [Fact]
    public void GrainStorageSerializerExtensions_NullSerializer_ThrowsWithExactParameterName()
    {
        IGrainStorageSerializer serializer = null!;

        var exception = Assert.Throws<ArgumentNullException>(
            () => serializer.Deserialize<object>(ReadOnlyMemory<byte>.Empty));

        Assert.Equal("serializer", exception.ParamName);
    }

    [Fact]
    public void JsonStorageSerializer_CanceledToken_PrecedesNullStream()
    {
        var serializer = new JsonGrainStorageSerializer(null!);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => serializer.SerializeAsync<object>(null, null!, cancellation.Token));
        Assert.Throws<OperationCanceledException>(
            () => serializer.DeserializeAsync<object>(null!, cancellation.Token));
    }

    [Fact]
    public void GrainStorageSerializerExtensions_ValidInput_ForwardsExactBytesOnce()
    {
        var serializer = new RecordingSerializer();
        var input = new byte[] { 1, 2, 3, 4 };

        var result = serializer.Deserialize<string>(input);

        Assert.Equal("deserialized", result);
        Assert.Equal(1, serializer.DeserializeCallCount);
        Assert.Equal(input, serializer.Input.ToMemory().ToArray());
    }

    [Fact]
    public void InconsistentStateException_NullStorageException_ThrowsWithExactParameterName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new InconsistentStateException("stored", "current", (Exception)null!));

        Assert.Equal("storageException", exception.ParamName);
    }

    [Fact]
    public void InconsistentStateException_ValidStorageException_PreservesDetails()
    {
        var inner = new InvalidOperationException("storage failed");

        var exception = new InconsistentStateException("stored", "current", inner);

        Assert.Equal(inner.Message, exception.Message);
        Assert.Equal("stored", exception.StoredEtag);
        Assert.Equal("current", exception.CurrentEtag);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void SystemTextJsonGrainStorageSerializer_ValidValue_RoundTrips()
    {
        var serializer = new SystemTextJsonGrainStorageSerializer(
            Options.Create(new SystemTextJsonGrainStorageSerializerOptions()));
        var input = new TestState { Name = "alpha", Value = 42 };

        var result = serializer.Deserialize<TestState>(serializer.Serialize(input));

        Assert.NotNull(result);
        Assert.Equal(input.Name, result.Name);
        Assert.Equal(input.Value, result.Value);
    }

    [Fact]
    public void DefaultStorageProviderSerializerOptionsConfigurator_NullOptions_ThrowsWithExactParameterName()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var configurator = new DefaultStorageProviderSerializerOptionsConfigurator<TestStorageOptions>(serviceProvider);

        var exception = Assert.Throws<ArgumentNullException>(() => configurator.PostConfigure(null, null!));

        Assert.Equal("options", exception.ParamName);
    }

    private sealed class RecordingSerializer : IGrainStorageSerializer
    {
        public int DeserializeCallCount { get; private set; }

        public BinaryData Input { get; private set; } = new(Array.Empty<byte>());

        public T? Deserialize<T>(BinaryData input)
        {
            DeserializeCallCount++;
            Input = input;
            return (T?)(object)"deserialized";
        }

        public BinaryData Serialize<T>(T? input) => throw new NotSupportedException();
    }

    private sealed class TestState
    {
        public string? Name { get; set; }

        public int Value { get; set; }
    }

    private sealed class TestStorageOptions : IStorageProviderSerializerOptions
    {
        public IGrainStorageSerializer GrainStorageSerializer { get; set; } = null!;
    }
}
