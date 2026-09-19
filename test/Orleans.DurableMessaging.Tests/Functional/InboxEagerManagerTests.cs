using System.Buffers;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.Timers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxEagerManagerTests : DurableMessagingBehaviorTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SynchronousManager_StagesBeforeWriteAndPreservesRequestOutcome(bool reverseStateOrder, bool rejectWrite)
    {
        var source = NewGrain();
        _ = await source.GetSnapshotAsync();
        var services = Fixture.GetGrainContext(source).ActivationServices;
        await using var manager = new EagerManager(services.GetRequiredService<IJournaledStateManager>(),
            services.GetRequiredKeyedService<IJournalFormat>("orleans-binary"), reverseStateOrder);
        var primary = (IStateMachine)Create("InboxJournalState", manager);
        manager.RegisterStateMachine("__orleans.durable-messaging.inbox", primary);
        var messages = (IDurableDictionary<(GrainId, Guid), DurableEnvelope>)primary;
        var processed = Dictionary<DateTimeOffset>(manager, "inbox-processed");
        var attempts = InternalDictionary(manager, "InboxMessageState", "inbox-message-state");
        var deadLetters = InternalDictionary(manager, "InboxDeadLetter", "inbox-dead-letters");
        var ownerId = new ObservedJournalValue<string>(manager);
        manager.RegisterStateMachine("__orleans.durable-messaging.inbox-job-id", ownerId);
        var ownerJob = Value<DurableJob>(manager, "inbox-job-handle");
        var completed = Value<string>(manager, "inbox-completed-job-id");
        var sequence = Value<long>(manager, "inbox-job-sequence");
        var handler = Substitute.For<IInboxHandler>();
        handler.CanHandle(Arg.Any<IInboxHandlerContext>()).Returns(true);
        var inbox = Create("DurableInbox", messages, new[] { handler }, 10);
        var context = Substitute.For<IGrainContext>();
        var grainId = GrainId.Create("eager-inbox", "standalone");
        context.GrainId.Returns(grainId);
        context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
        var jobs = Substitute.For<ILocalDurableJobManager>();
        DurableJob scheduled = null!;
        jobs.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<ScheduleJobRequest>();
            scheduled = new DurableJob
            {
                Id = "scheduler-returned-id",
                ShardId = "scheduler-returned-shard",
                Name = request.JobName,
                TargetGrainId = request.Target,
                DueTime = request.DueTime,
                Metadata = request.Metadata
            };
            return Task.FromResult(scheduled);
        });
        var rejection = new InvalidOperationException("Foreign state rejected this write request.");
        if (rejectWrite)
        {
            ownerId.ValidateWriting = () => throw rejection;
            context.When(value => value.Deactivate(Arg.Any<DeactivationReason>(), Arg.Any<CancellationToken>()))
                .Do(_ => throw new IOException("Secondary deactivation failure."));
        }
        var timers = Substitute.For<ITimerRegistry>();
        var type = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
        var extension = (IDurableInboxExtension)Create("DurableInboxExtension", context,
            timers, manager, services.GetRequiredService<SerializerSessionPool>(),
            services.GetRequiredService(typeof(ILogger<>).MakeGenericType(type)),
            services.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableMessagingInstruments")),
            inbox, messages, processed, attempts, deadLetters, ownerId, ownerJob, completed, sequence,
            Substitute.For<IDurableOutbox>(), jobs, Substitute.For<IDurableJobHandlerRegistry>(),
            Create("DurableMessagingPumpResults"), TimeProvider.System, TimeProvider.System,
            new DurableInboxOptions { MaxCapacity = 10 }, primary);
        using var lifetime = (IDisposable)extension;
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var envelope = new DurableEnvelopeBuilder(services.GetRequiredService<SerializerSessionPool>(), GrainId.Create("sender", "eager"))
            .To(grainId, "route").WithBody(42).Build();
        manager.BeforeCapture = () =>
        {
            Assert.Equal(envelope, Assert.Single(messages).Value);
            Assert.Same(scheduled, ownerJob.Value);
            Assert.Equal(scheduled.Metadata!["orleans.messaging.ownership-id"], ownerId.Value);
            Assert.Equal(1, sequence.Value);
        };
        var delivery = extension.DeliverAsync(envelope, TestContext.Current.CancellationToken);
        if (rejectWrite)
        {
            Assert.Same(rejection, await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.AsTask()));
            Assert.Same(rejection, Assert.Throws<InvalidOperationException>(() => primary.IsWritePrepared));
            Assert.Equal(1, manager.Requests);
            Assert.Equal(0, manager.Writes);
            Assert.False(manager.IsFenced);
            Assert.Empty(manager.Batches);
            Assert.Single(messages);
            Assert.Empty(timers.ReceivedCalls());
        }
        else
        {
            Assert.True(delivery.IsCompletedSuccessfully);
            Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
            Assert.Equal(1, manager.Writes);
            Assert.NotEmpty(Assert.Single(manager.Batches));
            Assert.Empty((IEnumerable<(GrainId, Guid)>)type.GetField("_provisionalAcceptances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(extension)!);
        }
        await jobs.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
    }

    private static object Create(string name, params object[] args) => Activator.CreateInstance(
        ReceiverTestServices.GetImplementationType(name), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DoNotWrapExceptions,
        binder: null, args, culture: null)!;

    private static IDurableDictionary<(GrainId, Guid), T> Dictionary<T>(EagerManager manager, string name)
    {
        var state = ReceiverTestServices.CreateDeferredDictionary<(GrainId, Guid), T>(manager);
        manager.RegisterStateMachine("__orleans.durable-messaging." + name, state);
        return (IDurableDictionary<(GrainId, Guid), T>)state;
    }

    private static object InternalDictionary(EagerManager manager, string type, string name)
    {
        var stateType = ReceiverTestServices.GetImplementationType("DeferredJournaledDictionary`2")
            .MakeGenericType(typeof((GrainId, Guid)), ReceiverTestServices.GetImplementationType(type));
        var state = (IStateMachine)Activator.CreateInstance(stateType, manager)!;
        manager.RegisterStateMachine("__orleans.durable-messaging." + name, state);
        return state;
    }

    private static IDurableValue<T> Value<T>(EagerManager manager, string name)
    {
        var state = ReceiverTestServices.CreateDeferredValue<T>(manager);
        manager.RegisterStateMachine("__orleans.durable-messaging." + name, state);
        return (IDurableValue<T>)state;
    }

    private sealed class EagerManager(IJournaledStateManager codecs, IJournalFormat format, bool reverseStateOrder) : IJournaledStateManager
    {
        private readonly Dictionary<string, IStateMachine> _states = [];
        private readonly JournalBufferWriter _writer = format.CreateWriter();
        private readonly Dictionary<IStateMachine, uint> _ids = [];
        private IEnumerable<IStateMachine> States => reverseStateOrder ? _states.Values.Reverse() : _states.Values;
        private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _failure;
        public Action? BeforeCapture { get; set; }
        public int Writes { get; private set; }
        public int Requests { get; private set; }
        public bool IsFenced => _failure is not null;
        public List<byte[]> Batches { get; } = [];
        public TCodec GetRequiredCommandCodec<TCodec>() where TCodec : notnull => codecs.GetRequiredCommandCodec<TCodec>();
        public void RegisterStateMachine(string name, IStateMachine state)
        {
            _states.Add(name, state);
            _ids.Add(state, checked((uint)(_ids.Count + 8)));
        }
        public bool TryGetStateMachine(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IStateMachine? state) => _states.TryGetValue(name, out state);
        public ValueTask InitializeAsync(CancellationToken token)
        {
            _failure?.Throw();
            token.ThrowIfCancellationRequested();
            foreach (var state in States) state.Reset(_writer.CreateJournalStreamWriter(new(_ids[state])));
            foreach (var state in States) state.OnRecoveryCompleted();
            return default;
        }
        public async ValueTask WriteStateAsync(CancellationToken token)
        {
            Requests++;
            _failure?.Throw();
            token.ThrowIfCancellationRequested();
            foreach (var state in States) state.ValidateWrite();
            try
            {
                while (States.FirstOrDefault(state => !state.IsWritePrepared) is { } unready)
                    await unready.PrepareWriteAsync(token);
                BeforeCapture?.Invoke();
                foreach (var state in States) state.WritePendingEntries(_writer.CreateJournalStreamWriter(new(_ids[state])));
                using var buffer = _writer.GetBuffer();
                if (buffer.Length > 0)
                {
                    Batches.Add(buffer.AsReadOnlySequence().ToArray());
                    _writer.Reset();
                    foreach (var state in States) state.OnWriteCompleted();
                }
                Writes++;
            }
            catch (Exception exception)
            {
                _failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                foreach (var state in States) state.OnFaulted(exception);
                throw;
            }
        }
        public ValueTask DeleteStateAsync(CancellationToken token)
        {
            _failure?.Throw();
            token.ThrowIfCancellationRequested();
            foreach (var state in States) state.ValidateDelete();
            foreach (var state in States) state.OnDeleteStarted();
            _writer.Reset();
            foreach (var state in States) state.Reset(_writer.CreateJournalStreamWriter(new(_ids[state])));
            Batches.Clear();
            return default;
        }
        public ValueTask DisposeAsync() { _writer.Dispose(); return default; }
    }
}
