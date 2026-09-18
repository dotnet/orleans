using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using Orleans.Timers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class OutboxCodecBoundaryTests
{
    [Fact]
    public async Task Send_ReusesEncodedBodyUntilActualJournalApplication()
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var envelope = fixture.CreateEnvelope();
        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));

        fixture.Outbox.Send(envelope);
        fixture.Outbox.Send(envelope);

        Assert.Equal(0, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(0, fixture.Probe.Count("OutboxMessageState"));
        Assert.Equal(0, fixture.Probe.Count(nameof(DurableJob)));
        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.Outbox.Count);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));
        Assert.Equal(new[] { "capture" }, fixture.Probe.Phases(nameof(DurableEnvelope)));
        Assert.Equal(new[] { "capture" }, fixture.Probe.Phases("OutboxMessageState"));
        Assert.Equal(new[] { "capture" }, fixture.Probe.Phases(nameof(DurableJob)));
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Equal(envelope.MessageId, Assert.Single(fixture.Messages).Key);
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        var restored = Assert.Single(recovered.Messages).Value;
        Assert.True(restored.Data.TryGetBody<Payload>(out var payload));
        Assert.Equal(4096, Assert.IsType<Payload>(payload).Bytes.Length);
    }

    [Fact]
    public async Task DeadLetter_EncodesAtJournalApplicationWithoutPayloadPreflight()
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.RouteNotFound("missing"));
        fixture.Outbox.Send(fixture.CreateEnvelope());
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();

        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(new[] { "capture" }, fixture.Probe.Phases("OutboxDeadLetter"));
        Assert.Equal(1, fixture.Probe.Count("OutboxMessageState"));
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableJob)));
        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.DeadLetterCount);
        Assert.Equal(2, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
    }

    [Fact]
    public async Task RetryState_EncodesOnceWhenApplied()
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.Backpressured(), maxAttempts: 3);
        fixture.Outbox.Send(fixture.CreateEnvelope());
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();

        Assert.Equal(new[] { "capture", "capture" }, fixture.Probe.Phases("OutboxMessageState"));
        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableJob)));
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.DeadLetterCount);
    }

    [Fact]
    public async Task BodyCodecFailure_IsReportedByBuilderBeforeIntentAdmission()
    {
        await using var fixture = await CodecFixture.CreateAsync();
        fixture.Probe.FailureType = nameof(Payload);
        Assert.Same(fixture.Probe.Failure, Assert.Throws<InvalidOperationException>(() => fixture.CreateEnvelope()));
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Equal(0, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Null(fixture.States.Failure);
    }

    [Theory]
    [InlineData(nameof(DurableEnvelope), "capture", 1)]
    [InlineData("OutboxMessageState", "capture", 1)]
    [InlineData(nameof(DurableJob), "capture", 1)]
    public async Task JournalCodecFailure_FencesActualManagerBeforePublishing(string failedType, string phase, int stagedMessages)
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var envelope = fixture.CreateEnvelope();
        fixture.Probe.FailureType = failedType;
        fixture.Outbox.Send(envelope);
        Assert.Equal(0, fixture.Probe.Count(failedType));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Same(fixture.Probe.Failure, error);
        Assert.Same(error, fixture.States.Failure);
        Assert.Equal(new[] { phase }, fixture.Probe.Phases(failedType));
        Assert.Equal(stagedMessages, fixture.Messages.Count);
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => fixture.Outbox.Send(envelope)));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(error, rejected.InnerException);

        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Empty(recovered.Messages);
        Assert.Null(recovered.Job.Value);
    }

    [Fact]
    public async Task DeadLetterCodecFailure_PreservesPreviouslyCommittedMessageOnFreshReplay()
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.RouteNotFound("missing"));
        var envelope = fixture.CreateEnvelope();
        fixture.Outbox.Send(envelope);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        fixture.Probe.FailureType = "OutboxDeadLetter";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DeliverAsync());
        Assert.Same(fixture.Probe.Failure, error);
        Assert.Same(error, fixture.States.Failure);
        Assert.Empty(fixture.Messages);
        Assert.Equal(new[] { "capture" }, fixture.Probe.Phases("OutboxDeadLetter"));
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Equal(envelope.MessageId, Assert.Single(recovered.Messages).Key);
        Assert.Equal(0, recovered.DeadLetterCount);
    }

    [Fact]
    public async Task RecoveryRepairRequestVeto_DeactivatesHealthyOwnerlessActivation()
    {
        await using var seed = await CodecFixture.CreateAsync();
        var journal = new JournalId($"repair-veto/{Guid.NewGuid():N}");
        await seed.SeedOwnerlessJournalAsync(journal, seed.CreateEnvelope());
        var veto = new RequestVetoState();
        await using var fixture = await CodecFixture.CreateAsync(seed.Storage, journal, additionalState: veto);
        var writes = fixture.Storage.GetSuccessfulWriteCount(journal);

        await fixture.RunRepairTimerAsync();

        Assert.Equal(1, veto.RequestCount);
        Assert.Equal(0, veto.FaultCount);
        Assert.Null(fixture.States.Failure);
        Assert.Single(fixture.Messages);
        Assert.Null(fixture.Job.Value);
        Assert.Equal(writes, fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Empty(fixture.Jobs.ReceivedCalls());
        var deactivation = Assert.Single(fixture.Context.ReceivedCalls(), call => call.GetMethodInfo().Name == "Deactivate");
        var reason = Assert.IsType<DeactivationReason>(deactivation.GetArguments()[0]);
        Assert.Equal(DeactivationReasonCode.ApplicationError, reason.ReasonCode);
        Assert.Same(veto.Failure, reason.Exception);
        Assert.Same(veto.Failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()));
        Assert.Null(fixture.States.Failure);

        await using var recovered = await CodecFixture.CreateAsync(seed.Storage, journal);
        await recovered.RunRepairTimerAsync();
        Assert.NotNull(recovered.Job.Value);
        Assert.Single(recovered.Messages);
    }

    [Fact]
    public async Task AdmittedRepairSchedulingFailure_UsesExistingTerminalFault()
    {
        await using var seed = await CodecFixture.CreateAsync();
        var journal = new JournalId($"repair-fault/{Guid.NewGuid():N}");
        await seed.SeedOwnerlessJournalAsync(journal, seed.CreateEnvelope());
        await using var fixture = await CodecFixture.CreateAsync(seed.Storage, journal);
        var failure = new IOException("Admitted scheduling failed.");
        fixture.Jobs.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DurableJob>(failure));
        var writes = fixture.Storage.GetSuccessfulWriteCount(journal);

        await fixture.RunRepairTimerAsync();

        Assert.Same(failure, fixture.States.Failure);
        Assert.Equal(writes, fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Single(fixture.Jobs.ReceivedCalls(), call => call.GetMethodInfo().Name == "ScheduleJobAsync");
        Assert.DoesNotContain(fixture.Context.ReceivedCalls(), call => call.GetMethodInfo().Name == "Deactivate");
        var fenced = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(failure, fenced.InnerException);
        await using var recovered = await CodecFixture.CreateAsync(seed.Storage, journal);
        await recovered.RunRepairTimerAsync();
        Assert.NotNull(recovered.Job.Value);
        Assert.Single(recovered.Messages);
    }

    [Theory]
    [InlineData("message", false)]
    [InlineData("receiver", false)]
    [InlineData("data", false)]
    [InlineData("message", true)]
    [InlineData("receiver", true)]
    [InlineData("data", true)]
    public async Task DirectSendMalformedStructure_FailsBeforeIntentOrJournalMutation(string field, bool existingIntent)
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var valid = fixture.CreateEnvelope();
        if (existingIntent) { fixture.Outbox.Send(valid); }
        var invalid = field switch
        {
            "message" => valid with { MessageId = Guid.Empty },
            "receiver" => valid with { ReceiverId = default },
            "data" => valid with { Data = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var error = Assert.ThrowsAny<ArgumentException>(() => fixture.Outbox.Send(invalid));

        Assert.Equal("envelope", error.ParamName);
        Assert.Equal(existingIntent ? 1 : 0, fixture.Outbox.Count);
        Assert.Empty(fixture.Messages);
        Assert.Empty(fixture.Jobs.ReceivedCalls());
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Equal(0, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Null(fixture.States.Failure);
        if (existingIntent) { Assert.Equal(valid, Assert.Single(fixture.Outbox.Messages)); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DirectSendBlankRoute_PreservesExplicitIdentityNullBodyAndRoutingOutcome(string? route)
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.RouteNotFound(route!));
        var message = fixture.CreateNullBodyEnvelope() with
        {
            MessageId = Guid.Parse("89cabd50-d5c6-4bed-a94e-d7c312ed5139"),
            RouteKey = route!
        };
        Assert.True(message.Data.TryGetBody<string>(out var body));
        Assert.Null(body);

        fixture.Outbox.Send(message);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(message.MessageId, Assert.Single(fixture.Messages).Key);
        await fixture.DeliverAsync();

        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.DeadLetterCount);
        Assert.Equal(2, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Null(fixture.States.Failure);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public async Task RealFacets_CaptureAckAndReplayAreIndependentOfRegistrationOrder(int rotation, bool snapshot)
    {
        await using var fixture = await CodecFixture.CreateAsync(stateOrder: rotation, snapshot: snapshot);
        Assert.Equal(7, fixture.States.StateCount);
        Assert.IsAssignableFrom<IDurableDictionary<Guid, DurableEnvelope>>(
            fixture.States.GetState<IJournaledState>("__orleans.durable-messaging.outbox"));
        var first = fixture.CreateEnvelope();
        fixture.Outbox.Send(first);
        var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        var write = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        var later = fixture.CreateEnvelope();
        fixture.Outbox.Send(later);
        Assert.Equal(2, fixture.Outbox.Count);
        Assert.Equal(first.MessageId, Assert.Single(fixture.Messages).Key);
        storage.Release();
        await write;

        Assert.False(fixture.Messages.ContainsKey(later.MessageId));
        Assert.True(fixture.Outbox.TryGetMessage(later.MessageId, out _));
        var owner = Assert.IsType<DurableJob>(fixture.Job.Value);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Same(owner, fixture.Job.Value);
        Assert.Single(fixture.Jobs.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILocalDurableJobManager.ScheduleJobAsync));
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId, stateOrder: 6 - rotation, snapshot: snapshot);
        Assert.Equal(2, recovered.Outbox.Count);
        Assert.True(recovered.Outbox.TryGetMessage(first.MessageId, out _));
        Assert.True(recovered.Outbox.TryGetMessage(later.MessageId, out _));
        Assert.Equal(owner.Id, recovered.Job.Value!.Id);
        Assert.Equal(owner.ShardId, recovered.Job.Value.ShardId);
        Assert.Empty(recovered.Jobs.ReceivedCalls());
        await recovered.Manager.DeleteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, recovered.Outbox.Count);
        Assert.Empty(recovered.Messages);
        Assert.Null(recovered.Job.Value);
        recovered.Outbox.Send(recovered.CreateEnvelope());
        await recovered.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(recovered.Messages);
        Assert.NotNull(recovered.Job.Value);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(0, true)]
    [InlineData(6, true)]
    public async Task OtherStateAwait_LateSendJoinsFinalCohortWithOneWakeup(int rotation, bool alreadyPending)
    {
        var preparation = new PreparationProbe();
        await using var fixture = await CodecFixture.CreateAsync(additionalState: preparation, stateOrder: rotation);
        if (alreadyPending) fixture.Outbox.Send(fixture.CreateEnvelope());
        var write = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await preparation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        fixture.Outbox.Send(fixture.CreateEnvelope());
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        preparation.Release.TrySetResult();
        await write;

        Assert.Equal(alreadyPending ? 2 : 1, fixture.Outbox.Count);
        Assert.Equal(fixture.Outbox.Count, fixture.Messages.Count);
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Single(fixture.Jobs.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILocalDurableJobManager.ScheduleJobAsync));
        var job = Assert.IsType<DurableJob>(fixture.Job.Value);
        Assert.Equal("opaque-shard", job.ShardId);
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Equal(fixture.Outbox.Count, recovered.Outbox.Count);
        Assert.Equal(job.Id, recovered.Job.Value!.Id);
    }

    [Fact]
    public async Task NoOpWrites_LeaveAllFacetsReadyForNextIntent()
    {
        await using var fixture = await CodecFixture.CreateAsync();
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var writes = fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(writes, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Empty(fixture.Jobs.ReceivedCalls());
        fixture.Outbox.Send(fixture.CreateEnvelope());
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(fixture.Messages);
        Assert.NotNull(fixture.Job.Value);
        Assert.Null(fixture.States.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task CanceledCallerDuringStatePreparation_PreservesOwnedCaptureAndAck(int rotation)
    {
        var preparation = new PreparationProbe();
        await using var fixture = await CodecFixture.CreateAsync(additionalState: preparation, stateOrder: rotation);
        fixture.Outbox.Send(fixture.CreateEnvelope());
        using var cancellation = new CancellationTokenSource();
        var write = fixture.Manager.WriteStateAsync(cancellation.Token).AsTask();
        await preparation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        preparation.Release.TrySetResult();
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(fixture.Messages);
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Null(fixture.States.Failure);
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Outbox.Count);
    }

    private sealed class RequestVetoState : ProbeState
    {
        public int RequestCount { get; private set; }
        public InvalidOperationException Failure { get; } = new("Request rejected before admission.");
        public override void ValidateWrite() { RequestCount++; throw Failure; }
    }

    private class ProbeState : IJournaledState, IDurableValueCommandHandler<int>
    {
        private IDurableValueCommandCodec<int> _codec = null!;
        private int _value;
        public int FaultCount { get; private set; }
        public void Bind(IJournaledStateManager manager) => _codec = manager.GetRequiredCommandCodec<IDurableValueCommandCodec<int>>();
        public virtual bool IsWritePrepared => true;
        public virtual ValueTask PrepareWriteAsync(CancellationToken cancellationToken) => default;
        public virtual void ValidateWrite() { }
        public void Reset(JournalStreamWriter writer) => _value = 0;
        public void OnRecoveryCompleted() { }
        public void AppendEntries(JournalStreamWriter writer) { }
        public void AppendSnapshot(JournalStreamWriter writer) => _codec.WriteSet(_value, writer);
        public void OnWriteCompleted() { }
        public void OnFaulted(Exception exception) => FaultCount++;
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);
        public void ApplySet(int value) => _value = value;
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class PreparationProbe : ProbeState
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _prepared;
        public override bool IsWritePrepared => _prepared;
        public override async ValueTask PrepareWriteAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            _prepared = true;
        }
    }

    [GenerateSerializer]
    public sealed record Payload([property: Id(0)] byte[] Bytes);

    public sealed class CodecProbe
    {
        private readonly ConcurrentQueue<(string Type, string Phase)> _calls = new();
        public required SerializerSessionPool InnerSessions { get; init; }
        public string Phase { get; set; } = "before-admission";
        public string? FailureType { get; set; }
        public InvalidOperationException Failure { get; } = new("Injected journal codec failure.");
        public void Writing(Type type)
        {
            _calls.Enqueue((type.Name, Phase));
            if (type.Name == FailureType)
            {
                throw Failure;
            }
        }
        public int Count(string type) => _calls.Count(call => call.Type == type);
        public string[] Phases(string type) => _calls.Where(call => call.Type == type).Select(static call => call.Phase).ToArray();
    }

    public sealed class CountingCodec<T>(CodecProbe probe) : IFieldCodec<T>
    {
        private readonly IFieldCodec<T> _inner = probe.InnerSessions.CodecProvider.GetCodec<T>();
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, [AllowNull] T value)
            where TBufferWriter : IBufferWriter<byte>
        {
            probe.Writing(typeof(T));
            _inner.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }
        [return: MaybeNull]
        public T ReadValue<TInput>(ref Reader<TInput> reader, Field field) => _inner.ReadValue(ref reader, field);
    }

    private sealed class CodecFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _innerServices;
        private readonly ServiceProvider _services;
        private readonly AsyncServiceScope _scope;
        private readonly Func<CancellationToken, Task> _deliver;
        public CodecProbe Probe { get; }
        public JournalId JournalId { get; }
        public ControlledJournalStorageProvider Storage { get; }
        public IJournaledStateManager Manager { get; }
        public IDurableOutbox Outbox { get; }
        public IDurableDictionary<Guid, DurableEnvelope> Messages { get; }
        public IDurableValue<DurableJob> Job { get; }
        public StateTrackingManager States { get; }
        public IGrainContext Context { get; }
        public ITimerRegistry TimerRegistry { get; }
        public ILocalDurableJobManager Jobs { get; }
        public int DeadLetterCount
        {
            get
            {
                var entries = States.GetState<IJournaledState>("__orleans.durable-messaging.outbox-dead-letters");
                return (int)entries.GetType().GetProperty("Count")!.GetValue(entries)!;
            }
        }

        private CodecFixture(ControlledJournalStorageProvider? storage, JournalId? journalId, DeliveryResult delivery, int maxAttempts, ProbeState? additionalState, int stateOrder, bool snapshot)
        {
            JournalId = journalId ?? new JournalId($"codec-boundary/{Guid.NewGuid():N}");
            Storage = storage ?? new ControlledJournalStorageProvider();
            Storage.Configure(Options.Create(new JournaledStateManagerOptions { JournalFormatKey = "orleans-binary" }));
            _innerServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
            Probe = new CodecProbe { InnerSessions = _innerServices.GetRequiredService<SerializerSessionPool>() };
            var services = new ServiceCollection();
            services.AddSerializer(builder => builder.Configure(options =>
            {
                options.AddFieldCodec(typeof(CountingCodec<Payload>));
                options.AddFieldCodec(typeof(CountingCodec<DurableEnvelope>));
                options.AddFieldCodec(typeof(CountingCodec<DurableJob>));
                options.AddFieldCodec(typeof(CountingCodec<>).MakeGenericType(Implementation("OutboxMessageState")));
                options.AddFieldCodec(typeof(CountingCodec<>).MakeGenericType(Implementation("OutboxDeadLetter")));
            }));
            services.AddSingleton(Probe);
            services.AddLogging();
            services.AddKeyedSingleton(JournalingTimeProviderNames.Journaling, TimeProvider.System);
            services.AddKeyedSingleton(DurableJobTimeProviderNames.DurableJobs, TimeProvider.System);
            services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
            var silo = Substitute.For<ISiloBuilder>();
            silo.Services.Returns(services);
            silo.AddJournalStorage();
            services.AddSingleton<IJournalStorageProvider>(snapshot ? new SnapshotStorageProvider(Storage) : Storage);
            services.AddScoped(sp => new StateTrackingManager(sp.GetRequiredService<IJournaledStateManagerFactory>().Create(JournalId), Probe));
            services.AddScoped<IJournaledStateManager>(sp => sp.GetRequiredService<StateTrackingManager>());
            var context = Context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(GrainId.Create("sender", "codec-boundary"));
            context.GrainInstance.Returns(new object());
            context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
            services.AddSingleton(context);
            TimerRegistry = Substitute.For<ITimerRegistry>();
            services.AddSingleton(TimerRegistry);
            services.AddSingleton(Substitute.For<IDurableJobHandlerRegistry>());
            var jobs = Jobs = Substitute.For<ILocalDurableJobManager>();
            jobs.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var request = call.ArgAt<ScheduleJobRequest>(0);
                return Task.FromResult(new DurableJob
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ShardId = "opaque-shard",
                    Name = request.JobName,
                    DueTime = request.DueTime,
                    TargetGrainId = request.Target,
                    Metadata = request.Metadata
                });
            });
            services.AddSingleton(jobs);
            var inbox = Substitute.For<IDurableInboxExtension>();
            inbox.DeliverAsync(Arg.Any<DurableEnvelope>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(delivery));
            var grains = Substitute.For<IGrainFactory>();
            grains.GetGrain<IDurableInboxExtension>(Arg.Any<GrainId>()).Returns(inbox);
            services.AddSingleton(grains);
            services.AddSingleton(Implementation("DurableMessagingInstruments"), Implementation("DurableMessagingInstruments")
                .GetMethod("CreateForDirectConstruction", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!);
            services.AddScoped(Implementation("DurableMessagingPumpResults"), _ => Activator.CreateInstance(Implementation("DurableMessagingPumpResults"), nonPublic: true)!);
            services.Configure<DurableInboxOptions>(options => options.MaxDeliveryAttempts = maxAttempts);
            _services = services.BuildServiceProvider();
            _scope = _services.CreateAsyncScope();
            Manager = _scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            Outbox = (IDurableOutbox)ActivatorUtilities.CreateInstance(_scope.ServiceProvider, Implementation("DurableOutbox"));
            States = _scope.ServiceProvider.GetRequiredService<StateTrackingManager>();
            if (additionalState is not null)
            {
                additionalState.Bind(Manager);
                Manager.RegisterState("codec-probe-value", additionalState);
            }
            States.RegisterStates(stateOrder);
            Messages = States.GetState<IDurableDictionary<Guid, DurableEnvelope>>("__orleans.durable-messaging.outbox");
            Job = States.GetState<IDurableValue<DurableJob>>("__orleans.durable-messaging.outbox-job-handle");
            _deliver = Outbox.GetType().GetMethod("DeliverPendingMessagesAsync")!.CreateDelegate<Func<CancellationToken, Task>>(Outbox);
        }

        public static async Task<CodecFixture> CreateAsync(ControlledJournalStorageProvider? storage = null, JournalId? journalId = null,
            DeliveryResult? delivery = null, int maxAttempts = 1, ProbeState? additionalState = null, int stateOrder = 0, bool snapshot = false)
        {
            var result = new CodecFixture(storage, journalId, delivery ?? DeliveryResult.Accepted(), maxAttempts, additionalState, stateOrder, snapshot);
            await result.Manager.InitializeAsync(TestContext.Current.CancellationToken);
            return result;
        }
        public DurableEnvelope CreateEnvelope() => new DurableEnvelopeBuilder(_scope.ServiceProvider.GetRequiredService<SerializerSessionPool>(), GrainId.Create("sender", "codec-boundary"))
            .To(GrainId.Create("receiver", "codec-boundary"), "codec").WithBody(new Payload(new byte[4096])).Build();
        public DurableEnvelope CreateNullBodyEnvelope() => new DurableEnvelopeBuilder(_scope.ServiceProvider.GetRequiredService<SerializerSessionPool>(), GrainId.Create("sender", "codec-boundary"))
            .To(GrainId.Create("receiver", "codec-boundary"), "codec").WithBody<string?>(null).Build();

        public async Task SeedOwnerlessJournalAsync(JournalId journal, DurableEnvelope envelope)
        {
            await using var manager = _services.GetRequiredService<IJournaledStateManagerFactory>().Create(journal);
            var type = typeof(IJournaledStateManager).Assembly.GetType("Orleans.Journaling.DurableDictionary`2", throwOnError: true)!
                .MakeGenericType(typeof(Guid), typeof(DurableEnvelope));
            var messages = (IDurableDictionary<Guid, DurableEnvelope>)ActivatorUtilities.CreateInstance(_scope.ServiceProvider, type,
                "__orleans.durable-messaging.outbox", manager);
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            messages.Add(envelope.MessageId, envelope);
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        }

        public async Task RunRepairTimerAsync()
        {
            var call = Assert.Single(TimerRegistry.ReceivedCalls(), value => value.GetMethodInfo().Name == "RegisterGrainTimer");
            var arguments = call.GetArguments();
            var callback = (Delegate)arguments[1]!;
            await ((Task)callback.DynamicInvoke(arguments[2], TestContext.Current.CancellationToken)!)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        public Task DeliverAsync() => _deliver(TestContext.Current.CancellationToken);
        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _services.DisposeAsync();
            await _innerServices.DisposeAsync();
        }
        private static Type Implementation(string name) => ReceiverTestServices.GetImplementationType(name);
    }

    private sealed class StateTrackingManager(IJournaledStateManager inner, CodecProbe probe) : IJournaledStateManager
    {
        private readonly Dictionary<string, IJournaledState> _states = new(StringComparer.Ordinal);
        public Exception? Failure { get; private set; }
        public int StateCount => _states.Count;
        public void RegisterState(string name, IJournaledState state) => _states.Add(name, state);
        public void RegisterStates(int rotation)
        {
            var entries = _states.ToArray();
            foreach (var entry in entries.Skip(rotation).Concat(entries.Take(rotation)))
            {
                inner.RegisterState(entry.Key, new TrackedState(this, entry.Value, probe));
            }
        }
        public T GetState<T>(string name) => (T)_states[name];
        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state) => _states.TryGetValue(name, out state);
        public TCodec GetRequiredCommandCodec<TCodec>() where TCodec : notnull => inner.GetRequiredCommandCodec<TCodec>();
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);
        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => inner.WriteStateAsync(cancellationToken);
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => inner.DeleteStateAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private sealed class TrackedState(StateTrackingManager owner, IJournaledState state, CodecProbe probe) : IJournaledState
        {
            public bool IsWritePrepared => state.IsWritePrepared;
            public async ValueTask PrepareWriteAsync(CancellationToken cancellationToken)
            {
                probe.Phase = "preparation";
                await state.PrepareWriteAsync(cancellationToken);
            }
            public void ValidateWrite() => state.ValidateWrite();
            public void ValidateDelete() => state.ValidateDelete();
            public void OnDeleteStarted() => state.OnDeleteStarted();
            public void AppendEntries(JournalStreamWriter writer) { probe.Phase = "capture"; state.AppendEntries(writer); }
            public void AppendSnapshot(JournalStreamWriter writer) { probe.Phase = "capture"; state.AppendSnapshot(writer); }
            public void OnWriteCompleted() => state.OnWriteCompleted();
            public void Reset(JournalStreamWriter writer) => state.Reset(writer);
            public void OnRecoveryCompleted() => state.OnRecoveryCompleted();
            public void ReplayEntry(JournalEntry entry, JournalReplayContext context) => state.ReplayEntry(entry, context);
            public void OnFaulted(Exception exception) { owner.Failure ??= exception; state.OnFaulted(exception); }
            public IJournaledState DeepCopy() => throw new NotSupportedException();
        }
    }

    private sealed class SnapshotStorageProvider(IJournalStorageProvider inner) : IJournalStorageProvider
    {
        public IJournalStorage CreateStorage(JournalId journalId) => new SnapshotStorage(inner.CreateStorage(journalId));
        private sealed class SnapshotStorage(IJournalStorage inner) : IJournalStorage
        {
            public bool IsCompactionRequested => true;
            public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => inner.ReadAsync(consumer, cancellationToken);
            public ValueTask<bool> CreateIfNotExistsAsync(IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) => inner.CreateIfNotExistsAsync(metadata, cancellationToken);
            public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default) => inner.GetMetadataAsync(cancellationToken);
            public ValueTask<IJournalMetadata?> UpdateMetadataAsync(IReadOnlyDictionary<string, string>? set = null, IEnumerable<string>? remove = null, string? expectedETag = null, CancellationToken cancellationToken = default) => inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);
            public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => inner.ReplaceAsync(value, cancellationToken);
            public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => inner.AppendAsync(value, cancellationToken);
            public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);
        }
    }
}
