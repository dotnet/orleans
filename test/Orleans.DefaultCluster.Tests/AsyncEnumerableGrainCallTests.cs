#nullable enable
using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;

namespace DefaultCluster.Tests;

public static class AsyncEnumerableGrainCallTestCollection
{
    public const string Name = nameof(AsyncEnumerableGrainCallTests);
}

[CollectionDefinition(AsyncEnumerableGrainCallTestCollection.Name)]
public sealed class AsyncEnumerableGrainCallTestCollectionDefinition : ICollectionFixture<AsyncEnumerableTestFixture>;

/// <summary>
/// Test fixture which uses a fake silo time provider so cleanup timers can be advanced deterministically.
/// </summary>
public sealed class AsyncEnumerableTestFixture : BaseInProcessTestClusterFixture
{
    private readonly TrackingFakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder
                .Configure<SiloMessagingOptions>(o => o.ClientGatewayShutdownNotificationTimeout = default)
                .UseInMemoryReminderService()
                .UseInMemoryDurableJobs()
                .AddMemoryGrainStorageAsDefault()
                .AddMemoryGrainStorage("MemoryStore");

            siloBuilder.UseFakeTimeProviderForGrainTimers(_timeProvider);
        });

        builder.ConfigureClient(clientBuilder => clientBuilder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_timeProvider)));
    }

    public Task AdvanceTimeByResponseTimeoutAsync() =>
        FakeTimeSilo.AdvanceAsync(_timeProvider, HostedCluster.GetSiloServiceProvider().GetRequiredService<IOptions<SiloMessagingOptions>>().Value.ResponseTimeout);

    public int GetTimerChangeCount(object timer) => _timeProvider.GetTimerChangeCount(timer);

    public Task WaitForTimerChangeAsync(object timer, int changeCount, CancellationToken cancellationToken) =>
        _timeProvider.WaitForTimerChangeAsync(timer, changeCount, cancellationToken);
}

/// <summary>
/// Diagnostic listener for monitoring AsyncEnumerableGrainExtension (for both grains and observers) behavior during tests.
/// This helper class allows tests to observe internal cleanup operations and verify
/// that enumerators are properly managed according to their lifecycle requirements.
/// </summary>
public sealed class AsyncEnumerableExtensionListener : IObserver<KeyValuePair<string, object?>>, IObserver<DiagnosticListener>, IDisposable
{
    private readonly object _lock = new();
    private readonly string _diagnosticListenerName;
    private readonly IDisposable _allListenersSubscription;
    private readonly Func<object?, bool> _isTargetExtension;

    private readonly TaskCompletionSource _extensionCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _extensionDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IDisposable? _instanceSubscription;
    private TaskCompletionSource? _cleanupCompleted;
    private object? _timer;
    private int _cleanupCount;

    public AsyncEnumerableExtensionListener(string diagnosticListenerName, Func<object?, bool> isTargetExtension)
    {
        _diagnosticListenerName = diagnosticListenerName;
        _isTargetExtension = isTargetExtension;
        _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public int CleanupCount
    {
        get
        {
            lock (_lock)
            {
                return _cleanupCount;
            }
        }
    }

    public object Timer
    {
        get
        {
            lock (_lock)
            {
                return _timer ?? throw new InvalidOperationException("The grain async enumerable extension has not been created.");
            }
        }
    }

    public Task WaitForExtensionCreatedAsync(CancellationToken cancellationToken) =>
        _extensionCreated.Task.WaitAsync(cancellationToken);

    public Task WaitForExtensionDisposedAsync(CancellationToken cancellationToken) =>
        _extensionDisposed.Task.WaitAsync(cancellationToken);

    public async Task WaitForCleanupCountAsync(int cleanupCount, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task cleanupCompletedTask;

            lock (_lock)
            {
                if (_cleanupCount >= cleanupCount)
                {
                    return;
                }

                _cleanupCompleted ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                cleanupCompletedTask = _cleanupCompleted.Task;
            }

            await cleanupCompletedTask.WaitAsync(cancellationToken);
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (!_isTargetExtension(value.Value))
        {
            return;
        }

        // Grain tests inspect IGrainTimer re-arming, but the observer version cleanup uses a PeriodicTimer,
        // so its tests advance the TimeProvider and wait for cleanup events instead.
        if (value.Value is AsyncEnumerableGrainExtension extension)
        {
            lock (_lock)
            {
                _timer ??= extension.Timer;
            }
        }

        switch (value.Key)
        {
            case "OnAsyncEnumeratorGrainExtensionCreated" or "OnAsyncEnumeratorGrainObserverExtensionCreated":
                _extensionCreated.TrySetResult();
                break;
            case "OnEnumeratorCleanupCompleted" or "OnObserverEnumeratorCleanupCompleted":
                {
                    TaskCompletionSource? cleanupCompleted;

                    lock (_lock)
                    {
                        ++_cleanupCount;

                        cleanupCompleted = _cleanupCompleted;

                        _cleanupCompleted = null;
                    }

                    cleanupCompleted?.TrySetResult();
                }
                break;
            case "OnAsyncEnumerableGrainObserverExtensionDisposed":
                _extensionDisposed.TrySetResult();
                break;
        }
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
    {
        if (value.Name == _diagnosticListenerName)
        {
            _instanceSubscription = value.Subscribe(this);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() => _instanceSubscription?.Dispose();
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

    public void Dispose()
    {
        _allListenersSubscription.Dispose();
        _instanceSubscription?.Dispose();
    }
}

/// <summary>
/// Tests support for grain methods which return <see cref="IAsyncEnumerable{T}"/>.
/// These tests verify Orleans' ability to handle streaming results from grain methods,
/// including batching, error handling, cancellation, and proper resource cleanup.
/// Orleans uses a grain extension mechanism to manage the lifecycle of async enumerators
/// across the distributed system.
/// </summary>
[Collection(AsyncEnumerableGrainCallTestCollection.Name)]
public class AsyncEnumerableGrainCallTests(AsyncEnumerableTestFixture fixture)
{
    private IGrainFactory GrainFactory => fixture.GrainFactory;
    private ILogger Logger => fixture.Logger;

    /// <summary>
    /// Tests basic async enumerable functionality where a grain produces values that are consumed by the client.
    /// Verifies that values are correctly transmitted and the enumerator is properly disposed after use.
    /// This demonstrates Orleans' support for streaming data from grains without keeping all data in memory.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await Task.Delay(200, cancellationToken);
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        }, cancellationToken);

        var values = new List<string>();
        await foreach (var entry in grain.GetValues(cancellationToken).WithCancellation(cancellationToken))
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(5, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests preemptive cancellation of async enumerable streams before any values are yielded.
    /// Verifies that when a CancellationToken is cancelled before the enumeration starts yielding values,
    /// the operation properly throws OperationCanceledException and no values are returned.
    /// This test ensures Orleans handles early cancellation gracefully in distributed streaming scenarios.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_CancelBeforeYield()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        // Set up cancellation tokens - one for test timeout, one for the grain call
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(35));
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var callId = Guid.NewGuid();

        // Task to cancel the enumeration after it starts but before it yields
        var enumerationStartedTask = Task.Run(async () =>
        {
            await grain.WaitForCall(callId); // Wait for enumeration to begin
            callCts.Cancel(); // Cancel before any values are yielded
        }, cancellationToken);

        try
        {
            // Start enumeration with a slow grain method and un-cancelled token
            await foreach (var entry in grain.SleepyEnumerable(callId, TimeSpan.FromSeconds(25), callCts.Token))
            {
                Assert.Fail("Should have thrown due to cancellation before yielding any values.");
            }

            Assert.Fail("Enumeration should not have completed without an exception.");
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Verify the cancellation token was indeed cancelled
            Assert.True(callCts.Token.IsCancellationRequested);
        }

        await enumerationStartedTask;

        // Wait for the grain to record the cancellation
        while (true)
        {
            var canceledCalls = await grain.GetCanceledCalls();
            if (canceledCalls.Contains(callId))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            if (timeoutCts.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Fail("Test timed out waiting for cancellation to be recorded.");
            }
        }
    }

    /// <summary>
    /// Tests error handling in async enumerable streams when an exception is thrown during enumeration.
    /// Verifies that exceptions are properly propagated to the client and resources are cleaned up.
    /// The errorIndex parameter determines when the error occurs, testing both immediate and delayed errors.
    /// The waitAfterYield parameter tests error handling with and without async delays after yielding values.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task ObservableGrain_AsyncEnumerable_Throws(int errorIndex, bool waitAfterYield)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string ErrorMessage = "This is my error!";
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        try
        {
            await foreach (var entry in grain.GetValuesWithError(errorIndex, waitAfterYield, ErrorMessage, cancellationToken).WithBatchSize(10).WithCancellation(cancellationToken))
            {
                values.Add(entry);
                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }
        }
        catch (InvalidOperationException iox)
        {
            Assert.Equal(ErrorMessage, iox.Message);
        }

        Assert.Equal(errorIndex, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests cancellation handling in async enumerable streams when the grain cancels the enumeration.
    /// Verifies that OperationCanceledException is properly propagated and resources are cleaned up.
    /// This tests Orleans' ability to handle cooperative cancellation in distributed streaming scenarios.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task ObservableGrain_AsyncEnumerable_Cancellation(int errorIndex, bool waitAfterYield)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // This special error message is interpreted to indicate that cancellation
        // should occur when the index is reached.
        const string ErrorMessage = "cancel";
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        try
        {
            await foreach (var entry in grain.GetValuesWithError(errorIndex, waitAfterYield, ErrorMessage, cancellationToken).WithBatchSize(10).WithCancellation(cancellationToken))
            {
                values.Add(entry);
                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }
        }
        catch (OperationCanceledException oce)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedMessage = new OperationCanceledException().Message;
            Assert.Equal(expectedMessage, oce.Message);
        }

        Assert.Equal(errorIndex, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests client-side cancellation of async enumerable streams using CancellationToken.
    /// Verifies that cancellation requests from the client are properly handled, including:
    /// - Preemptive cancellation (before enumeration starts)
    /// - Mid-stream cancellation (during enumeration)
    /// - Proper cleanup and disposal of server-side resources
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task ObservableGrain_AsyncEnumerable_CancellationToken(int errorIndex, bool waitAfterYield)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string ErrorMessage = "Throwing!";
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (errorIndex == 0)
            {
                cts.Cancel();
            }

            await foreach (var entry in grain.GetValuesWithError(int.MaxValue, waitAfterYield, ErrorMessage, cts.Token).WithBatchSize(1))
            {
                values.Add(entry);
                if (values.Count == errorIndex)
                {
                    cts.Cancel();
                }

                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }
        }
        catch (OperationCanceledException oce)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedMessage = new OperationCanceledException().Message;
            Assert.Equal(expectedMessage, oce.Message);
        }

        Assert.Equal(errorIndex, values.Count);

        if (errorIndex == 0)
        {
            // Check that the enumerator was not disposed since it was cancelled preemptively and therefore no call should have been made.
            var grainCalls = await grain.GetIncomingCalls();
            Assert.DoesNotContain(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration)));
            Assert.DoesNotContain(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.DisposeAsync)));
        }
        if (errorIndex > 0)
        {
            // Check that the enumerator is disposed, but only if it was not cancelled preemptively.
            var grainCalls = await grain.GetIncomingCalls();
            Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration)));
            Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.DisposeAsync)));
        }
    }

    /// <summary>
    /// Tests client-side cancellation using the WithCancellation extension method.
    /// Similar to CancellationToken test but uses the extension method approach for cancellation.
    /// Verifies that the WithCancellation extension properly integrates with Orleans' async enumerable support.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task ObservableGrain_AsyncEnumerable_CancellationToken_WithCancellationExtension(int errorIndex, bool waitAfterYield)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string ErrorMessage = "Throwing!";
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (errorIndex == 0)
            {
                cts.Cancel();
            }

            await foreach (var entry in grain.GetValuesWithError(int.MaxValue, waitAfterYield, ErrorMessage, cancellationToken).WithBatchSize(1).WithCancellation(cts.Token))
            {
                values.Add(entry);
                if (values.Count == errorIndex)
                {
                    cts.Cancel();
                }

                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }
        }
        catch (OperationCanceledException oce)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedMessage = new OperationCanceledException().Message;
            Assert.Equal(expectedMessage, oce.Message);
        }

        Assert.Equal(errorIndex, values.Count);

        if (errorIndex == 0)
        {
            // Check that the enumerator was not disposed since it was cancelled preemptively and therefore no call should have been made.
            var grainCalls = await grain.GetIncomingCalls();
            Assert.DoesNotContain(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration)));
            Assert.DoesNotContain(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.DisposeAsync)));
        }
        if (errorIndex > 0)
        {
            // Check that the enumerator is disposed, but only if it was not cancelled preemptively.
            var grainCalls = await grain.GetIncomingCalls();
            Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration)));
            Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.DisposeAsync)));
        }
    }

    /// <summary>
    /// Tests batching optimization for async enumerable streams.
    /// Verifies that Orleans automatically batches multiple values to reduce network round-trips.
    /// This optimization is crucial for performance when streaming many small values.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Batch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        foreach (var value in Enumerable.Range(0, 50))
        {
            await grain.OnNext(value.ToString());
        }

        await grain.Complete();

        var values = new List<string>();
        await foreach (var entry in grain.GetValues(cancellationToken).WithCancellation(cancellationToken))
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(50, values.Count);

        var grainCalls = await grain.GetIncomingCalls();
        var moveNextCallCount = grainCalls.Count(element =>
            element.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension))
            && (element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext)) || element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration))));
        Assert.True(moveNextCallCount < values.Count);

        // Check that the enumerator is disposed
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests custom batch size configuration for async enumerable streams.
    /// Verifies that the WithBatchSize extension method correctly controls the number of items per batch.
    /// This allows clients to tune the trade-off between latency and throughput.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_SplitBatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        foreach (var value in Enumerable.Range(0, 50))
        {
            await grain.OnNext(value.ToString());
        }

        await grain.Complete();

        var values = new List<string>();
        await foreach (var entry in grain.GetValues(cancellationToken).WithBatchSize(25).WithCancellation(cancellationToken))
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(50, values.Count);

        var grainCalls = await grain.GetIncomingCalls();
        var moveNextCallCount = grainCalls.Count(element =>
            element.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension))
            && (element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext)) || element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration))));
        Assert.True(moveNextCallCount < values.Count);

        // Check that the enumerator is disposed
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests disabling batching by setting batch size to 1.
    /// Verifies that each value results in a separate network call when batching is disabled.
    /// This mode provides lowest latency but highest overhead for streaming scenarios.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_NoBatching()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        foreach (var value in Enumerable.Range(0, 50))
        {
            await grain.OnNext(value.ToString());
        }

        await grain.Complete();

        var values = new List<string>();
        await foreach (var entry in grain.GetValues(cancellationToken).WithBatchSize(1).WithCancellation(cancellationToken))
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(50, values.Count);

        var grainCalls = await grain.GetIncomingCalls();
        var moveNextCallCount = grainCalls.Count(element =>
            element.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension))
            && (element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext)) || element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration))));

        // One call for every value and one final call to complete the enumeration
        Assert.Equal(values.Count + 1, moveNextCallCount);

        // Check that the enumerator is disposed
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests cancellation during active enumeration.
    /// Verifies that cancelling mid-stream properly stops enumeration and cleans up resources.
    /// This simulates real-world scenarios where clients need to stop consuming data early.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_WithCancellation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await Task.Delay(200, cancellationToken);
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        }, cancellationToken);

        var values = new List<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await foreach (var entry in grain.GetValues(cancellationToken).WithCancellation(cts.Token))
            {
                values.Add(entry);
                if (values.Count == 3)
                {
                    cts.Cancel();
                }

                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }

            Assert.Fail("Expected an exception to be thrown");
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Expected
        }

        Assert.Equal(3, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests async enumerable behavior with a slow-producing grain.
    /// Verifies that the client can stop consuming before all values are produced.
    /// This tests Orleans' ability to handle backpressure and early termination in streaming scenarios.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_SlowProducer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await Task.Delay(2000, cancellationToken);
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        }, cancellationToken);

        var values = new List<string>();
        await foreach (var entry in grain.GetValues(cancellationToken).WithCancellation(cancellationToken))
        {
            values.Add(entry);
            if (values.Count == 2)
            {
                break;
            }

            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(2, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests async enumerable behavior with a slow-consuming client.
    /// Verifies that the enumerator is not prematurely cleaned up when the client consumes slowly.
    /// Uses diagnostic listeners to monitor the cleanup timer behavior.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_SlowConsumer()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());
        using var listener = CreateListener(grain.GetGrainId());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 3))
            {
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        }, cts.Token);

        var values = new List<string>();
        await foreach (var entry in grain.GetValues(cts.Token).WithBatchSize(1).WithCancellation(cts.Token))
        {
            values.Add(entry);

            await AdvanceToNextCleanupAsync(listener, cts.Token);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(3, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests enumerator eviction when a client consumes too slowly.
    /// Verifies that Orleans properly cleans up abandoned enumerators after a timeout period.
    /// This prevents resource leaks when clients fail to complete enumeration.
    /// The test ensures proper error handling when trying to continue after eviction.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_SlowConsumer_Evicted()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());
        using var listener = CreateListener(grain.GetGrainId());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        }, cts.Token);

        var values = new List<string>();
        try
        {
            await foreach (var entry in grain.GetValues(cts.Token).WithBatchSize(1).WithCancellation(cts.Token))
            {
                values.Add(entry);

                if (values.Count == 3)
                {
                    await AdvanceToNextCleanupAsync(listener, cts.Token);
                    await AdvanceToNextCleanupAsync(listener, cts.Token);
                }

                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }

            Assert.Fail("Expected an exception to be thrown");
        }
        catch (EnumerationAbortedException ex)
        {
            Assert.Contains("the remote target does not have a record of this enumerator", ex.Message);
        }

        Assert.Equal(3, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests async enumerable behavior when the grain is deactivated during enumeration.
    /// Verifies that grain deactivation properly terminates active enumerations with an appropriate error.
    /// This ensures clean shutdown and prevents hanging clients when grains are deactivated.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Deactivate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());
        var valuesObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 2))
            {
                await grain.OnNext(value.ToString());
            }

            await valuesObserved.Task.WaitAsync(cancellationToken);
            await grain.Deactivate();
        }, cancellationToken);

        var values = new List<string>();
        await Assert.ThrowsAsync<EnumerationAbortedException>(async () =>
        {
            try
            {
                await foreach (var entry in grain.GetValues(cancellationToken))
                {
                    values.Add(entry);
                    if (values.Count == 2)
                    {
                        valuesObserved.TrySetResult();
                    }

                    Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
                }
            }
            finally
            {
                if (values.Count < 2)
                {
                    valuesObserved.TrySetException(
                        new InvalidOperationException($"Enumeration ended after observing {values.Count} of 2 values."));
                }
            }
        }).WaitAsync(cancellationToken);

        await producer.WaitAsync(cancellationToken);
        Assert.Equal(2, values.Count);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Fail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        foreach (var value in Enumerable.Range(0, 3))
        {
            await grain.OnNext(value.ToString());
        }

        await grain.Fail();

        var values = new List<string>();
        var exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var entry in grain.GetValues(cancellationToken).WithCancellation(cancellationToken))
            {
                values.Add(entry);
            }
        });

        Assert.Equal(3, values.Count);
        Assert.NotNull(exception);
        Assert.Equal("I've failed you!", exception.Message);

        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    private static AsyncEnumerableExtensionListener CreateListener(GrainId grainId) =>
        new AsyncEnumerableExtensionListener(
            diagnosticListenerName: "Orleans.Runtime.AsyncEnumerableGrainExtension",
            isTargetExtension: value => value is AsyncEnumerableGrainExtension { GrainContext.GrainId: var id } && id == grainId);

    private async Task AdvanceToNextCleanupAsync(AsyncEnumerableExtensionListener listener, CancellationToken cancellationToken)
    {
        var cleanupCount = listener.CleanupCount + 1;
        var timer = listener.Timer;
        var timerChangeCount = fixture.GetTimerChangeCount(timer);
        await fixture.AdvanceTimeByResponseTimeoutAsync().WaitAsync(cancellationToken);
        await listener.WaitForCleanupCountAsync(cleanupCount, cancellationToken);

        // Cleanup is reported from inside the callback, before the one-shot timer is rearmed.
        await fixture.WaitForTimerChangeAsync(timer, timerChangeCount + 1, cancellationToken);
    }
}

/// <summary>
/// Tests support for observer methods which return <see cref="IAsyncEnumerable{T}"/>.
/// </summary>
[Collection(AsyncEnumerableGrainCallTestCollection.Name)]
public class AsyncEnumerableObserverCallTests(AsyncEnumerableTestFixture fixture)
{
    private IGrainFactory GrainFactory => fixture.GrainFactory;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable()
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timeoutCts.CancelAfter(TestTimeout);

            var producer = Task.Run(async () =>
            {
                foreach (var value in Enumerable.Range(0, 5))
                {
                    await Task.Delay(20, timeoutCts.Token);
                    await observerRef.OnNext(value.ToString());
                }

                await observerRef.Complete();
            }, timeoutCts.Token);

            var values = new List<string>();
            await foreach (var value in observerRef.GetValues(timeoutCts.Token).WithCancellation(timeoutCts.Token))
            {
                values.Add(value);
            }

            await producer.WaitAsync(timeoutCts.Token);
            Assert.Equal(new[] { "0", "1", "2", "3", "4" }, values);

            var calls = await observerRef.GetIncomingCalls();
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_Batch()
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            foreach (var value in Enumerable.Range(0, 50))
            {
                await observerRef.OnNext(value.ToString());
            }

            await observerRef.Complete();

            var values = new List<string>();
            await foreach (var value in observerRef.GetValues(cancellationToken).WithCancellation(cancellationToken))
            {
                values.Add(value);
            }

            Assert.Equal(50, values.Count);

            var calls = await observerRef.GetIncomingCalls();
            var pullCount = CountPullCalls(calls);

            Assert.True(pullCount < values.Count, $"Expected batching to reduce pull calls, but observed {pullCount} pulls for {values.Count} values.");
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_SplitBatch()
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            foreach (var value in Enumerable.Range(0, 50))
            {
                await observerRef.OnNext(value.ToString());
            }

            await observerRef.Complete();

            var values = new List<string>();
            await foreach (var value in observerRef.GetValues(cancellationToken).WithBatchSize(25).WithCancellation(cancellationToken))
            {
                values.Add(value);
            }

            Assert.Equal(50, values.Count);

            var calls = await observerRef.GetIncomingCalls();
            Assert.True(CountPullCalls(calls) < values.Count);
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_NoBatching()
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            foreach (var value in Enumerable.Range(0, 50))
            {
                await observerRef.OnNext(value.ToString());
            }

            await observerRef.Complete();

            var values = new List<string>();
            await foreach (var value in observerRef.GetValues(cancellationToken).WithBatchSize(1).WithCancellation(cancellationToken))
            {
                values.Add(value);
            }

            Assert.Equal(50, values.Count);

            var calls = await observerRef.GetIncomingCalls();
            Assert.Equal(values.Count + 1, CountPullCalls(calls));
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task Observer_AsyncEnumerable_Throws(int errorIndex, bool waitAfterYield)
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            const string ErrorMessage = "This is my error!";

            var values = new List<int>();

            var exception = await Record.ExceptionAsync(async () =>
            {
                await foreach (var value in observerRef
                    .GetValuesWithError(errorIndex, waitAfterYield, ErrorMessage, cancellationToken)
                    .WithBatchSize(10)
                    .WithCancellation(cancellationToken))
                {
                    values.Add(value);
                }
            });

            Assert.IsType<InvalidOperationException>(exception);
            Assert.Equal(ErrorMessage, exception.Message);
            Assert.Equal(errorIndex, values.Count);

            var calls = await observerRef.GetIncomingCalls();
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task Observer_AsyncEnumerable_CancellationToken(int errorIndex, bool waitAfterYield)
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            const string ErrorMessage = "Throwing!";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (errorIndex == 0)
            {
                cts.Cancel();
            }

            var values = new List<int>();
            var exception = await Record.ExceptionAsync(async () =>
            {
                await foreach (var value in observerRef
                    .GetValuesWithError(int.MaxValue, waitAfterYield, ErrorMessage, cts.Token)
                    // Batch size of 1 is important otherwise the assert on errorIndex = values.Count would fail when
                    // we do NOT wait async after yield since the iterator would supply up to batch size of elements
                    // and the client would notice cancellation at that point which would result in say errIdx = 1, val.Count = 10 (if batch size is 10)
                    // This is true for all tests with cancellation.
                    .WithBatchSize(1))
                {
                    values.Add(value);
                    if (values.Count == errorIndex)
                    {
                        cts.Cancel();
                    }
                }
            });

            Assert.IsType<OperationCanceledException>(exception);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(errorIndex, values.Count);

            var calls = await observerRef.GetIncomingCalls();
            if (errorIndex == 0)
            {
                Assert.DoesNotContain(calls, IsStartEnumerationCall);
                Assert.DoesNotContain(calls, IsDisposeCall);
            }
            else
            {
                Assert.Contains(calls, IsStartEnumerationCall);
                Assert.Contains(calls, IsDisposeCall);
            }
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_CancelBeforeYield()
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timeoutCts.CancelAfter(TestTimeout);

            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var callId = Guid.NewGuid();

            var cancelTask = Task.Run(async () =>
            {
                await observerRef.WaitForCall(callId).AsTask().WaitAsync(timeoutCts.Token);
                callCts.Cancel();
            }, timeoutCts.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in observerRef.SleepyEnumerable(callId, TimeSpan.FromSeconds(25), callCts.Token))
                {
                    Assert.Fail("The sequence must be canceled before yielding.");
                }
            });

            await cancelTask.WaitAsync(timeoutCts.Token);

            // Wait for the iterator's finally block to record cancellation.
            while (!(await observerRef.GetCanceledCalls()).Contains(callId))
            {
                await Task.Delay(10, timeoutCts.Token);
            }

            var calls = await observerRef.GetIncomingCalls();
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task Observer_AsyncEnumerable_Cancellation(int errorIndex, bool waitAfterYield)
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            const string ErrorMessage = "cancel"; // iterator knows to throw OperationCanceledException on this.

            var values = new List<int>();
            var exception = await Record.ExceptionAsync(async () =>
            {
                await foreach (var value in observerRef
                    .GetValuesWithError(errorIndex, waitAfterYield, ErrorMessage, cancellationToken)
                    .WithBatchSize(1).WithCancellation(cancellationToken))
                {
                    values.Add(value);
                }
            });

            Assert.IsType<OperationCanceledException>(exception);
            Assert.Equal(errorIndex, values.Count);

            var calls = await observerRef.GetIncomingCalls();
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task Observer_AsyncEnumerable_CancellationToken_WithCancellationExtension(int errorIndex, bool waitAfterYield)
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            const string ErrorMessage = "Throwing!";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (errorIndex == 0)
            {
                cts.Cancel();
            }

            var values = new List<int>();
            var exception = await Record.ExceptionAsync(async () =>
            {
                await foreach (var value in observerRef
                    .GetValuesWithError(int.MaxValue, waitAfterYield, ErrorMessage, cancellationToken)
                    .WithBatchSize(1).WithCancellation(cts.Token))
                {
                    values.Add(value);
                    if (values.Count == errorIndex)
                    {
                        cts.Cancel();
                    }
                }
            });

            Assert.IsType<OperationCanceledException>(exception);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(errorIndex, values.Count);

            var calls = await observerRef.GetIncomingCalls();
            if (errorIndex == 0)
            {
                // The cancellation should be preemptive, so the client must not have contacted the observer at all.
                Assert.DoesNotContain(calls, IsStartEnumerationCall);
                Assert.DoesNotContain(calls, IsDisposeCall);
            }
            else
            {
                Assert.Contains(calls, IsStartEnumerationCall);
                Assert.Contains(calls, IsDisposeCall);
            }
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_WithCancellation()
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var producer = Task.Run(async () =>
            {
                try
                {
                    foreach (var value in Enumerable.Range(0, 5))
                    {
                        await Task.Delay(200, cancellationToken);
                        await observerRef.OnNext(value.ToString());
                    }

                    await observerRef.Complete();
                }
                catch (ObserverNotReachableException)
                {
                    // Expected: RunObserverTestAsync deregisters the observer in its finally block,
                    // so this task would keep writing to the (now missing) observer.
                }
            }, cancellationToken);

            var values = new List<string>();
            var exception = await Record.ExceptionAsync(async () =>
            {
                await foreach (var value in observerRef.GetValues(cancellationToken).WithCancellation(cts.Token))
                {
                    values.Add(value);
                    if (values.Count == 3)
                    {
                        cts.Cancel();
                    }
                }
            });

            Assert.IsType<OperationCanceledException>(exception);
            Assert.Equal(3, values.Count);

            var calls = await observerRef.GetIncomingCalls();
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_EnumeratorDisposed_WhileMoveNextIsPending()
    {
        await RunObserverTestAsync(async (observerRef, observerImpl) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var enumerator = observerRef.GetBlockingEnumerable(cancellationToken).GetAsyncEnumerator(cancellationToken);

            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeout));
            Assert.Equal("first", enumerator.Current);

            var pendingMoveNext = enumerator.MoveNextAsync().AsTask();

            await observerImpl.BlockingMoveNextStarted.WaitAsync(TestTimeout);
            await enumerator.DisposeAsync().AsTask().WaitAsync(TestTimeout);
            await observerImpl.BlockingEnumeratorFinished.WaitAsync(TestTimeout);

            var completedTask = await Task.WhenAny(pendingMoveNext).WaitAsync(TestTimeout, cancellationToken);
            Assert.Same(pendingMoveNext, completedTask);

            // Disposal may surface as cancellation or a terminal enumeration error, depending on timing.
            try
            {
                await pendingMoveNext;
            }
            catch (Exception)
            {
                // Completion is what matters here: the pending move must not remain stuck behind disposal.
            }

            var calls = await observerRef.GetIncomingCalls();
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_ObserverDeregistration_DisposesWholeExtension()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var observerImpl = new ObservableGrainObserver();
        var observerRef = GrainFactory.CreateObjectReference<IObservableGrainObserver>(observerImpl);
        var enumerator = observerRef.GetBlockingEnumerable(cancellationToken).GetAsyncEnumerator(cancellationToken);

        using var listener = CreateListener(observerImpl);

        // The first MoveNextAsync yields "first" and server-side the batching loop starts
        // the enumerator's second MoveNextAsync, which parks on an infinite delay.
        // Note: This is also what lazily creates the observer-side extension.
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeout, cancellationToken));
        Assert.Equal("first", enumerator.Current);

        await listener.WaitForExtensionCreatedAsync(cancellationToken);

        // Now we wait until the server-side enumerator is parked on its pending MoveNextAsync.
        await observerImpl.BlockingMoveNextStarted.WaitAsync(TestTimeout, cancellationToken);

        // Now we issue a second client MoveNextAsync. Server-side this observes the already parked
        // producer MoveNextAsync and will not complete until the enumerator is cancelled or the request is rejected.
        var pendingMoveNext = enumerator.MoveNextAsync().AsTask();

        // LocalObjectData owns the extension and must cancel/dispose it when the object reference is removed.
        // This also deregisters the observer from the silo, which races with the delivery of the MoveNextAsync above.
        GrainFactory.DeleteObjectReference<IObservableGrainObserver>(observerRef);

        // We wait for the extension's disposal.
        await listener.WaitForExtensionDisposedAsync(cancellationToken);

        // Which in turn should cancel the enumerator which is running.
        await observerImpl.BlockingEnumeratorFinished.WaitAsync(TestTimeout, cancellationToken);

        // Now crucially, the in-flight MoveNextAsync must not be left hanging!
        // Depending on the order of arrival, either the extension answers it (cancellation, because the
        // MoveNext reached the extension before deregistration of the observer) or Dispatch rejects it
        // and returns a ObserverNotReachableException-baked response, because deregistration won the race).
        // Both are terminal responses, as this tests is about NOT leaving the client waiting.
        var completedTask = await Task.WhenAny(pendingMoveNext).WaitAsync(TestTimeout, cancellationToken);
        Assert.Same(pendingMoveNext, completedTask);

        try
        {
            await pendingMoveNext;
        }
        catch (Exception)
        {
            // Cancellation or a terminal error response is expected after owner deregistration.
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_MoveNextCall_AfterDeregistration_Fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var observerImpl = new ObservableGrainObserver();
        var observerRef = GrainFactory.CreateObjectReference<IObservableGrainObserver>(observerImpl);
        var enumerator = observerRef.GetBlockingEnumerable(cancellationToken).GetAsyncEnumerator(cancellationToken);

        using var listener = CreateListener(observerImpl);

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeout, cancellationToken));

        await listener.WaitForExtensionCreatedAsync(cancellationToken);
        await observerImpl.BlockingMoveNextStarted.WaitAsync(TestTimeout, cancellationToken);

        GrainFactory.DeleteObjectReference<IObservableGrainObserver>(observerRef);

        await listener.WaitForExtensionDisposedAsync(cancellationToken);
        await observerImpl.BlockingEnumeratorFinished.WaitAsync(TestTimeout, cancellationToken);

        // By now the object manager has removed the observer from localObjects.
        // The next MoveNext should hit the Dispatch message path, and must return an exception response.
        await Assert.ThrowsAnyAsync<ObserverNotReachableException>(
            async () => await enumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeout, cancellationToken));
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_SingleValue()
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            await observerRef.OnNext("only");
            await observerRef.Complete();

            var values = new List<string>();

            await foreach (var value in observerRef.GetValues(cancellationToken).WithBatchSize(10).WithCancellation(cancellationToken))
            {
                values.Add(value);
            }

            Assert.Equal(new[] { "only" }, values);

            var calls = await observerRef.GetIncomingCalls();

            Assert.Equal(1, CountPullCalls(calls));
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_BatchOnce()
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            foreach (var value in Enumerable.Range(0, 50))
            {
                await observerRef.OnNext(value.ToString());
            }

            await observerRef.Complete();

            var values = new List<string>();

            await foreach (var value in observerRef.GetValues(cancellationToken).WithBatchSize(100).WithCancellation(cancellationToken))
            {
                values.Add(value);
            }

            Assert.Equal(50, values.Count);

            var calls = await observerRef.GetIncomingCalls();

            Assert.Equal(1, CountPullCalls(calls));
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_SlowConsumer()
    {
        await RunObserverTestAsync(async (observerRef, observerImpl) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);

            cts.CancelAfter(TimeSpan.FromSeconds(30));

            using var listener = CreateListener(observerImpl);

            foreach (var value in Enumerable.Range(0, 3))
            {
                await observerRef.OnNext(value.ToString());
            }

            await observerRef.Complete();

            var values = new List<string>();

            await foreach (var value in observerRef.GetValues(cts.Token).WithBatchSize(1).WithCancellation(cts.Token))
            {
                values.Add(value);

                if (values.Count == 1)
                {
                    await listener.WaitForExtensionCreatedAsync(cts.Token);
                }

                await AdvanceToNextObserverCleanupAsync(listener, cts.Token);
            }

            Assert.Equal(new[] { "0", "1", "2" }, values);

            var calls = await observerRef.GetIncomingCalls();

            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_SlowConsumer_Evicted()
    {
        await RunObserverTestAsync(async (observerRef, observerImpl) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            cts.CancelAfter(TimeSpan.FromSeconds(30));

            using var listener = CreateListener(observerImpl);

            foreach (var value in Enumerable.Range(0, 5))
            {
                await observerRef.OnNext(value.ToString());
            }

            await observerRef.Complete();

            var values = new List<string>();

            try
            {
                await foreach (var value in observerRef.GetValues(cts.Token).WithBatchSize(1).WithCancellation(cts.Token))
                {
                    values.Add(value);

                    if (values.Count == 1)
                    {
                        await listener.WaitForExtensionCreatedAsync(cts.Token);
                    }

                    if (values.Count == 3)
                    {
                        await AdvanceToNextObserverCleanupAsync(listener, cts.Token);
                        await AdvanceToNextObserverCleanupAsync(listener, cts.Token);
                    }
                }

                Assert.Fail("Expected an exception to be thrown");
            }
            catch (EnumerationAbortedException ex)
            {
                Assert.Contains("the remote target does not have a record of this enumerator", ex.Message);
            }

            Assert.Equal(3, values.Count);
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_SlowProducer()
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            var producer = Task.Run(async () =>
            {
                foreach (var value in Enumerable.Range(0, 5))
                {
                    await Task.Delay(2000, cancellationToken);
                    await observerRef.OnNext(value.ToString());
                }

                await observerRef.Complete();
            }, cancellationToken);

            var values = new List<string>();
            await foreach (var value in observerRef.GetValues(cancellationToken).WithCancellation(cancellationToken))
            {
                values.Add(value);
                if (values.Count == 2)
                {
                    break;
                }
            }

            Assert.Equal(2, values.Count);

            var calls = await observerRef.GetIncomingCalls();
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task Observer_AsyncEnumerable_Fail()
    {
        await RunObserverTestAsync(async (observerRef, _) =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            foreach (var value in Enumerable.Range(0, 3))
            {
                await observerRef.OnNext(value.ToString());
            }

            await observerRef.Fail();

            var values = new List<string>();
            var exception = await Record.ExceptionAsync(async () =>
            {
                await foreach (var value in observerRef.GetValues(cancellationToken).WithCancellation(cancellationToken))
                {
                    values.Add(value);
                }
            });

            Assert.Equal(3, values.Count);
            Assert.NotNull(exception);
            Assert.Equal("I've failed you!", exception.Message);

            var calls = await observerRef.GetIncomingCalls();
            Assert.Contains(calls, IsDisposeCall);
        });
    }

    private async Task RunObserverTestAsync(Func<IObservableGrainObserver, ObservableGrainObserver, Task> test)
    {
        var implementation = new ObservableGrainObserver();
        var observerRef = GrainFactory.CreateObjectReference<IObservableGrainObserver>(implementation);

        try
        {
            await test(observerRef, implementation);
        }
        finally
        {
            GrainFactory.DeleteObjectReference<IObservableGrainObserver>(observerRef);
        }
    }

    private static int CountPullCalls(List<(string InterfaceName, string MethodName)> calls) =>
        calls.Count(call =>
            call.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension), StringComparison.Ordinal)
            && (call.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration), StringComparison.Ordinal)
                || call.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext), StringComparison.Ordinal)));

    private static bool IsDisposeCall((string InterfaceName, string MethodName) call) =>
        call.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension), StringComparison.Ordinal)
        && call.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync), StringComparison.Ordinal);

    private static bool IsStartEnumerationCall((string InterfaceName, string MethodName) call) =>
        call.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension), StringComparison.Ordinal) &&
        call.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration), StringComparison.Ordinal);

    private static AsyncEnumerableExtensionListener CreateListener(ObservableGrainObserver observerImpl) =>
       new AsyncEnumerableExtensionListener(
           diagnosticListenerName: "Orleans.Runtime.AsyncEnumerableGrainObserverExtension",
           isTargetExtension: value => value is InvokableObjectManager.AsyncEnumerableGrainObserverExtension
           { GrainContext.GrainInstance: var instance } && ReferenceEquals(instance, observerImpl));

    private async Task AdvanceToNextObserverCleanupAsync(AsyncEnumerableExtensionListener listener, CancellationToken cancellationToken)
    {
        var cleanupCount = listener.CleanupCount + 1;

        await fixture.AdvanceTimeByResponseTimeoutAsync().WaitAsync(cancellationToken);
        await listener.WaitForCleanupCountAsync(cleanupCount, cancellationToken);
    }
}
