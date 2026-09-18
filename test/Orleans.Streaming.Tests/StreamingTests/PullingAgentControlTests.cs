using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Streaming.Diagnostics;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using UnitTests.StorageTests;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public sealed class PullingAgentControlTests
{
    private const string ProviderName = "grain-hosted-control";
    private static readonly QueueId Queue = QueueId.GetQueueId("Control", 0, 1);
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAgents_RetiresInactivePublishersAfterMigrationAndPreservesOtherHost(bool namedStorage)
    {
        await using var setup = new Setup(2, explicitPubSub: true, namedStorage: namedStorage);
        using var events = new DiagnosticEventCollector(StreamingEvents.ListenerName);
        await setup.Deploy();
        var source = setup.Cluster.Silos[0];
        var destination = setup.Cluster.Silos[1];
        await setup.Command(source, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        var stream = new QualifiedStreamId(ProviderName, StreamId.Create("retirement", Guid.NewGuid()));
        var pubSub = new GrainBasedPubSubRuntime(setup.Cluster.Client);
        var grainId = PullingAgentId.Create(ProviderName, Queue);
        var grain = setup.Cluster.Client.GetGrain<IPullingAgentGrain>(grainId);
        await setup.Publish(stream, events);
        await grain.Probe(TestContext.Current.CancellationToken);
        await setup.MakeInactive(stream, events);
        Assert.Equal(1, await pubSub.ProducerCount(stream, TestContext.Current.CancellationToken));
        Assert.True(setup.Cluster.TryGetGrainContext(grainId, out var originalContext));
        Assert.Equal(0, Assert.IsType<PullingAgentGrain>(originalContext.GrainInstance).PubSubCacheSize);
        var original = (await grain.Probe(TestContext.Current.CancellationToken)).Address;

        await setup.Command(destination, PersistentStreamProviderCommand.StartAgents);
        Assert.True(await grain.Rebalance(original, destination.SiloAddress, TestContext.Current.CancellationToken));
        Assert.Equal(destination.SiloAddress, await setup.NextInitialization());
        var current = await grain.Probe(TestContext.Current.CancellationToken);
        Assert.Equal(destination.SiloAddress, current.Address.SiloAddress);
        Assert.NotEqual(original.ActivationId, current.Address.ActivationId);
        Assert.Equal(1, await pubSub.ProducerCount(stream, TestContext.Current.CancellationToken));

        // A local stop must leave the current remote owner and its durable publishers intact.
        await setup.Command(source, PersistentStreamProviderCommand.StopAgents);
        Assert.True((await grain.Probe(TestContext.Current.CancellationToken)).IsRunning);
        Assert.Equal(1, await pubSub.ProducerCount(stream, TestContext.Current.CancellationToken));
        Assert.Equal(1, await setup.Command(destination, PersistentStreamProviderCommand.GetNumberRunningAgents));
        await setup.Command(destination, PersistentStreamProviderCommand.StopAgents);
        Assert.Equal(0, await pubSub.ProducerCount(stream, TestContext.Current.CancellationToken));
        Assert.False((await grain.Probe(TestContext.Current.CancellationToken)).IsRunning);
        Assert.Equal(2, setup.Initializations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAgents_RetiresDormantPublishersBeforeSystemTargetRollback(bool namedStorage)
    {
        using var services = new ServiceCollection().AddLogging().AddSerializer().BuildServiceProvider();
        var storage = new MockStorageProvider("PubSubStore", services.GetRequiredService<ILoggerFactory>(), services.GetRequiredService<DeepCopier>());
        var stream = new QualifiedStreamId(ProviderName, StreamId.Create("rollback", Guid.NewGuid()));
        await using (var setup = new Setup(1, explicitPubSub: true, namedStorage: namedStorage, storage: storage))
        {
            using var events = new DiagnosticEventCollector(StreamingEvents.ListenerName);
            await setup.Deploy();
            var silo = setup.Cluster.Silos[0];
            await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
            await setup.NextInitialization();
            await setup.Publish(stream, events);
            var grainId = PullingAgentId.Create(ProviderName, Queue);
            await setup.Cluster.Client.GetGrain<IPullingAgentGrain>(grainId).Probe(TestContext.Current.CancellationToken);
            await setup.MakeInactive(stream, events);
            await setup.GetManager(silo).Stop(TestContext.Current.CancellationToken);
            await setup.Coordinator.GetAgents(TestContext.Current.CancellationToken);
            await setup.Cluster.DeactivateAsync(grainId).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
            var pubSub = new GrainBasedPubSubRuntime(setup.Cluster.Client);
            Assert.Equal(1, await pubSub.ProducerCount(stream, TestContext.Current.CancellationToken));

            await setup.Command(silo, PersistentStreamProviderCommand.StopAgents);
            Assert.Equal(0, await pubSub.ProducerCount(stream, TestContext.Current.CancellationToken));
            Assert.Equal(1, setup.Initializations);
        }

        await using var rollback = new Setup(1, explicitPubSub: true, namedStorage: namedStorage, storage: storage,
            hostingMode: StreamPullingAgentHostingMode.SystemTarget);
        using var rollbackEvents = new DiagnosticEventCollector(StreamingEvents.ListenerName);
        await rollback.Deploy();
        var rollbackPubSub = new GrainBasedPubSubRuntime(rollback.Cluster.Client);
        Assert.Equal(0, await rollbackPubSub.ProducerCount(stream, TestContext.Current.CancellationToken));
        await rollback.Command(rollback.Cluster.Silos[0], PersistentStreamProviderCommand.StartAgents);
        await rollback.NextInitialization();
        await rollback.Publish(stream, rollbackEvents);
        Assert.Equal(1, await rollbackPubSub.ProducerCount(stream, TestContext.Current.CancellationToken));
        var persisted = Assert.IsType<PubSubGrainState>(storage.GetLastState());
        Assert.True(Assert.Single(persisted.Producers).Producer.IsSystemTarget());
    }

    [Fact]
    public async Task StartupStopped_RequiresStartAndRemainsStoppedAfterCallbacksAndReconciliation()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        setup.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, setup.Initializations);
        Assert.Equal(StreamLifecycleOptions.RunState.Initialized, await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));

        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        Assert.Equal(silo.SiloAddress, await setup.NextInitialization());
        Assert.Equal(1, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
        await setup.Command(silo, PersistentStreamProviderCommand.StopAgents);
        var readsAtStop = setup.Reads;

        var grain = setup.Cluster.Client.GetGrain<IPullingAgentGrain>(PullingAgentId.Create(ProviderName, Queue));
        await grain.AddSubscriber(
            GuidId.GetGuidId(Guid.NewGuid()),
            new QualifiedStreamId(ProviderName, StreamId.Create("namespace", "stream")),
            GrainId.Create("consumer", "stopped"),
            null,
            TestContext.Current.CancellationToken);
        Assert.False((await grain.Probe(TestContext.Current.CancellationToken)).IsRunning);
        await setup.Coordinator.NotifyHostChanged(TestContext.Current.CancellationToken);
        setup.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, setup.Initializations);
        Assert.Equal(1, setup.Shutdowns);
        Assert.Equal(readsAtStop, setup.Reads);
        Assert.Equal(StreamLifecycleOptions.RunState.AgentsStopped, await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));

        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        Assert.Equal(silo.SiloAddress, await setup.NextInitialization());
        Assert.Equal(2, setup.Initializations);
        Assert.Equal(1, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    [Fact]
    public async Task LifecycleStop_DefersReceiverDrainToGrainDeactivation()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        Assert.Equal(1, setup.CoordinatorNotifications);

        var manager = setup.GetManager(silo);
        await manager.Stop(TestContext.Current.CancellationToken).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(0, setup.Shutdowns);
        Assert.Equal(1, setup.CoordinatorNotifications);
        Assert.Equal(1, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
        var grainId = PullingAgentId.Create(ProviderName, Queue);
        Assert.True(setup.Cluster.TryGetGrainContext(grainId, out var context));
        context.Deactivate(new(DeactivationReasonCode.ShuttingDown, "Exercise grain-owned silo-shutdown cleanup."), TestContext.Current.CancellationToken);
        await context.Deactivated.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, setup.Shutdowns);
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    [Theory]
    [InlineData(nameof(IPullingCoordinatorGrain.EnsureRunning))]
    [InlineData(nameof(IPullingCoordinatorGrain.NotifyHostChanged))]
    public async Task LifecycleStop_JoinsInFlightStartAndRejectsQueuedStarts(string pausedMethod)
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        var notifications = setup.CoordinatorNotifications;
        setup.PausedCoordinatorMethod = pausedMethod;
        setup.CoordinatorCallBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = setup.GetManager(silo);
        var starting = manager.StartAgents(TestContext.Current.CancellationToken);
        Task? queuedStart = null;
        Task? stopping = null;
        Exception? startFailure = null;
        Exception? queuedStartFailure = null;

        try
        {
            await setup.CoordinatorCallStarted.Task.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            queuedStart = manager.StartAgents(TestContext.Current.CancellationToken);
            stopping = manager.Stop(TestContext.Current.CancellationToken);
            Assert.Equal(StreamLifecycleOptions.RunState.AgentsStopped,
                await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
            Assert.False(starting.IsCompleted);
            Assert.False(queuedStart.IsCompleted);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(1, setup.Initializations);
            Assert.Equal(0, setup.Shutdowns);
        }
        finally
        {
            setup.CoordinatorCallBarrier.TrySetResult();
            startFailure = await Record.ExceptionAsync(() => starting.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken));
            if (queuedStart is not null)
            {
                queuedStartFailure = await Record.ExceptionAsync(() => queuedStart.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken));
            }

            await (stopping ?? manager.Stop(TestContext.Current.CancellationToken))
                .WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        }

        Assert.IsType<InvalidOperationException>(startFailure);
        Assert.IsType<InvalidOperationException>(queuedStartFailure);
        Assert.Equal(notifications + (pausedMethod == nameof(IPullingCoordinatorGrain.NotifyHostChanged) ? 1 : 0),
            setup.CoordinatorNotifications);
        Assert.Equal(StreamLifecycleOptions.RunState.AgentsStopped,
            await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
        Assert.Equal(1, setup.Initializations);
        Assert.Equal(0, setup.Shutdowns);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAgents(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LifecycleStop_JoinsAdmittedAdministrativeDrain()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        var notifications = setup.CoordinatorNotifications;
        setup.ShutdownBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = setup.GetManager(silo);
        var draining = manager.StopAgents(TestContext.Current.CancellationToken);
        Task? stopping = null;

        try
        {
            await setup.ShutdownEntered.Task.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            stopping = manager.Stop(TestContext.Current.CancellationToken);
            Assert.Equal(StreamLifecycleOptions.RunState.AgentsStopped,
                await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
            Assert.False(draining.IsCompleted);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(1, setup.Shutdowns);
        }
        finally
        {
            setup.ShutdownBarrier.TrySetResult();
            await draining.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            await (stopping ?? manager.Stop(TestContext.Current.CancellationToken))
                .WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        }

        Assert.Equal(notifications, setup.CoordinatorNotifications);
        Assert.Equal(1, setup.Shutdowns);
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    [Fact]
    public async Task FailedStop_RemainsObservableToDeactivationUntilSuccessfulRestart()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        var grainId = PullingAgentId.Create(ProviderName, Queue);
        Assert.True(setup.Cluster.TryGetGrainContext(grainId, out var context));
        var grain = Assert.IsType<PullingAgentGrain>(context.GrainInstance);
        var failure = new InvalidOperationException("Final checkpoint failed.");
        setup.ShutdownFailure = failure;

        var stopFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Command(silo, PersistentStreamProviderCommand.StopAgents));
        Assert.Equal(failure.Message, stopFailure.Message);
        var deactivationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.RunOrQueueTask(() => grain.OnDeactivateAsync(
                new(DeactivationReasonCode.ShuttingDown, "Observe the previous failed drain."),
                TestContext.Current.CancellationToken)));
        Assert.Same(failure, deactivationFailure);
        Assert.Equal(1, setup.Shutdowns);

        setup.ShutdownFailure = null;
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        await setup.Command(silo, PersistentStreamProviderCommand.StopAgents);
        Assert.Equal(2, setup.Initializations);
        Assert.Equal(2, setup.Shutdowns);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopAgents_DrainsEveryQueueBeforeReportingFailures(int failureCount)
    {
        await using var setup = new Setup(1, queueCount: 3);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        for (var i = 0; i < setup.Queues.Length; i++)
        {
            await setup.NextInitialization();
        }

        var failures = Enumerable.Range(0, failureCount)
            .Select(i => new InvalidOperationException($"Queue {i} checkpoint failed.")).ToArray();
        var attempted = new ConcurrentQueue<QueueId>();
        var lastShutdownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLastShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.OnShutdown = queue =>
        {
            attempted.Enqueue(queue);
            var index = attempted.Count - 1;
            if (index < failures.Length)
            {
                return Task.FromException(failures[index]);
            }

            if (attempted.Count == setup.Queues.Length)
            {
                lastShutdownEntered.TrySetResult();
                return releaseLastShutdown.Task;
            }

            return Task.CompletedTask;
        };

        var notifications = setup.CoordinatorNotifications;
        var stopping = setup.Command(silo, PersistentStreamProviderCommand.StopAgents);
        Exception? failure = null;
        try
        {
            await Task.WhenAny(lastShutdownEntered.Task, stopping).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            Assert.True(lastShutdownEntered.Task.IsCompletedSuccessfully, "Stop returned before reaching the final queue.");
            Assert.False(stopping.IsCompleted);
            Assert.Equal(setup.Queues.Order(), attempted.Order());
            Assert.Equal(3, setup.Shutdowns);
            Assert.Equal(StreamLifecycleOptions.RunState.AgentsStopped,
                await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
            Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
        }
        finally
        {
            releaseLastShutdown.TrySetResult();
            failure = await Record.ExceptionAsync(() => stopping.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken));
        }

        AssertFailures(failure);
        Assert.Equal(notifications + 1, setup.CoordinatorNotifications);
        var retryFailure = await Record.ExceptionAsync(() =>
            setup.Command(silo, PersistentStreamProviderCommand.StopAgents));
        AssertFailures(retryFailure);
        Assert.Equal(3, setup.Initializations);
        Assert.Equal(3, setup.Shutdowns);

        void AssertFailures(Exception? exception)
        {
            if (failureCount == 1)
            {
                Assert.Equal(failures[0].Message, Assert.IsType<InvalidOperationException>(exception).Message);
            }
            else
            {
                var aggregate = Assert.IsType<AggregateException>(exception);
                Assert.Equal(failures.Select(error => error.Message), aggregate.InnerExceptions.Select(error => error.Message));
                Assert.All(aggregate.InnerExceptions, error => Assert.IsType<InvalidOperationException>(error));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAgents_DrainsActivationTriggeredWhileReceiverIsInitializing(bool cancelAfterAdmission)
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        var grainId = PullingAgentId.Create(ProviderName, Queue);
        await setup.Cluster.DeactivateAsync(grainId).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);

        setup.InitializationBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = setup.Cluster.Client.GetGrain<IPullingAgentGrain>(grainId)
            .Probe(TestContext.Current.CancellationToken);
        await setup.NextInitialization();
        using var cancellation = new CancellationTokenSource();
        var stopping = setup.GetManager(silo).StopAgents(cancellation.Token);
        try
        {
            Assert.Equal(StreamLifecycleOptions.RunState.AgentsStopped, await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
            Assert.False(stopping.IsCompleted);
            Assert.False(activation.IsCompleted);
            if (cancelAfterAdmission)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    stopping.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken));
                Assert.False(activation.IsCompleted);
            }
        }
        finally
        {
            setup.InitializationBarrier.TrySetResult();
            await activation.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            if (!cancelAfterAdmission)
            {
                await stopping.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            }

            await setup.GetManager(silo).StopAgents(TestContext.Current.CancellationToken)
                .WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        }

        var readsAtStop = setup.Reads;
        setup.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, setup.Initializations);
        Assert.Equal(2, setup.Shutdowns);
        Assert.Equal(readsAtStop, setup.Reads);
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    [Fact]
    public async Task GrainStop_CanceledBeforeAdmissionPreservesRunningAgent()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        var grainId = PullingAgentId.Create(ProviderName, Queue);
        Assert.True(setup.Cluster.TryGetGrainContext(grainId, out var context));
        var grain = Assert.IsType<PullingAgentGrain>(context.GrainInstance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.RunOrQueueTask(() => grain.Stop(silo.SiloAddress, cancellation.Token)));

        Assert.Equal(0, setup.Shutdowns);
        Assert.Equal(1, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
        Assert.True((await setup.Cluster.Client.GetGrain<IPullingAgentGrain>(grainId)
            .Probe(TestContext.Current.CancellationToken)).IsRunning);
    }

    [Fact]
    public async Task Deactivation_WithCanceledDeadlineStillShutsDownReceiver()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        var grainId = PullingAgentId.Create(ProviderName, Queue);
        Assert.True(setup.Cluster.TryGetGrainContext(grainId, out var context));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Exercise an expired deactivation deadline."), cancellation.Token);
        await context.Deactivated.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, setup.Shutdowns);
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    [Fact]
    public async Task StopAgents_ReportsZeroRunningAgentsWhileAwaitingReceiverShutdown()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        setup.ShutdownBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = setup.Command(silo, PersistentStreamProviderCommand.StopAgents);
        try
        {
            await setup.ShutdownEntered.Task.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
            Assert.Equal(StreamLifecycleOptions.RunState.AgentsStopped, await setup.Command(silo, PersistentStreamProviderCommand.GetAgentsState));
        }
        finally
        {
            setup.ShutdownBarrier.TrySetResult();
            await stopping;
        }

        Assert.Equal(1, setup.Shutdowns);
    }

    [Fact]
    public async Task ReconciliationTimer_ReactivatesQueueWithoutExternalGrainTraffic()
    {
        await using var setup = new Setup(1);
        await setup.Deploy();
        var silo = setup.Cluster.Silos[0];
        var meter = silo.ServiceProvider.GetRequiredService<OrleansInstruments>().Meter;
        var cacheGaugeRegistrations = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)
                    && instrument.Name == InstrumentNames.STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE)
                {
                    Interlocked.Increment(ref cacheGaugeRegistrations);
                }
            },
        };
        listener.Start();
        Assert.Equal(1, cacheGaugeRegistrations);
        await setup.Command(silo, PersistentStreamProviderCommand.StartAgents);
        await setup.NextInitialization();
        var grainId = PullingAgentId.Create(ProviderName, Queue);
        Assert.True(setup.Cluster.TryGetGrainContext(grainId, out var original));
        var originalAddress = original.Address;

        await setup.Cluster.DeactivateAsync(grainId).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(1, setup.Shutdowns);
        Assert.Equal(0, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));

        setup.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(silo.SiloAddress, await setup.NextInitialization());

        // The initialization above was triggered by supervision; this call observes its completed activation.
        var status = await setup.Cluster.Client.GetGrain<IPullingAgentGrain>(grainId)
            .Probe(TestContext.Current.CancellationToken);
        var address = status.Address;
        Assert.True(status.IsRunning);
        Assert.Equal(grainId, address.GrainId);
        Assert.Equal(originalAddress.SiloAddress, address.SiloAddress);
        Assert.NotEqual(originalAddress.ActivationId, address.ActivationId);
        Assert.Equal(2, setup.Initializations);
        Assert.Equal(1, cacheGaugeRegistrations);
        Assert.Equal(1, await setup.Command(silo, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    [Fact]
    public async Task StopOnPreviousHost_LeavesMovedActivationRunning()
    {
        await using var setup = new Setup(2);
        await setup.Deploy();
        var source = setup.Cluster.Silos[0];
        var destination = setup.Cluster.Silos[1];
        await setup.Command(source, PersistentStreamProviderCommand.StartAgents);
        await setup.Command(destination, PersistentStreamProviderCommand.StartAgents);
        Assert.Equal(source.SiloAddress, await setup.NextInitialization());

        var grain = setup.Cluster.Client.GetGrain<IPullingAgentGrain>(PullingAgentId.Create(ProviderName, Queue));
        var previous = await grain.Probe(TestContext.Current.CancellationToken);
        Assert.True(await grain.Rebalance(previous.Address, destination.SiloAddress, TestContext.Current.CancellationToken));
        Assert.Equal(destination.SiloAddress, await setup.NextInitialization());
        var address = (await grain.Probe(TestContext.Current.CancellationToken)).Address;
        Assert.False(await grain.Rebalance(previous.Address, source.SiloAddress, TestContext.Current.CancellationToken));
        await grain.Stop(source.SiloAddress, TestContext.Current.CancellationToken);
        await setup.Command(source, PersistentStreamProviderCommand.StopAgents);

        Assert.Equal(destination.SiloAddress, address.SiloAddress);
        Assert.Equal(2, setup.Initializations);
        Assert.Equal(1, setup.Shutdowns);
        Assert.Equal(0, await setup.Command(source, PersistentStreamProviderCommand.GetNumberRunningAgents));
        Assert.Equal(1, await setup.Command(destination, PersistentStreamProviderCommand.GetNumberRunningAgents));
        Assert.Equal(StreamLifecycleOptions.RunState.AgentsStarted, await setup.Command(destination, PersistentStreamProviderCommand.GetAgentsState));

        await setup.Command(destination, PersistentStreamProviderCommand.StopAgents);
        Assert.Equal(2, setup.Shutdowns);
        Assert.Equal(0, await setup.Command(destination, PersistentStreamProviderCommand.GetNumberRunningAgents));
    }

    private sealed class Setup : IAsyncDisposable
    {
        private readonly Channel<SiloAddress> _initialized = Channel.CreateUnbounded<SiloAddress>();
        private int _initializations;
        private int _shutdowns;
        private int _reads;
        private int _coordinatorNotifications;
        private readonly ConcurrentQueue<IBatchContainer> _batches = new();
        private readonly StreamPullingAgentHostingMode _hostingMode;

        internal InProcessTestCluster Cluster { get; }
        internal IPullingCoordinatorGrain Coordinator => Cluster.Client.GetGrain<IPullingCoordinatorGrain>(
            PullingCoordinatorGrain.GetGrainId(ProviderName));
        internal FakeTimeProvider Clock { get; } = new();
        internal TaskCompletionSource ShutdownEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CoordinatorCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? CoordinatorCallBarrier { get; set; }
        internal string? PausedCoordinatorMethod { get; set; }
        internal TaskCompletionSource? ShutdownBarrier { get; set; }
        internal TaskCompletionSource? InitializationBarrier { get; set; }
        internal Exception? ShutdownFailure { get; set; }
        internal Func<QueueId, Task>? OnShutdown { get; set; }
        internal QueueId[] Queues { get; }
        internal int Initializations => Volatile.Read(ref _initializations);
        internal int Shutdowns => Volatile.Read(ref _shutdowns);
        internal int Reads => Volatile.Read(ref _reads);
        internal int CoordinatorNotifications => Volatile.Read(ref _coordinatorNotifications);

        internal Setup(
            short siloCount,
            bool explicitPubSub = false,
            bool namedStorage = false,
            IGrainStorage? storage = null,
            StreamPullingAgentHostingMode hostingMode = StreamPullingAgentHostingMode.Grain,
            int queueCount = 1)
        {
            _hostingMode = hostingMode;
            Queues = Enumerable.Range(0, queueCount)
                .Select(index => QueueId.GetQueueId("Control", (uint)index, (uint)index + 1)).ToArray();
            var builder = new InProcessTestClusterBuilder(siloCount);
            builder.ConfigureSilo((_, siloBuilder) =>
            {
                siloBuilder.Services.AddSingleton<TimeProvider>(Clock);
                siloBuilder.Services.UseTimeProviderForBackgroundAreas(TimeProvider.System);
                siloBuilder.Services.AddKeyedSingleton<TimeProvider>(StreamingTimeProviderNames.Streaming, Clock);
                siloBuilder.Services.AddSingleton<IOutgoingGrainCallFilter>(new NotificationRecorder(this));
                if (explicitPubSub)
                {
                    var storageName = namedStorage ? ProviderName : "PubSubStore";
                    if (storage is null)
                    {
                        siloBuilder.AddMemoryGrainStorage(storageName);
                    }
                    else
                    {
                        siloBuilder.Services.AddKeyedSingleton(storageName, storage);
                    }
                }
                siloBuilder.AddPersistentStreams(
                    ProviderName,
                    (services, name) => CreateFactory(name, services.GetRequiredService<ILocalSiloDetails>().SiloAddress,
                        services.GetRequiredService<ILoggerFactory>()),
                    configurator =>
                    {
                        configurator.ConfigurePullingAgent(options => options.Configure(value =>
                        {
                            value.HostingMode = hostingMode;
                            value.GetQueueMsgsTimerPeriod = TimeSpan.FromSeconds(1);
                        }));
                        configurator.ConfigureLifecycle(options => options.Configure(value =>
                            value.StartupState = StreamLifecycleOptions.RunState.AgentsStopped));
                        if (hostingMode == StreamPullingAgentHostingMode.Grain)
                        {
                            configurator.ConfigurePartitionBalancing((_, _) =>
                                throw new InvalidOperationException("Grain hosting must not construct a queue balancer."));
                        }
                        else
                        {
                            configurator.UseConsistentRingQueueBalancer();
                        }
                    });
                siloBuilder.Services.Configure<StreamPubSubOptions>(ProviderName, options => options.PubSubType =
                    explicitPubSub ? StreamPubSubType.ExplicitGrainBasedOnly : StreamPubSubType.ImplicitOnly);
            });
            Cluster = builder.Build();
        }

        internal async Task Deploy()
        {
            await Cluster.DeployAsync(TestContext.Current.CancellationToken);
            await Cluster.WaitForLivenessToStabilizeAsync().WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            await Cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        }

        internal async Task<SiloAddress> NextInitialization()
        {
            try
            {
                return await _initialized.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                    .WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Waiting for receiver initialization for {ProviderName}/{Queue}; initialized={Initializations}, shutdown={Shutdowns}, reads={Reads}.",
                    exception);
            }
        }

        internal async Task<object?> Command(InProcessSiloHandle silo, PersistentStreamProviderCommand command)
        {
            var result = await silo.ServiceProvider.GetRequiredKeyedService<IControllable>(ProviderName).ExecuteCommand((int)command, null)
                .WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            if (command == PersistentStreamProviderCommand.StartAgents && _hostingMode == StreamPullingAgentHostingMode.Grain)
            {
                Clock.Advance(TimeSpan.Zero);
                await Coordinator.GetAgents(TestContext.Current.CancellationToken).WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
            }

            return result;
        }

        internal IPersistentStreamPullingManager GetManager(InProcessSiloHandle silo)
            => silo.ServiceProvider.GetRequiredService<IInternalGrainFactory>()
                .GetSystemTarget<IPersistentStreamPullingManager>(
                    SystemTargetGrainId.Create(Constants.StreamPullingAgentManagerType, silo.SiloAddress, ProviderName).GrainId);

        internal async Task Publish(QualifiedStreamId stream, DiagnosticEventCollector events)
        {
            var registered = events.WaitForEventAsync(nameof(StreamingEvents.PullingAgentStreamRegistered),
                evt => evt.Payload is StreamingEvents.PullingAgentStreamRegistered value && value.StreamId == stream.StreamId,
                PhaseTimeout, TestContext.Current.CancellationToken);
            _batches.Enqueue(new RetirementBatch(stream.StreamId));
            Clock.Advance(TimeSpan.FromSeconds(1));
            await registered;
        }

        internal async Task MakeInactive(QualifiedStreamId stream, DiagnosticEventCollector events)
        {
            var inactive = events.WaitForEventAsync(nameof(StreamingEvents.StreamInactive),
                evt => evt.Payload is StreamingEvents.StreamInactive value && value.StreamId == stream.StreamId,
                PhaseTimeout, TestContext.Current.CancellationToken);
            Clock.Advance(new StreamPullingAgentOptions().StreamInactivityPeriod + TimeSpan.FromSeconds(1));
            await inactive;
        }

        private IQueueAdapterFactory CreateFactory(string name, SiloAddress silo, ILoggerFactory loggerFactory)
        {
            var mapper = Substitute.For<IConsistentRingStreamQueueMapper>();
            mapper.GetAllQueues().Returns(Queues);
            mapper.GetQueueForStream(Arg.Any<StreamId>()).Returns(Queue);
            mapper.GetQueuesForRange(Arg.Any<IRingRange>()).Returns(Queues);
            var adapterCache = new SimpleQueueAdapterCache(new SimpleQueueCacheOptions { CacheSize = 100 }, name, loggerFactory);
            var adapter = Substitute.For<IQueueAdapter>();
            adapter.Name.Returns(name);
            adapter.Direction.Returns(StreamProviderDirection.ReadOnly);
            adapter.IsRewindable.Returns(true);
            adapter.CreateReceiver(Arg.Any<QueueId>()).Returns(call =>
            {
                var queue = call.Arg<QueueId>();
                var receiver = Substitute.For<IQueueAdapterReceiver>();
                receiver.Initialize(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    Interlocked.Increment(ref _initializations);
                    Assert.True(_initialized.Writer.TryWrite(silo));
                    return InitializationBarrier?.Task ?? Task.CompletedTask;
                });
                receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    Interlocked.Increment(ref _reads);
                    return Task.FromResult<IList<IBatchContainer>>(_batches.TryDequeue(out var batch) ? [batch] : []);
                });
                receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    Interlocked.Increment(ref _shutdowns);
                    ShutdownEntered.TrySetResult();
                    if (OnShutdown is { } onShutdown)
                    {
                        return onShutdown(queue);
                    }

                    if (ShutdownFailure is { } failure)
                    {
                        return Task.FromException(failure);
                    }

                    return ShutdownBarrier?.Task ?? Task.CompletedTask;
                });
                return receiver;
            });

            var factory = Substitute.For<IQueueAdapterFactory>();
            factory.CreateAdapter(Arg.Any<CancellationToken>()).Returns(Task.FromResult(adapter));
            factory.GetStreamQueueMapper().Returns(mapper);
            factory.GetQueueAdapterCache().Returns(adapterCache);
            factory.GetDeliveryFailureHandler(Arg.Any<QueueId>()).Returns(Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler()));
            return factory;
        }

        public ValueTask DisposeAsync() => Cluster.DisposeAsync();

        private sealed class RetirementBatch(StreamId streamId) : IBatchContainer
        {
            public StreamId StreamId => streamId;
            public StreamSequenceToken SequenceToken { get; } = new EventSequenceTokenV2(1);
            public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];
            public bool ImportRequestContext() => false;
        }

        private sealed class NotificationRecorder(Setup setup) : IOutgoingGrainCallFilter
        {
            public async Task Invoke(IOutgoingGrainCallContext context)
            {
                if (context.TargetId.Equals(PullingCoordinatorGrain.GetGrainId(ProviderName))
                    && context.MethodName == nameof(IPullingCoordinatorGrain.NotifyHostChanged))
                {
                    Interlocked.Increment(ref setup._coordinatorNotifications);
                }

                await context.Invoke();
                if (context.TargetId.Equals(PullingCoordinatorGrain.GetGrainId(ProviderName))
                    && context.MethodName == setup.PausedCoordinatorMethod
                    && setup.CoordinatorCallBarrier is { } barrier)
                {
                    setup.CoordinatorCallStarted.TrySetResult();
                    await barrier.Task;
                }
            }
        }
    }

}
