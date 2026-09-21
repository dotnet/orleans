using System.Buffers;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    public async Task SynchronousManager_StagesBeforeWriteAndCompletesWithoutStateFaultNotifications(bool reverseStateOrder, bool failWrite)
    {
        var source = NewGrain();
        _ = await source.GetSnapshotAsync();
        var services = Fixture.GetGrainContext(source).ActivationServices;
        var format = services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey;
        var builder = InboxStateManagerBoundaryTests.CreateBuilder(format);
        builder.Services.AddScoped<IJournaledStateManager>(sp =>
            new EagerManager(sp.GetRequiredKeyedService<IJournalFormat>(format), reverseStateOrder));
        await using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var stateServices = scope.ServiceProvider;
        var manager = Assert.IsType<EagerManager>(stateServices.GetRequiredService<IJournaledStateManager>());
        var messages = stateServices.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("__orleans.durable-messaging.inbox");
        var processed = stateServices.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("__orleans.durable-messaging.inbox-processed");
        var attempts = stateServices.GetRequiredKeyedService(typeof(IDurableDictionary<,>)
            .MakeGenericType(typeof((GrainId, Guid)), ReceiverTestServices.GetImplementationType("InboxMessageState")), "__orleans.durable-messaging.inbox-message-state");
        var deadLetters = stateServices.GetRequiredKeyedService(typeof(IDurableDictionary<,>)
            .MakeGenericType(typeof((GrainId, Guid)), ReceiverTestServices.GetImplementationType("InboxDeadLetter")), "__orleans.durable-messaging.inbox-dead-letters");
        var ownerId = stateServices.GetRequiredKeyedService<IDurableValue<string>>("__orleans.durable-messaging.inbox-job-id");
        var ownerJob = stateServices.GetRequiredKeyedService<IDurableValue<DurableJob>>("__orleans.durable-messaging.inbox-job-handle");
        var completed = stateServices.GetRequiredKeyedService<IDurableValue<string>>("__orleans.durable-messaging.inbox-completed-job-id");
        var sequence = stateServices.GetRequiredKeyedService<IDurableValue<long>>("__orleans.durable-messaging.inbox-job-sequence");
        var handler = Substitute.For<IInboxHandler>();
        handler.CanHandle(Arg.Any<IInboxHandlerContext>()).Returns(true);
        var inbox = Create("DurableInbox", messages, new[] { handler }, 10);
        var context = Substitute.For<IGrainContext>();
        var grainId = GrainId.Create("eager-inbox", "standalone");
        context.GrainId.Returns(grainId);
        context.ActivationServices.Returns(stateServices);
        context.GrainInstance.Returns(Substitute.For<IDurableMessagingGrain>());
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
        var failure = new IOException("Owner failed while capturing the staged write.");
        if (failWrite)
        {
            context.When(value => value.Deactivate(Arg.Any<DeactivationReason>(), Arg.Any<CancellationToken>()))
                .Do(_ => throw new IOException("Secondary deactivation failure."));
        }
        var timers = Substitute.For<ITimerRegistry>();
        var type = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
        var extension = (IDurableInboxExtension)Create("DurableInboxExtension", context,
            timers, manager, stateServices.GetRequiredService<SerializerSessionPool>(),
            services.GetRequiredService(typeof(ILogger<>).MakeGenericType(type)),
            services.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableMessagingInstruments")),
            inbox, messages, processed, attempts, deadLetters, ownerId, ownerJob, completed, sequence,
            Substitute.For<IDurableOutbox>(), jobs, Substitute.For<IDurableJobHandlerRegistry>(),
            Create("DurableMessagingPumpResults"), TimeProvider.System, TimeProvider.System,
            new DurableInboxOptions { MaxCapacity = 10 });
        using var lifetime = (IDisposable)extension;
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await ((ILifecycleObserver)extension).OnStart(TestContext.Current.CancellationToken);
        var envelope = new DurableEnvelopeBuilder(stateServices.GetRequiredService<SerializerSessionPool>(), GrainId.Create("sender", "eager"))
            .To(grainId, "route").WithBody(42).Build();
        manager.BeforeCapture = () =>
        {
            Assert.Equal(envelope, Assert.Single(messages).Value);
            Assert.Same(scheduled, ownerJob.Value);
            Assert.Equal(scheduled.Metadata!["orleans.messaging.ownership-id"], ownerId.Value);
            Assert.Equal(1, sequence.Value);
            if (failWrite) throw failure;
        };
        var delivery = extension.DeliverAsync(envelope, TestContext.Current.CancellationToken);
        if (failWrite)
        {
            Assert.True(delivery.IsCompleted);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => delivery.AsTask()));
            var captured = (System.Runtime.ExceptionServices.ExceptionDispatchInfo)type
                .GetField("_failure", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(extension)!;
            Assert.Same(failure, captured.SourceException);
            Assert.Equal(1, manager.Requests);
            Assert.Equal(0, manager.Writes);
            Assert.True(manager.IsFenced);
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

    private sealed class EagerManager(IJournalFormat format, bool reverseStateOrder) : IJournaledStateManager
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
        public ValueTask WriteStateAsync(CancellationToken token)
        {
            Requests++;
            _failure?.Throw();
            token.ThrowIfCancellationRequested();
            try
            {
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
                return ValueTask.CompletedTask;
            }
            catch (Exception exception)
            {
                _failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }
        public ValueTask DeleteStateAsync(CancellationToken token)
        {
            _failure?.Throw();
            token.ThrowIfCancellationRequested();
            _writer.Reset();
            foreach (var state in States) state.Reset(_writer.CreateJournalStreamWriter(new(_ids[state])));
            Batches.Clear();
            return default;
        }
        public ValueTask DisposeAsync() { _writer.Dispose(); return default; }
    }
}
