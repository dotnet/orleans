using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Timers;
using Xunit;

namespace NonSilo.Tests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GrainTimerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ImmediateTick_UsesOrdinaryActivationAdmission(bool interleave, bool keepAlive)
    {
        using var fixture = new TimerFixture();
        using var reentrancy = RequestContext.AllowCallChainReentrancy();
        var state = new object();
        var parent = new UncopyableContextValue();
        var calls = 0;
        RequestContext.Set("parent", parent);
        try
        {
            using var timer = fixture.Register((value, token) =>
            {
                Assert.Same(state, value);
                Assert.False(token.IsCancellationRequested);
                Assert.Equal(Guid.Empty, RequestContext.ReentrancyId);
                Assert.Null(RequestContext.Get("parent"));
                calls++;
                return Task.CompletedTask;
            }, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan, interleave, keepAlive);

            var request = Assert.Single(fixture.Requests);
            Assert.Equal(0, calls);
            Assert.Equal(1, fixture.Time.TimerCreations);
            Assert.Equal(interleave, request.Options.Interleave);
            Assert.Equal(keepAlive, request.Options.KeepAlive);
            Assert.Same(parent, RequestContext.Get("parent"));
            await fixture.InvokeAsync(request);
            fixture.Time.Advance(TimeSpan.FromDays(1));
            Assert.Equal(1, calls);
            Assert.Single(fixture.Requests);
        }
        finally
        {
            RequestContext.Remove("parent");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void MessageFactory_CreateLocalRequest_CreatesOneWayLocalMessage(bool interleave, bool keepAlive)
    {
        using var fixture = new TimerFixture();
        RequestContext.Set("parent", new UncopyableContextValue());
        try
        {
            var body = Substitute.For<ILocalRequest>();
            var message = fixture.MessageFactory.CreateLocalRequest(fixture.Grain, body, new() { Interleave = interleave, KeepAlive = keepAlive });

            Assert.Same(body, message.BodyObject);
            Assert.Equal(Message.Directions.OneWay, message.Direction);
            Assert.Equal(interleave, message.IsAlwaysInterleave);
            Assert.Equal(keepAlive, message.IsKeepAlive);
            Assert.True(message.IsLocalOnly);
            Assert.Null(message.TimeToLive);
            Assert.Null(message.RequestContextData);
            Assert.True(message.InterfaceType.IsDefault);
            Assert.Equal(fixture.Grain.GrainId, message.TargetGrain);
            Assert.Equal(message.SendingGrain, message.TargetGrain);
            Assert.Equal(fixture.Grain.Address.SiloAddress, message.TargetSilo);
            Assert.Equal(message.SendingSilo, message.TargetSilo);
        }
        finally
        {
            RequestContext.Remove("parent");
        }
    }

    [Fact]
    public void MessageFactory_DefaultContextIsCopiedWhileExplicitContextSkipsExport()
    {
        using var fixture = new TimerFixture();
        var value = new List<int> { 1, 2 };
        RequestContext.Set("parent", value);
        try
        {
            var message = fixture.MessageFactory.CreateMessage(null, Orleans.CodeGeneration.InvokeMethodOptions.OneWay);
            var copied = Assert.IsType<List<int>>(message.RequestContextData!["parent"]);
            Assert.Equal(value, copied);
            Assert.NotSame(value, copied);
            value.Add(3);
            Assert.Equal(new[] { 1, 2 }, copied);

            RequestContext.Set("parent", new UncopyableContextValue());
            Assert.Throws<CodecNotFoundException>(() =>
                fixture.MessageFactory.CreateMessage(null, Orleans.CodeGeneration.InvokeMethodOptions.OneWay));
            var independent = fixture.MessageFactory.CreateMessage(null, Orleans.CodeGeneration.InvokeMethodOptions.OneWay, requestContextData: null);
            Assert.Null(independent.RequestContextData);
            Assert.IsType<UncopyableContextValue>(RequestContext.Get("parent"));
        }
        finally
        {
            RequestContext.Remove("parent");
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2000)]
    public async Task ChangeWhileQueued_PreservesTickAndUsesLatestFollowingSchedule(int milliseconds)
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Register((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        timer.Change(TimeSpan.Zero, TimeSpan.Zero);
        timer.Change(TimeSpan.FromMilliseconds(milliseconds), Timeout.InfiniteTimeSpan);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Single(fixture.Requests);
        await fixture.InvokeAsync(fixture.Requests[0]);
        Assert.Equal(1, calls);
        await VerifyFollowingTickAsync(fixture, milliseconds);
        Assert.Equal(milliseconds < 0 ? 1 : 2, calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2000)]
    public async Task ChangeDuringCallback_DefersFollowingTickUntilCompletion(int milliseconds)
    {
        using var fixture = new TimerFixture();
        var release = NewCompletion();
        var started = NewCompletion();
        var calls = 0;
        using var timer = fixture.Register(async (_, _) =>
        {
            if (++calls == 1)
            {
                started.SetResult();
                await release.Task;
            }
        }, 0, TimeSpan.Zero, TimeSpan.Zero, interleave: true);
        var invocation = fixture.InvokeAsync(Assert.Single(fixture.Requests));
        try
        {
            await started.Task;
            timer.Change(TimeSpan.Zero, TimeSpan.Zero);
            timer.Change(TimeSpan.FromMilliseconds(milliseconds), Timeout.InfiniteTimeSpan);
            fixture.Time.Advance(TimeSpan.FromDays(1));
            fixture.Time.FireDispatchedCallback();
            Assert.Equal(1, calls);
            Assert.Single(fixture.Requests);
        }
        finally
        {
            release.SetResult();
            await invocation;
        }
        await VerifyFollowingTickAsync(fixture, milliseconds);
        Assert.Equal(milliseconds < 0 ? 1 : 2, calls);
    }

    [Fact]
    public async Task ZeroPeriod_QueuesSuccessiveTurnsAndDisposalDrainsLastTurn()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Register((_, _) =>
        {
            Assert.Equal(++calls, fixture.Requests.Count);
            return Task.CompletedTask;
        }, 0, TimeSpan.Zero, TimeSpan.Zero);
        for (var i = 0; i < 8; i++)
        {
            await fixture.InvokeAsync(fixture.Requests[i]);
            Assert.Equal(i + 2, fixture.Requests.Count);
        }
        timer.Dispose();
        await fixture.InvokeAsync(fixture.Requests[8]);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(8, calls);
        Assert.Equal(9, fixture.Requests.Count);
        Assert.Equal(1, fixture.Time.TimerCreations);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(100.5)]
    public async Task DelayedTick_UsesProviderTimingAndResumesPeriod(double milliseconds)
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        var delay = TimeSpan.FromMilliseconds(milliseconds);
        using var timer = fixture.Register((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, delay, delay);
        for (var i = 0; i < 2; i++)
        {
            fixture.Time.Advance(delay - TimeSpan.FromTicks(1));
            Assert.Equal(i, fixture.Requests.Count);
            fixture.Time.Advance(TimeSpan.FromTicks(1));
            Assert.Equal(i + 1, fixture.Requests.Count);
            await fixture.InvokeAsync(fixture.Requests[i]);
            Assert.Equal(i + 1, calls);
        }
        Assert.Equal(1, fixture.Time.TimerCreations);
    }

    [Fact]
    public async Task DispatchedTick_RacingDelayedChange_IsAdmittedOnce()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Register((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        timer.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        fixture.Time.FireDispatchedCallback();
        fixture.Time.FireDispatchedCallback();
        Assert.Equal(0, calls);
        await fixture.InvokeAsync(Assert.Single(fixture.Requests));
        fixture.Time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.Single(fixture.Requests);
        fixture.Time.Advance(TimeSpan.FromTicks(1));
        await fixture.InvokeAsync(fixture.Requests[1]);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PausedTimer_DispatchedTickLeavesSchedulePaused()
    {
        using var fixture = new TimerFixture();
        using var timer = fixture.Register((_, _) => throw new InvalidOperationException("Unexpected tick"),
            0, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        fixture.Time.FireDispatchedCallback();
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposedTimer_QueuedTickDrains(bool delayed, bool interleave)
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Register((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, 0, delayed ? TimeSpan.FromSeconds(1) : TimeSpan.Zero, TimeSpan.Zero, interleave);
        if (delayed)
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
        }
        timer.Dispose();
        timer.Change(TimeSpan.Zero, TimeSpan.Zero);
        fixture.Time.FireDispatchedCallback();
        await fixture.InvokeAsync(Assert.Single(fixture.Requests));
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, calls);
        Assert.Single(fixture.Requests);
        Assert.Collection(fixture.Events,
            evt => Assert.IsType<GrainTimerEvents.Created>(evt),
            evt => Assert.IsType<GrainTimerEvents.Disposed>(evt));
    }

    [Fact]
    public async Task DisposeDuringEnqueue_StateLockIsReleasedBeforeActivationAdmission()
    {
        using var fixture = new TimerFixture();
        var entered = NewCompletion();
        var release = NewCompletion();
        using var timer = fixture.Register((_, _) => throw new InvalidOperationException("Unexpected tick"),
            0, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        fixture.BeforeReceive = () =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var admission = Task.Run(() => timer.Change(TimeSpan.Zero, TimeSpan.Zero), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Task.Run(timer.Dispose, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.SetResult();
            await admission;
        }
        await fixture.InvokeAsync(Assert.Single(fixture.Requests));
        Assert.Empty(fixture.Events.OfType<GrainTimerEvents.TickStart>());
    }

    [Fact]
    public async Task DisposeWithBlockedCancellation_QueuedTickDrainsBeforeCancellationReturns()
    {
        using var fixture = new TimerFixture();
        var entered = NewCompletion();
        var release = NewCompletion();
        var calls = 0;
        CancellationToken token = default;
        using var timer = fixture.Register((_, value) =>
        {
            calls++;
            token = value;
            return Task.CompletedTask;
        }, 0, TimeSpan.Zero, Timeout.InfiniteTimeSpan, interleave: true);
        await fixture.InvokeAsync(Assert.Single(fixture.Requests));
        using var registration = token.Register(() =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        });
        timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        var disposal = Task.Run(timer.Dispose, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Task.Run(() => fixture.InvokeAsync(fixture.Requests[1]), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(token.IsCancellationRequested);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(1, calls);
        }
        finally
        {
            release.SetResult();
            await disposal;
        }
        Assert.Single(fixture.Events.OfType<GrainTimerEvents.TickStart>());
        Assert.Single(fixture.Events.OfType<GrainTimerEvents.Disposed>());
    }

    [Fact]
    public async Task DisposeDuringCallback_ReentrantFailingCancellationCompletesDisposalOnce()
    {
        using var fixture = new TimerFixture();
        var registry = Substitute.For<IGrainTimerRegistry>();
        fixture.Grain.GetComponent(typeof(IGrainTimerRegistry)).Returns(registry);
        var started = NewCompletion();
        var canceled = NewCompletion();
        var exception = new InvalidOperationException("Cancellation registration failed");
        IGrainTimer? timer = null;
        timer = fixture.Register(async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                timer!.Change(TimeSpan.Zero, TimeSpan.Zero);
                timer.Dispose();
                canceled.SetResult();
                throw exception;
            });
            started.SetResult();
            await canceled.Task;
            Assert.True(token.IsCancellationRequested);
        }, 0, TimeSpan.Zero, TimeSpan.Zero);
        using (timer)
        {
            var invocation = fixture.InvokeAsync(Assert.Single(fixture.Requests));
            try
            {
                await started.Task;
            }
            finally
            {
                timer.Dispose();
                await invocation;
            }
            timer.Dispose();
            var cancellationError = Assert.IsType<AggregateException>(fixture.SingleLoggedError());
            Assert.Same(exception, Assert.Single(cancellationError.InnerExceptions));
            registry.Received(1).OnTimerDisposed(timer);
            fixture.Time.Advance(TimeSpan.FromDays(1));
            Assert.Single(fixture.Requests);
            Assert.Single(fixture.Events.OfType<GrainTimerEvents.Disposed>());
            Assert.Single(fixture.Events.OfType<GrainTimerEvents.TickStop>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackFailure_PairsDiagnosticsAndSchedulesNextPeriod(bool asynchronous)
    {
        using var fixture = new TimerFixture();
        var release = NewCompletion();
        var started = NewCompletion();
        var exception = new InvalidOperationException("Callback failed");
        var calls = 0;
        using var timer = fixture.Register((_, _) =>
        {
            if (++calls > 1)
            {
                return Task.CompletedTask;
            }
            if (asynchronous)
            {
                return FailAsync();
            }
            throw exception;
        }, 0, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        var invocation = fixture.InvokeAsync(Assert.Single(fixture.Requests), exception);
        try
        {
            if (asynchronous)
            {
                await started.Task;
                fixture.Time.Advance(TimeSpan.FromDays(1));
                Assert.Single(fixture.Requests);
            }
        }
        finally
        {
            release.SetResult();
            await invocation;
        }
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        await fixture.InvokeAsync(fixture.Requests[1]);
        Assert.Equal(2, calls);
        Assert.Collection(fixture.Events,
            evt => Assert.IsType<GrainTimerEvents.Created>(evt),
            evt => Assert.IsType<GrainTimerEvents.TickStart>(evt),
            evt => Assert.Same(exception, Assert.IsType<GrainTimerEvents.TickStop>(evt).Exception),
            evt => Assert.IsType<GrainTimerEvents.TickStart>(evt),
            evt => Assert.Null(Assert.IsType<GrainTimerEvents.TickStop>(evt).Exception));

        async Task FailAsync()
        {
            started.SetResult();
            await release.Task;
            throw exception;
        }
    }

    [Fact]
    public async Task LegacyTimer_UsesInterleavingTurnsAndStopsAfterDisposal()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        using var timer = fixture.Registry.RegisterTimer(fixture.Grain, _ =>
        {
            calls++;
            return Task.CompletedTask;
        }, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        var request = Assert.Single(fixture.Requests);
        Assert.True(request.Options.Interleave);
        Assert.False(request.Options.KeepAlive);
        await fixture.InvokeAsync(request);
        timer.Dispose();
        fixture.Time.FireDispatchedCallback();
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, calls);
        Assert.Single(fixture.Requests);
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class UncopyableContextValue { }

    private static async Task VerifyFollowingTickAsync(TimerFixture fixture, int milliseconds)
    {
        if (milliseconds > 0)
        {
            Assert.Single(fixture.Requests);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(milliseconds) - TimeSpan.FromTicks(1));
            Assert.Single(fixture.Requests);
            fixture.Time.Advance(TimeSpan.FromTicks(1));
        }
        if (milliseconds >= 0)
        {
            Assert.Equal(2, fixture.Requests.Count);
            await fixture.InvokeAsync(fixture.Requests[1]);
        }
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(milliseconds < 0 ? 1 : 2, fixture.Requests.Count);
    }

    private sealed class TimerFixture : IObserver<GrainTimerEvents.TimerEvent>, IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        private readonly IDisposable _subscription;
        private readonly ILogger _logger = Substitute.For<ILogger>();
        public TrackingTimeProvider Time { get; } = new();
        public IGrainContext Grain { get; } = Substitute.For<IGrainContext>();
        public List<(ILocalRequest Request, LocalRequestOptions Options)> Requests { get; } = [];
        public List<GrainTimerEvents.TimerEvent> Events { get; } = [];
        public Action? BeforeReceive { get; set; }
        public MessageFactory MessageFactory { get; }
        public TimerRegistry Registry { get; }

        public TimerFixture()
        {
            _logger.IsEnabled(LogLevel.Error).Returns(true);
            var loggerFactory = Substitute.For<ILoggerFactory>();
            loggerFactory.CreateLogger(Arg.Any<string>()).Returns(_logger);
            var grainId = GrainId.Create("timer-test", "one");
            Grain.GrainId.Returns(grainId);
            Grain.When(context => context.ReceiveLocalRequest(Arg.Any<ILocalRequest>(), Arg.Any<LocalRequestOptions>())).Do(call =>
            {
                BeforeReceive?.Invoke();
                Requests.Add(((ILocalRequest)call[0], (LocalRequestOptions)call[1]));
            });
            Grain.Address.Returns(GrainAddress.GetAddress(SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1), grainId, ActivationId.NewId()));
            MessageFactory = new(_services.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, null!);
            Registry = new(loggerFactory, Time);
            _subscription = GrainTimerEvents.AllEvents.Subscribe(this);
        }

        public IGrainTimer Register<T>(Func<T, CancellationToken, Task> callback, T state, TimeSpan dueTime,
            TimeSpan period, bool interleave = false, bool keepAlive = false) =>
            Registry.RegisterGrainTimer(Grain, callback, state, new(dueTime, period) { Interleave = interleave, KeepAlive = keepAlive });

        public async Task InvokeAsync((ILocalRequest Request, LocalRequestOptions Options) request, Exception? expectedException = null)
        {
            RequestContextExtensions.Import(null);
            var invokable = request.Request;
            invokable.SetTarget(Grain);
            using var response = await invokable.Invoke();
            Assert.Same(expectedException, response.Exception);
        }

        public Exception SingleLoggedError()
        {
            var log = Assert.Single(_logger.ReceivedCalls(), call =>
                call.GetMethodInfo().Name == nameof(ILogger.Log) && Equals(call.GetArguments()[0], LogLevel.Error));
            return Assert.IsAssignableFrom<Exception>(log.GetArguments()[3]);
        }

        public void OnNext(GrainTimerEvents.TimerEvent value)
        {
            if (ReferenceEquals(value.GrainContext, Grain))
            {
                lock (Events)
                {
                    Events.Add(value);
                }
            }
        }

        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
        public void Dispose()
        {
            _subscription.Dispose();
            _services.Dispose();
        }
    }

    private sealed class TrackingTimeProvider : FakeTimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public int TimerCreations { get; private set; }
        public void FireDispatchedCallback() => _callback!(_state);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerCreations++;
            _callback = callback;
            _state = state;
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
