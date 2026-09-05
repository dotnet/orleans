using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace Orleans.Persistence.Memory.Tests;

[TestCategory("BVT"), TestCategory("Persistence")]
public class MemoryGrainStorageInputValidationTests
{
    public static TheoryData<StorageOperation, bool> StorageOperations { get; } = new()
    {
        { StorageOperation.Read, false },
        { StorageOperation.Read, true },
        { StorageOperation.Write, false },
        { StorageOperation.Write, true },
        { StorageOperation.Clear, false },
        { StorageOperation.Clear, true },
    };

    [Theory]
    [MemberData(nameof(StorageOperations))]
    public async Task StorageOperation_NullGrainType_ThrowsWithoutMutatingStateOrAccessingStorage(
        StorageOperation operation,
        bool useCancellationOverload)
    {
        var (storage, storageGrain) = CreateStorage();
        var value = new TestState { Value = 42 };
        var state = new GrainState<TestState>(value, "initial-etag") { RecordExists = true };

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => InvokeStorageOperation(
                storage,
                operation,
                useCancellationOverload,
                null!,
                state,
                TestContext.Current.CancellationToken));

        Assert.Equal("grainType", exception.ParamName);
        Assert.Same(value, state.State);
        Assert.Equal("initial-etag", state.ETag);
        Assert.True(state.RecordExists);
        Assert.Equal(0, storageGrain.OperationCount);
    }

    [Theory]
    [MemberData(nameof(StorageOperations))]
    public async Task StorageOperation_NullGrainState_ThrowsBeforeAccessingStorage(
        StorageOperation operation,
        bool useCancellationOverload)
    {
        var (storage, storageGrain) = CreateStorage();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => InvokeStorageOperation(
                storage,
                operation,
                useCancellationOverload,
                "state",
                null!,
                TestContext.Current.CancellationToken));

        Assert.Equal("grainState", exception.ParamName);
        Assert.Equal(0, storageGrain.OperationCount);
    }

    [Theory]
    [InlineData(StorageOperation.Read)]
    [InlineData(StorageOperation.Write)]
    [InlineData(StorageOperation.Clear)]
    public async Task StorageOperation_CanceledToken_TakesPrecedenceWithoutMutatingStateOrAccessingStorage(
        StorageOperation operation)
    {
        var (storage, storageGrain) = CreateStorage();
        var value = new TestState { Value = 42 };
        var state = new GrainState<TestState>(value, "initial-etag") { RecordExists = true };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeStorageOperation(storage, operation, true, null!, state, cancellation.Token));

        Assert.Same(value, state.State);
        Assert.Equal("initial-etag", state.ETag);
        Assert.True(state.RecordExists);
        Assert.Equal(0, storageGrain.OperationCount);
    }

    [Fact]
    public async Task StorageOperations_ValidInputs_PreserveKeySerializationAndStateLifecycle()
    {
        var (storage, storageGrain) = CreateStorage();
        var grainId = GrainId.Create("test", "key");
        var originalState = new TestState { Value = 42 };
        var writtenState = new GrainState<TestState>(originalState);

        await storage.WriteStateAsync("state", grainId, writtenState);

        Assert.Equal($"state/{grainId}", storageGrain.LastKey);
        Assert.Equal("etag-1", writtenState.ETag);
        Assert.True(writtenState.RecordExists);

        originalState.Value = 99;
        var readState = new GrainState<TestState>(new());

        await storage.ReadStateAsync("state", grainId, readState);

        Assert.NotSame(originalState, readState.State);
        Assert.Equal(42, Assert.IsType<TestState>(readState.State).Value);
        Assert.Equal("etag-1", readState.ETag);
        Assert.True(readState.RecordExists);

        await storage.ClearStateAsync("state", grainId, readState);

        Assert.Null(readState.ETag);
        Assert.False(readState.RecordExists);
        Assert.Equal(0, readState.State.Value);

        var stateAfterClear = new GrainState<TestState>(new() { Value = -1 });
        await storage.ReadStateAsync("state", grainId, stateAfterClear);

        Assert.Null(stateAfterClear.ETag);
        Assert.False(stateAfterClear.RecordExists);
        Assert.Equal(0, Assert.IsType<TestState>(stateAfterClear.State).Value);
    }

    private static Task InvokeStorageOperation(
        MemoryGrainStorage storage,
        StorageOperation operation,
        bool useCancellationOverload,
        string grainType,
        IGrainState<TestState> grainState,
        CancellationToken cancellationToken = default)
    {
        var grainId = GrainId.Create("test", "key");
        return (operation, useCancellationOverload) switch
        {
            (StorageOperation.Read, false) => storage.ReadStateAsync(grainType, grainId, grainState),
            (StorageOperation.Read, true) => storage.ReadStateAsync(grainType, grainId, grainState, cancellationToken),
            (StorageOperation.Write, false) => storage.WriteStateAsync(grainType, grainId, grainState),
            (StorageOperation.Write, true) => storage.WriteStateAsync(grainType, grainId, grainState, cancellationToken),
            (StorageOperation.Clear, false) => storage.ClearStateAsync(grainType, grainId, grainState),
            (StorageOperation.Clear, true) => storage.ClearStateAsync(grainType, grainId, grainState, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static (MemoryGrainStorage Storage, RecordingMemoryStorageGrain StorageGrain) CreateStorage()
    {
        var storageGrain = new RecordingMemoryStorageGrain();
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grainFactory).Grain = storageGrain;
        var storage = new MemoryGrainStorage(
            "MemoryStore",
            new MemoryGrainStorageOptions { NumStorageGrains = 1 },
            NullLogger<MemoryGrainStorage>.Instance,
            grainFactory,
            new JsonStorageSerializer(),
            new ActivatorProvider());
        return (storage, storageGrain);
    }

    public enum StorageOperation
    {
        Read,
        Write,
        Clear,
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public object Grain { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(IGrainFactory.GetGrain) ? Grain : throw new NotSupportedException();
    }

    private sealed class RecordingMemoryStorageGrain : IMemoryStorageGrain
    {
        private readonly Dictionary<string, object?> _store = [];
        private int _etag;

        public int OperationCount { get; private set; }

        public string? LastKey { get; private set; }

        public Task<IGrainState<T>?> ReadStateAsync<T>(string grainStoreKey)
        {
            OperationCount++;
            LastKey = grainStoreKey;
            _store.TryGetValue(grainStoreKey, out var state);
            return Task.FromResult((IGrainState<T>?)state);
        }

        public Task<string> WriteStateAsync<T>(string grainStoreKey, IGrainState<T> grainState)
        {
            OperationCount++;
            LastKey = grainStoreKey;
            var etag = $"etag-{++_etag}";
            grainState.ETag = etag;
            _store[grainStoreKey] = grainState;
            return Task.FromResult(etag);
        }

        public Task DeleteStateAsync<T>(string grainStoreKey, string? eTag)
        {
            OperationCount++;
            LastKey = grainStoreKey;
            _store[grainStoreKey] = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ActivatorProvider : IActivatorProvider
    {
        public IActivator<T> GetActivator<T>() => new DefaultActivator<T>();
    }

    private sealed class DefaultActivator<T> : IActivator<T>
    {
        public T Create() => Activator.CreateInstance<T>();
    }

    private sealed class JsonStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => new(JsonSerializer.SerializeToUtf8Bytes(input));

        public T? Deserialize<T>(BinaryData input) => JsonSerializer.Deserialize<T>(input.ToMemory().Span);
    }

    private sealed class TestState
    {
        public int Value { get; set; }
    }
}
