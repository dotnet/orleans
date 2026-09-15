using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Linq;
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
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class HostedClientDrainTests
{
    [Fact]
    public async Task OnStop_DrainsChannelAdmittedUndispatchedMessages()
    {
        var fixture = new HostedFixture(nameof(OnStop_DrainsChannelAdmittedUndispatchedMessages));
        var first = fixture.Track(new ClassifierHeldInvokable(1601, fixture.DescribeState, fixture.Events));
        var second = fixture.Track(new ScopeCheckingInvokable(1602, fixture.DescribeState, fixture.Events));
        try
        {
            await fixture.StartAsync();
            fixture.Deliver(first);
            await fixture.WaitAsync(first.ClassifierEntered.Task, "1601: second GetInterfaceType entered on hosted pump");
            Assert.Equal(2, first.ClassifierCalls);
            Assert.Equal(0, first.InvocationCount);

            // The only reader is blocked BEFORE manager admission. Returning from this call proves
            // message 1602 was admitted to the channel, not merely queued by the test scheduler.
            fixture.Deliver(second);
            Assert.Equal(0, second.InvocationCount);
            var stop = fixture.Stop(TestContext.Current.CancellationToken);
            Assert.False(stop.IsCompleted, fixture.DescribeState());

            first.ClassifierRelease.TrySetResult();
            await fixture.WaitAsync(first.ScopeEntered.Task, "1601: invocation entered after classifier release");
            Assert.Equal(0, second.InvocationCount);
            first.Release.TrySetResult();
            await fixture.WaitAsync(second.ScopeEntered.Task, "1602: channel-admitted invocation entered");
            Assert.True(first.Exited.Task.IsCompletedSuccessfully, fixture.DescribeState());
            Assert.False(second.Exited.Task.IsCompleted, fixture.DescribeState());
            Assert.False(stop.IsCompleted, fixture.DescribeState());

            second.Release.TrySetResult();
            await fixture.WaitAsync(stop, "hosted pump and both manager admissions drained");
            fixture.AssertExecuted(first, second);
            Assert.Equal(
                new[] { "1601:entered", "1601:exiting", "1602:entered", "1602:exiting" },
                fixture.Events.ToArray());
            Assert.Equal(2, first.ClassifierCalls);
            fixture.AssertScopeLive();
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    [Fact]
    public async Task ReceiveMessage_CompletesOutstandingResponseDuringDrain()
    {
        var fixture = new HostedFixture(nameof(ReceiveMessage_CompletesOutstandingResponseDuringDrain));
        var invocation = fixture.Track(new ScopeCheckingInvokable(1701, fixture.DescribeState, fixture.Events));
        CallbackData? callback = null;
        var completion = new CallbackCompletionSource();
        var outgoing = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(1702),
            SendingGrain = fixture.Hosted.GrainId,
            TargetGrain = fixture.Sender,
        };
        var key = (outgoing.SendingGrain, outgoing.Id);
        var succeeded = false;
        try
        {
            var callbacks = ReadCallbacks(fixture.Runtime);
            var shared = new SharedCallbackData(
                message => callbacks.TryRemove((message.SendingGrain, message.Id), out _),
                NullLogger<CallbackData>.Instance,
                fixture.Clock,
                TimeSpan.FromMinutes(1),
                cancelOnTimeout: false,
                waitForCancellationAcknowledgement: false,
                cancellationManager: Substitute.For<IGrainCallCancellationManager>());
            callback = new CallbackData(shared, completion, outgoing, fixture.ApplicationRequests);
            Assert.True(callbacks.TryAdd(key, callback), fixture.DescribeState());
            Assert.False(callback.IsCompleted);
            Assert.Equal(0, completion.CompletionCount);

            await fixture.StartAsync();
            fixture.Deliver(invocation);
            await fixture.WaitAsync(invocation.ScopeEntered.Task, "1701: observer held before response");
            var stop = fixture.Stop(TestContext.Current.CancellationToken);
            Assert.False(stop.IsCompleted, fixture.DescribeState());

            // Model an already-issued outbound call, without SendRequest/routing. Success responses
            // take InsideRuntimeClient's callback-only branch: no directory or transport is touched.
            using var responseBody = Response.FromResult(731);
            var response = new Message
            {
                Direction = Message.Directions.Response,
                Result = Message.ResponseTypes.Success,
                Id = outgoing.Id,
                TargetGrain = outgoing.SendingGrain,
                SendingGrain = outgoing.TargetGrain,
                BodyObject = responseBody,
            };
            fixture.Hosted.ReceiveMessage(response);
            await fixture.WaitAsync(
                completion.Completed.Task,
                "1702: real callback completed while 1701 remains held",
                () => $"callbackCount={completion.CompletionCount}; ledgerContainsKey={callbacks.ContainsKey(key)}");
            Assert.Equal(731, Assert.IsType<int>(completion.Result));
            Assert.Null(completion.Exception);
            Assert.Equal(1, completion.CompletionCount);
            Assert.True(callback.IsCompleted);
            Assert.False(callbacks.ContainsKey(key));
            Assert.False(invocation.Release.Task.IsCompleted);
            Assert.False(invocation.Exited.Task.IsCompleted, fixture.DescribeState());
            Assert.False(stop.IsCompleted, fixture.DescribeState());

            // A duplicate response must not complete the original promise a second time.
            fixture.Hosted.ReceiveMessage(response);
            Assert.Equal(1, completion.CompletionCount);
            Assert.False(callbacks.ContainsKey(key));
            fixture.AssertScopeLive();

            invocation.Release.TrySetResult();
            await fixture.WaitAsync(stop, "1701: actual hosted drain after response completion");
            fixture.AssertExecuted(invocation);
            Assert.Equal(1, completion.CompletionCount);
            succeeded = true;
        }
        finally
        {
            // Failure-only rescue, after an assertion/phase has failed. It cannot establish any of
            // the success-path completion assertions above and never enters a transport path.
            try
            {
                if (!succeeded && callback is { IsCompleted: false })
                {
                    callback.OnHostShutdown();
                }
            }
            finally
            {
                await fixture.CleanupAsync();
            }
        }
    }

    [Fact]
    public async Task OnStop_CancellationControlBypassesClosedChannel()
    {
        var fixture = new HostedFixture(nameof(OnStop_CancellationControlBypassesClosedChannel));
        var invocation = fixture.Track(new CancellableScopeInvokable(1751, fixture.DescribeState, fixture.Events));
        var control = new CancellationControlInvokable(fixture.Sender, new CorrelationId(invocation.Id));
        control.Release.TrySetResult();
        try
        {
            await fixture.StartAsync();
            fixture.Deliver(invocation);
            await fixture.WaitAsync(invocation.ScopeEntered.Task, "1751: cancellable observer held");
            var stop = fixture.Stop(TestContext.Current.CancellationToken); // Closes the channel synchronously.
            Assert.False(stop.IsCompleted, fixture.DescribeState());
            Assert.Equal(0, invocation.CancelCount);

            // This must bypass the now-closed application channel and reach the real
            // LocalObjectData cancellation extension, not just an interleaved test body.
            fixture.Hosted.ReceiveMessage(new Message
            {
                Id = new CorrelationId(1752),
                TargetGrain = fixture.ObserverId,
                SendingGrain = fixture.Sender,
                Direction = Message.Directions.OneWay,
                IsAlwaysInterleave = true,
                BodyObject = control,
            });
            await fixture.WaitAsync(control.CancellationSent.Task, "1752: cancellation passed closed channel");
            await fixture.WaitAsync(stop, "1751: observer exited because hosted control canceled it");
            Assert.Equal(1, control.InvocationCount);
            Assert.True(control.Exited.Task.IsCompletedSuccessfully);
            Assert.Same(invocation.TargetHolder, control.CancellationTarget);
            Assert.Equal(1, invocation.CancelCount);
            Assert.True(((IInvokable)invocation).GetCancellationToken().IsCancellationRequested);
            fixture.AssertExecuted(invocation);
            fixture.AssertScopeLive();
        }
        finally
        {
            // No test release on the success path: only CancelRequest releases this body.
            await fixture.CleanupAsync();
        }
    }

    [Fact]
    public async Task OnStop_CancellationDefersScopeDisposalUntilDrain()
    {
        var fixture = new HostedFixture(nameof(OnStop_CancellationDefersScopeDisposalUntilDrain));
        var invocation = fixture.Track(new ScopeCheckingInvokable(1801, fixture.DescribeState, fixture.Events));
        using var stopCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            await fixture.StartAsync();
            fixture.Deliver(invocation);
            await fixture.WaitAsync(invocation.ScopeEntered.Task, "1801: observer using hosted scope");
            var canceledStop = fixture.Stop(stopCancellation.Token);
            Assert.False(canceledStop.IsCompleted, fixture.DescribeState());
            stopCancellation.Cancel();
            await fixture.WaitAsync(canceledStop, "canceled OnStop returned normally");
            Assert.True(canceledStop.IsCompletedSuccessfully, fixture.DescribeState());
            fixture.AssertScopeLive();
            Assert.False(invocation.Exited.Task.IsCompleted, fixture.DescribeState());

            // Host teardown can request Dispose after the canceled lifecycle wait has returned.
            await fixture.DisposeOnWorkerAsync("Dispose after canceled OnStop returned");
            fixture.AssertScopeLive();
            var drain = fixture.Stop(TestContext.Current.CancellationToken);
            Assert.False(drain.IsCompleted, fixture.DescribeState());
            Assert.False(invocation.Release.Task.IsCompleted);

            invocation.Release.TrySetResult();
            await fixture.WaitAsync(drain, "1801: uncanceled hosted drain");
            await fixture.WaitAsync(fixture.Scopes.ScopeDisposed.Task, "owned scope disposed after canceled stop");
            fixture.AssertExecuted(invocation);
            fixture.AssertDisposedAfterExecution();
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    [Fact]
    public async Task Dispose_ReturnsWithoutDisposingScopeDuringDrain()
    {
        var fixture = new HostedFixture(nameof(Dispose_ReturnsWithoutDisposingScopeDuringDrain));
        var invocation = fixture.Track(new ScopeCheckingInvokable(1901, fixture.DescribeState, fixture.Events));
        try
        {
            await fixture.StartAsync();
            fixture.Deliver(invocation);
            await fixture.WaitAsync(invocation.ScopeEntered.Task, "1901: observer held before direct Dispose");
            Assert.Equal(0, fixture.StopCalls);

            // Each worker signals only AFTER the synchronous Dispose call returns. A blocking
            // Dispose fails the bounded handshake; task scheduling/elapsed time is not the oracle.
            await fixture.DisposeOnWorkerAsync("first direct Dispose returned");
            await fixture.DisposeOnWorkerAsync("repeated direct Dispose returned");
            Assert.Equal(2, fixture.DisposeReturns);
            Assert.Equal(0, fixture.StopCalls);
            Assert.False(invocation.Release.Task.IsCompleted);
            Assert.False(invocation.Exited.Task.IsCompleted, fixture.DescribeState());
            fixture.AssertScopeLive();

            var drain = fixture.Stop(TestContext.Current.CancellationToken);
            Assert.False(drain.IsCompleted, fixture.DescribeState());
            invocation.Release.TrySetResult();
            await fixture.WaitAsync(drain, "1901: actual drain initiated by direct Dispose");
            await fixture.WaitAsync(fixture.Scopes.ScopeDisposed.Task, "scope disposal following direct Dispose");
            fixture.AssertExecuted(invocation);
            fixture.AssertDisposedAfterExecution();
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    [Fact]
    public async Task Dispose_DisposesOwnedScopeExactlyOnceAcrossRepeatedCalls()
    {
        var fixture = new HostedFixture(nameof(Dispose_DisposesOwnedScopeExactlyOnceAcrossRepeatedCalls));
        var invocation = fixture.Track(new ScopeCheckingInvokable(2001, fixture.DescribeState, fixture.Events));
        try
        {
            Assert.Equal(1, fixture.Scopes.CreateCount);
            await fixture.StartAsync();
            fixture.Deliver(invocation);
            await fixture.WaitAsync(invocation.ScopeEntered.Task, "2001: admitted scope owner");
            await fixture.DisposeOnWorkerAsync("ownership: first Dispose returned");
            await fixture.DisposeOnWorkerAsync("ownership: second Dispose returned during drain");
            await fixture.DisposeOnWorkerAsync("ownership: third Dispose returned during drain");
            Assert.Equal(3, fixture.DisposeReturns);
            Assert.False(invocation.Exited.Task.IsCompleted, fixture.DescribeState());
            fixture.AssertScopeLive();

            var drain = fixture.Stop(TestContext.Current.CancellationToken);
            Assert.False(drain.IsCompleted, fixture.DescribeState());
            invocation.Release.TrySetResult();
            await fixture.WaitAsync(drain, "2001: actual drain before ownership assertions");
            await fixture.WaitAsync(fixture.Scopes.ScopeDisposed.Task, "ownership: first scope disposal completed");
            fixture.AssertExecuted(invocation);
            fixture.AssertDisposedAfterExecution();

            // Do not just sample the count after the first background disposal signal. With the
            // real drain now complete, an unguarded Dispose would synchronously call the wrapper
            // again, before these handshakes. Underlying DI idempotence cannot hide those calls.
            await fixture.DisposeOnWorkerAsync("ownership: fourth Dispose returned after completed drain");
            await fixture.DisposeOnWorkerAsync("ownership: fifth Dispose returned after completed drain");
            Assert.Equal(5, fixture.DisposeReturns);
            fixture.AssertDisposedAfterExecution();
            Assert.Equal(
                new[] { "2001:entered", "2001:exiting", "scope-disposing", "marker-disposed", "scope-disposed" },
                fixture.Events.ToArray());
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    private static ConcurrentDictionary<(GrainId, CorrelationId), CallbackData> ReadCallbacks(InsideRuntimeClient runtime)
    {
        // The sole reflection seam: read the existing ledger, never replace it or inspect hosted
        // channel/manager/lifecycle/disposal state. This models an outstanding outbound call.
        var field = typeof(InsideRuntimeClient).GetField("callbacks", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(runtime) is not ConcurrentDictionary<(GrainId, CorrelationId), CallbackData> callbacks)
        {
            throw new InvalidOperationException(
                $"{nameof(ReceiveMessage_CompletesOutstandingResponseDuringDrain)} requires " +
                "InsideRuntimeClient.callbacks to be ConcurrentDictionary<(GrainId, CorrelationId), CallbackData>.");
        }

        return callbacks;
    }

    private sealed class HostedFixture
    {
        private readonly string _test;
        private readonly ServiceProvider _root;
        private readonly DrainObserver _observer = new();
        private readonly List<ScopeCheckingInvokable> _invocations = [];
        private readonly List<Task> _workers = [];
        private readonly ConcurrentQueue<Task> _stops = new();
        private readonly ILifecycleObserver _lifecycleObserver;
        private readonly IDisposable _subscription = Substitute.For<IDisposable>();
        private int _disposeReturns;

        internal HostedFixture(string test)
        {
            _test = test;
            var services = new ServiceCollection();
            services.AddSerializer();
            services.AddLogging();
            services.AddMetrics();
            services.AddScoped(_ => new ScopeMarker(Events));
            _root = services.BuildServiceProvider();

            Scopes = new ScopeTrackingProvider(
                _root,
                () => _invocations.All(invocation => invocation.Exited.Task.IsCompletedSuccessfully),
                Events);
            var deepCopier = _root.GetRequiredService<DeepCopier>();
            var instruments = new OrleansInstruments(_root.GetRequiredService<IMeterFactory>());
            ApplicationRequests = new ApplicationRequestInstruments(instruments);
            var messaging = new MessagingInstruments(instruments);
            var processing = new MessagingProcessingInstruments(instruments);
            var loggerFactory = NullLoggerFactory.Instance;
            var trace = new MessagingTrace(loggerFactory, messaging, processing);
            var messageFactory = new MessageFactory(deepCopier, NullLogger<MessageFactory>.Instance, trace);
            var options = Options.Create(new SiloMessagingOptions());
            var mapping = new InterfaceToImplementationMappingCache();
            var referenceRuntime = Substitute.For<IGrainReferenceRuntime>();
            var referenceActivator = new GrainReferenceActivator(
                Scopes, [new UntypedReferenceProvider(_root, referenceRuntime)]);
            var silo = Substitute.For<ILocalSiloDetails>();
            // Address data only: no socket, port allocation, DNS, or silo startup.
            silo.SiloAddress.Returns(SiloAddress.New(IPAddress.Loopback, 0, 1));
            silo.GatewayAddress.Returns((SiloAddress)null!);
            Runtime = new InsideRuntimeClient(
                silo, Scopes, messageFactory, loggerFactory, options, trace, referenceActivator,
                new GrainInterfaceTypeResolver(
                    [], _root.GetRequiredService<Orleans.Serialization.TypeSystem.TypeConverter>()),
                new GrainInterfaceTypeToGrainTypeResolver(Substitute.For<IClusterManifestProvider>()),
                deepCopier, Clock, mapping, instruments);

            // Constructor-only transport graph. GatewayAddress == null guards the throwing factory.
            // Catalog/connections/placement/locator are only reached by transport operations:
            // we never start/stop MessageCenter, send outbound calls, or deliver rejection/status
            // responses. All observer bodies are valid OneWay invokables, including on failure.
            var messageCenter = new MessageCenter(
                siloDetails: silo,
                messageFactory: messageFactory,
                catalog: null!,
                gatewayFactory: _ => throw new InvalidOperationException("The hosted drain fixture must not create a gateway."),
                logger: NullLogger<MessageCenter>.Instance,
                siloStatusOracle: Substitute.For<ISiloStatusOracle>(),
                senderManager: null!,
                messagingTrace: new RuntimeMessagingTrace(loggerFactory, messaging, processing),
                messagingInstruments: messaging,
                messagingProcessingInstruments: processing,
                messagingOptions: options,
                placementService: null!,
                grainLocator: null!,
                messageStatisticsSink: new NoOpMessageStatisticsSink());
            var grainFactory = Substitute.For<IInternalGrainFactory>();
            grainFactory.GetGrain(Arg.Any<GrainId>())
                .Returns(call => referenceActivator.CreateReference(call.Arg<GrainId>(), default));
            Hosted = new HostedClient(
                Runtime, silo, NullLogger<HostedClient>.Instance, referenceRuntime, grainFactory,
                messageCenter, trace, deepCopier, referenceActivator, mapping);
            Marker = Hosted.ActivationServices.GetRequiredService<ScopeMarker>();
            ObserverId = Assert.IsType<GrainReference>(Hosted.CreateObjectReference(_observer)).GrainId;

            ILifecycleObserver? observer = null;
            var subscriptions = 0;
            var lifecycle = Substitute.For<ISiloLifecycle>();
            lifecycle.HighestCompletedStage.Returns(0);
            lifecycle.LowestStoppedStage.Returns(int.MaxValue);
            lifecycle.Subscribe(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ILifecycleObserver>())
                .Returns(call =>
                {
                    Assert.Equal("HostedClient", call.Arg<string>());
                    Assert.Equal(ServiceLifecycleStage.RuntimeGrainServices, call.Arg<int>());
                    observer = call.Arg<ILifecycleObserver>();
                    subscriptions++;
                    return _subscription;
                });
            ((ILifecycleParticipant<ISiloLifecycle>)Hosted).Participate(lifecycle);
            Assert.Equal(1, subscriptions);
            _lifecycleObserver = observer ?? throw new InvalidOperationException("HostedClient did not subscribe its lifecycle observer.");
        }

        internal HostedClient Hosted { get; }
        internal InsideRuntimeClient Runtime { get; }
        internal ScopeTrackingProvider Scopes { get; }
        internal ScopeMarker Marker { get; }
        internal FakeTimeProvider Clock { get; } = new();
        internal ApplicationRequestInstruments ApplicationRequests { get; }
        internal ConcurrentQueue<string> Events { get; } = new();
        internal GrainId Sender { get; } = ClientGrainId.Create("hosted-drain-sender").GrainId;
        internal GrainId ObserverId { get; }
        internal int StopCalls => _stops.Count;
        internal int DisposeReturns => Volatile.Read(ref _disposeReturns);

        internal T Track<T>(T invocation) where T : ScopeCheckingInvokable
        {
            // All requests are registered before any lifecycle start or worker is launched.
            _invocations.Add(invocation);
            return invocation;
        }

        internal Task StartAsync() =>
            WaitAsync(_lifecycleObserver.OnStart(CancellationToken.None), "captured HostedClient OnStart");

        internal Task Stop(CancellationToken cancellationToken)
        {
            // Invoke directly: this closes the channel before returning, not on a scheduled worker.
            var stop = _lifecycleObserver.OnStop(cancellationToken);
            _stops.Enqueue(stop);
            return stop;
        }

        internal void Deliver(ScopeCheckingInvokable invocation) => Hosted.ReceiveMessage(new Message
        {
            Id = new CorrelationId(invocation.Id),
            TargetGrain = ObserverId,
            SendingGrain = Sender,
            Direction = Message.Directions.OneWay,
            BodyObject = invocation,
        });

        internal async Task DisposeOnWorkerAsync(string phase)
        {
            var returned = DrainTestHelpers.CreateSignal();
            var worker = Task.Run(() =>
            {
                try
                {
                    ((IDisposable)Hosted).Dispose();
                    Interlocked.Increment(ref _disposeReturns);
                    returned.TrySetResult();
                }
                catch (Exception exception)
                {
                    returned.TrySetException(exception);
                    throw;
                }
            }, TestContext.Current.CancellationToken);
            _workers.Add(worker);
            await WaitAsync(returned.Task, phase);
            await WaitAsync(worker, $"{phase}: worker joined");
        }

        internal Task WaitAsync(Task task, string phase, Func<string>? additionalState = null) =>
            DrainTestHelpers.AwaitPhaseAsync(
                task, phase, () => $"{DescribeState()} {additionalState?.Invoke()}",
                TestContext.Current.CancellationToken);

        internal string DescribeState() =>
            $"{_test}; observer={ObserverId}; " +
            $"requests=[{string.Join("; ", _invocations.Select(invocation => invocation.DescribeState()))}]; " +
            $"stops=[{string.Join(",", _stops.Select(stop => stop.Status))}]; " +
            $"disposeReturns={DisposeReturns}; scopesCreated={Scopes.CreateCount}; " +
            $"wrapperDisposeCalls={Scopes.DisposeCount}; markerDisposals={Marker.DisposeCount}; " +
            $"events=[{string.Join(",", Events)}]";

        internal void AssertScopeLive()
        {
            Assert.Equal(1, Scopes.CreateCount);
            Assert.Equal(0, Scopes.DisposeCount);
            Assert.Equal(0, Marker.DisposeCount);
            Assert.Same(Marker, Hosted.ActivationServices.GetRequiredService<ScopeMarker>());
        }

        internal void AssertExecuted(params ScopeCheckingInvokable[] invocations)
        {
            foreach (var invocation in invocations)
            {
                Assert.Equal(1, invocation.InvocationCount);
                Assert.True(invocation.Exited.Task.IsCompletedSuccessfully, DescribeState());
                Assert.Null(invocation.Failure);
                Assert.Same(_observer, invocation.ObservedTarget);
                Assert.Same(Marker, invocation.ObservedMarker);
                Assert.True(invocation.MarkerLiveOnEntry, DescribeState());
                Assert.True(invocation.MarkerLiveBeforeExit, DescribeState());
            }
        }

        internal void AssertDisposedAfterExecution()
        {
            Assert.Equal(1, Scopes.CreateCount);
            Assert.Equal(1, Scopes.DisposeCount);
            Assert.Equal(1, Marker.DisposeCount);
            Assert.True(Scopes.AllBodiesExitedAtDisposal, DescribeState());
            Assert.True(Scopes.ScopeDisposed.Task.IsCompletedSuccessfully, DescribeState());
        }

        internal async Task CleanupAsync()
        {
            foreach (var invocation in _invocations)
            {
                invocation.ReleaseForCleanup();
            }

            // Cleanup has an independent, uncanceled backstop. If a worker/drain is unfinished,
            // throw with context and deliberately leave providers intact for any live execution.
            var drain = Stop(CancellationToken.None);
            foreach (var worker in _workers)
            {
                try
                {
                    await DrainTestHelpers.AwaitCleanupAsync(
                        worker, "cleanup: Dispose worker joined", DescribeState);
                }
                catch (OperationCanceledException) when (worker.IsCanceled && TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                    // Task.Run can be canceled before the worker starts. There is no
                    // worker execution to join; the independently started drain still must finish.
                }
            }

            await DrainTestHelpers.AwaitCleanupAsync(
                Task.WhenAll(_stops), "cleanup: actual hosted drains joined", DescribeState);
            await DrainTestHelpers.AwaitCleanupAsync(drain, "cleanup: pump and manager drained", DescribeState);
            // Also join bodies which actually entered, so a failing early-drain regression cannot
            // let cleanup dispose providers while those already-started bodies are unwinding.
            // Uninvoked/rejected messages have no Exited signal and are not waited here.
            await DrainTestHelpers.AwaitCleanupAsync(
                Task.WhenAll(_invocations.Where(invocation => invocation.InvocationCount != 0)
                    .Select(invocation => invocation.Exited.Task)),
                "cleanup: entered invocation bodies exited", DescribeState);
            ((IDisposable)Hosted).Dispose();
            await DrainTestHelpers.AwaitCleanupAsync(
                Scopes.ScopeDisposed.Task, "cleanup: owned scope disposal completed", DescribeState);
            _subscription.Dispose();

            // ScopeDisposed is emitted after the real scope's Dispose, not after HostedClient's
            // whole background method. Its remaining tail ONLY calls MessageCenter.SetHostedClient(null)
            // (a field assignment); it cannot use the serializer/metrics root disposed below.
            // InsideRuntimeClient has no public Dispose/stop API in this snapshot. We never
            // Participate/ConsumeServices/start it: its monitor has no task, and its timer belongs
            // to this unadvanced FakeTimeProvider, with no real-time/thread-pool timer to tear down.
            _root.Dispose();
            foreach (var invocation in _invocations)
            {
                ((IDisposable)invocation).Dispose();
            }

            GC.KeepAlive(_observer);
        }
    }

    private class ScopeCheckingInvokable : TestInvokable
    {
        private readonly Func<string> _describeState;
        private readonly ConcurrentQueue<string> _events;

        internal ScopeCheckingInvokable(long id, Func<string> describeState, ConcurrentQueue<string> events)
            : base($"hosted-{id}")
        {
            Id = id;
            _describeState = describeState;
            _events = events;
        }

        internal long Id { get; }
        internal TaskCompletionSource ScopeEntered { get; } = DrainTestHelpers.CreateSignal();
        internal object? ObservedTarget { get; private set; }
        internal ScopeMarker? ObservedMarker { get; private set; }
        internal bool MarkerLiveOnEntry { get; private set; }
        internal bool MarkerLiveBeforeExit { get; private set; }
        internal Exception? Failure { get; private set; }

        protected override async ValueTask<Response> InvokeCoreAsync()
        {
            try
            {
                ObservedTarget = TargetHolder?.GetTarget();
                // Resolve through the actual LocalObjectData -> HostedClient component chain.
                // The test also checks identity against HostedClient.ActivationServices.
                ObservedMarker = TargetHolder?.GetComponent(typeof(ScopeMarker)) as ScopeMarker
                    ?? throw new InvalidOperationException($"Request {Id} did not receive its hosted scope marker.");
                MarkerLiveOnEntry = ObservedMarker.DisposeCount == 0;
                _events.Enqueue($"{Id}:entered");
                ScopeEntered.TrySetResult();
                await DrainTestHelpers.AwaitPhaseAsync(
                    Release.Task, $"{Id}: body release", _describeState, TestContext.Current.CancellationToken);
                MarkerLiveBeforeExit = ObservedMarker.DisposeCount == 0;
                _events.Enqueue($"{Id}:exiting");
                return Response.Completed;
            }
            catch (Exception exception)
            {
                // OneWay exceptions are logged by production, so retain failures for the test
                // instead of mistaking the base helper's finally/Exited signal for body success.
                Failure = exception;
                ScopeEntered.TrySetException(exception);
                throw;
            }
        }

        internal string DescribeState() =>
            $"id={Id}, invokes={InvocationCount}, scopeEntered={ScopeEntered.Task.Status}, " +
            $"release={Release.Task.Status}, exited={Exited.Task.Status}, failure={Failure?.Message}";
    }

    private sealed class CancellableScopeInvokable(
        long id, Func<string> describeState, ConcurrentQueue<string> events)
        : ScopeCheckingInvokable(id, describeState, events)
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _cancelCount;
        internal int CancelCount => Volatile.Read(ref _cancelCount);
        protected override bool SupportsCancellation => true;
        protected override CancellationToken RequestCancellationToken => _cancellation.Token;

        protected override bool CancelRequest()
        {
            Interlocked.Increment(ref _cancelCount);
            _cancellation.Cancel();
            // A cooperative observer is allowed to finish normally after cancellation.
            Release.TrySetResult();
            return true;
        }

        protected override void DisposeCore() => _cancellation.Dispose();
    }

    private sealed class ClassifierHeldInvokable : ScopeCheckingInvokable
    {
        private readonly Func<string> _describeState;
        private int _classifierCalls;

        internal ClassifierHeldInvokable(long id, Func<string> describeState, ConcurrentQueue<string> events)
            : base(id, describeState, events) => _describeState = describeState;

        internal TaskCompletionSource ClassifierEntered { get; } = DrainTestHelpers.CreateSignal();
        internal TaskCompletionSource ClassifierRelease { get; } = DrainTestHelpers.CreateSignal();
        internal int ClassifierCalls => Volatile.Read(ref _classifierCalls);

        protected override Type InterfaceType
        {
            get
            {
                // Call 1 is Hosted.ReceiveMessage on the orchestrator. Call 2 is the hosted
                // pump's LocalObjectData.ReceiveMessage, immediately before gate.TryEnter().
                if (Interlocked.Increment(ref _classifierCalls) == 2)
                {
                    ClassifierEntered.TrySetResult();
                    DrainTestHelpers.AwaitPhaseAsync(
                        ClassifierRelease.Task, $"{Id}: classifier release on pump worker", _describeState,
                        TestContext.Current.CancellationToken)
                        .GetAwaiter().GetResult();
                }

                return typeof(IGrainObserver);
            }
        }

        internal override void ReleaseForCleanup()
        {
            ClassifierRelease.TrySetResult();
            base.ReleaseForCleanup();
        }
    }

    private sealed class ScopeMarker(ConcurrentQueue<string> events) : IDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            events.Enqueue("marker-disposed");
        }
    }

    private sealed class ScopeTrackingProvider(
        ServiceProvider root,
        Func<bool> allBodiesExited,
        ConcurrentQueue<string> events) : IServiceProvider, IServiceScopeFactory
    {
        private int _createCount;
        private int _disposeCount;
        private int _earlyDisposals;
        internal int CreateCount => Volatile.Read(ref _createCount);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal bool AllBodiesExitedAtDisposal => DisposeCount != 0 && Volatile.Read(ref _earlyDisposals) == 0;
        internal TaskCompletionSource ScopeDisposed { get; } = DrainTestHelpers.CreateSignal();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceScopeFactory) ? this : root.GetService(serviceType);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _createCount);
            return new CountingScope(root.CreateScope(), this);
        }

        private void DisposeScope(IServiceScope scope)
        {
            // Count EVERY wrapper call, even if real DI makes subsequent disposals no-ops.
            Interlocked.Increment(ref _disposeCount);
            if (!allBodiesExited())
            {
                Interlocked.Increment(ref _earlyDisposals);
            }

            events.Enqueue("scope-disposing");
            try
            {
                scope.Dispose();
                events.Enqueue("scope-disposed");
                ScopeDisposed.TrySetResult();
            }
            catch (Exception exception)
            {
                ScopeDisposed.TrySetException(exception);
                throw;
            }
        }

        private sealed class CountingScope(IServiceScope scope, ScopeTrackingProvider owner) : IServiceScope
        {
            public IServiceProvider ServiceProvider => scope.ServiceProvider;
            public void Dispose() => owner.DisposeScope(scope);
        }
    }

    private sealed class UntypedReferenceProvider(
        IServiceProvider services,
        IGrainReferenceRuntime runtime) : IGrainReferenceActivatorProvider
    {
        public bool TryGet(
            GrainType grainType,
            GrainInterfaceType interfaceType,
            [NotNullWhen(true)] out IGrainReferenceActivator? activator)
        {
            var shared = new GrainReferenceShared(
                grainType, interfaceType, 0, runtime, InvokeMethodOptions.None,
                services.GetRequiredService<CodecProvider>(),
                services.GetRequiredService<CopyContextPool>(), services);
            activator = new UntypedReferenceActivator(shared);
            return true;
        }

        private sealed class UntypedReferenceActivator(GrainReferenceShared shared) : IGrainReferenceActivator
        {
            public GrainReference CreateReference(GrainId grainId) => GrainReference.FromGrainId(shared, grainId);
        }
    }
}
