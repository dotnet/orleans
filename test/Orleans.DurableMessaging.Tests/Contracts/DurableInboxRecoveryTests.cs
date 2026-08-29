using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.Timers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableInboxRecoveryTests
{
    [Fact]
    public async Task RecoveryWithMessagesAndNoOwnerPersistsReplacementJob()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var sender = GrainId.Create("sender", "one");
        var receiver = GrainId.Create("receiver", "one");
        var envelope = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = sender,
            ReceiverId = receiver,
            RouteKey = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            Data = (DurableEnvelopeData)RuntimeHelpers.GetUninitializedObject(typeof(DurableEnvelopeData)),
        };
        var inboxState = new TestDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope>
        {
            [(sender, envelope.MessageId)] = envelope,
        };
        var jobId = new TestDurableValue<string>();
        var manager = new RecordingStateManager();
        var timers = new ImmediateTimerRegistry();
        var jobs = new RecordingJobManager(failuresBeforeSuccess: 1);
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(receiver);
        context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
        var inbox = new DurableInbox(inboxState);

        var extension = new DurableInboxExtension(
            context,
            Substitute.For<IGrainFactory>(),
            timers,
            manager,
            services.GetRequiredService<SerializerSessionPool>(),
            NullLogger<DurableInboxExtension>.Instance,
            DurableMessagingInstruments.CreateForDirectConstruction(),
            inbox,
            inboxState,
            new Dictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset>(),
            new Dictionary<(GrainId SenderId, Guid MessageId), InboxMessageState>(),
            new Dictionary<(GrainId SenderId, Guid MessageId), InboxDeadLetter>(),
            jobId,
            new TestDurableValue<string>(),
            new TestDurableValue<long>(),
            Substitute.For<IDurableOutbox>(),
            jobs,
            Substitute.For<IDurableJobHandlerRegistry>(),
            new DurableMessagingPumpResults(),
            TimeProvider.System,
            TimeProvider.System,
            new DurableInboxOptions { BackpressureRetryDelay = TimeSpan.FromMilliseconds(1) });

        extension.OnRecoveryCompleted();
        await timers.WaitAsync();

        Assert.Equal(2, jobs.ScheduleCount);
        Assert.False(string.IsNullOrEmpty(jobId.Value));
        Assert.True(manager.WriteCount >= 1);
    }

    private sealed class RecordingStateManager : IJournaledStateManager
    {
        public int WriteCount { get; private set; }
        public void RegisterObserver(IJournaledStateObserver observer) { }
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterState(string name, IJournaledState state) { }
        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            WriteCount++;
            return default;
        }

        public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken) => default;
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class ImmediateTimerRegistry : ITimerRegistry
    {
        private readonly List<Task> _callbacks = [];

        [Obsolete]
        public IDisposable RegisterTimer(
            IGrainContext grainContext,
            Func<object?, Task> callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => throw new NotSupportedException();

        public IGrainTimer RegisterGrainTimer<TState>(
            IGrainContext grainContext,
            Func<TState, CancellationToken, Task> callback,
            TState state,
            GrainTimerCreationOptions options)
        {
            _callbacks.Add(callback(state, CancellationToken.None));
            return Substitute.For<IGrainTimer>();
        }

        public async Task WaitAsync()
        {
            for (var index = 0; ; index++)
            {
                Task callback;
                lock (_callbacks)
                {
                    if (index >= _callbacks.Count)
                    {
                        return;
                    }

                    callback = _callbacks[index];
                }

                await callback;
            }
        }
    }

    private sealed class RecordingJobManager(int failuresBeforeSuccess) : ILocalDurableJobManager
    {
        public int ScheduleCount { get; private set; }

        public Task<DurableJob> ScheduleJobAsync(
            ScheduleJobRequest request,
            CancellationToken cancellationToken)
        {
            ScheduleCount++;
            if (ScheduleCount <= failuresBeforeSuccess)
            {
                throw new IOException("Expected transient scheduling failure.");
            }

            return Task.FromResult(new DurableJob
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = "test",
                Metadata = request.Metadata,
            });
        }

        public Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class TestDurableValue<T> : IDurableValue<T>
    {
        public T? Value { get; set; }
    }

    private sealed class TestDurableDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IDurableDictionary<TKey, TValue>
        where TKey : notnull;

    [Fact]
    public async Task DeliverAsync_RecoveryDuringProvisionalAcceptance_DoesNotReturnAccepted()
    {
        var receiver = GrainId.Create("receiver", "provisional-acceptance");
        var sender = GrainId.Create("sender", "provisional-acceptance");
        const string route = "messages/provisional-acceptance";
        using var harness = new GenerationHarness(receiver);
        harness.Inbox.RegisterHandler(route, new NoOpHandler());
        var envelope = CreateEnvelope(sender, receiver, route);
        var key = (sender, envelope.MessageId);
        var write = harness.Manager.BlockWrite();
        harness.Manager.RecoverState = () =>
        {
            harness.InboxState.Clear();
            harness.Processed.Clear();
            harness.MessageStates.Clear();
            harness.JobId.Value = null;
        };

        var delivery = harness.Extension.DeliverAsync(
            envelope,
            TestContext.Current.CancellationToken).AsTask();
        await write.WaitUntilEnteredAsync(
            receiver,
            "provisional acceptance write",
            TestContext.Current.CancellationToken);

        try
        {
            Assert.False(delivery.IsCompleted);
            Assert.True(harness.InboxState.ContainsKey(key));

            harness.Manager.RecoverCommittedState();

            Assert.Empty(harness.InboxState);
            Assert.Empty(harness.Processed);
            Assert.Empty(harness.MessageStates);
        }
        finally
        {
            write.Release();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => delivery);

        Assert.Equal(
            "Durable inbox acceptance was interrupted by state recovery or deletion.",
            exception.Message);
        Assert.Empty(harness.InboxState);
        Assert.Empty(harness.Processed);
        Assert.Empty(harness.MessageStates);
        Assert.Equal(1, harness.Manager.WriteCount);
        Assert.Equal(1, harness.Manager.RevertCount);
    }

    [Fact]
    public async Task ProcessMessageAsync_RecoveryDuringHandler_DoesNotMarkMessageProcessed()
    {
        var receiver = GrainId.Create("receiver", "stale-handler");
        var sender = GrainId.Create("sender", "stale-handler");
        const string route = "messages/stale-handler";
        const string jobId = "inbox-job-1";
        using var harness = new GenerationHarness(receiver);
        var envelope = CreateEnvelope(sender, receiver, route);
        var key = (sender, envelope.MessageId);
        var effects = new Dictionary<Guid, int>();
        var probe = new Orleans.DurableMessaging.Tests.Support.HandlerProbe();
        var handler = new EffectHandler(probe, receiver, effects);
        harness.Inbox.RegisterHandler(route, handler);
        harness.Manager.RecoverState = () =>
        {
            harness.InboxState.Clear();
            harness.InboxState[key] = envelope;
            harness.Processed.Clear();
            harness.MessageStates.Clear();
            harness.MessageStates[key] = new InboxMessageState();
            harness.JobId.Value = jobId;
            effects.Clear();
        };
        harness.Manager.RecoverCommittedState();

        using (var barrier = probe.Arm(receiver, route))
        {
            var processing = harness.Extension.ExecuteJobCoreAsync(
                jobId,
                clearOwnershipWhenEmpty: false,
                hasStableOwnership: false,
                TestContext.Current.CancellationToken).AsTask();
            await barrier.WaitUntilEnteredAsync();

            harness.Manager.RecoverCommittedState();
            barrier.Release();
            await processing;

            Assert.Equal(1, handler.InvocationCount);
            Assert.True(harness.InboxState.ContainsKey(key));
            Assert.True(harness.MessageStates.TryGetValue(key, out var recoveredState));
            Assert.Equal(0, recoveredState.AttemptCount);
            Assert.Empty(harness.Processed);
            Assert.Empty(effects);
            Assert.Equal(0, harness.Manager.WriteCount);
            Assert.Equal(1, harness.Manager.RevertCount);
        }

        await harness.Extension.ExecuteJobCoreAsync(
            jobId,
            clearOwnershipWhenEmpty: false,
            hasStableOwnership: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.InvocationCount);
        Assert.Equal(1, Assert.Single(effects).Value);
        Assert.Empty(harness.InboxState);
        Assert.Empty(harness.MessageStates);
        Assert.True(harness.Processed.ContainsKey(key));
        Assert.Equal(1, harness.Manager.WriteCount);
        Assert.Equal(1, harness.Manager.RevertCount);
    }

    private static DurableEnvelope CreateEnvelope(GrainId sender, GrainId receiver, string route) =>
        new()
        {
            MessageId = Guid.NewGuid(),
            SenderId = sender,
            ReceiverId = receiver,
            RouteKey = route,
            CreatedAt = DateTimeOffset.UtcNow,
            Data = (DurableEnvelopeData)RuntimeHelpers.GetUninitializedObject(typeof(DurableEnvelopeData)),
        };

    private sealed class GenerationHarness : IDisposable
    {
        private readonly ServiceProvider _services;

        public GenerationHarness(GrainId receiver)
        {
            _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(receiver);
            context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
            Inbox = new DurableInbox(InboxState);
            Extension = new DurableInboxExtension(
                context,
                Substitute.For<IGrainFactory>(),
                new PassiveTimerRegistry(),
                Manager,
                _services.GetRequiredService<SerializerSessionPool>(),
                NullLogger<DurableInboxExtension>.Instance,
                DurableMessagingInstruments.CreateForDirectConstruction(),
                Inbox,
                InboxState,
                Processed,
                MessageStates,
                new Dictionary<(GrainId SenderId, Guid MessageId), InboxDeadLetter>(),
                JobId,
                new TestDurableValue<string>(),
                new TestDurableValue<long>(),
                Substitute.For<IDurableOutbox>(),
                new SuccessfulJobManager(),
                Substitute.For<IDurableJobHandlerRegistry>(),
                new DurableMessagingPumpResults(),
                TimeProvider.System,
                TimeProvider.System,
                new DurableInboxOptions
                {
                    MaxCapacity = 8,
                    MaxProcessingAttempts = 3,
                    InboxBatchSize = 8,
                    BackpressureRetryDelay = TimeSpan.FromMilliseconds(1),
                });
        }

        public RecoveryStateManager Manager { get; } = new();
        public TestDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> InboxState { get; } = [];
        public Dictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> Processed { get; } = [];
        public Dictionary<(GrainId SenderId, Guid MessageId), InboxMessageState> MessageStates { get; } = [];
        public TestDurableValue<string> JobId { get; } = new();
        public DurableInbox Inbox { get; }
        public DurableInboxExtension Extension { get; }

        public void Dispose()
        {
            Extension.Dispose();
            _services.Dispose();
        }
    }

    private sealed class RecoveryStateManager : IJournaledStateManager
    {
        private readonly List<IJournaledStateObserver> _observers = [];
        private WriteBarrier? _blockedWrite;

        public Action RecoverState { get; set; } = static () => { };
        public int WriteCount { get; private set; }
        public int RevertCount { get; private set; }

        public WriteBarrier BlockWrite()
        {
            var barrier = new WriteBarrier();
            if (Interlocked.CompareExchange(ref _blockedWrite, barrier, null) is not null)
            {
                throw new InvalidOperationException("A write barrier is already armed.");
            }

            return barrier;
        }

        public void RecoverCommittedState()
        {
            RecoverState();
            foreach (var observer in _observers)
            {
                observer.OnRecoveryCompleted();
            }
        }

        public void RegisterObserver(IJournaledStateObserver observer) => _observers.Add(observer);
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterState(string name, IJournaledState state) { }
        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }

        public async ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            WriteCount++;
            if (Interlocked.Exchange(ref _blockedWrite, null) is { } barrier)
            {
                barrier.Entered.TrySetResult();
                await barrier.Continue.Task.WaitAsync(cancellationToken);
            }
        }

        public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevertCount++;
            RecoverCommittedState();
            return default;
        }

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;

        public sealed class WriteBarrier
        {
            internal TaskCompletionSource Entered { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Continue { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task WaitUntilEnteredAsync(
                GrainId grainId,
                string phase,
                CancellationToken cancellationToken)
            {
                try
                {
                    await Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        $"Journal '{JournalId.FromGrainId(grainId)}' did not enter phase '{phase}'.",
                        exception);
                }
            }

            public void Release() => Continue.TrySetResult();
        }
    }

    private sealed class EffectHandler(
        Orleans.DurableMessaging.Tests.Support.HandlerProbe probe,
        GrainId receiver,
        Dictionary<Guid, int> effects) : IInboxHandler
    {
        public int InvocationCount { get; private set; }

        public bool CanHandle(IInboxHandlerContext context) => true;

        public async ValueTask HandleAsync(
            IInboxHandlerContext context,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (probe.TryGet(receiver, context.Envelope.RouteKey, out var barrier))
            {
                barrier.Entered.TrySetResult();
                await barrier.Continue.Task.WaitAsync(cancellationToken);
            }

            effects.TryGetValue(context.Envelope.MessageId, out var count);
            effects[context.Envelope.MessageId] = count + 1;
        }
    }

    private sealed class NoOpHandler : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => true;
        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) => default;
    }

    private sealed class PassiveTimerRegistry : ITimerRegistry
    {
        [Obsolete]
        public IDisposable RegisterTimer(
            IGrainContext grainContext,
            Func<object?, Task> callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => throw new NotSupportedException();

        public IGrainTimer RegisterGrainTimer<TState>(
            IGrainContext grainContext,
            Func<TState, CancellationToken, Task> callback,
            TState state,
            GrainTimerCreationOptions options) =>
            Substitute.For<IGrainTimer>();
    }

    private sealed class SuccessfulJobManager : ILocalDurableJobManager
    {
        public Task<DurableJob> ScheduleJobAsync(
            ScheduleJobRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DurableJob
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = "test",
                Metadata = request.Metadata,
            });

        public Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
