using System;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Tester;

public class CallbackDataTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void AlreadyCanceledTokenCompletesCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var completion = new TestResponseCompletionSource();
        var unregisterCount = 0;
        CallbackDataOwner owner = default;
        owner = CallbackDataPool.Rent(
            CreateSharedData(_ =>
            {
                Interlocked.Increment(ref unregisterCount);
                CallbackDataPool.Return(owner);
            }),
            completion,
            new Message(),
            CreateInstruments(serviceProvider));
        using var lease = owner.Acquire();
        var callback = lease.Value;

        callback.SubscribeForCancellation(cancellation.Token);

        Assert.True(callback.IsCompleted);
        Assert.Equal(1, unregisterCount);
        var exception = Assert.IsType<OperationCanceledException>(completion.Response!.Exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CancellationSubscriptionAfterCompletionDoesNotRetainCompletionSource()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();

        var completionReference = CreateCompletedCallback(cancellation.Token, CreateInstruments(serviceProvider));

        for (var attempt = 0; attempt < 10 && completionReference.IsAlive; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(completionReference.IsAlive);
        GC.KeepAlive(cancellation);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void StaleOwnerCannotLeaseReusedCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);
        var staleOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        var lease = staleOwner.Acquire();
        var callback = lease.Value;
        lease.Dispose();
        CallbackDataPool.Return(staleOwner);

        var currentOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using var currentLease = currentOwner.Acquire();
        var reusedCallback = currentLease.Value;
        Assert.Same(callback, reusedCallback);

        using var staleLease = staleOwner.Acquire();
        Assert.False(staleLease.TryGetValue(out _));
        CallbackDataPool.Return(staleOwner);
        Assert.Same(reusedCallback, currentLease.Value);

        CallbackDataPool.Return(currentOwner);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void WrongCorrelationStatusUpdateDoesNotAffectReusedCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);
        var staleOwner = CallbackDataPool.Rent(
            CreateSharedData(),
            new TestResponseCompletionSource(),
            new Message { Id = new CorrelationId(1) },
            instruments);
        using (var lease = staleOwner.Acquire())
        {
            Assert.Equal(new CorrelationId(1), lease.Value.Message.Id);
        }
        CallbackDataPool.Return(staleOwner);

        var completion = new TestResponseCompletionSource();
        CallbackDataOwner currentOwner = default;
        currentOwner = CallbackDataPool.Rent(
            CreateSharedData(_ => CallbackDataPool.Return(currentOwner)),
            completion,
            new Message { Id = new CorrelationId(2) },
            instruments);
        using var currentLease = currentOwner.Acquire();
        Assert.Equal(new CorrelationId(2), currentLease.Value.Message.Id);

        using var staleLease = staleOwner.Acquire();
        if (staleLease.TryGetValue(out var staleCallback))
        {
            staleCallback.OnStatusUpdate(new StatusResponse(isExecuting: true, isWaiting: false, diagnostics: ["stale"]));
        }

        currentLease.Value.OnTimeout();

        var exception = Assert.IsType<TimeoutException>(completion.Response!.Exception);
        Assert.DoesNotContain("stale", exception.Message);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void ActiveLeaseDelaysCallbackReuse()
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);
        var firstOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        var firstLease = firstOwner.Acquire();
        var firstCallback = firstLease.Value;
        CallbackDataPool.Return(firstOwner);

        var secondOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using var secondLease = secondOwner.Acquire();
        Assert.NotSame(firstCallback, secondLease.Value);
        CallbackDataPool.Return(secondOwner);

        firstLease.Dispose();

        var reusedOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using var reusedLease = reusedOwner.Acquire();
        Assert.Same(firstCallback, reusedLease.Value);
        CallbackDataPool.Return(reusedOwner);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CopiedLeaseDoubleReleaseThrows()
    {
        using var serviceProvider = CreateServiceProvider();
        var owner = CallbackDataPool.Rent(
            CreateSharedData(),
            new TestResponseCompletionSource(),
            new Message(),
            CreateInstruments(serviceProvider));
        var lease = owner.Acquire();
        var callback = lease.Value;
        var copiedLease = lease;
        lease.Dispose();

        var threw = false;
        try
        {
            copiedLease.Dispose();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.True(threw);
        CallbackDataPool.Return(owner);

        var reusedOwner = CallbackDataPool.Rent(
            CreateSharedData(),
            new TestResponseCompletionSource(),
            new Message(),
            CreateInstruments(serviceProvider));
        using var reusedLease = reusedOwner.Acquire();
        Assert.Same(callback, reusedLease.Value);

        var concurrentOwner = CallbackDataPool.Rent(
            CreateSharedData(),
            new TestResponseCompletionSource(),
            new Message(),
            CreateInstruments(serviceProvider));
        using var concurrentLease = concurrentOwner.Acquire();
        Assert.NotSame(callback, concurrentLease.Value);

        CallbackDataPool.Return(concurrentOwner);
        CallbackDataPool.Return(reusedOwner);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void StatusLeaseDelaysReuseAfterResponseTransfer()
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);
        var owner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        var senderLease = owner.Acquire();
        var callback = senderLease.Value;
        senderLease.Dispose();
        var statusLease = owner.Acquire();

        using (var responseLease = owner.TransferToLease())
        {
            responseLease.Value.DoCallback(new Message { BodyObject = Response.Completed });
        }

        var concurrentOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using (var concurrentLease = concurrentOwner.Acquire())
        {
            Assert.NotSame(callback, concurrentLease.Value);
        }
        CallbackDataPool.Return(concurrentOwner);

        var localOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using var localLease = localOwner.Acquire();
        statusLease.Value.OnStatusUpdate(new StatusResponse(isExecuting: true, isWaiting: false, diagnostics: []));
        statusLease.Dispose();

        var reusedOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using var reusedLease = reusedOwner.Acquire();
        Assert.Same(callback, reusedLease.Value);
        CallbackDataPool.Return(reusedOwner);
        CallbackDataPool.Return(localOwner);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task InFlightCancellationDelaysReuseUntilCallbackReturns()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var instruments = CreateInstruments(serviceProvider);
        using var completion = new BlockingResponseCompletionSource();
        CallbackDataOwner owner = default;
        owner = CallbackDataPool.Rent(
            CreateSharedData(_ => CallbackDataPool.Return(owner)),
            completion,
            new Message(),
            instruments);
        var senderLease = owner.Acquire();
        var callback = senderLease.Value;
        callback.SubscribeForCancellation(cancellation.Token);
        senderLease.Dispose();

        var prematurelyReused = false;
        completion.SetProbe(() =>
        {
            var probeOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
            using var probeLease = probeOwner.Acquire();
            prematurelyReused = ReferenceEquals(callback, probeLease.Value);
            CallbackDataPool.Return(probeOwner);
        });

        var cancellationTask = Task.Run(() =>
        {
            var localOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
            using var localLease = localOwner.Acquire();
            cancellation.Cancel();
            var reusedOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
            using var reusedLease = reusedOwner.Acquire();
            var reusedCallback = reusedLease.Value;
            CallbackDataPool.Return(reusedOwner);
            CallbackDataPool.Return(localOwner);
            return reusedCallback;
        });

        completion.WaitUntilProbed();
        try
        {
            Assert.False(prematurelyReused);
        }
        finally
        {
            completion.Release();
        }

        Assert.Same(callback, await cancellationTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void StaleCancellationCallbackDoesNotAffectReusedCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var instruments = CreateInstruments(serviceProvider);
        CallbackDataOwner completedOwner = default;
        completedOwner = CallbackDataPool.Rent(
            CreateSharedData(_ => CallbackDataPool.Return(completedOwner)),
            new TestResponseCompletionSource(),
            new Message(),
            instruments);
        var completedLease = completedOwner.Acquire();
        var completedCallback = completedLease.Value;
        completedCallback.SubscribeForCancellation(cancellation.Token);
        completedCallback.OnHostShutdown();
        completedLease.Dispose();

        var currentCompletion = new TestResponseCompletionSource();
        var currentOwner = CallbackDataPool.Rent(CreateSharedData(), currentCompletion, new Message(), instruments);
        using var currentLease = currentOwner.Acquire();
        Assert.Same(completedCallback, currentLease.Value);

        cancellation.Cancel();

        Assert.False(currentLease.Value.IsCompleted);
        Assert.Null(currentCompletion.Response);
        CallbackDataPool.Return(currentOwner);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void ConcurrentOwnerReturnsAreIdempotent()
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);

        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var owner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
            var lease = owner.Acquire();
            var callback = lease.Value;

            Parallel.Invoke(
                () => CallbackDataPool.Return(owner),
                () => CallbackDataPool.Return(owner));
            lease.Dispose();

            var reusedOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
            using var reusedLease = reusedOwner.Acquire();
            Assert.Same(callback, reusedLease.Value);

            var concurrentOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
            using var concurrentLease = concurrentOwner.Acquire();
            Assert.NotSame(callback, concurrentLease.Value);

            CallbackDataPool.Return(concurrentOwner);
            CallbackDataPool.Return(reusedOwner);
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CancellationAndResponseRaceCompletesExactlyOnce() =>
        RunTerminalRace(static (callback, _, cancellation) =>
        {
            callback.SubscribeForCancellation(cancellation.Token);
            cancellation.Cancel();
        });

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void TimeoutAndResponseRaceCompletesExactlyOnce() =>
        RunTerminalRace(static (callback, _, _) => callback.OnTimeout());

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void ShutdownAndResponseRaceCompletesExactlyOnce() =>
        RunTerminalRace(static (callback, _, _) => callback.OnHostShutdown());

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CompletionExceptionDoesNotPreventCallbackReuse()
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);
        var owner = CallbackDataPool.Rent(
            CreateSharedData(),
            new ThrowingResponseCompletionSource(),
            new Message(),
            instruments);
        var lease = owner.TransferToLease();
        var callback = lease.Value;

        try
        {
            var response = new Message { BodyObject = Response.Completed };
            Assert.Throws<InvalidOperationException>(() => callback.DoCallback(response));
        }
        finally
        {
            lease.Dispose();
        }

        var reusedOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using var reusedLease = reusedOwner.Acquire();
        Assert.Same(callback, reusedLease.Value);
        CallbackDataPool.Return(reusedOwner);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void OwnerTransferPreventsNewLeasesAndReturnsAfterLeaseDisposal()
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);
        var owner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        var transferLease = owner.TransferToLease();
        var callback = transferLease.Value;

        using var staleLease = owner.Acquire();
        Assert.False(staleLease.TryGetValue(out _));
        CallbackDataPool.Return(owner);

        transferLease.Dispose();

        var reusedOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using var reusedLease = reusedOwner.Acquire();
        Assert.Same(callback, reusedLease.Value);
        CallbackDataPool.Return(reusedOwner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCompletedCallback(CancellationToken cancellationToken, ApplicationRequestInstruments instruments)
    {
        var completion = new TestResponseCompletionSource();
        CallbackDataOwner owner = default;
        owner = CallbackDataPool.Rent(
            CreateSharedData(_ => CallbackDataPool.Return(owner)),
            completion,
            new Message(),
            instruments);
        using var lease = owner.Acquire();
        var callback = lease.Value;

        callback.OnHostShutdown();
        callback.SubscribeForCancellation(cancellationToken);

        return new WeakReference(completion);
    }

    private static void RunTerminalRace(Action<CallbackData, CallbackDataOwner, CancellationTokenSource> complete)
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);

        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            using var cancellation = new CancellationTokenSource();
            using var start = new Barrier(3);
            var completion = new TestResponseCompletionSource();
            var registered = 1;
            CallbackDataOwner owner = default;
            owner = CallbackDataPool.Rent(
                CreateSharedData(_ =>
                {
                    if (Interlocked.Exchange(ref registered, 0) == 1)
                    {
                        CallbackDataPool.Return(owner);
                    }
                }),
                completion,
                new Message(),
                instruments);
            using var lease = owner.Acquire();
            var callback = lease.Value;
            var response = new Message { BodyObject = Response.Completed };

            var completionTask = Task.Run(() =>
            {
                start.SignalAndWait();
                complete(callback, owner, cancellation);
            });
            var responseTask = Task.Run(() =>
            {
                start.SignalAndWait();
                if (Interlocked.Exchange(ref registered, 0) == 1)
                {
                    using var responseLease = owner.TransferToLease();
                    responseLease.Value.DoCallback(response);
                }
            });

            start.SignalAndWait();
            Task.WaitAll(completionTask, responseTask);

            Assert.Equal(1, completion.CompletionCount);
            Assert.NotNull(completion.Response);
            Assert.Equal(0, Volatile.Read(ref registered));
        }
    }

    private static SharedCallbackData CreateSharedData(Action<Message>? unregister = null) =>
        new(
            unregister ?? (_ => { }),
            logger: NullLogger<CallbackData>.Instance,
            responseTimeout: TimeSpan.FromMinutes(1),
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: false,
            cancellationManager: null);

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        return services.BuildServiceProvider();
    }

    private static ApplicationRequestInstruments CreateInstruments(IServiceProvider serviceProvider) =>
        new(new OrleansInstruments(serviceProvider.GetRequiredService<IMeterFactory>()));

    private sealed class TestResponseCompletionSource : IResponseCompletionSource
    {
        private int _completionCount;
        private Response? _response;

        public int CompletionCount => Volatile.Read(ref _completionCount);

        public Response? Response => Volatile.Read(ref _response);

        public void Complete(Response value)
        {
            Interlocked.Increment(ref _completionCount);
            Interlocked.CompareExchange(ref _response, value, null);
        }

        public void Complete() => Complete(Orleans.Serialization.Invocation.Response.Completed);
    }

    private sealed class ThrowingResponseCompletionSource : IResponseCompletionSource
    {
        public void Complete(Response value) => throw new InvalidOperationException("Test completion failure.");

        public void Complete() => throw new InvalidOperationException("Test completion failure.");
    }

    private sealed class BlockingResponseCompletionSource : IResponseCompletionSource, IDisposable
    {
        private readonly ManualResetEventSlim _probed = new();
        private readonly ManualResetEventSlim _release = new();
        private Action? _probe;

        public void Complete(Response value)
        {
            try
            {
                _probe!();
            }
            finally
            {
                _probed.Set();
            }

            _release.Wait();
        }

        public void Complete() => Complete(Response.Completed);

        public void SetProbe(Action probe) => _probe = probe;

        public void WaitUntilProbed()
        {
            if (!_probed.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Cancellation callback did not probe the callback pool.");
            }
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _probed.Dispose();
            _release.Dispose();
        }
    }
}
