using System.Reflection;
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
public class MemoryGrainStorageCancellationTests
{
    [Fact]
    public async Task ReadStateAsync_CancellationOverload_PassesTokenToStorageGrain()
    {
        var storageGrain = new RecordingMemoryStorageGrain();
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grainFactory).Grain = storageGrain;
        var storage = new MemoryGrainStorage(
            "MemoryStore",
            new MemoryGrainStorageOptions { NumStorageGrains = 1 },
            NullLogger<MemoryGrainStorage>.Instance,
            grainFactory,
            new UnusedStorageSerializer(),
            new ActivatorProvider());
        var state = new GrainState<TestState>(new());
        using var cancellation = new CancellationTokenSource();

        await storage.ReadStateAsync(
            "state",
            GrainId.Create("test", "key"),
            state,
            cancellation.Token);

        Assert.True(storageGrain.UsedCancellationOverload);
        Assert.Equal(cancellation.Token, storageGrain.CancellationToken);
    }

    [Fact]
    public async Task ReadStateAsync_CancellationOverload_UsesLegacySubclassOverride()
    {
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grainFactory).Grain = new RecordingMemoryStorageGrain();
        var storage = new LegacyOverrideMemoryGrainStorage(
            grainFactory,
            new UnusedStorageSerializer(),
            new ActivatorProvider());
        var state = new GrainState<TestState>(new());

        await storage.ReadStateAsync(
            "state",
            GrainId.Create("test", "key"),
            state,
            TestContext.Current.CancellationToken);

        Assert.True(storage.LegacyReadCalled);
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public object Grain { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(IGrainFactory.GetGrain) ? Grain : throw new NotSupportedException();
    }

    private sealed class RecordingMemoryStorageGrain : IMemoryStorageGrain
    {
        public bool UsedCancellationOverload { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IGrainState<T>?> ReadStateAsync<T>(string grainStoreKey)
            => Task.FromResult<IGrainState<T>?>(null);

        public Task<IGrainState<T>?> ReadStateAsync<T>(
            string grainStoreKey,
            CancellationToken cancellationToken)
        {
            UsedCancellationOverload = true;
            CancellationToken = cancellationToken;
            return Task.FromResult<IGrainState<T>?>(null);
        }

        public Task<string> WriteStateAsync<T>(string grainStoreKey, IGrainState<T> grainState)
            => throw new NotSupportedException();

        public Task DeleteStateAsync<T>(string grainStoreKey, string? eTag)
            => throw new NotSupportedException();
    }

    private sealed class LegacyOverrideMemoryGrainStorage(
        IGrainFactory grainFactory,
        IGrainStorageSerializer serializer,
        IActivatorProvider activatorProvider)
        : MemoryGrainStorage(
            "MemoryStore",
            new MemoryGrainStorageOptions { NumStorageGrains = 1 },
            NullLogger<MemoryGrainStorage>.Instance,
            grainFactory,
            serializer,
            activatorProvider)
    {
        public bool LegacyReadCalled { get; private set; }

        public override Task ReadStateAsync<T>(
            string grainType,
            GrainId grainId,
            IGrainState<T> grainState)
        {
            LegacyReadCalled = true;
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

    private sealed class UnusedStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => throw new NotSupportedException();

        public T? Deserialize<T>(BinaryData input) => throw new NotSupportedException();
    }

    private sealed class TestState;
}
