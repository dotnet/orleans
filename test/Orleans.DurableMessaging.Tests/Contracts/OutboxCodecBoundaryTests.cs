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
    public async Task Send_ReusesBodyAndEncodesStandardDictionaryCommandsDuringStaging()
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var envelope = fixture.CreateEnvelope();
        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));

        await fixture.SendAsync(envelope);
        await fixture.SendAsync(envelope);

        Assert.Equal(1, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(1, fixture.Probe.Count("OutboxMessageState"));
        Assert.Equal(0, fixture.Probe.Count(nameof(DurableJob)));
        Assert.Equal(envelope.MessageId, Assert.Single(fixture.Messages).Key);
        Assert.Equal(1, fixture.Outbox.Count);
        await fixture.WriteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));
        Assert.Equal(new[] { "staging" }, fixture.Probe.Phases(nameof(DurableEnvelope)));
        Assert.Equal(new[] { "staging" }, fixture.Probe.Phases("OutboxMessageState"));
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
        await fixture.SendAsync(fixture.CreateEnvelope());
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();

        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(new[] { "staging" }, fixture.Probe.Phases("OutboxDeadLetter"));
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
        await fixture.SendAsync(fixture.CreateEnvelope());
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();

        Assert.Equal(new[] { "staging", "staging" }, fixture.Probe.Phases("OutboxMessageState"));
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
    [InlineData(nameof(DurableEnvelope), "staging", 0)]
    [InlineData("OutboxMessageState", "staging", 1)]
    [InlineData(nameof(DurableJob), "capture", 1)]
    public async Task CodecFailure_ReportsAtStandardEncodingBoundaryAndPreservesReplay(string failedType, string phase, int stagedMessages)
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var envelope = fixture.CreateEnvelope();
        fixture.Probe.FailureType = failedType;
        InvalidOperationException error;
        if (phase == "staging")
        {
            error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SendAsync(envelope));
            Assert.Null(fixture.States.Failure);
            var deactivation = Assert.Single(fixture.Context.ReceivedCalls(), call => call.GetMethodInfo().Name == "Deactivate");
            Assert.Same(error, Assert.IsType<DeactivationReason>(deactivation.GetArguments()[0]).Exception);
        }
        else
        {
            await fixture.SendAsync(envelope);
            Assert.Equal(0, fixture.Probe.Count(failedType));
            error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(TestContext.Current.CancellationToken).AsTask());
            Assert.Same(error, fixture.States.Failure);
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(TestContext.Current.CancellationToken).AsTask());
            Assert.Same(error, rejected.InnerException);
        }
        Assert.Same(fixture.Probe.Failure, error);
        Assert.Equal(new[] { phase }, fixture.Probe.Phases(failedType));
        Assert.Equal(stagedMessages, fixture.Messages.Count);
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => fixture.Outbox.PrepareSendAsync([envelope], TestContext.Current.CancellationToken)));
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Empty(recovered.Messages);
        Assert.Null(recovered.Job.Value);
    }

    [Fact]
    public async Task DeadLetterCodecFailure_PreservesPreviouslyCommittedMessageOnFreshReplay()
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.RouteNotFound("missing"));
        var envelope = fixture.CreateEnvelope();
        await fixture.SendAsync(envelope);
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
        fixture.Probe.FailureType = "OutboxDeadLetter";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DeliverAsync());
        Assert.Same(fixture.Probe.Failure, error);
        Assert.Null(fixture.States.Failure);
        Assert.Single(fixture.Context.ReceivedCalls(), call => call.GetMethodInfo().Name == "Deactivate");
        Assert.Empty(fixture.Messages);
        Assert.Equal(new[] { "staging" }, fixture.Probe.Phases("OutboxDeadLetter"));
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Equal(envelope.MessageId, Assert.Single(recovered.Messages).Key);
        Assert.Equal(0, recovered.DeadLetterCount);
    }

    [Fact]
    public async Task FeatureRepairSchedulingFailure_RequestsDeactivationWithoutJournalMutation()
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

        Assert.Null(fixture.States.Failure);
        Assert.Equal(writes, fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Single(fixture.Jobs.ReceivedCalls(), call => call.GetMethodInfo().Name == "ScheduleJobAsync");
        var deactivation = Assert.Single(fixture.Context.ReceivedCalls(), call => call.GetMethodInfo().Name == "Deactivate");
        Assert.Same(failure, Assert.IsType<DeactivationReason>(deactivation.GetArguments()[0]).Exception);
        Assert.Null(fixture.Job.Value);
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
        if (existingIntent) { await fixture.SendAsync(valid); }
        var invalid = field switch
        {
            "message" => valid with { MessageId = Guid.Empty },
            "receiver" => valid with { ReceiverId = default },
            "data" => valid with { Data = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var error = Assert.ThrowsAny<ArgumentException>(() => fixture.Outbox.PrepareSendAsync([invalid], TestContext.Current.CancellationToken));

        Assert.Equal(field == "receiver" ? "messages" : "envelope", error.ParamName);
        Assert.Equal(existingIntent ? 1 : 0, fixture.Outbox.Count);
        Assert.Equal(existingIntent ? 1 : 0, fixture.Messages.Count);
        Assert.Equal(existingIntent ? 1 : 0, fixture.Jobs.ReceivedCalls().Count());
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Equal(existingIntent ? 1 : 0, fixture.Probe.Count(nameof(DurableEnvelope)));
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

        await fixture.SendAsync(message);
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
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
            fixture.States.GetState<IStateMachine>("__orleans.durable-messaging.outbox"));
        var first = fixture.CreateEnvelope();
        await fixture.SendAsync(first);
        var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        var write = fixture.WriteAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        var later = fixture.CreateEnvelope();
        await fixture.SendAsync(later);
        Assert.Equal(2, fixture.Outbox.Count);
        Assert.Equal(2, fixture.Messages.Count);
        storage.Release();
        await write;

        Assert.True(fixture.Messages.ContainsKey(later.MessageId));
        await using (var captured = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId))
        {
            Assert.Equal(first.MessageId, Assert.Single(captured.Messages).Key);
            Assert.False(captured.Messages.ContainsKey(later.MessageId));
        }
        Assert.True(fixture.Outbox.TryGetMessage(later.MessageId, out _));
        var owner = Assert.IsType<DurableJob>(fixture.Job.Value);
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
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
        await recovered.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, recovered.Outbox.Count);
        Assert.Empty(recovered.Messages);
        Assert.Null(recovered.Job.Value);
        Assert.ThrowsAny<OperationCanceledException>(() => recovered.Outbox.PrepareSendAsync([first], TestContext.Current.CancellationToken));
        await using var fresh = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        await fresh.SendAsync(fresh.CreateEnvelope());
        await fresh.WriteAsync(TestContext.Current.CancellationToken);
        Assert.Single(fresh.Messages);
        Assert.NotNull(fresh.Job.Value);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(0, true)]
    [InlineData(6, true)]
    public async Task PreparedLease_StagingDuringStorageAwaitBelongsToNextCohort(int rotation, bool alreadyPending)
    {
        await using var fixture = await CodecFixture.CreateAsync(stateOrder: rotation);
        using var prepared = await fixture.Outbox.PrepareSendAsync([fixture.CreateEnvelope()], TestContext.Current.CancellationToken);
        if (alreadyPending) await fixture.SendAsync(fixture.CreateEnvelope());
        var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        var write = fixture.WriteAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        Assert.Equal(alreadyPending ? 1 : 0, fixture.Messages.Count);
        fixture.Outbox.Send(prepared);
        prepared.Dispose();
        Assert.Equal(alreadyPending ? 2 : 1, fixture.Outbox.Count);
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        storage.Release();
        await write;
        Assert.Equal(alreadyPending ? 2 : 1, fixture.Messages.Count);
        await using (var captured = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId))
        {
            Assert.Equal(alreadyPending ? 1 : 0, captured.Messages.Count);
        }
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(alreadyPending ? 2 : 1, fixture.Messages.Count);
        Assert.Equal(2, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Single(fixture.Jobs.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILocalDurableJobManager.ScheduleJobAsync));
        var job = Assert.IsType<DurableJob>(fixture.Job.Value);
        Assert.Equal("opaque-shard", job.ShardId);
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Equal(fixture.Outbox.Count, recovered.Outbox.Count);
        Assert.Equal(job.Id, recovered.Job.Value!.Id);
    }

    [Fact]
    public async Task EmptyJournal_UsesCanonicalOwnerBoundCollectionsAndOneSequenceState()
    {
        await using var fixture = await CodecFixture.CreateAsync();
        Assert.Equal(7, fixture.States.StateCount);
        foreach (var name in BootstrapOutboxServices.StateNames.Take(6))
        {
            var state = fixture.States.GetState<IStateMachine>(name);
            Assert.Same(typeof(IStateMachine).Assembly, state.GetType().Assembly);
            Assert.Same(state, fixture.ResolveStandardState(name));
        }
        var sequence = fixture.States.GetState<IStateMachine>(BootstrapOutboxServices.StateNames[6]);
        Assert.Same(typeof(IDurableOutbox).Assembly, sequence.GetType().Assembly);
        Assert.IsAssignableFrom<IDurableValue<long>>(sequence);
        Assert.False(fixture.Manager is IDurableStateManager);
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        using var batch = await fixture.Outbox.PrepareSendAsync([fixture.CreateEnvelope()], TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Outbox.Count);
        fixture.Outbox.Send(batch);
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
        Assert.Single(fixture.Messages);
        Assert.Equal(1, ((IDurableValue<long>)sequence).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualManager_ExplicitInitialRecoveryRetryPrecedesOutboxStartup(bool hasCommittedOwner)
    {
        await using var seed = await CodecFixture.CreateAsync();
        var envelope = seed.CreateEnvelope();
        var journal = hasCommittedOwner ? seed.JournalId : new JournalId($"outbox-retry/{Guid.NewGuid():N}");
        if (hasCommittedOwner)
        {
            await seed.SendAsync(envelope);
            await seed.WriteAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            await seed.SeedOwnerlessJournalAsync(journal, envelope);
        }
        var reads = seed.Storage.GetReadCount(journal);
        var writes = seed.Storage.GetSuccessfulWriteCount(journal);
        await using var fixture = await CodecFixture.CreateAsync(seed.Storage, journal, initialize: false);
        fixture.Probe.ReadFailureType = nameof(DurableEnvelope);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(fixture.Probe.Failure, failure.GetBaseException());
        Assert.Contains("Failed to recover journaling state", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Probe.ReadFailures);
        Assert.Equal(reads + 1, fixture.Storage.GetReadCount(journal));
        Assert.Empty(fixture.Jobs.ReceivedCalls());
        Assert.DoesNotContain(fixture.TimerRegistry.ReceivedCalls(), call => call.GetMethodInfo().Name == "RegisterGrainTimer");
        Assert.Equal(writes, fixture.Storage.GetSuccessfulWriteCount(journal));

        fixture.Probe.ReadFailureType = null;
        await fixture.Manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(reads + 2, fixture.Storage.GetReadCount(journal));
        Assert.Equal(envelope.MessageId, Assert.Single(fixture.Messages).Key);
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Empty(fixture.Jobs.ReceivedCalls());
        Assert.DoesNotContain(fixture.TimerRegistry.ReceivedCalls(), call => call.GetMethodInfo().Name == "RegisterGrainTimer");
        await ((ILifecycleObserver)fixture.Outbox).OnStart(TestContext.Current.CancellationToken);
        if (hasCommittedOwner)
        {
            Assert.Equal(seed.Job.Value!.Id, fixture.Job.Value!.Id);
            Assert.Equal(seed.Job.Value.ShardId, fixture.Job.Value.ShardId);
            Assert.Empty(fixture.Jobs.ReceivedCalls());
        }
        else
        {
            Assert.Null(fixture.Job.Value);
            await fixture.RunRepairTimerAsync();
            Assert.Single(fixture.Jobs.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILocalDurableJobManager.ScheduleJobAsync));
            Assert.NotNull(fixture.Job.Value);
        }
        Assert.Equal(writes + (hasCommittedOwner ? 0 : 1), fixture.Storage.GetSuccessfulWriteCount(journal));
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Null(fixture.States.Failure);
    }

    [Fact]
    public async Task CoalescedOwnerRepair_CompletesAfterPriorAckWithoutAnotherStorageWrite()
    {
        await using var seed = await CodecFixture.CreateAsync();
        var journal = new JournalId($"standard-cohort/{Guid.NewGuid():N}");
        await seed.SeedOwnerlessJournalAsync(journal, seed.CreateEnvelope());
        await using var fixture = await CodecFixture.CreateAsync(seed.Storage, journal);
        await fixture.SendAsync(fixture.CreateEnvelope());
        var writes = fixture.Storage.GetSuccessfulWriteCount(journal);
        using var storage = fixture.Storage.BlockWrite(journal);
        var first = fixture.WriteAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        var repair = fixture.RunRepairTimerAsync();
        Assert.False(repair.IsCompleted);
        Assert.Equal(writes, fixture.Storage.GetSuccessfulWriteCount(journal));
        storage.Release();
        await Task.WhenAll(first, repair).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(writes + 1, fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(2, fixture.Messages.Count);
        Assert.NotNull(fixture.Job.Value);
        Assert.Single(fixture.Jobs.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILocalDurableJobManager.ScheduleJobAsync));
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
    }

    [Fact]
    public async Task NoOpWrites_LeaveAllFacetsReadyForNextIntent()
    {
        await using var fixture = await CodecFixture.CreateAsync();
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
        var writes = fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId);
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(writes, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Empty(fixture.Jobs.ReceivedCalls());
        await fixture.SendAsync(fixture.CreateEnvelope());
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
        Assert.Single(fixture.Messages);
        Assert.NotNull(fixture.Job.Value);
        Assert.Null(fixture.States.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task CanceledCallerDuringStorageAck_PreservesOwnedCaptureAndAck(int rotation)
    {
        await using var fixture = await CodecFixture.CreateAsync(stateOrder: rotation);
        await fixture.SendAsync(fixture.CreateEnvelope());
        var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        using var cancellation = new CancellationTokenSource();
        var write = fixture.WriteAsync(cancellation.Token).AsTask();
        await storage.WaitUntilEnteredAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        storage.Release();
        await fixture.WriteAsync(TestContext.Current.CancellationToken);
        Assert.Single(fixture.Messages);
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Null(fixture.States.Failure);
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Outbox.Count);
    }

    [GenerateSerializer]
    public sealed record Payload([property: Id(0)] byte[] Bytes);

    public sealed class CodecProbe
    {
        private readonly ConcurrentQueue<(string Type, string Phase)> _calls = new();
        public required SerializerSessionPool InnerSessions { get; init; }
        public string Phase { get; set; } = "before-admission";
        public string? FailureType { get; set; }
        public string? ReadFailureType { get; set; }
        public int ReadFailures { get; private set; }
        public void Reading(Type type)
        {
            if (type.Name == ReadFailureType)
            {
                ReadFailures++;
                throw Failure;
            }
        }
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
        public T ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            probe.Reading(typeof(T));
            return _inner.ReadValue(ref reader, field);
        }
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
                var entries = States.GetState<IStateMachine>("__orleans.durable-messaging.outbox-dead-letters");
                return (int)entries.GetType().GetProperty("Count")!.GetValue(entries)!;
            }
        }

        private CodecFixture(ControlledJournalStorageProvider? storage, JournalId? journalId, DeliveryResult delivery, int maxAttempts, int stateOrder, bool snapshot)
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
            silo.AddJournaling();
            services.AddSingleton<IJournalStorageProvider>(snapshot ? new SnapshotStorageProvider(Storage) : Storage);
            services.AddScoped(sp => new StateTrackingManager(sp.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(JournalId), Probe));
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
            var dependencies = _scope.ServiceProvider;
            Outbox = (IDurableOutbox)ActivatorUtilities.CreateInstance(dependencies, Implementation("DurableOutbox"),
                dependencies.GetRequiredKeyedService<IDurableValueCommandCodec<long>>("orleans-binary"));
            States = _scope.ServiceProvider.GetRequiredService<StateTrackingManager>();
            States.RegisterStates(stateOrder);
            Messages = States.GetState<IDurableDictionary<Guid, DurableEnvelope>>("__orleans.durable-messaging.outbox");
            Job = States.GetState<IDurableValue<DurableJob>>("__orleans.durable-messaging.outbox-job-handle");
            _deliver = Outbox.GetType().GetMethod("DeliverPendingMessagesAsync")!.CreateDelegate<Func<CancellationToken, Task>>(Outbox);
        }

        public static async Task<CodecFixture> CreateAsync(ControlledJournalStorageProvider? storage = null, JournalId? journalId = null,
            DeliveryResult? delivery = null, int maxAttempts = 1, int stateOrder = 0, bool snapshot = false, bool initialize = true)
        {
            var result = new CodecFixture(storage, journalId, delivery ?? DeliveryResult.Accepted(), maxAttempts, stateOrder, snapshot);
            if (initialize)
            {
                await result.Manager.InitializeAsync(TestContext.Current.CancellationToken);
                await ((ILifecycleObserver)result.Outbox).OnStart(TestContext.Current.CancellationToken);
            }
            return result;
        }
        public DurableEnvelope CreateEnvelope() => new DurableEnvelopeBuilder(_scope.ServiceProvider.GetRequiredService<SerializerSessionPool>(), GrainId.Create("sender", "codec-boundary"))
            .To(GrainId.Create("receiver", "codec-boundary"), "codec").WithBody(new Payload(new byte[4096])).Build();
        public DurableEnvelope CreateNullBodyEnvelope() => new DurableEnvelopeBuilder(_scope.ServiceProvider.GetRequiredService<SerializerSessionPool>(), GrainId.Create("sender", "codec-boundary"))
            .To(GrainId.Create("receiver", "codec-boundary"), "codec").WithBody<string?>(null).Build();

        public object ResolveStandardState(string name)
        {
            Type contract = name switch
            {
                "__orleans.durable-messaging.outbox" => typeof(IDurableDictionary<Guid, DurableEnvelope>),
                "__orleans.durable-messaging.outbox-message-state" => typeof(IDurableDictionary<,>).MakeGenericType(typeof(Guid), Implementation("OutboxMessageState")),
                "__orleans.durable-messaging.outbox-dead-letters" => typeof(IDurableDictionary<,>).MakeGenericType(typeof(Guid), Implementation("OutboxDeadLetter")),
                "__orleans.durable-messaging.outbox-job-id" or "__orleans.durable-messaging.outbox-completed-job-id" => typeof(IDurableValue<string>),
                "__orleans.durable-messaging.outbox-job-handle" => typeof(IDurableValue<DurableJob>),
                _ => throw new ArgumentOutOfRangeException(nameof(name))
            };
            return _scope.ServiceProvider.GetRequiredKeyedService(contract, name);
        }

        public async Task SeedOwnerlessJournalAsync(JournalId journal, DurableEnvelope envelope)
        {
            await using var manager = _services.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(journal);
            var services = new ServiceCollection();
            services.AddSerializer();
            services.AddLogging();
            services.AddKeyedSingleton(JournalingTimeProviderNames.Journaling, TimeProvider.System);
            services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
            var silo = Substitute.For<ISiloBuilder>();
            silo.Services.Returns(services);
            silo.AddJournaling();
            services.AddSingleton<IJournaledStateManager>(manager);
            await using var dependencies = services.BuildServiceProvider();
            var messages = dependencies.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEnvelope>>("__orleans.durable-messaging.outbox");
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

        public async Task SendAsync(DurableEnvelope envelope)
        {
            using var batch = await Outbox.PrepareSendAsync([envelope], TestContext.Current.CancellationToken);
            Probe.Phase = "staging";
            Outbox.Send(batch);
        }

        public async ValueTask WriteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = WriteOwnedAsync();
            await operation.WaitAsync(cancellationToken);

            async Task WriteOwnedAsync()
            {
                try
                {
                    await Manager.WriteStateAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    Outbox.GetType().GetMethod("FailPersistence", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .CreateDelegate<Action<Exception>>(Outbox)(exception);
                    throw;
                }
            }
        }

        public async ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteOwnedAsync().WaitAsync(cancellationToken);

            async Task DeleteOwnedAsync()
            {
                try
                {
                    await ((ILifecycleObserver)Outbox).OnStop(CancellationToken.None);
                    await Manager.DeleteStateAsync(CancellationToken.None);
                }
                finally
                {
                    await DisposeAsync();
                }
            }
        }

        public Task DeliverAsync()
        {
            Probe.Phase = "staging";
            return _deliver(TestContext.Current.CancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            await ((ILifecycleObserver)Outbox).OnStop(CancellationToken.None);
            await _scope.DisposeAsync();
            await _services.DisposeAsync();
            await _innerServices.DisposeAsync();
        }
        private static Type Implementation(string name) => ReceiverTestServices.GetImplementationType(name);
    }

    private sealed class StateTrackingManager(IJournaledStateManager inner, CodecProbe probe) : IJournaledStateManager
    {
        private readonly Dictionary<string, IStateMachine> _states = new(StringComparer.Ordinal);
        public Exception? Failure { get; private set; }
        public int StateCount => _states.Count;
        public void RegisterStateMachine(string name, IStateMachine state) => _states.Add(name, state);
        public void RegisterStates(int rotation)
        {
            var entries = _states.ToArray();
            foreach (var entry in entries.Skip(rotation).Concat(entries.Take(rotation)))
            {
                inner.RegisterStateMachine(entry.Key, new TrackedState(entry.Value, probe));
            }
        }
        public T GetState<T>(string name) => (T)_states[name];
        public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IStateMachine? state) => _states.TryGetValue(name, out state);
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);
        public async ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            try
            {
                await inner.WriteStateAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                Failure ??= exception;
                throw;
            }
        }
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => inner.DeleteStateAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private sealed class TrackedState(IStateMachine state, CodecProbe probe) : IStateMachine
        {
            public void WritePendingEntries(JournalStreamWriter writer) { probe.Phase = "capture"; state.WritePendingEntries(writer); }
            public void WriteSnapshot(JournalStreamWriter writer) { probe.Phase = "capture"; state.WriteSnapshot(writer); }
            public void OnWriteCompleted() => state.OnWriteCompleted();
            public void Reset(JournalStreamWriter writer) => state.Reset(writer);
            public void OnRecoveryCompleted() => state.OnRecoveryCompleted();
            public void ReplayEntry(JournalEntry entry, JournalReplayContext context) => state.ReplayEntry(entry, context);
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
