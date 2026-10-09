using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
        using var envelope = fixture.CreateEnvelope();
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
        Assert.Equal(4096, Assert.IsType<Payload>(recovered.ReadApplication(restored).Body).Bytes.Length);
    }

    [Fact]
    public async Task DeadLetter_EncodesAtJournalApplicationWithoutPayloadPreflight()
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.HandlerNotFound());
        using (var newEnvelope = fixture.CreateEnvelope()) await fixture.SendAsync(newEnvelope);
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
        using (var newEnvelope = fixture.CreateEnvelope()) await fixture.SendAsync(newEnvelope);
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
    public async Task BodyCodecFailure_IsReportedByApplicationBeforeIntentAdmission()
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
        using var envelope = fixture.CreateEnvelope();
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
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => fixture.Outbox.Send(envelope)));
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Empty(recovered.Messages);
        Assert.Null(recovered.Job.Value);
    }

    [Fact]
    public async Task DeadLetterCodecFailure_PreservesPreviouslyCommittedMessageOnFreshReplay()
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.HandlerNotFound());
        using var envelope = fixture.CreateEnvelope();
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
        using (var seedEnvelope = seed.CreateEnvelope()) await seed.SeedOwnerlessJournalAsync(journal, seedEnvelope);
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
    [InlineData("sender", false)]
    [InlineData("receiver", false)]
    [InlineData("message", true)]
    [InlineData("sender", true)]
    [InlineData("receiver", true)]
    public async Task DirectSendMalformedStructure_FailsBeforeIntentOrJournalMutation(string field, bool existingIntent)
    {
        await using var fixture = await CodecFixture.CreateAsync();
        using var valid = fixture.CreateEnvelope();
        if (existingIntent) { await fixture.SendAsync(valid); }
        var invalid = field switch
        {
            "message" => valid with { MessageId = default },
            "sender" => valid with { SenderId = default },
            "receiver" => valid with { ReceiverId = default },
            "payload" => valid with { Payload = default },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        if (field == "sender")
        {
            var ownership = Assert.Throws<InvalidOperationException>(() => fixture.Outbox.Send(invalid));
            Assert.Contains("does not match the owning grain", ownership.Message, StringComparison.Ordinal);
        }
        else
        {
            var error = Assert.ThrowsAny<ArgumentException>(() => fixture.Outbox.Send(invalid));
            Assert.Equal("envelope", error.ParamName);
        }
        Assert.Equal(existingIntent ? 1 : 0, fixture.Outbox.Count);
        Assert.Equal(existingIntent ? 1 : 0, fixture.Messages.Count);
        Assert.Empty(fixture.Jobs.ReceivedCalls());
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Equal(existingIntent ? 1 : 0, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Null(fixture.States.Failure);
        if (existingIntent) { Assert.Equal(valid, Assert.Single(fixture.Outbox.Messages)); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DirectSendApplicationRoute_PreservesExplicitIdentityNullBodyAndMissingHandlerOutcome(string? route)
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.HandlerNotFound());
        using var template = fixture.CreateNullBodyEnvelope();
        using var message = template with
        {
            MessageId = HierarchicalKey.Create("test", "codec-boundary", "nullable-body", "0"),
            Payload = fixture.EncodeApplication(route!, null)
        };
        Assert.Null(fixture.ReadApplication(message).Body);

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
        Assert.IsAssignableFrom<IDurableDictionary<HierarchicalKey, DurableEnvelope>>(
            fixture.States.GetState<IStateMachine>("__orleans.durable-messaging.outbox"));
        using var first = fixture.CreateEnvelope();
        await fixture.SendAsync(first);
        var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        var write = fixture.WriteAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        using var later = fixture.CreateEnvelope();
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
        Assert.ThrowsAny<OperationCanceledException>(() => recovered.Outbox.Send(first));
        await using var fresh = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        using (var newEnvelope = fresh.CreateEnvelope()) await fresh.SendAsync(newEnvelope);
        await fresh.WriteAsync(TestContext.Current.CancellationToken);
        Assert.Single(fresh.Messages);
        Assert.NotNull(fresh.Job.Value);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(0, true)]
    [InlineData(6, true)]
    public async Task OpaqueSend_StagingDuringStorageAwaitBelongsToNextCohort(int rotation, bool alreadyPending)
    {
        await using var fixture = await CodecFixture.CreateAsync(stateOrder: rotation);
        using var prepared = fixture.CreateEnvelope();
        if (alreadyPending)
        {
            using var existingEnvelope = fixture.CreateEnvelope();
            await fixture.SendAsync(existingEnvelope);
        }
        var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        var write = fixture.WriteAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        Assert.Equal(alreadyPending ? 1 : 0, fixture.Messages.Count);
        fixture.Outbox.Send(prepared);
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
        using var batch = fixture.CreateEnvelope();
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
        using var envelope = seed.CreateEnvelope();
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
        var recoveredCommand = HierarchicalKey.Create("test", "owner-repair", "command", "recovered");
        var arrivingCommand = HierarchicalKey.Create("test", "owner-repair", "command", "arriving");
        using (var seedEnvelope = seed.CreateEnvelope(recoveredCommand)) await seed.SeedOwnerlessJournalAsync(journal, seedEnvelope);
        await using var fixture = await CodecFixture.CreateAsync(seed.Storage, journal);
        using (var newEnvelope = fixture.CreateEnvelope(arrivingCommand)) await fixture.SendAsync(newEnvelope);
        Assert.NotEqual(recoveredCommand, arrivingCommand);
        Assert.Equal(2, fixture.Outbox.Count);
        Assert.True(fixture.Outbox.TryGetMessage(recoveredCommand, out _));
        Assert.True(fixture.Outbox.TryGetMessage(arrivingCommand, out _));
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
        using (var newEnvelope = fixture.CreateEnvelope()) await fixture.SendAsync(newEnvelope);
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
        using (var newEnvelope = fixture.CreateEnvelope()) await fixture.SendAsync(newEnvelope);
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

    [Fact]
    public Task ApplicationIdentity_EquivalentReconstructionPreservesOriginalRetainedIntent() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var command = HierarchicalKey.Create("tenant/acme", "orders", "1042", "reserve-stock");
        var reconstructed = HierarchicalKey.Parse(command.ToString(), provider: null);
        Assert.Equal(command, reconstructed);
        using var envelope = fixture.CreateEnvelope(command, "inventory.reserve.v1");
        using var repeat = fixture.CreateEnvelope(reconstructed, "inventory.reserve.v1");
        fixture.Outbox.Send(envelope);
        fixture.Outbox.Send(repeat);
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.True(fixture.Outbox.TryGetMessage(reconstructed, out var stored));
        Assert.Same(envelope.Payload.First, stored.Payload.First);
        Assert.NotSame(repeat.Payload.First, stored.Payload.First);
        Assert.Equal("inventory.reserve.v1", stored.Subject);
        Assert.Equal(envelope.Payload.ToArray(), stored.Payload.ToArray());
        using var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        var writing = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        fixture.Outbox.Send(repeat);
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Receiver.ReceivedCalls());
        storage.Release();
        await writing;
        await using (var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId))
        {
            Assert.True(recovered.Outbox.TryGetMessage(reconstructed, out var restored));
            Assert.Equal(command, restored.MessageId);
            Assert.Equal("inventory.reserve.v1", restored.Subject);
            Assert.Equal(envelope.Payload.ToArray(), restored.Payload.ToArray());
            Assert.Equal(fixture.Job.Value!.Id, recovered.Job.Value!.Id);
            Assert.Equal(fixture.Job.Value.ShardId, recovered.Job.Value.ShardId);
        }
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
        var delivery = Assert.Single(fixture.Receiver.ReceivedCalls());
        Assert.Equal(command, ((DurableEnvelope)delivery.GetArguments()[0]!).MessageId);
        Assert.Single(fixture.Jobs.ReceivedCalls());
        Assert.Equal(envelope.Payload.ToArray(), repeat.Payload.ToArray());
    });

    [Theory]
    [InlineData("destination", false)]
    [InlineData("subject", false)]
    [InlineData("body", false)]
    [InlineData("destination", true)]
    [InlineData("subject", true)]
    [InlineData("body", true)]
    public Task ApplicationIdentity_ConflictingIntentPreservesOwnerPayloadAndAttempts(string conflict, bool committed) => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var command = HierarchicalKey.Create("tenant", "orders", "1042", "reserve-stock");
        using var original = fixture.CreateEnvelope(command, "inventory.reserve.v1");
        var page = original.Payload.First;
        var references = typeof(ArcBufferPage).GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int Pins() => (int)references.GetValue(page)!;
        Assert.Equal(1, Pins());
        fixture.Outbox.Send(original);
        if (committed) await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var owner = fixture.Job.Value;
        var writes = fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId);
        var commandEncodes = fixture.Probe.Count(nameof(DurableEnvelope));
        Assert.Equal(2, Pins());
        using (var conflicting = conflict == "body"
            ? original with { Payload = fixture.EncodeApplication("changed-body", 99) }
            : original.Retain() with
            {
                ReceiverId = conflict == "destination" ? GrainId.Create("receiver", "other") : original.ReceiverId,
                Subject = conflict == "subject" ? "inventory.reserve.V1" : original.Subject
            })
        {
            var error = Assert.Throws<InvalidOperationException>(() => fixture.Outbox.Send(conflicting));
            Assert.Contains(command.ToString(), error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(2, Pins());
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Equal(commandEncodes, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(writes, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Same(owner, fixture.Job.Value);
        Assert.True(fixture.Outbox.TryGetMessage(HierarchicalKey.Parse(command.ToString(), provider: null), out var stored));
        Assert.Same(page, stored.Payload.First);
        Assert.Equal(original.ReceiverId, stored.ReceiverId);
        Assert.Equal(original.Subject, stored.Subject);
        Assert.Equal(original.Payload.ToArray(), stored.Payload.ToArray());
        Assert.Null(fixture.States.Failure);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
        Assert.Equal(1, Pins());
        Assert.Single(fixture.Jobs.ReceivedCalls());
        Assert.Single(fixture.Receiver.ReceivedCalls());
    });

    [Fact]
    public Task ApplicationIdentity_ParentAndRecipientChildrenRemainIndependentAcrossReplay() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var command = HierarchicalKey.Create("tenant", "campaign", "1042", "notify");
        var firstId = command.CreateChildKey("recipient/a");
        var secondId = command.CreateChildKey("recipient/b");
        Assert.NotEqual(command, firstId);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, HierarchicalKey.Parse(command.ToString(), provider: null).CreateChildKey("recipient/a"));
        using var parent = fixture.CreateEnvelope(command, "campaign.notify.v1");
        using var first = fixture.CreateEnvelope(firstId, "campaign.notify.v1");
        using var second = fixture.CreateEnvelope(secondId, "campaign.notify.v1") with { ReceiverId = GrainId.Create("receiver", "other") };
        using var retry = fixture.CreateEnvelope(HierarchicalKey.Parse(firstId.ToString(), provider: null), "campaign.notify.v1");
        fixture.Outbox.Send(parent);
        fixture.Outbox.Send(first);
        fixture.Outbox.Send(second);
        fixture.Outbox.Send(retry);
        Assert.Equal(3, fixture.Outbox.Count);
        Assert.Equal(3, fixture.Probe.Count(nameof(DurableEnvelope)));
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await using (var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId))
        {
            Assert.Equal(3, recovered.Outbox.Count);
            foreach (var id in new[] { command, firstId, secondId }) Assert.True(recovered.Outbox.TryGetMessage(id, out _));
            Assert.Equal(fixture.Job.Value!.Id, recovered.Job.Value!.Id);
            Assert.Equal(fixture.Job.Value.ShardId, recovered.Job.Value.ShardId);
        }
        using var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        var delivering = fixture.DeliverAsync();
        await storage.WaitUntilEnteredAsync();
        Assert.Empty(fixture.Messages);
        Assert.Equal(3, fixture.Receiver.ReceivedCalls().Count());
        storage.Release();
        await delivering;
        await using var drained = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Empty(drained.Messages);
        Assert.Single(fixture.Jobs.ReceivedCalls());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ApplicationIdentity_AttemptsDeadLettersAndRemovalRetainExactKeyAcrossReplay(bool deadLetter) => OnOwnerAsync(async () =>
    {
        var outcome = deadLetter ? DeliveryResult.HandlerNotFound() : DeliveryResult.Backpressured();
        await using var fixture = await CodecFixture.CreateAsync(delivery: outcome, maxAttempts: deadLetter ? 1 : 3);
        var command = HierarchicalKey.Create("tenant", "orders", "1042", "reserve-stock");
        using var message = fixture.CreateEnvelope(command, "inventory.reserve.v1");
        fixture.Outbox.Send(message);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();
        var recoveredKey = HierarchicalKey.Parse(command.ToString(), provider: null);
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId, delivery: outcome, maxAttempts: deadLetter ? 1 : 3);
        Assert.Empty(recovered.Jobs.ReceivedCalls());
        if (deadLetter)
        {
            Assert.Equal(command, Assert.Single(recovered.DeadLetterKeys));
            Assert.Empty(recovered.AttemptKeys);
            Assert.False(recovered.Outbox.TryGetMessage(recoveredKey, out _));
            var dead = recovered.ReadDeadLetterEnvelope(recoveredKey);
            Assert.Equal(command, dead.MessageId);
            Assert.Equal(message.Subject, dead.Subject);
            Assert.Equal(message.Payload.ToArray(), dead.Payload.ToArray());
            Assert.True(recovered.RemoveDeadLetter(recoveredKey));
            await recovered.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
            await using var removed = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
            Assert.Empty(removed.DeadLetterKeys);
            Assert.Empty(removed.Messages);
        }
        else
        {
            Assert.Equal(command, Assert.Single(recovered.AttemptKeys));
            Assert.Empty(recovered.DeadLetterKeys);
            Assert.True(recovered.Outbox.TryGetMessage(recoveredKey, out var pending));
            Assert.Equal(command, pending.MessageId);
            Assert.Equal(message.Subject, pending.Subject);
            Assert.Equal(message.Payload.ToArray(), pending.Payload.ToArray());
            Assert.Equal(fixture.Job.Value!.Id, recovered.Job.Value!.Id);
            Assert.Equal(fixture.Job.Value.ShardId, recovered.Job.Value.ShardId);
        }
        var delivered = Assert.Single(fixture.Receiver.ReceivedCalls());
        Assert.Equal(command, ((DurableEnvelope)delivered.GetArguments()[0]!).MessageId);
        Assert.Null(fixture.States.Failure);
    });

    [Fact]
    public Task ApplicationIdentity_RetryAfterOwnerRetirementPreservesTheOriginalCommandKey() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var command = HierarchicalKey.Create("tenant", "orders", "1042", "reserve-stock");
        using var first = fixture.CreateEnvelope(command, "inventory.reserve.v1");
        fixture.Outbox.Send(first);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();
        var retiredJob = fixture.Job.Value!;
        await fixture.PumpAsync(retiredJob);
        Assert.Null(fixture.Job.Value);
        using var retry = fixture.CreateEnvelope(HierarchicalKey.Parse(command.ToString(), provider: null), "inventory.reserve.v1");
        fixture.Outbox.Send(retry);
        Assert.Equal(command, Assert.Single(fixture.Messages).Key);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var replacement = fixture.Job.Value!;
        Assert.NotEqual(retiredJob.Id, replacement.Id);
        Assert.Equal(2, fixture.Jobs.ReceivedCalls().Count());
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.True(recovered.Outbox.TryGetMessage(command, out var stored));
        Assert.Equal(retry.Subject, stored.Subject);
        Assert.Equal(retry.Payload.ToArray(), stored.Payload.ToArray());
        Assert.Equal(replacement.Id, recovered.Job.Value!.Id);
        Assert.Equal(replacement.ShardId, recovered.Job.Value.ShardId);
        await recovered.DeliverAsync();
        Assert.Empty(recovered.Messages);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SynchronousSend_RespectsOpaqueIdentityAndOriginalEnvelope(bool duplicateFirst) => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        using var envelope = fixture.CreateEnvelope();
        if (!duplicateFirst) fixture.Outbox.Send(envelope);
        fixture.Outbox.Send(envelope);
        using var conflict = envelope with { Payload = fixture.EncodeApplication("conflicting-route", null) };
        Assert.Throws<InvalidOperationException>(() => fixture.Outbox.Send(conflict));
        fixture.Outbox.Send(envelope);
        fixture.Outbox.Send(envelope);
        Assert.Equal(envelope, Assert.Single(fixture.Messages).Value);
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableEnvelope)));
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
        // There is no prepared-handle reservation after ACK. Sending again is a new intent;
        // receiver-side durable deduplication owns suppression of repeated business effects.
        fixture.Outbox.Send(envelope);
        Assert.Equal(envelope.MessageId, Assert.Single(fixture.Messages).Key);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
        Assert.Equal(4, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Single(fixture.Jobs.ReceivedCalls());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SynchronousSend_SchedulesBeforeCaptureAndIncludesArrivalsDuringScheduling(bool snapshot) => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync(snapshot: snapshot, businessState: true);
        using var scheduling = fixture.BlockSchedule();
        using var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        using var first = fixture.CreateEnvelope();
        fixture.Business!.Value = 42;
        fixture.Outbox.Send(first);
        var write = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await scheduling.WaitAsync();
        Assert.Equal(0, fixture.Probe.Captures);
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.True((await fixture.CallbackAsync(scheduling.Job!)).IsInProgress);
        using var second = fixture.CreateEnvelope();
        fixture.Outbox.Send(second);
        fixture.Outbox.Send(first);
        Assert.Equal(2, fixture.Outbox.Count);
        scheduling.Release();
        await storage.WaitUntilEnteredAsync();
        Assert.Same(scheduling.Job, fixture.Job.Value);
        Assert.True((await fixture.CallbackAsync(scheduling.Job!)).IsInProgress);
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Receiver.ReceivedCalls());
        storage.Release();
        await write;
        await using (var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId, businessState: true))
        {
            Assert.Equal(42, recovered.Business!.Value);
            Assert.Equal(new[] { first.MessageId, second.MessageId }.OrderBy(static key => key.ToString(), StringComparer.Ordinal), recovered.Messages.Keys.OrderBy(static key => key.ToString(), StringComparer.Ordinal));
            Assert.Equal(scheduling.Job!.Id, recovered.Job.Value!.Id);
            Assert.Equal(scheduling.Job.ShardId, recovered.Job.Value.ShardId);
        }
        await fixture.DeliverAsync();
        Assert.Equal(2, fixture.Receiver.ReceivedCalls().Count());
        Assert.Empty(fixture.Messages);
        Assert.Single(fixture.Jobs.ReceivedCalls());
    });

    [Fact]
    public Task SynchronousSend_ArrivalDuringLaterHookHasWakeupBeforeCapture() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperationAsync = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            }
        });
        var write = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            using (var newEnvelope = fixture.CreateEnvelope()) fixture.Outbox.Send(newEnvelope);
        }
        finally { release.TrySetResult(); }
        await write;
        Assert.Single(fixture.Jobs.ReceivedCalls());
        Assert.NotNull(fixture.Job.Value);
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Single(recovered.Messages);
        Assert.Equal(fixture.Job.Value.Id, recovered.Job.Value!.Id);
    });

    [Fact]
    public Task SynchronousSend_EmptyFinalHookAndCaptureShareOneTurn() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        using var envelope = fixture.CreateEnvelope();
        Task? staged = null;
        var hook = Assert.IsAssignableFrom<IJournaledStateCaptureHook>(Assert.Single(fixture.Manager.Hooks));
        fixture.Manager.Hooks[0] = new CaptureBoundaryHook(hook, () =>
        {
            staged ??= Task.Factory.StartNew(() => fixture.Outbox.Send(envelope), TestContext.Current.CancellationToken,
                TaskCreationOptions.None, TaskScheduler.Current);
        });
        fixture.Manager.Hooks.Add(new JournaledStateHook { BeforeOperationAsync = async (_, _) => await Task.Yield() });
        using var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        var write = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        await Assert.IsAssignableFrom<Task>(staged);
        Assert.Equal(envelope.MessageId, Assert.Single(fixture.Messages).Key);
        Assert.Empty(fixture.Jobs.ReceivedCalls());
        storage.Release();
        await write;
        await using (var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId))
        {
            Assert.Empty(recovered.Messages);
            Assert.Null(recovered.Job.Value);
        }
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Receiver.ReceivedCalls());
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(fixture.Jobs.ReceivedCalls());
        await fixture.DeliverAsync();
        Assert.Equal(envelope.MessageId, ((DurableEnvelope)Assert.Single(fixture.Receiver.ReceivedCalls()).GetArguments()[0]!).MessageId);
        Assert.Empty(fixture.Messages);
    }, preventInlining: true);

    [Fact]
    public Task SynchronousSend_StorageAwaitArrivalRemainsPendingUntilItsOwnAck() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        using var first = fixture.CreateEnvelope();
        fixture.Outbox.Send(first);
        using var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        var write = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.WaitUntilEnteredAsync();
        using var later = fixture.CreateEnvelope();
        fixture.Outbox.Send(later);
        storage.Release();
        await write;
        using var removal = fixture.Storage.BlockWrite(fixture.JournalId);
        var deliver = fixture.DeliverAsync();
        await removal.WaitUntilEnteredAsync();
        var delivery = Assert.Single(fixture.Receiver.ReceivedCalls());
        Assert.Equal(first.MessageId, ((DurableEnvelope)delivery.GetArguments()[0]!).MessageId);
        Assert.Equal(later.MessageId, Assert.Single(fixture.Messages).Key);
        await using (var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId))
        {
            Assert.Equal(first.MessageId, Assert.Single(recovered.Messages).Key);
        }
        removal.Release();
        await deliver;
        await fixture.DeliverAsync();
        Assert.Equal(2, fixture.Receiver.ReceivedCalls().Count());
        Assert.Empty(fixture.Messages);
        Assert.Single(fixture.Jobs.ReceivedCalls());
    });

    [Fact]
    public Task SynchronousSend_SchedulingFailureRetainsBusinessAndIntentForExplicitRetry() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync(businessState: true);
        var failure = new IOException("Scheduling failed before journal capture.");
        using var scheduling = fixture.BlockSchedule();
        fixture.Business!.Value = 42;
        using var envelope = fixture.CreateEnvelope();
        fixture.Outbox.Send(envelope);
        var write = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await scheduling.WaitAsync();
        scheduling.Fail(failure);
        var error = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => write);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(0, fixture.Probe.Captures);
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Equal(42, fixture.Business.Value);
        Assert.Equal(envelope.MessageId, Assert.Single(fixture.Messages).Key);
        Assert.Null(fixture.Job.Value);
        await using (var uncommitted = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId, businessState: true))
        {
            Assert.Equal(0, uncommitted.Business!.Value);
            Assert.Empty(uncommitted.Messages);
        }
        using var retry = fixture.BlockSchedule();
        var retried = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await retry.WaitAsync();
        Assert.Equal(0, fixture.Probe.Captures);
        retry.Release();
        await retried;
        Assert.Equal(2, fixture.Jobs.ReceivedCalls().Count());
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId, businessState: true);
        Assert.Equal(42, recovered.Business!.Value);
        Assert.Equal(envelope.MessageId, Assert.Single(recovered.Messages).Key);
        Assert.Equal(retry.Job!.Id, recovered.Job.Value!.Id);
    });

    [Fact]
    public Task SynchronousSend_CanceledCallerLeavesSchedulingAndAckOwnedByJournal() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        using var scheduling = fixture.BlockSchedule();
        using var cancellation = new CancellationTokenSource();
        using (var newEnvelope = fixture.CreateEnvelope()) fixture.Outbox.Send(newEnvelope);
        var write = fixture.Manager.WriteStateAsync(cancellation.Token).AsTask();
        await scheduling.WaitAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.False(scheduling.Token.IsCancellationRequested);
        Assert.Equal(0, fixture.Probe.Captures);
        scheduling.Release();
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(fixture.Jobs.ReceivedCalls());
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
    });

    [Fact]
    public Task SynchronousSend_OverlappingWritesShareOnePhysicalWakeup() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        using var scheduling = fixture.BlockSchedule();
        using var envelope = fixture.CreateEnvelope();
        fixture.Outbox.Send(envelope);
        var prepared = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await scheduling.WaitAsync();
        fixture.Outbox.Send(envelope);
        var write = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        scheduling.Release();
        await prepared;
        await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(fixture.Jobs.ReceivedCalls());
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Equal(envelope.MessageId, Assert.Single(fixture.Messages).Key);
        Assert.Same(scheduling.Job, fixture.Job.Value);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SynchronousSend_OwnerRetirementArrivalRetainsPreCaptureOwnerOrPreparesPostCaptureOwner(bool beforeCapture) => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        using (var newEnvelope = fixture.CreateEnvelope()) fixture.Outbox.Send(newEnvelope);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();
        var old = fixture.Job.Value!;
        using var next = fixture.CreateEnvelope();
        if (beforeCapture)
        {
            fixture.Manager.Hooks.Insert(0, new JournaledStateHook { BeforeOperation = (_, _) => fixture.Outbox.Send(next) });
        }
        using var storage = fixture.Storage.BlockWrite(fixture.JournalId);
        var retirement = fixture.PumpAsync(old).AsTask();
        await storage.WaitUntilEnteredAsync();
        if (!beforeCapture) fixture.Outbox.Send(next);
        Assert.Empty(fixture.Receiver.ReceivedCalls().Skip(1));
        storage.Release();
        await retirement.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (beforeCapture) fixture.Manager.Hooks.RemoveAt(0);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(next.MessageId, Assert.Single(fixture.Messages).Key);
        var replacement = fixture.Job.Value!;
        if (beforeCapture)
        {
            Assert.Same(old, replacement);
            Assert.Single(fixture.Jobs.ReceivedCalls());
        }
        else
        {
            Assert.NotEqual(old.Id, replacement.Id);
            Assert.Equal(2, fixture.Jobs.ReceivedCalls().Count());
        }
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Equal(next.MessageId, Assert.Single(recovered.Messages).Key);
        Assert.Equal(replacement.Id, recovered.Job.Value!.Id);
        Assert.Equal(replacement.ShardId, recovered.Job.Value.ShardId);
        await recovered.DeliverAsync();
        Assert.Empty(recovered.Messages);
    });

    [Fact]
    public Task SynchronousSend_PostCommitFailurePreservesAcknowledgedOwnedDelivery() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        using (var newEnvelope = fixture.CreateEnvelope()) fixture.Outbox.Send(newEnvelope);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var failure = new IOException("After hook failed after acknowledged delivery removal.");
        var hook = new JournaledStateHook { AfterOperation = (_, _) => throw failure };
        fixture.Manager.Hooks.Add(hook);
        var error = await Assert.ThrowsAsync<JournaledStatePostCommitException>(() => fixture.DeliverAsync());
        Assert.Same(failure, error.InnerException);
        Assert.Empty(fixture.Messages);
        Assert.Equal(2, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        fixture.Manager.Hooks.Remove(hook);
        using (var newEnvelope = fixture.CreateEnvelope()) fixture.Outbox.Send(newEnvelope);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
        Assert.Equal(2, fixture.Receiver.ReceivedCalls().Count());
    });

    [Fact]
    public Task SynchronousSend_OwnedPreCommitFailureRetiresStagedOperationForFreshReplay() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        using var envelope = fixture.CreateEnvelope();
        fixture.Outbox.Send(envelope);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var failure = new IOException("Blocked prerequisite on an owned delivery write.");
        fixture.Manager.Hooks.Add(new JournaledStateHook { BeforeOperation = (_, _) => throw failure });
        var error = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => fixture.DeliverAsync());
        Assert.Same(failure, error.InnerException);
        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Same(error, Assert.Throws<JournaledStatePreCommitException>(() => { using var newEnvelope = fixture.CreateEnvelope(); fixture.Outbox.Send(newEnvelope); }));
        var deactivate = Assert.Single(fixture.Context.ReceivedCalls(), call => call.GetMethodInfo().Name == "Deactivate");
        Assert.Same(error, Assert.IsType<DeactivationReason>(deactivate.GetArguments()[0]).Exception);
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Equal(envelope.MessageId, Assert.Single(recovered.Messages).Key);
        await recovered.DeliverAsync();
        Assert.Empty(recovered.Messages);
    });

    [Fact]
    public Task SynchronousSend_StopBeforeQueuedHookPreventsNewScheduling() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Manager.Hooks.Insert(0, new JournaledStateHook
        {
            BeforeOperationAsync = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            }
        });
        using (var newEnvelope = fixture.CreateEnvelope()) fixture.Outbox.Send(newEnvelope);
        var write = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await ((ILifecycleObserver)fixture.Outbox).OnStop(CancellationToken.None);
        }
        finally { release.TrySetResult(); }
        var error = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => write);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Empty(fixture.Jobs.ReceivedCalls());
        Assert.Equal(0, fixture.Probe.Captures);
        await fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Messages);
    });

    [Fact]
    public Task SynchronousSend_StopDrainsSchedulingBeforeTerminalDeletion() => OnOwnerAsync(async () =>
    {
        await using var fixture = await CodecFixture.CreateAsync();
        using var scheduling = fixture.BlockSchedule();
        using (var newEnvelope = fixture.CreateEnvelope()) fixture.Outbox.Send(newEnvelope);
        var write = fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await scheduling.WaitAsync();
        var stop = ((ILifecycleObserver)fixture.Outbox).OnStop(CancellationToken.None);
        Assert.True(scheduling.Token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        scheduling.Release();
        var error = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => write);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        await stop;
        Assert.Equal(0, fixture.Probe.Captures);
        await fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Messages);
        Assert.ThrowsAny<OperationCanceledException>(() => { using var newEnvelope = fixture.CreateEnvelope(); fixture.Outbox.Send(newEnvelope); });
        await using var fresh = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        using (var newEnvelope = fresh.CreateEnvelope()) fresh.Outbox.Send(newEnvelope);
        await fresh.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(fresh.Messages);
    });

    private static async Task OnOwnerAsync(Func<Task> action, bool preventInlining = false)
    {
        var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, maxConcurrencyLevel: 1);
        try
        {
            await Task.Factory.StartNew(action, TestContext.Current.CancellationToken, TaskCreationOptions.None,
                preventInlining ? new NonInliningScheduler(scheduler.ExclusiveScheduler) : scheduler.ExclusiveScheduler)
                .Unwrap().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            scheduler.Complete();
            await scheduler.Completion;
        }
    }

    private sealed class CaptureBoundaryHook(IJournaledStateHook inner, Action afterPrerequisite) : IJournaledStateCaptureHook
    {
        public async ValueTask BeforeOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
        {
            await inner.BeforeOperationAsync(operation, cancellationToken);
            afterPrerequisite();
        }
    }

    private sealed class NonInliningScheduler(TaskScheduler scheduler) : TaskScheduler
    {
        protected override void QueueTask(Task task) => Task.Factory.StartNew(() => TryExecuteTask(task),
            CancellationToken.None, TaskCreationOptions.None, scheduler);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }

    private sealed class ScheduleBarrier(ILocalDurableJobManager jobs) : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DurableJob? Job { get; private set; }
        public CancellationToken Token { get; private set; }
        public void Arm() => jobs.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ScheduleAsync(call.ArgAt<ScheduleJobRequest>(0), call.ArgAt<CancellationToken>(1)));
        private async Task<DurableJob> ScheduleAsync(ScheduleJobRequest request, CancellationToken token)
        {
            Token = token;
            Job = new DurableJob
            {
                Id = Guid.NewGuid().ToString("N"),
                ShardId = "opaque-shard",
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                Metadata = request.Metadata
            };
            _entered.TrySetResult();
            await _continue.Task;
            return Job;
        }
        public Task WaitAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        public void Release() => _continue.TrySetResult();
        public void Fail(Exception failure) => _continue.TrySetException(failure);
        public void Dispose() => Release();
    }

    [GenerateSerializer]
    public sealed record Payload([property: Id(0)] byte[] Bytes);

    public sealed class CodecProbe
    {
        private readonly ConcurrentQueue<(string Type, string Phase)> _calls = new();
        public required SerializerSessionPool InnerSessions { get; init; }
        public string Phase { get; set; } = "before-admission";
        public int Captures { get; set; }
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
        public IDurableValue<int>? Business { get; }
        public IDurableInboxExtension Receiver { get; }
        public IDurableDictionary<HierarchicalKey, DurableEnvelope> Messages { get; }
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

        public IEnumerable<HierarchicalKey> AttemptKeys => (IEnumerable<HierarchicalKey>)States
            .GetState<IStateMachine>("__orleans.durable-messaging.outbox-message-state")
            .GetType().GetProperty("Keys")!.GetValue(States.GetState<IStateMachine>("__orleans.durable-messaging.outbox-message-state"))!;
        public IEnumerable<HierarchicalKey> DeadLetterKeys => (IEnumerable<HierarchicalKey>)DeadLetterState.GetType()
            .GetProperty("Keys")!.GetValue(DeadLetterState)!;
        private IStateMachine DeadLetterState => States.GetState<IStateMachine>("__orleans.durable-messaging.outbox-dead-letters");
        public DurableEnvelope ReadDeadLetterEnvelope(HierarchicalKey messageId)
        {
            var value = DeadLetterState.GetType().GetProperty("Item")!.GetValue(DeadLetterState, [messageId])!;
            return (DurableEnvelope)value.GetType().GetProperty("Envelope")!.GetValue(value)!;
        }
        public bool RemoveDeadLetter(HierarchicalKey messageId) =>
            (bool)DeadLetterState.GetType().GetMethod("Remove", [typeof(HierarchicalKey)])!.Invoke(DeadLetterState, [messageId])!;

        private CodecFixture(ControlledJournalStorageProvider? storage, JournalId? journalId, DeliveryResult delivery, int maxAttempts, int stateOrder, bool snapshot, bool businessState)
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
            ReceiverTestServices.AddValueLifecycles(services);
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
            var inbox = Receiver = Substitute.For<IDurableInboxExtension>();
            inbox.DeliverAsync(Arg.Any<DurableEnvelope>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(delivery));
            var grains = Substitute.For<IGrainFactory>();
            grains.GetGrain<IDurableInboxExtension>(Arg.Any<GrainId>()).Returns(inbox);
            services.AddSingleton(grains);
            services.AddSingleton(Implementation("DurableMessagingInstruments"), Implementation("DurableMessagingInstruments")
                .GetMethod("CreateForDirectConstruction", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!);
            services.AddScoped(Implementation("DurableMessagingPumpResults"), _ => Activator.CreateInstance(Implementation("DurableMessagingPumpResults"), nonPublic: true)!);
            services.Configure<DurableInboxOptions>(options =>
            {
                options.MaxDeliveryAttempts = maxAttempts;
                // These capture/retirement boundaries deliberately exercise the zero-grace policy.
                options.OutboxIdleRetirementGracePeriod = TimeSpan.Zero;
            });
            _services = services.BuildServiceProvider();
            _scope = _services.CreateAsyncScope();
            Manager = _scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            var dependencies = _scope.ServiceProvider;
            Outbox = (IDurableOutbox)ActivatorUtilities.CreateInstance(dependencies, Implementation("DurableOutbox"),
                dependencies.GetRequiredKeyedService<IDurableValueCommandCodec<long>>("orleans-binary"));
            States = _scope.ServiceProvider.GetRequiredService<StateTrackingManager>();
            if (businessState) Business = dependencies.GetRequiredKeyedService<IDurableValue<int>>("business");
            States.RegisterStates(stateOrder);
            Messages = States.GetState<IDurableDictionary<HierarchicalKey, DurableEnvelope>>("__orleans.durable-messaging.outbox");
            Job = States.GetState<IDurableValue<DurableJob>>("__orleans.durable-messaging.outbox-job-handle");
            _deliver = Outbox.GetType().GetMethod("DeliverPendingMessagesAsync")!.CreateDelegate<Func<CancellationToken, Task>>(Outbox);
        }

        public static async Task<CodecFixture> CreateAsync(ControlledJournalStorageProvider? storage = null, JournalId? journalId = null,
            DeliveryResult? delivery = null, int maxAttempts = 1, int stateOrder = 0, bool snapshot = false, bool initialize = true, bool businessState = false)
        {
            var result = new CodecFixture(storage, journalId, delivery ?? DeliveryResult.Accepted(), maxAttempts, stateOrder, snapshot, businessState);
            if (initialize)
            {
                await result.Manager.InitializeAsync(TestContext.Current.CancellationToken);
                await ((ILifecycleObserver)result.Outbox).OnStart(TestContext.Current.CancellationToken);
            }
            return result;
        }
        private int _messageSequence;
        private HierarchicalKey NextMessageId() => HierarchicalKey.Create(
            "test", "codec-boundary", "command", (++_messageSequence).ToString(CultureInfo.InvariantCulture));
        public DurableEnvelope CreateEnvelope() => CreateEnvelope(NextMessageId());
        public DurableEnvelope CreateEnvelope(HierarchicalKey messageId, string subject = "codec") =>
            TestApplicationProtocol.Create(_scope.ServiceProvider.GetRequiredService<SerializerSessionPool>(),
                GrainId.Create("sender", "codec-boundary"), GrainId.Create("receiver", "codec-boundary"),
                subject, new Payload(new byte[4096]), messageId);
        public DurableEnvelope CreateNullBodyEnvelope() => TestApplicationProtocol.Create(
            _scope.ServiceProvider.GetRequiredService<SerializerSessionPool>(), GrainId.Create("sender", "codec-boundary"),
            GrainId.Create("receiver", "codec-boundary"), "codec", null, NextMessageId());

        public ArcBuffer EncodeApplication(string route, object? body) =>
            TestApplicationProtocol.Encode(_scope.ServiceProvider.GetRequiredService<SerializerSessionPool>(), new TestApplicationMessage(route, body));
        public TestApplicationMessage ReadApplication(DurableEnvelope envelope) =>
            TestApplicationProtocol.Read(_scope.ServiceProvider.GetRequiredService<SerializerSessionPool>(), envelope);

        public object ResolveStandardState(string name)
        {
            Type contract = name switch
            {
                "__orleans.durable-messaging.outbox" => typeof(IDurableDictionary<HierarchicalKey, DurableEnvelope>),
                "__orleans.durable-messaging.outbox-message-state" => typeof(IDurableDictionary<,>).MakeGenericType(typeof(HierarchicalKey), Implementation("OutboxMessageState")),
                "__orleans.durable-messaging.outbox-dead-letters" => typeof(IDurableDictionary<,>).MakeGenericType(typeof(HierarchicalKey), Implementation("OutboxDeadLetter")),
                "__orleans.durable-messaging.outbox-job-id" or "__orleans.durable-messaging.outbox-completed-job-id" => typeof(IDurableValue<string>),
                "__orleans.durable-messaging.outbox-job-handle" => typeof(IDurableValue<DurableJob>),
                _ => throw new ArgumentOutOfRangeException(nameof(name))
            };
            return _scope.ServiceProvider.GetRequiredKeyedService(contract, name);
        }

        public ScheduleBarrier BlockSchedule()
        {
            var result = new ScheduleBarrier(Jobs);
            result.Arm();
            return result;
        }

        public ValueTask<DurableJobRunResult> CallbackAsync(DurableJob job)
        {
            var context = Substitute.For<IJobRunContext>();
            context.Job.Returns(job);
            context.RunId.Returns("early-run");
            context.DequeueCount.Returns(1);
            return ((IDurableJobFeatureHandler)Outbox).ExecuteJobAsync(context, TestContext.Current.CancellationToken);
        }

        public ValueTask<DurableJobRunResult> PumpAsync(DurableJob job)
        {
            var generation = Outbox.GetType().GetField("_stateGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Outbox);
            return (ValueTask<DurableJobRunResult>)Outbox.GetType().GetMethod("ExecuteJobCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Outbox, [job.Metadata!["orleans.messaging.ownership-id"], job, generation, TestContext.Current.CancellationToken, TestContext.Current.CancellationToken, true])!;
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
            ReceiverTestServices.AddValueLifecycles(services);
            services.AddSingleton<IJournaledStateManager>(manager);
            await using var dependencies = services.BuildServiceProvider();
            var messages = dependencies.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DurableEnvelope>>("__orleans.durable-messaging.outbox");
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

        public Task SendAsync(DurableEnvelope envelope)
        {
            Probe.Phase = "staging";
            Outbox.Send(envelope);
            return Task.CompletedTask;
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
                catch (Exception exception) when (exception is not JournaledStatePreCommitException and not JournaledStatePostCommitException)
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
        public IList<IJournaledStateHook> Hooks => inner.Hooks;
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
            public void WritePendingEntries(JournalStreamWriter writer) { probe.Phase = "capture"; probe.Captures++; state.WritePendingEntries(writer); }
            public void WriteSnapshot(JournalStreamWriter writer) { probe.Phase = "capture"; probe.Captures++; state.WriteSnapshot(writer); }
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
