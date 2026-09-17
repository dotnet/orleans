#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Timers;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Theory]
    [InlineData(25)]
    [InlineData(1000)]
    public async Task CohortPublisherReceiptReschedulesActualTimer(int sealMilliseconds)
    {
        await using var harness = new CohortPublisherHarness();
        await harness.StartAsync();
        harness.Dissemination.AutomaticReceipt = null;
        var tick = await harness.StartTickAsync();
        var first = await harness.Dissemination.NextPublicationAsync();
        var sealDelay = TimeSpan.FromMilliseconds(sealMilliseconds);
        harness.Clock.Advance(sealDelay);
        first.Receipt.SetResult(new(true, harness.Period - sealDelay));
        await tick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(harness.Period - sealDelay, harness.TimerClock.DueTime);
        if (sealDelay < harness.Period)
        {
            harness.Clock.Advance(harness.Period - sealDelay - TimeSpan.FromMilliseconds(1));
            Assert.Single(harness.Dissemination.Publications);
            harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        var second = await harness.Dissemination.NextPublicationAsync();
        Assert.Equal(harness.Period, harness.Clock.GetElapsedTime(harness.StartTimestamp));
        Assert.True(second.Version > first.Version);
        Assert.Equal(second.Version, harness.Publisher.LocalRuntimeStatistics.DateTime.Ticks);
        Assert.False(second.Token.IsCancellationRequested);
        Assert.False(second.Receipt.Task.IsCompleted);
        Assert.Equal(2, harness.Dissemination.Publications.Count);
        Assert.Empty(harness.DirectUpdates);
    }

    [Fact]
    public async Task CohortPublisherReceiptSubtractsDirectDeliveryTime()
    {
        await using var harness = new CohortPublisherHarness();
        await harness.StartAsync();
        harness.Dissemination.AutomaticReceipt = null;
        harness.Dissemination.UnconfirmedPeers = [harness.Peer];
        var directStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDirect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.DirectHandler = token =>
        {
            directStarted.TrySetResult();
            return releaseDirect.Task.WaitAsync(token);
        };
        var tick = await harness.StartTickAsync();
        var publication = await harness.Dissemination.NextPublicationAsync();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(25));
        publication.Receipt.SetResult(new(true, TimeSpan.FromMilliseconds(975)));
        await directStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(200));
        releaseDirect.SetResult();
        await tick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMilliseconds(775), harness.TimerClock.DueTime);
        Assert.Equal(publication.Version, Assert.Single(harness.DirectUpdates).DateTime.Ticks);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(775));
        var next = await harness.Dissemination.NextPublicationAsync();
        Assert.Equal(harness.Period, harness.Clock.GetElapsedTime(harness.StartTimestamp));
        Assert.True(next.Version > publication.Version);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("rejected")]
    [InlineData("failed")]
    public async Task CohortPublisherDirectFallbackRetainsNormalTimerCadence(string outcome)
    {
        await using var harness = new CohortPublisherHarness();
        await harness.StartAsync();
        harness.Options.Dissemination.Enabled = outcome != "disabled";
        harness.Dissemination.AutomaticReceipt = new(false, TimeSpan.Zero);
        if (outcome == "failed")
        {
            harness.Dissemination.Failure = new InvalidOperationException("Receipt transport failed.");
        }

        var directStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDirect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.DirectHandler = token =>
        {
            directStarted.TrySetResult();
            return releaseDirect.Task.WaitAsync(token);
        };
        var tick = await harness.StartTickAsync();
        await directStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(200));
        releaseDirect.SetResult();
        await tick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(harness.Period, harness.TimerClock.DueTime);
        Assert.Single(harness.DirectUpdates);
        Assert.Equal(outcome == "disabled" ? 0 : 1, harness.Dissemination.Publications.Count);
        Assert.Equal(0, harness.Dissemination.QueryCount);
        harness.Clock.Advance(harness.Period - TimeSpan.FromMilliseconds(1));
        Assert.Single(harness.DirectUpdates);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var nextTick = await harness.Timers.NextTickAsync();
        await nextTick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, harness.DirectUpdates.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), harness.Clock.GetElapsedTime(harness.StartTimestamp));
    }

    [Fact]
    public async Task CohortPublisherReceiptBudgetAllowsOnePeriodAndExpiresAfterTwo()
    {
        await using var harness = new CohortPublisherHarness();
        await harness.StartAsync();
        harness.Dissemination.AutomaticReceipt = null;
        var tick = await harness.StartTickAsync();
        var publication = await harness.Dissemination.NextPublicationAsync();

        harness.Clock.Advance(harness.Period);
        await harness.OwnerBarrierAsync();
        Assert.False(publication.Token.IsCancellationRequested);
        Assert.False(tick.IsCompleted);
        Assert.Empty(harness.DirectUpdates);

        harness.Clock.Advance(harness.Period - TimeSpan.FromMilliseconds(1));
        Assert.False(publication.Token.IsCancellationRequested);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await tick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(publication.Token.IsCancellationRequested);
        Assert.Single(harness.DirectUpdates);
        Assert.Equal(harness.Period, harness.TimerClock.DueTime);
        Assert.Equal(0, harness.Dissemination.QueryCount);
    }

    [Fact]
    public async Task CohortPublisherCallerCancellationPreservesIdentityWithoutFallback()
    {
        await using var harness = new CohortPublisherHarness();
        await harness.StartAsync();
        harness.Dissemination.AutomaticReceipt = null;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var publish = harness.Publisher.RunOrQueueTask(
            async token =>
            {
                await harness.Publisher.PublishStatistics(token);
                return true;
            },
            caller.Token);
        var publication = await harness.Dissemination.NextPublicationAsync();
        caller.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publish.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await harness.OwnerBarrierAsync();

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.True(publication.Token.IsCancellationRequested);
        Assert.Empty(harness.DirectUpdates);
        Assert.Equal(0, harness.Dissemination.QueryCount);
    }

    [Fact]
    public async Task CohortPublisherStartupReceiptWaitKeepsInboundOwnerApplicationLive()
    {
        await using var harness = new CohortPublisherHarness();
        harness.Dissemination.AutomaticReceipt = null;
        var startup = harness.StartAsync(clearStartup: false);
        var publication = await harness.Dissemination.NextPublicationAsync();
        var tick = await harness.StartTickAsync();
        await harness.OwnerBarrierAsync();

        var incoming = CreatePhase5Statistics(42);
        var applied = await harness.Publisher.ApplyDisseminatedRuntimeStatisticsAsync(
            harness.Peer, incoming, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DisseminationApplyResult.Applied, applied);
        Assert.Same(incoming, harness.Publisher.PeriodicStatistics[harness.Peer]);
        Assert.Single(harness.Dissemination.Publications);
        Assert.Equal(publication.Version, harness.Publisher.LocalRuntimeStatistics.DateTime.Ticks);
        Assert.False(startup.IsCompleted);
        Assert.False(tick.IsCompleted);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(25));
        publication.Receipt.SetResult(new(true, TimeSpan.FromMilliseconds(975)));
        await Task.WhenAll(startup, tick).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(harness.Dissemination.Publications);
        Assert.Equal(TimeSpan.FromMilliseconds(975), harness.TimerClock.DueTime);
        Assert.Empty(harness.DirectUpdates);
    }

    [Fact]
    public async Task CohortPublisherOverlappingCallerCancellationDoesNotCancelPendingSample()
    {
        await using var harness = new CohortPublisherHarness();
        await harness.StartAsync();
        harness.Dissemination.AutomaticReceipt = null;
        var tick = await harness.StartTickAsync();
        var publication = await harness.Dissemination.NextPublicationAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var overlapping = harness.Publisher.RunOrQueueTask(
            async token =>
            {
                await harness.Publisher.PublishStatistics(token);
                return true;
            },
            caller.Token);
        await harness.OwnerBarrierAsync();
        caller.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => overlapping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.False(publication.Token.IsCancellationRequested);
        Assert.Single(harness.Dissemination.Publications);
        publication.Receipt.SetResult(new(true, harness.Period));
        await tick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(harness.DirectUpdates);
    }

    [Fact]
    public async Task CohortPublisherStopRetainsDisposedTimerAndCancelsReceipt()
    {
        await using var harness = new CohortPublisherHarness();
        await harness.StartAsync();
        harness.Dissemination.AutomaticReceipt = null;
        var tick = await harness.StartTickAsync();
        var publication = await harness.Dissemination.NextPublicationAsync();
        var timer = harness.Timers.Timer;
        await harness.Lifecycle.OnStop(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await harness.OwnerBarrierAsync();
        harness.Clock.Advance(harness.Period * 3);

        Assert.True(publication.Token.IsCancellationRequested);
        Assert.True(harness.TimerClock.Disposed);
        Assert.Same(timer, typeof(DeploymentLoadPublisher).GetField(
            "_publishTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Publisher));
        Assert.Single(harness.Dissemination.Publications);
        Assert.Empty(harness.DirectUpdates);
    }

    [Fact]
    public async Task CohortPublisherExplicitPublicationPreservesDisposedTimerPause()
    {
        await using var harness = new CohortPublisherHarness();
        await harness.StartAsync();
        // Only timer registration changes the schedule: the explicit startup publication does not.
        Assert.Equal(1, harness.TimerClock.ChangeCount);
        var timer = harness.Timers.Timer;
        var firstVersion = harness.Publisher.LocalRuntimeStatistics.DateTime.Ticks;
        await harness.Lifecycle.OnStop(TestContext.Current.CancellationToken);

        await harness.Publisher.RunOrQueueTask(
            async token =>
            {
                await harness.Publisher.PublishStatistics(token);
                return true;
            },
            TestContext.Current.CancellationToken);
        harness.Clock.Advance(harness.Period * 3);

        Assert.True(harness.Publisher.LocalRuntimeStatistics.DateTime.Ticks > firstVersion);
        Assert.Single(harness.Dissemination.Publications);
        Assert.Equal(1, harness.TimerClock.ChangeCount);
        Assert.True(harness.TimerClock.Disposed);
        Assert.Same(timer, typeof(DeploymentLoadPublisher).GetField(
            "_publishTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Publisher));
        Assert.Empty(harness.Timers.AllTicks);
        Assert.Empty(harness.DirectUpdates);
    }

    [Fact]
    public async Task CohortPublisherCompatibilityWrapperPublishesOneReceipt()
    {
        await using var harness = new CohortPublisherHarness();
        await harness.StartAsync();
        var result = await harness.Publisher.RunOrQueueTask(
            token => harness.Publisher.TryPublishStatisticsViaDissemination(
                harness.Publisher.LocalRuntimeStatistics, token),
            TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(
            harness.Publisher.LocalRuntimeStatistics.DateTime.Ticks,
            Assert.Single(harness.Dissemination.Publications).Version);
        Assert.Empty(harness.DirectUpdates);
    }

    private sealed class CohortPublisherHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _serializerServices;

        public CohortPublisherHarness()
        {
            var local = CreateSilo(40501);
            Peer = CreateSilo(40502);
            var statusOracle = new Phase4FakeSiloStatusOracle();
            statusOracle.SetStatus(local, SiloStatus.Active);
            statusOracle.SetStatus(Peer, SiloStatus.Active);
            var details = new FakeLocalSiloDetails(local);
            Options = new() { DeploymentLoadPublisherRefreshTime = Period };
            Options.Dissemination.Enabled = true;
            _serializerServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
            TimerClock = new(Clock);
            Timers = new(new TimerRegistry(
                NullLoggerFactory.Instance,
                TimerClock,
                new MessageFactory(_serializerServices.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, null!),
                details));
            var shared = new SystemTargetShared(
                runtimeClient: null!,
                details,
                NullLoggerFactory.Instance,
                Microsoft.Extensions.Options.Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                Timers,
                new ActivationDirectory(CreatePhase4Instruments<CatalogInstruments>()),
                CreatePhase4Instruments<SchedulerInstruments>(),
                CreatePhase4Instruments<GrainInstruments>(),
                CreatePhase4Instruments<MessagingInstruments>(),
                CreatePhase4Instruments<MessagingProcessingInstruments>());
            var grainFactory = Substitute.For<IInternalGrainFactory>();
            var siloControl = Substitute.For<ISiloControl>();
            siloControl.GetRuntimeStatistics(Arg.Any<CancellationToken>()).Returns(CreatePhase5Statistics(0));
            grainFactory.GetSystemTarget<ISiloControl>(
                Constants.SiloControlType, Arg.Any<SiloAddress>()).Returns(siloControl);
            var directTarget = Substitute.For<IDeploymentLoadPublisher>();
            directTarget.UpdateRuntimeStatistics(
                Arg.Any<SiloAddress>(), Arg.Any<SiloRuntimeStatistics>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    DirectUpdates.Enqueue(call.ArgAt<SiloRuntimeStatistics>(1));
                    return DirectHandler(call.ArgAt<CancellationToken>(2));
                });
            grainFactory.GetSystemTarget<IDeploymentLoadPublisher>(
                Constants.DeploymentLoadPublisherSystemTargetType, Peer).Returns(directTarget);
            var services = new Phase4MutableServiceProvider();
            services.Add<TimeProvider>(Clock);
            Publisher = new(
                details,
                statusOracle,
                Microsoft.Extensions.Options.Options.Create(Options),
                grainFactory,
                NullLoggerFactory.Instance,
                shared.ActivationDirectory,
                new Phase4FakeActivationWorkingSet(),
                new Phase4FakeEnvironmentStatisticsProvider(),
                Microsoft.Extensions.Options.Options.Create(new LoadSheddingOptions()),
                services,
                shared);
            services.Add<IDisseminationService>(Dissemination);
            services.Add(new DeploymentLoadStatisticsDisseminationNamespace(
                Publisher,
                new TestOptionsMonitor<DeploymentLoadPublisherOptions>(Options),
                _serializerServices.GetRequiredService<Serializer>()));
            var lifecycle = Substitute.For<ISiloLifecycle>();
            lifecycle.Subscribe(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ILifecycleObserver>()).Returns(call =>
            {
                Lifecycle = call.ArgAt<ILifecycleObserver>(2);
                return Substitute.For<IDisposable>();
            });
            ((ILifecycleParticipant<ISiloLifecycle>)Publisher).Participate(lifecycle);
            StartTimestamp = Clock.GetTimestamp();
        }

        public TimeSpan Period => TimeSpan.FromSeconds(1);
        public long StartTimestamp { get; }
        public SiloAddress Peer { get; }
        public FakeTimeProvider Clock { get; } = new();
        public CohortPublisherTimerClock TimerClock { get; }
        public CohortPublisherTimerRegistry Timers { get; }
        public DeploymentLoadPublisherOptions Options { get; }
        public DeploymentLoadPublisher Publisher { get; }
        public CohortPublisherDisseminationService Dissemination { get; } = new();
        public ILifecycleObserver Lifecycle { get; private set; } = null!;
        public ConcurrentQueue<SiloRuntimeStatistics> DirectUpdates { get; } = new();
        public Func<CancellationToken, Task> DirectHandler { get; set; } = static _ => Task.CompletedTask;

        public async Task StartAsync(bool clearStartup = true)
        {
            await Lifecycle.OnStart(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (clearStartup)
            {
                Dissemination.Publications.Clear();
                _ = await Dissemination.NextPublicationAsync();
                Dissemination.QueryCount = 0;
                DirectUpdates.Clear();
            }
        }

        public async Task<Task> StartTickAsync()
        {
            await Publisher.RunOrQueueTask(() =>
            {
                Timers.Timer.Change(TimeSpan.Zero, Period);
                return Task.CompletedTask;
            });
            return await Timers.NextTickAsync();
        }

        public Task OwnerBarrierAsync() => Publisher.QueueTask(static () => Task.CompletedTask);

        public async ValueTask DisposeAsync()
        {
            await Lifecycle.OnStop(CancellationToken.None);
            foreach (var tick in Timers.AllTicks)
            {
                try
                {
                    await tick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                }
                catch (OperationCanceledException) when (TimerClock.Disposed)
                {
                }
            }

            await OwnerBarrierAsync();
            _serializerServices.Dispose();
        }
    }

    private sealed class CohortPublisherDisseminationService : IDisseminationService
    {
        private readonly Channel<CohortPublisherPublication> _publications = Channel.CreateUnbounded<CohortPublisherPublication>();

        public ConcurrentQueue<CohortPublisherPublication> Publications { get; } = new();
        public DisseminationPublicationReceipt? AutomaticReceipt { get; set; } = new(true, TimeSpan.FromSeconds(1));
        public Exception? Failure { get; set; }
        public IReadOnlyList<SiloAddress> UnconfirmedPeers { get; set; } = [];
        public int QueryCount { get; set; }

        public ValueTask<bool> Publish(
            IDisseminationNamespace disseminationNamespace, DisseminationKey key, long version, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Load publication must use the receipt-returning path.");

        public ValueTask<DisseminationPublicationReceipt> PublishAggregated(
            IDisseminationNamespace disseminationNamespace, DisseminationKey key, long version, CancellationToken cancellationToken)
        {
            var publication = new CohortPublisherPublication(version, cancellationToken);
            Publications.Enqueue(publication);
            if (Failure is { } failure)
            {
                publication.Receipt.SetException(failure);
            }
            else if (AutomaticReceipt is { } receipt)
            {
                publication.Receipt.SetResult(receipt);
            }

            var result = new ValueTask<DisseminationPublicationReceipt>(publication.Receipt.Task.WaitAsync(cancellationToken));
            _publications.Writer.TryWrite(publication);
            return result;
        }

        public IReadOnlyList<SiloAddress> GetUnconfirmedPeers(IDisseminationNamespace disseminationNamespace)
        {
            QueryCount++;
            return UnconfirmedPeers;
        }

        public Task<CohortPublisherPublication> NextPublicationAsync() =>
            _publications.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private sealed record CohortPublisherPublication(long Version, CancellationToken Token)
    {
        public TaskCompletionSource<DisseminationPublicationReceipt> Receipt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Only message delivery is replaced: timer firing, Change, cancellation and callback completion
    // use the real GrainTimer, and its callback runs on the publisher's actual SystemTarget scheduler.
    private sealed class CohortPublisherTimerRegistry(TimerRegistry inner) : ITimerRegistry
    {
        private readonly Channel<Task> _ticks = Channel.CreateUnbounded<Task>();

        public IGrainTimer Timer { get; private set; } = null!;
        public ConcurrentQueue<Task> AllTicks { get; } = new();

        public IGrainTimer RegisterGrainTimer<TState>(
            IGrainContext grainContext, Func<TState, CancellationToken, Task> callback, TState state, GrainTimerCreationOptions options)
        {
            Assert.True(options.Interleave);
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(grainContext.GrainId);
            context.When(value => value.ReceiveMessage(Arg.Any<object>())).Do(call =>
            {
                var message = Assert.IsType<Message>(call.Arg<object>());
                var invocation = Assert.IsAssignableFrom<IInvokable>(message.BodyObject);
                var tick = grainContext.QueueTask(async () =>
                {
                    using var response = await invocation.Invoke();
                    if (response.Exception is { } exception)
                    {
                        ExceptionDispatchInfo.Capture(exception).Throw();
                    }
                });
                AllTicks.Enqueue(tick);
                _ticks.Writer.TryWrite(tick);
            });
            // Replace startup jitter with a deterministic offset; keep the runtime timer itself.
            return Timer = inner.RegisterGrainTimer(context, callback, state, options with { DueTime = options.Period });
        }

        public IDisposable RegisterTimer(IGrainContext grainContext, Func<object?, Task> callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException();

        public Task<Task> NextTickAsync() =>
            _ticks.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private sealed class CohortPublisherTimerClock(FakeTimeProvider inner) : TimeProvider
    {
        public TimeSpan DueTime { get; private set; }
        public int ChangeCount { get; private set; }
        public bool Disposed { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new RecordingTimer(this, inner.CreateTimer(callback, state, dueTime, period));

        private sealed class RecordingTimer(CohortPublisherTimerClock owner, ITimer innerTimer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.DueTime = dueTime;
                owner.ChangeCount++;
                return innerTimer.Change(dueTime, period);
            }

            public void Dispose()
            {
                owner.Disposed = true;
                innerTimer.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
