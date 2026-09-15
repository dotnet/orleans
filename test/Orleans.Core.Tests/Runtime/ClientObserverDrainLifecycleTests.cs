using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class ClientObserverDrainLifecycleTests
{
    [Fact]
    public async Task ClusterClientStop_AllowsUpperStageCleanupThenDrainsBeforeConnectionClosure()
    {
        var lifecycle = new LifecycleProbe();
        var fixture = new ExternalClientFixture(lifecycle);
        var events = new ConcurrentQueue<string>();
        var source = new CallbackCompletionSource();
        var outboundCompleted = DrainTestHelpers.CreateSignal();
        var release = DrainTestHelpers.CreateSignal();
        var callbacks = ReadCallbacks(fixture.Runtime);
        var outbound = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(2103),
            SendingGrain = fixture.ObserverId.GrainId,
            TargetGrain = ClientGrainId.Create("cleanup-target").GrainId
        };
        var callback = new CallbackData(
            new SharedCallbackData(
                message => callbacks.TryRemove(message.Id, out _),
                NullLogger<CallbackData>.Instance,
                fixture.TimeProvider,
                TimeSpan.FromMinutes(1),
                cancelOnTimeout: false,
                waitForCancellationAcknowledgement: false,
                cancellationManager: null),
            source,
            outbound,
            new ApplicationRequestInstruments(fixture.Instruments));
        var invocation = new ObservedInvocation("unused", async () =>
        {
            var result = await AwaitCallbackAsync(source);
            events.Enqueue("outbound-completed");
            outboundCompleted.TrySetResult();
            await release.Task;
            return Assert.IsType<string>(result);
        })
        {
            OnCompleted = () => events.Enqueue("observer-exit")
        };
        var late = new TestInvokable("late-must-not-run");
        late.Release.TrySetResult();
        Task? stop = null;
        lifecycle.OnStopping = async _ =>
        {
            Assert.False(fixture.Connections.Closed.IsCompleted);
            Assert.False(callback.IsCompleted);
            using var body = Response.FromResult("cleanup-completed");
            fixture.Runtime.ReceiveResponse(new Message
            {
                Direction = Message.Directions.Response,
                Result = Message.ResponseTypes.Success,
                Id = outbound.Id,
                SendingGrain = outbound.TargetGrain,
                TargetGrain = outbound.SendingGrain,
                BodyObject = body
            });
            await fixture.WaitAsync(outboundCompleted.Task, "upper-stage outbound cleanup completed", invocation);
            events.Enqueue("upper-stage-stop");
        };

        try
        {
            Assert.True(callbacks.TryAdd(outbound.Id, callback));
            await fixture.StartFakeLifecycleAsync();
            fixture.Dispatch(invocation, 2101);
            await fixture.WaitAsync(invocation.WorkEntered.Task, "2101 observer held", invocation);
            fixture.AssertHeldInvocation(invocation);

            stop = fixture.ClusterClient!.StopAsync(TestContext.Current.CancellationToken);
            await fixture.WaitAsync(lifecycle.StopReturned.Task, "upper lifecycle stage returned", invocation);
            await fixture.WaitAsync(fixture.ObserverDrainStarted.Task, "client adapter started observer drain", invocation);
            Assert.False(stop.IsCompleted, fixture.Describe(invocation, stop));
            Assert.False(fixture.Connections.Closed.IsCompleted);
            Assert.False(invocation.Exited.Task.IsCompleted);
            Assert.Equal(1, source.CompletionCount);
            Assert.Null(source.Exception);
            Assert.Equal("cleanup-completed", source.Result);
            Assert.Empty(callbacks);
            fixture.Dispatch(late, 2102);

            release.TrySetResult();
            await fixture.WaitAsync(stop, "2101 cluster stop after observer release", invocation);
            await fixture.WaitAsync(fixture.Manager.StopAsync(), "2101 actual manager drain", invocation);

            Assert.True(fixture.Connections.Closed.IsCompletedSuccessfully);
            Assert.Equal(new[] { "outbound-completed", "upper-stage-stop", "observer-exit" }, events.ToArray());
            Assert.Equal(1, lifecycle.StartCount);
            Assert.Equal(1, lifecycle.StopCount);
            Assert.Equal(TestContext.Current.CancellationToken, lifecycle.StopToken);
            Assert.Equal(0, late.InvocationCount);
            Assert.False(late.Entered.Task.IsCompleted);
            fixture.AssertCompletedInvocation(invocation, "cleanup-completed");
            fixture.AssertRootIsLive();
        }
        finally
        {
            if (!callback.IsCompleted)
            {
                callback.OnHostShutdown();
            }

            release.TrySetResult();
            late.ReleaseForCleanup();
            await fixture.CleanupAsync(stop);
        }
    }

    [Fact]
    public async Task OutsideRuntimeStop_FaultsAwaitedCallbackBeforeObserverDrain()
    {
        var fixture = new ExternalClientFixture();
        var source = new CallbackCompletionSource();
        var callbackAwaiting = DrainTestHelpers.CreateSignal();
        var events = new ConcurrentQueue<string>();
        var unregistered = new ConcurrentQueue<(Message Message, bool Removed)>();
        var callbacks = ReadCallbacks(fixture.Runtime);
        var outbound = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(2202),
            SendingGrain = fixture.ObserverId.GrainId,
            TargetGrain = ObserverGrainId.Create(
                ClientGrainId.Create("remote-client"), IdSpan.Create("outbound-observer")).GrainId
        };
        var shared = new SharedCallbackData(
            message =>
            {
                unregistered.Enqueue((message, callbacks.TryRemove(message.Id, out _)));
                events.Enqueue("callback-unregistered");
            },
            NullLogger<CallbackData>.Instance,
            fixture.TimeProvider,
            TimeSpan.FromMinutes(1),
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: false,
            cancellationManager: null);
        var callback = new CallbackData(
            shared, source, outbound, new ApplicationRequestInstruments(fixture.Instruments));
        SiloUnavailableException? observedShutdown = null;
        var invocation = new ObservedInvocation("unused", async () =>
        {
            try
            {
                // This promise is completed only by the real CallbackData shutdown path.
                callbackAwaiting.TrySetResult();
                await AwaitCallbackAsync(source);
                return "unexpected-outbound-success";
            }
            catch (SiloUnavailableException exception)
            {
                observedShutdown = exception;
                events.Enqueue("callback-fault-observed");
                return "outbound-shutdown-observed";
            }
        })
        {
            OnCompleted = () => events.Enqueue("observer-exit")
        };
        Task? stop = null;
        var succeeded = false;

        try
        {
            Assert.True(callbacks.TryAdd(outbound.Id, callback));
            Assert.Same(callback, Assert.Single(callbacks).Value);
            fixture.Dispatch(invocation, 2201);
            await fixture.WaitAsync(callbackAwaiting.Task, "2201 awaiting outbound callback 2202", invocation);
            fixture.AssertHeldInvocation(invocation);
            Assert.False(source.Completed.Task.IsCompleted);
            Assert.Equal(0, source.CompletionCount);
            Assert.False(callback.IsCompleted);

            // Exercise the lifecycle entry point itself: pre-draining through its helper
            // would conceal StopAsync failing to fault callbacks before awaiting observers.
            stop = fixture.Runtime.StopAsync(TestContext.Current.CancellationToken);
            await fixture.WaitAsync(source.Completed.Task, "2202 callback faulted by runtime shutdown", invocation);
            await fixture.WaitAsync(stop, "2201 callback-dependent observer drain and callback monitor joined", invocation);
            events.Enqueue("drain-completed");

            var exception = Assert.IsType<SiloUnavailableException>(source.Exception);
            Assert.Equal(
                $"The local Orleans host is shutting down and can no longer process the request: {outbound}.",
                exception.Message);
            Assert.Same(exception, observedShutdown);
            Assert.Null(source.Result);
            Assert.Equal(1, source.CompletionCount);
            Assert.True(callback.IsCompleted);
            var removal = Assert.Single(unregistered);
            Assert.Same(outbound, removal.Message);
            Assert.True(removal.Removed);
            Assert.False(callbacks.ContainsKey(outbound.Id));
            Assert.Empty(callbacks);
            Assert.Equal(
                new[] { "callback-unregistered", "callback-fault-observed", "observer-exit", "drain-completed" },
                events.ToArray());
            fixture.AssertCompletedInvocation(invocation, "outbound-shutdown-observed");
            fixture.AssertRootIsLive();
            succeeded = true;
        }
        finally
        {
            if (!succeeded)
            {
                // Failure-only rescue. Never release the observer or complete its source on success.
                // A missing/late BreakOutstandingMessages must remain a failed test, not a hung worker.
                callback.OnHostShutdown();
            }

            await fixture.CleanupAsync(stop);
        }
    }

    [Fact]
    public async Task OutsideRuntimeStop_CancellationBoundsCallerWaitNotDrain()
    {
        var fixture = new ExternalClientFixture();
        var invocation = new ObservedInvocation("completed-after-caller-cancellation");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task? stop = null;
        Task? actualDrain = null;
        Task? uncanceledStop = null;

        try
        {
            fixture.Dispatch(invocation, 2301);
            await fixture.WaitAsync(invocation.WorkEntered.Task, "2301 observer held", invocation);
            fixture.AssertHeldInvocation(invocation);

            stop = fixture.Runtime.StopAsync(cancellation.Token);
            Assert.False(stop.IsCompleted, fixture.Describe(invocation, stop));
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => fixture.WaitAsync(stop, "runtime caller cancellation while observer remains held", invocation));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(stop.IsCanceled);
            fixture.AssertHeldInvocation(invocation);

            actualDrain = fixture.Manager.StopAsync();
            Assert.False(actualDrain.IsCompleted, fixture.Describe(invocation, actualDrain));
            invocation.Release.TrySetResult();
            await fixture.WaitAsync(actualDrain, "2301 actual drain after caller cancellation", invocation);
            uncanceledStop = fixture.Runtime.StopAsync(TestContext.Current.CancellationToken);
            await fixture.WaitAsync(uncanceledStop, "2301 uncanceled stop joins callback monitor", invocation);
            Assert.True(actualDrain.IsCompletedSuccessfully);
            fixture.AssertCompletedInvocation(invocation, "completed-after-caller-cancellation");
            fixture.AssertRootIsLive();
        }
        finally
        {
            invocation.ReleaseForCleanup();
            await fixture.CleanupAsync(stop, actualDrain, uncanceledStop);
        }
    }

    [Fact]
    public async Task ClusterClientStop_CooperativeLifecycleHonorsCallerCancellation()
    {
        var lifecycle = new LifecycleProbe();
        var fixture = new ExternalClientFixture(lifecycle);
        var invocation = new ObservedInvocation("cluster-observer-completed");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task? stop = null;
        Task? actualDrain = null;
        Task? uncanceledStop = null;

        try
        {
            await fixture.StartFakeLifecycleAsync();
            fixture.Dispatch(invocation, 2311);
            await fixture.WaitAsync(invocation.WorkEntered.Task, "2311 cluster observer held", invocation);
            fixture.AssertHeldInvocation(invocation);

            // The fake subscriber returns promptly and checks the supplied token. This does not
            // promise bounded shutdown for a lifecycle subscriber which ignores cancellation.
            stop = fixture.ClusterClient!.StopAsync(cancellation.Token);
            Assert.False(stop.IsCompleted, fixture.Describe(invocation, stop));
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => fixture.WaitAsync(stop, "cluster caller cancellation while observer remains held", invocation));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(stop.IsCanceled);
            Assert.Equal(1, lifecycle.StartCount);
            fixture.AssertHeldInvocation(invocation);

            actualDrain = fixture.Manager.StopAsync();
            Assert.False(actualDrain.IsCompleted, fixture.Describe(invocation, actualDrain));
            invocation.Release.TrySetResult();
            await fixture.WaitAsync(actualDrain, "2311 actual drain after cluster caller cancellation", invocation);
            uncanceledStop = fixture.Runtime.StopAsync(TestContext.Current.CancellationToken);
            await fixture.WaitAsync(uncanceledStop, "2311 uncanceled runtime cleanup", invocation);
            Assert.True(actualDrain.IsCompletedSuccessfully);
            fixture.AssertCompletedInvocation(invocation, "cluster-observer-completed");
            fixture.AssertRootIsLive();
        }
        finally
        {
            invocation.ReleaseForCleanup();
            await fixture.CleanupAsync(stop, actualDrain, uncanceledStop);
        }
    }

    [Fact]
    public async Task OutsideRuntimeDispose_ClosesAdmissionAndReturnsBeforeDrain()
    {
        var fixture = new ExternalClientFixture();
        var invocation = new ObservedInvocation("completed-after-dispose-returned");
        var late = new TestInvokable("late-must-not-run");
        late.Release.TrySetResult();
        var disposeReturned = DrainTestHelpers.CreateSignal();
        Task? disposeWorker = null;
        Task? repeatDisposeWorker = null;
        Task? actualDrain = null;
        Task? stop = null;

        try
        {
            fixture.Dispatch(invocation, 2401);
            await fixture.WaitAsync(invocation.WorkEntered.Task, "2401 observer held", invocation);
            fixture.AssertHeldInvocation(invocation);

            // There is deliberately no StopAsync before the first Dispose.
            disposeWorker = Task.Run(() =>
            {
                fixture.Runtime.Dispose();
                disposeReturned.TrySetResult();
            }, TestContext.Current.CancellationToken);
            await fixture.WaitAsync(disposeReturned.Task, "Dispose returned before releasing 2401", invocation);
            await fixture.WaitAsync(disposeWorker, "first Dispose worker joined", invocation);
            fixture.AssertHeldInvocation(invocation);

            fixture.Dispatch(late, 2402);
            repeatDisposeWorker = Task.Run(() => fixture.Runtime.Dispose(), TestContext.Current.CancellationToken);
            await fixture.WaitAsync(repeatDisposeWorker, "repeated Dispose returned while 2401 held", invocation);
            fixture.AssertHeldInvocation(invocation);

            actualDrain = fixture.Manager.StopAsync();
            Assert.False(actualDrain.IsCompleted, fixture.Describe(invocation, actualDrain));
            invocation.Release.TrySetResult();
            await fixture.WaitAsync(actualDrain, "2401 actual drain after direct Dispose", invocation);
            stop = fixture.Runtime.StopAsync(TestContext.Current.CancellationToken);
            await fixture.WaitAsync(stop, "direct Dispose cleanup joins callback monitor", invocation);
            fixture.AssertCompletedInvocation(invocation, "completed-after-dispose-returned");
            Assert.Equal(0, late.InvocationCount);
            Assert.False(late.Entered.Task.IsCompleted);
            fixture.AssertRootIsLive();
        }
        finally
        {
            invocation.ReleaseForCleanup();
            late.ReleaseForCleanup();
            await fixture.CleanupAsync(disposeWorker, repeatDisposeWorker, actualDrain, stop);
        }
    }

    private static async Task<object?> AwaitCallbackAsync(CallbackCompletionSource source)
    {
        await source.Completed.Task;
        if (source.Exception is { } exception)
        {
            throw exception;
        }

        return source.Result;
    }

    private static InvokableObjectManager ReadLocalObjects(OutsideRuntimeClient runtime)
    {
        var field = typeof(OutsideRuntimeClient).GetField("localObjects", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OutsideRuntimeClient.localObjects is required to admit observer work without transport.");
        return field.GetValue(runtime) as InvokableObjectManager
            ?? throw new InvalidOperationException("OutsideRuntimeClient.localObjects was not an initialized InvokableObjectManager after ConsumeServices.");
    }

    private static ConcurrentDictionary<CorrelationId, CallbackData> ReadCallbacks(OutsideRuntimeClient runtime)
    {
        var field = typeof(OutsideRuntimeClient).GetField("callbacks", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OutsideRuntimeClient.callbacks is required to model an already-issued outbound request.");
        return field.GetValue(runtime) as ConcurrentDictionary<CorrelationId, CallbackData>
            ?? throw new InvalidOperationException("OutsideRuntimeClient.callbacks was not a ConcurrentDictionary<CorrelationId, CallbackData>.");
    }

    private sealed record ActivationSnapshot(
        ClientGrainContext Context,
        IServiceProvider Services,
        RootServiceMarker Marker,
        int MarkerDisposeCount);

    private sealed class RootServiceMarker : IDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ObservedInvocation : TestInvokable
    {
        private readonly string _result;
        private readonly Func<Task<string>>? _work;
        private int _completionCount;

        internal ObservedInvocation(string result, Func<Task<string>>? work = null) : base(result)
        {
            _result = result;
            _work = work;
        }

        internal TaskCompletionSource WorkEntered { get; } = DrainTestHelpers.CreateSignal();
        internal ActivationSnapshot? Before { get; private set; }
        internal ActivationSnapshot? After { get; private set; }
        internal Exception? Failure { get; private set; }
        internal string? CompletedResult { get; private set; }
        internal int CompletionCount => Volatile.Read(ref _completionCount);
        internal Action? OnCompleted { get; init; }

        protected override async ValueTask<Response> InvokeCoreAsync()
        {
            try
            {
                Before = CaptureActivationServices();
                WorkEntered.TrySetResult();
                if (_work is null)
                {
                    await Release.Task;
                    CompletedResult = _result;
                }
                else
                {
                    CompletedResult = await _work();
                }

                After = CaptureActivationServices();
                Interlocked.Increment(ref _completionCount);
                OnCompleted?.Invoke();
                return Response.FromResult(CompletedResult);
            }
            catch (Exception exception)
            {
                // OneWay processing logs invocation failures; expose them to the test as well.
                Failure = exception;
                throw;
            }
        }

        private ActivationSnapshot CaptureActivationServices()
        {
            // LocalObjectData.ActivationServices intentionally throws. Resolve the real root
            // context through the actual invocation target holder's component fallback instead.
            var context = TargetHolder?.GetComponent(typeof(ClientGrainContext)) as ClientGrainContext
                ?? throw new InvalidOperationException("The observer target holder did not expose its ClientGrainContext.");
            var services = context.ActivationServices;
            var marker = services.GetRequiredService<RootServiceMarker>();
            return new(context, services, marker, marker.DisposeCount);
        }
    }

    private sealed class LifecycleProbe : ILifecycleParticipant<IClusterClientLifecycle>, ILifecycleObserver
    {
        private int _startCount;
        private int _stopCount;
        internal ClusterClientLifecycle Lifecycle { get; private set; } = null!;
        internal TaskCompletionSource Started { get; } = DrainTestHelpers.CreateSignal();
        internal TaskCompletionSource StopEntered { get; } = DrainTestHelpers.CreateSignal();
        internal TaskCompletionSource StopReturned { get; } = DrainTestHelpers.CreateSignal();
        internal int StartCount => Volatile.Read(ref _startCount);
        internal int StopCount => Volatile.Read(ref _stopCount);
        internal CancellationToken StopToken { get; private set; }
        internal Func<CancellationToken, Task>? OnStopping { get; set; }

        public void Participate(IClusterClientLifecycle lifecycle)
        {
            Lifecycle = lifecycle as ClusterClientLifecycle
                ?? throw new InvalidOperationException("ClusterClient did not supply its real ClusterClientLifecycle.");
            lifecycle.Subscribe("upper-stage-cleanup", ServiceLifecycleStage.RuntimeInitialize, this);
        }

        public Task OnStart(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _startCount);
            Started.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task OnStop(CancellationToken cancellationToken)
        {
            StopToken = cancellationToken;
            Interlocked.Increment(ref _stopCount);
            StopEntered.TrySetResult();
            if (OnStopping is { } onStopping)
            {
                await onStopping(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            StopReturned.TrySetResult();
        }
    }

    private sealed class ExternalClientFixture
    {
        private readonly LifecycleProbe? _lifecycle;
        private readonly DrainObserver _observer = new();
        private readonly ClientGrainContext _rootContext;
        private readonly RootServiceMarker _rootMarker;
        private readonly GrainId _sender = ClientGrainId.Create("observer-drain-sender").GrainId;

        internal ExternalClientFixture(LifecycleProbe? lifecycle = null)
        {
            _lifecycle = lifecycle;
            var options = Options.Create(new ClientMessagingOptions { LocalAddress = IPAddress.Loopback });
            var localDetails = new LocalClientDetails(options);
            var services = new ServiceCollection();
            services.AddSerializer();
            services.AddLogging();
            services.AddMetrics();
            services.AddSingleton(_ => new RootServiceMarker());
            services.AddSingleton(Substitute.For<IInternalGrainFactory>());
            services.AddSingleton(Substitute.For<IGrainReferenceRuntime>());
            services.AddSingleton(Substitute.For<IGrainCallCancellationManager>());
            OutsideRuntimeClient runtime = null!;
            services.AddSingleton(_ => new ClientGrainContext(runtime));
            services.AddSingleton(provider => new ClientManifestProvider(
                [],
                Options.Create(new GrainTypeOptions()),
                new GrainInterfaceTypeResolver(
                    [], provider.GetRequiredService<Orleans.Serialization.TypeSystem.TypeConverter>())));
            services.AddSingleton(provider => new ClientClusterManifestProvider(
                provider,
                localDetails,
                gatewayManager: null!, // Constructor-only sentinel: this provider is never started.
                NullLogger<ClientClusterManifestProvider>.Instance,
                provider.GetRequiredService<ClientManifestProvider>(),
                Options.Create(new TypeManagementOptions())));
            if (lifecycle is not null)
            {
                services.AddSingleton<ILifecycleParticipant<IClusterClientLifecycle>>(lifecycle);
                services.AddSingleton<ILifecycleParticipant<IClusterClientLifecycle>>(_ =>
                    new ConnectionManagerLifecycleAdapter<IClusterClientLifecycle>(Connections, ct =>
                    {
                        var drain = runtime.StopObserverInvocationsAsync();
                        ObserverDrainStarted.TrySetResult();
                        return drain.WaitAsync(ct);
                    }));
            }

            // MessageFactory is consumed but never sends through the deliberately absent transport.
            MessagingTrace trace = null!;
            services.AddSingleton(provider => new MessageFactory(
                provider.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, trace));
            Services = services.BuildServiceProvider();
            Instruments = new OrleansInstruments(Services.GetRequiredService<IMeterFactory>());
            trace = new MessagingTrace(
                NullLoggerFactory.Instance, new MessagingInstruments(Instruments), new MessagingProcessingInstruments(Instruments));
            runtime = Runtime = new OutsideRuntimeClient(
                localDetails, NullLoggerFactory.Instance, options, trace, Services,
                TimeProvider, new InterfaceToImplementationMappingCache(), Instruments);
            if (lifecycle is null)
            {
                Runtime.ConsumeServices();
            }
            else
            {
                // ClusterClient consumes services itself. Consuming twice would replace the manager
                // and orphan a callback monitor, so only one of these branches may do so.
                ClusterClient = new ClusterClient(Services, Runtime, NullLoggerFactory.Instance, options, localDetails);
            }

            Manager = ReadLocalObjects(Runtime);
            _rootContext = Services.GetRequiredService<ClientGrainContext>();
            _rootMarker = Services.GetRequiredService<RootServiceMarker>();
            ObserverId = ObserverGrainId.Create(localDetails.ClientId, IdSpan.Create("observer"));
            Assert.True(Manager.TryRegister(_observer, ObserverId));
        }

        internal ServiceProvider Services { get; }
        internal FakeTimeProvider TimeProvider { get; } = new();
        internal OrleansInstruments Instruments { get; }
        internal OutsideRuntimeClient Runtime { get; }
        internal ClusterClient? ClusterClient { get; }
        internal InvokableObjectManager Manager { get; }
        internal ObserverGrainId ObserverId { get; }
        internal ConnectionManager Connections { get; } = new(
            Options.Create(new ConnectionOptions()),
            connectionFactory: null!,
            NullLogger<ConnectionManager>.Instance);
        internal TaskCompletionSource ObserverDrainStarted { get; } = DrainTestHelpers.CreateSignal();

        internal void Dispatch(TestInvokable invocation, long id) => Manager.Dispatch(new Message
        {
            // No ClientMessageCenter or CurrentActivationAddress exists in this unstarted fixture.
            Direction = Message.Directions.OneWay,
            TargetGrain = ObserverId.GrainId,
            SendingGrain = _sender,
            Id = new CorrelationId(id),
            BodyObject = invocation
        });

        internal async Task StartFakeLifecycleAsync()
        {
            var lifecycle = _lifecycle ?? throw new InvalidOperationException("No fake lifecycle participant was registered.");
            await WaitAsync(lifecycle.Lifecycle.OnStart(CancellationToken.None), "only fake lifecycle subscribers started");
            await WaitAsync(lifecycle.Started.Task, "fake transport start observed");
            Assert.Equal(1, lifecycle.StartCount);
            Assert.Equal(0, lifecycle.StopCount);
        }

        internal Task WaitAsync(Task task, string phase, ObservedInvocation? invocation = null) =>
            DrainTestHelpers.AwaitPhaseAsync(task, phase, () => Describe(invocation, task), TestContext.Current.CancellationToken);

        internal string Describe(ObservedInvocation? invocation, Task? operation) =>
            $"Observer={ObserverId}; Invoke={invocation?.InvocationCount}; Complete={invocation?.CompletionCount}; " +
            $"Entered={invocation?.WorkEntered.Task.Status}; Exited={invocation?.Exited.Task.Status}; " +
            $"Failure={invocation?.Failure}; Operation={operation?.Status}; " +
            $"LifecycleStart={_lifecycle?.StartCount}; LifecycleStop={_lifecycle?.StopCount}; RootDisposed={_rootMarker.DisposeCount}.";

        internal void AssertHeldInvocation(ObservedInvocation invocation)
        {
            Assert.Equal(1, invocation.InvocationCount);
            Assert.Equal(0, invocation.CompletionCount);
            Assert.False(invocation.Exited.Task.IsCompleted, Describe(invocation, null));
            Assert.Null(invocation.Failure);
            Assert.Null(invocation.CompletedResult);
            Assert.Null(invocation.After);
            AssertActivationSnapshot(invocation.Before);
            AssertRootIsLive();
        }

        internal void AssertCompletedInvocation(ObservedInvocation invocation, string expectedResult)
        {
            Assert.Equal(1, invocation.InvocationCount);
            Assert.Equal(1, invocation.CompletionCount);
            Assert.True(invocation.Exited.Task.IsCompletedSuccessfully, Describe(invocation, null));
            Assert.Null(invocation.Failure);
            Assert.Equal(expectedResult, invocation.CompletedResult);
            Assert.Same(_observer, invocation.TargetHolder!.GetTarget());
            AssertActivationSnapshot(invocation.Before);
            AssertActivationSnapshot(invocation.After);
        }

        private void AssertActivationSnapshot(ActivationSnapshot? value)
        {
            var snapshot = Assert.IsType<ActivationSnapshot>(value);
            Assert.Same(_rootContext, snapshot.Context);
            Assert.Same(Services, snapshot.Services);
            Assert.Same(_rootMarker, snapshot.Marker);
            Assert.Equal(0, snapshot.MarkerDisposeCount);
        }

        internal void AssertRootIsLive()
        {
            // OutsideRuntimeClient borrows this root provider; it does not own an observer scope.
            Assert.Same(Services, Runtime.ServiceProvider);
            Assert.Same(Services, _rootContext.ActivationServices);
            Assert.Same(_rootMarker, Services.GetRequiredService<RootServiceMarker>());
            Assert.Equal(0, _rootMarker.DisposeCount);
        }

        internal async Task CleanupAsync(params Task?[] operations)
        {
            // The caller releases its holds (or performs failure-only callback rescue) first.
            // Independent, uncanceled backstops prevent runner/stop cancellation skipping cleanup.
            foreach (var operation in operations)
            {
                if (operation is null)
                {
                    continue;
                }

                try
                {
                    await DrainTestHelpers.AwaitPhaseAsync(
                        operation, "cleanup joins recorded stop/dispose operation", () => Describe(null, operation));
                }
                catch (OperationCanceledException) when (operation.IsCanceled)
                {
                    // This is the caller's bounded wait, not proof that admitted execution ended.
                }
            }

            var drain = Manager.StopAsync();
            await DrainTestHelpers.AwaitPhaseAsync(drain, "cleanup actual observer drain", () => Describe(null, drain));
            if (_lifecycle is { StartCount: > 0, StopCount: 0 } lifecycle)
            {
                // A drain-first cluster implementation can leave fake teardown uncalled when the
                // caller cancels. Stop only those fake subscribers after proving the real drain.
                var lifecycleStop = lifecycle.Lifecycle.OnStop(CancellationToken.None);
                await DrainTestHelpers.AwaitPhaseAsync(
                    lifecycleStop, "cleanup fake lifecycle stop", () => Describe(null, lifecycleStop));
            }

            var stop = Runtime.StopAsync(CancellationToken.None);
            await DrainTestHelpers.AwaitPhaseAsync(stop, "cleanup joins runtime callback monitor", () => Describe(null, stop));
            if (!Connections.Closed.IsCompleted)
            {
                await Connections.Close(CancellationToken.None);
            }

            GC.KeepAlive(_observer);
            // The fire-and-forget Dispose continuation is not directly awaited. Its only resource
            // teardown is MessageCenter?.Dispose(), and this fixture never constructs that transport.
            // If either positive drain/monitor join above fails, do NOT dispose these live services.
            await Services.DisposeAsync();
        }
    }
}
