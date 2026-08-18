using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace UnitTests.Storage;

[TestCategory("BVT"), TestCategory("Storage")]
public class StateStorageBridgeCancellationTests
{
    [Theory]
    [InlineData(StorageOperation.Read)]
    [InlineData(StorageOperation.Write)]
    [InlineData(StorageOperation.Clear)]
    public async Task IGrainStorage_CancellationOverload_DelegatesToLegacyOverload(StorageOperation operation)
    {
        IGrainStorage storage = new LegacyGrainStorage();
        var grainId = GrainId.Create("state-storage-bridge-test", Guid.NewGuid().ToString("N"));
        IGrainState<TestState> grainState = new GrainState<TestState>(new());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await InvokeAsync(storage, operation, grainId, grainState, cancellation.Token);

        var legacyStorage = Assert.IsType<LegacyGrainStorage>(storage);
        Assert.Equal(1, legacyStorage.CallCount);
        Assert.Equal(operation, legacyStorage.LastOperation);
        Assert.Equal("state", legacyStorage.LastStateName);
        Assert.Equal(grainId, legacyStorage.LastGrainId);
        Assert.Same(grainState, legacyStorage.LastGrainState);
    }

    [Theory]
    [InlineData(StorageOperation.Read)]
    [InlineData(StorageOperation.Write)]
    [InlineData(StorageOperation.Clear)]
    public async Task IStorage_CancellationOverload_PassesTokenToGrainStorage(StorageOperation operation)
    {
        using var context = TestGrainContext.Create();
        var grainStorage = new RecordingGrainStorage();
        IStorage storage = CreateBridge(context, grainStorage);
        using var cancellation = new CancellationTokenSource();

        await RunInGrainContextAsync(
            context,
            () => InvokeAsync(storage, operation, cancellation.Token));

        AssertProviderInvocation(grainStorage, context, operation, cancellation.Token);
    }

    [Theory]
    [InlineData(StorageOperation.Read)]
    [InlineData(StorageOperation.Write)]
    [InlineData(StorageOperation.Clear)]
    public async Task IStorage_ParameterlessOverload_PassesNoneToGrainStorage(StorageOperation operation)
    {
        using var context = TestGrainContext.Create();
        var grainStorage = new RecordingGrainStorage();
        IStorage storage = CreateBridge(context, grainStorage);

        await RunInGrainContextAsync(context, () => InvokeAsync(storage, operation));

        AssertProviderInvocation(grainStorage, context, operation, CancellationToken.None);
    }

    [Theory]
    [InlineData(StorageOperation.Read)]
    [InlineData(StorageOperation.Write)]
    [InlineData(StorageOperation.Clear)]
    public async Task IStorage_CallerCancellation_PropagatesProviderException(StorageOperation operation)
    {
        using var context = TestGrainContext.Create();
        var grainStorage = new RecordingGrainStorage();
        IStorage storage = CreateBridge(context, grainStorage);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException(cancellation.Token);
        grainStorage.ExceptionToThrow = expected;

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => RunInGrainContextAsync(
                context,
                () => InvokeAsync(storage, operation, cancellation.Token)));

        Assert.Same(expected, actual);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        AssertProviderInvocation(grainStorage, context, operation, cancellation.Token);
    }

    private static StateStorageBridge<TestState> CreateBridge(
        TestGrainContext context,
        RecordingGrainStorage storage)
        => new("state", context, storage);

    private static void AssertProviderInvocation(
        RecordingGrainStorage storage,
        TestGrainContext context,
        StorageOperation operation,
        CancellationToken expectedCancellationToken)
    {
        Assert.Equal(1, storage.CallCount);
        Assert.Equal(operation, storage.LastOperation);
        Assert.Equal("state", storage.LastStateName);
        Assert.Equal(context.GrainId, storage.LastGrainId);
        Assert.IsAssignableFrom<IGrainState<TestState>>(storage.LastGrainState);
        Assert.True(storage.UsedCancellationOverload);
        Assert.Equal(expectedCancellationToken, storage.LastCancellationToken);
    }

    private static Task InvokeAsync(
        IGrainStorage storage,
        StorageOperation operation,
        GrainId grainId,
        IGrainState<TestState> grainState,
        CancellationToken cancellationToken)
        => operation switch
        {
            StorageOperation.Read => storage.ReadStateAsync("state", grainId, grainState, cancellationToken),
            StorageOperation.Write => storage.WriteStateAsync("state", grainId, grainState, cancellationToken),
            StorageOperation.Clear => storage.ClearStateAsync("state", grainId, grainState, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static Task InvokeAsync(
        IStorage storage,
        StorageOperation operation,
        CancellationToken cancellationToken)
        => operation switch
        {
            StorageOperation.Read => storage.ReadStateAsync(cancellationToken),
            StorageOperation.Write => storage.WriteStateAsync(cancellationToken),
            StorageOperation.Clear => storage.ClearStateAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static Task InvokeAsync(IStorage storage, StorageOperation operation)
        => operation switch
        {
            StorageOperation.Read => storage.ReadStateAsync(),
            StorageOperation.Write => storage.WriteStateAsync(),
            StorageOperation.Clear => storage.ClearStateAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static Task RunInGrainContextAsync(TestGrainContext context, Func<Task> action)
        => Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.None,
            context.WorkItemGroup.TaskScheduler).Unwrap();

    public enum StorageOperation
    {
        Read,
        Write,
        Clear
    }

    public sealed class TestState
    {
    }

    private sealed class TestActivatorProvider : IActivatorProvider
    {
        public IActivator<T> GetActivator<T>() => new TestActivator<T>();
    }

    private sealed class TestActivator<T> : IActivator<T>
    {
        public T Create() => Activator.CreateInstance<T>();
    }

    private sealed class LegacyGrainStorage : IGrainStorage
    {
        public int CallCount { get; private set; }

        public StorageOperation LastOperation { get; private set; }

        public string LastStateName { get; private set; } = null!;

        public GrainId LastGrainId { get; private set; }

        public object LastGrainState { get; private set; } = null!;

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => Record(StorageOperation.Read, stateName, grainId, grainState);

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => Record(StorageOperation.Write, stateName, grainId, grainState);

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => Record(StorageOperation.Clear, stateName, grainId, grainState);

        private Task Record<T>(
            StorageOperation operation,
            string stateName,
            GrainId grainId,
            IGrainState<T> grainState)
        {
            CallCount++;
            LastOperation = operation;
            LastStateName = stateName;
            LastGrainId = grainId;
            LastGrainState = grainState;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingGrainStorage : IGrainStorage
    {
        public int CallCount { get; private set; }

        public StorageOperation LastOperation { get; private set; }

        public string LastStateName { get; private set; } = null!;

        public GrainId LastGrainId { get; private set; }

        public object LastGrainState { get; private set; } = null!;

        public bool UsedCancellationOverload { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public OperationCanceledException? ExceptionToThrow { get; set; }

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => Record(StorageOperation.Read, stateName, grainId, grainState, false, CancellationToken.None);

        public Task ReadStateAsync<T>(
            string stateName,
            GrainId grainId,
            IGrainState<T> grainState,
            CancellationToken cancellationToken)
            => Record(StorageOperation.Read, stateName, grainId, grainState, true, cancellationToken);

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => Record(StorageOperation.Write, stateName, grainId, grainState, false, CancellationToken.None);

        public Task WriteStateAsync<T>(
            string stateName,
            GrainId grainId,
            IGrainState<T> grainState,
            CancellationToken cancellationToken)
            => Record(StorageOperation.Write, stateName, grainId, grainState, true, cancellationToken);

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => Record(StorageOperation.Clear, stateName, grainId, grainState, false, CancellationToken.None);

        public Task ClearStateAsync<T>(
            string stateName,
            GrainId grainId,
            IGrainState<T> grainState,
            CancellationToken cancellationToken)
            => Record(StorageOperation.Clear, stateName, grainId, grainState, true, cancellationToken);

        private Task Record<T>(
            StorageOperation operation,
            string stateName,
            GrainId grainId,
            IGrainState<T> grainState,
            bool usedCancellationOverload,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastOperation = operation;
            LastStateName = stateName;
            LastGrainId = grainId;
            LastGrainState = grainState;
            UsedCancellationOverload = usedCancellationOverload;
            LastCancellationToken = cancellationToken;
            return ExceptionToThrow is { } exception
                ? Task.FromException(exception)
                : Task.CompletedTask;
        }
    }

    private sealed class TestGrainContext : IGrainContext, IDisposable
    {
        private ServiceProvider _activationServices = null!;

        private TestGrainContext()
        {
        }

        public static TestGrainContext Create()
        {
            var context = new TestGrainContext();
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddLogging();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<SchedulerInstruments>();
            services.AddSingleton<StorageInstruments>();
            services.AddSingleton<IActivatorProvider, TestActivatorProvider>();
            services.AddSingleton<StateStorageBridgeSharedMap>();
            services.Configure<SchedulingOptions>(options =>
            {
                options.DelayWarningThreshold = TimeSpan.FromMilliseconds(100);
                options.ActivationSchedulingQuantum = TimeSpan.FromMilliseconds(100);
                options.TurnWarningLengthThreshold = TimeSpan.FromMilliseconds(100);
                options.StoppedActivationWarningInterval = TimeSpan.FromMilliseconds(200);
            });

            context._activationServices = services.BuildServiceProvider();
            var loggerFactory = context._activationServices.GetRequiredService<ILoggerFactory>();
            context.ObservableLifecycle = new GrainLifecycle(loggerFactory.CreateLogger<GrainLifecycle>());
            context.WorkItemGroup = new WorkItemGroup(
                context,
                context._activationServices.GetRequiredService<IOptions<SchedulingOptions>>(),
                context._activationServices.GetRequiredService<SchedulerInstruments>());

            return context;
        }

        public WorkItemGroup WorkItemGroup { get; private set; } = null!;

        public GrainReference GrainReference => throw new NotImplementedException();

        public GrainId GrainId { get; } = GrainId.Create(
            "state-storage-bridge-test",
            Guid.NewGuid().ToString("N"));

        public IAddressable GrainInstance => throw new NotImplementedException();

        public ActivationId ActivationId => throw new NotImplementedException();

        public GrainAddress Address => throw new NotImplementedException();

        public IServiceProvider ActivationServices => _activationServices;

        public IDictionary<object, object> Items { get; } = new Dictionary<object, object>();

        public IGrainLifecycle ObservableLifecycle { get; private set; } = null!;

        public IWorkItemScheduler Scheduler => WorkItemGroup;

        public bool IsExemptFromCollection => false;

        public PlacementStrategy PlacementStrategy => throw new NotImplementedException();

        object IGrainContext.GrainInstance => throw new NotImplementedException();

        public void Activate(
            Dictionary<string, object>? requestContext,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public void Deactivate(
            DeactivationReason deactivationReason,
            CancellationToken cancellationToken)
        {
        }

        public Task Deactivated => Task.CompletedTask;

        public void Dispose()
        {
            (Scheduler as IDisposable)?.Dispose();
            _activationServices.Dispose();
        }

        public object GetComponent(Type componentType) => throw new NotImplementedException();

        public object GetTarget() => throw new NotImplementedException();

        public void ReceiveMessage(object message) => throw new NotImplementedException();

        public void SetComponent<TComponent>(TComponent? value) where TComponent : class
            => throw new NotImplementedException();

        bool IEquatable<IGrainContext>.Equals(IGrainContext? other) => ReferenceEquals(this, other);

        void IGrainContext.Rehydrate(IRehydrationContext context) => throw new NotImplementedException();

        void IGrainContext.Migrate(
            Dictionary<string, object>? requestContext,
            CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
