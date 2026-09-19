using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxCancellationCleanupTests : DurableMessagingBehaviorTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HandlerCancellation_CallbackFailurePreservesCleanupAndOriginalCause(bool throws, bool terminalFailure)
    {
        var receiver = NewGrain();
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var extension = context.ActivationServices.GetRequiredService(CancellationCleanupProbe.ExtensionType);
        var shutdown = CancellationCleanupProbe.Field<CancellationTokenSource>(extension, "_shutdownCts");
        var token = shutdown.Token;
        using var logs = new CancellationCleanupProbe.Logs();
        Fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        using var handler = new CancelingHandler(throws);
        await OnTurnAsync(context, () => context.ActivationServices.GetRequiredService<IDurableInbox>().RegisterHandler("cancel-cleanup", handler));
        using var envelope = CreateEnvelope(receiver, NewMessage(310, "cancellation"), "cancel-cleanup");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId()));
        var firstFailure = new IOException("Original terminal journal failure.");
        using var seeded = await OnTurnAsync(context, () => CancellationCleanupProbe.SeedResults(extension));
        await OnTurnAsync(context, () =>
        {
            Assert.True(CancellationCleanupProbe.CoordinatorIsActive(extension));
            if (terminalFailure) CancellationCleanupProbe.ExtensionType.GetMethod("OnFaulted")!.Invoke(extension, [firstFailure]);
            var stop = ((ILifecycleObserver)extension).OnStop(CancellationToken.None);
            Assert.True(stop.IsCompletedSuccessfully);
            CancellationCleanupProbe.AssertClean(extension, seeded);
            Assert.True(shutdown.Token.IsCancellationRequested);
            Assert.Equal(1, handler.CallbackCalls);
            logs.AssertCallback(throws, handler.CallbackFailure);
            if (terminalFailure) Assert.Same(firstFailure, CancellationCleanupProbe.Field<ExceptionDispatchInfo>(extension, "_failure").SourceException);
            else Assert.Null(CancellationCleanupProbe.Field<ExceptionDispatchInfo?>(extension, "_failure"));
        });
        Assert.True(token.IsCancellationRequested);
        Assert.False(handler.Finished);
        handler.Release.TrySetResult();
        var failure = await grain.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (terminalFailure) Assert.Same(firstFailure, failure);
        else Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.DoesNotContain(handler.CallbackFailure, failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [failure]);
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(handler.Finished);
        Assert.Throws<ObjectDisposedException>(() => shutdown.Token);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        Assert.Equal(1, CancellationCleanupProbe.Field<SemaphoreSlim>(extension, "_gate").CurrentCount);
        Assert.Empty(CancellationCleanupProbe.Field<IList>(extension, "_pendingWrites"));
        ((IDisposable)extension).Dispose();
        Assert.Equal(1, handler.CallbackCalls);
        logs.AssertCallback(throws, handler.CallbackFailure);
        await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Empty(recovered.Effects);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Equal(envelope.Value.MessageId, Assert.Single(recovered.InboxDeadLetters).MessageId);
    }

    private sealed class CancelingHandler(bool throws) : IInboxHandler, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception CallbackFailure { get; } = new InvalidOperationException("Application cancellation callback failed.");
        public int CallbackCalls { get; private set; }
        public bool Finished { get; private set; }
        public bool CanHandle(IInboxHandlerContext context) => true;
        public async ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                CallbackCalls++;
                if (throws) throw CallbackFailure;
            });
            Entered.TrySetResult();
            try
            {
                await Release.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return () => throw new InvalidOperationException("A canceled handler must never apply.");
            }
            finally { Finished = true; }
        }
        public void Dispose() => Release.TrySetResult();
    }

    private static Task OnTurnAsync(IGrainContext context, Action action) => OnTurnAsync(context, () => { action(); return true; });
    private static Task<T> OnTurnAsync<T>(IGrainContext context, Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { done.SetResult(action()); }
            catch (Exception exception) { done.SetException(exception); }
        });
        return done.Task;
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxCancellationCleanupContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispose_CallbackFailureStillReleasesOwnedResourcesOnce(bool throws)
    {
        using var probe = new CancellationCleanupProbe(throws);
        ((IDisposable)probe.Extension).Dispose();
        probe.AssertClean();
        Assert.Throws<ObjectDisposedException>(() => probe.Shutdown.Token);
        ((IDisposable)probe.Extension).Dispose();
        probe.AssertClean();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnStop_CallbackFailureStillDrainsOwnedDelivery(bool throws)
    {
        using var probe = new CancellationCleanupProbe(throws);
        var owned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationCleanupProbe.SetField(probe.Extension, "_activeDelivery", owned.Task);
        var stopping = ((ILifecycleObserver)probe.Extension).OnStop(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        probe.AssertClean();
        Assert.True(probe.Shutdown.Token.CanBeCanceled);
        owned.SetException(new IOException("Already observed delivery failure."));
        await stopping;
        ((IDisposable)probe.Extension).Dispose();
        Assert.Throws<ObjectDisposedException>(() => probe.Shutdown.Token);
        probe.AssertClean();
    }
}

internal sealed class CancellationCleanupProbe : IDisposable
{
    internal static readonly Type ExtensionType = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
    private readonly CancellationTokenSource _handlerCancellation;
    private readonly CancellationTokenRegistration _registration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly bool _throws;
    private readonly Seeded _seeded;
    private readonly Logs _logs = new();
    private readonly Exception _callbackFailure = new InvalidOperationException("Linked handler cancellation failed.");
    private int _calls;
    public object Extension { get; } = RuntimeHelpers.GetUninitializedObject(ExtensionType);
    public CancellationTokenSource Shutdown { get; } = new();
    public CancellationCleanupProbe(bool throws)
    {
        _throws = throws;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        SetField(Extension, "_logger", Activator.CreateInstance(typeof(Logger<>).MakeGenericType(ExtensionType), _loggerFactory)!);
        SetField(Extension, "_shutdownCts", Shutdown);
        SetField(Extension, "_shutdownToken", Shutdown.Token);
        SetField(Extension, "_pumpCoordinator", Activator.CreateInstance(ReceiverTestServices.GetImplementationType("DurableMessagingPumpCoordinator"))!);
        SetField(Extension, "_pumpResults", Activator.CreateInstance(ReceiverTestServices.GetImplementationType("DurableMessagingPumpResults"), nonPublic: true)!);
        var instruments = ReceiverTestServices.GetImplementationType("DurableMessagingInstruments")
            .GetMethod("CreateForDirectConstruction", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        SetField(Extension, "_instruments", instruments);
        instruments.GetType().GetMethod("OnInboxDepthChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instruments, [1]);
        SetField(Extension, "_metricsActive", 1);
        SetField(Extension, "_reportedDepth", 1);
        SetField(Extension, "_activeDelivery", Task.CompletedTask);
        var coordinator = Field<object>(Extension, "_pumpCoordinator");
        object?[] arguments = ["owner", Shutdown.Token, null];
        Assert.True((bool)coordinator.GetType().GetMethod("TryAcquire")!.Invoke(coordinator, arguments)!);
        _seeded = SeedResults(Extension);
        _handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(Shutdown.Token);
        _registration = _handlerCancellation.Token.Register(() => { _calls++; if (_throws) throw _callbackFailure; });
    }
    public void AssertClean()
    {
        Assert.True(Shutdown.IsCancellationRequested);
        Assert.True(_handlerCancellation.IsCancellationRequested);
        Assert.Equal(1, _calls);
        AssertClean(Extension, _seeded);
        _logs.AssertCallback(_throws, _callbackFailure);
    }
    public static T Field<T>(object instance, string name) => (T)ExtensionType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    public static void SetField(object instance, string name, object value) => ExtensionType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    public static bool CoordinatorIsActive(object extension)
    {
        var coordinator = Field<object>(extension, "_pumpCoordinator");
        return (bool)coordinator.GetType().GetProperty("IsActive")!.GetValue(coordinator)!;
    }
    public static Seeded SeedResults(object extension)
    {
        var results = Field<object>(extension, "_pumpResults");
        var keyType = ReceiverTestServices.GetImplementationType("DurableMessagingPumpExecutionKey");
        var entries = (IDictionary)results.GetType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(results)!;
        object? captured = null;
        var cancellation = new CancellationTokenSource();
        foreach (var name in new[] { ReceiverTestServices.InboxJobName, "test/other-pump" })
        {
            var key = Activator.CreateInstance(keyType, name, "cleanup", "run", 1L)!;
            object?[] arguments = [key, cancellation.Token, null];
            Assert.True((bool)results.GetType().GetMethod("TryStart")!.Invoke(results, arguments)!);
            if (name == ReceiverTestServices.InboxJobName) captured = entries[key];
        }
        var registration = (CancellationTokenRegistration)captured!.GetType().GetProperty("CancellationRegistration")!.GetValue(captured)!;
        Assert.NotEqual(default, registration);
        return new Seeded(cancellation, captured);
    }
    public static void AssertClean(object extension, Seeded seeded)
    {
        Assert.False(CoordinatorIsActive(extension));
        Assert.Equal(0, Field<int>(extension, "_metricsActive"));
        Assert.Equal(0, Field<int>(extension, "_reportedDepth"));
        var instruments = Field<object>(extension, "_instruments");
        var depth = instruments.GetType().GetField("_inboxDepth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instruments)!;
        Assert.Equal(0L, depth.GetType().GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(depth));
        var results = Field<object>(extension, "_pumpResults");
        var entries = (IDictionary)results.GetType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(results)!;
        var key = Assert.Single(entries.Keys.Cast<object>());
        Assert.Equal("test/other-pump", key.GetType().GetProperty("JobName")!.GetValue(key));
        Assert.Equal(default(CancellationTokenRegistration), seeded.Entry.GetType().GetProperty("CancellationRegistration")!.GetValue(seeded.Entry));
    }
    public void Dispose()
    {
        _seeded.Dispose();
        _registration.Dispose();
        _handlerCancellation.Dispose();
        Shutdown.Dispose();
        _loggerFactory.Dispose();
    }
    internal sealed record Seeded(CancellationTokenSource Cancellation, object Entry) : IDisposable
    {
        public void Dispose() => Cancellation.Dispose();
    }

    internal sealed class Logs : ILoggerProvider
    {
        private readonly ConcurrentQueue<Exception> _exceptions = new();
        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
        public void Dispose() { }
        public void AssertCallback(bool throws, Exception failure)
        {
            if (throws) Assert.Same(failure, Assert.Single(Assert.IsType<AggregateException>(Assert.Single(_exceptions)).Flatten().InnerExceptions));
            else Assert.Empty(_exceptions);
        }
        private sealed class Sink(Logs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "Orleans.DurableMessaging.DurableInboxExtension" && id.Name == "LogCancellationCallbackFailure" && exception is not null)
                    owner._exceptions.Enqueue(exception);
            }
        }
    }
}
