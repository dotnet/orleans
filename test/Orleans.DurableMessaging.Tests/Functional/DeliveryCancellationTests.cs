using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DeliveryCancellationTests : DurableMessagingBehaviorTestBase
{
    [Theory]
    [InlineData(false, "schedule")]
    [InlineData(true, "schedule")]
    [InlineData(false, "admission")]
    [InlineData(true, "admission")]
    [InlineData(false, "storage")]
    [InlineData(true, "storage")]
    public async Task CallerCancellation_RetainsOwnedDeliveryUntilCommit(bool proxy, string phase)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var extension = GetExtension(context);
        using var blocked = await BlockAsync(receiver, phase);
        using var envelope = CreateEnvelope(receiver, NewMessage(150, phase));
        using var cancellation = new CancellationTokenSource();
        var delivery = StartDelivery(proxy, receiver, context, extension, envelope.Value, cancellation.Token);
        await blocked.Entered();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal(0, GetGate(extension).CurrentCount);
        Assert.Single(GetPendingOwners(extension));
        var duplicate = StartDelivery(false, receiver, context, extension, envelope.Value, TestContext.Current.CancellationToken);
        await OnTurnAsync(context, static () => { });
        Assert.False(duplicate.IsCompleted);
        Assert.Equal(1, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        blocked.Release();
        await blocked.Preceding;
        Assert.Equal(DeliveryStatus.Duplicate, (await duplicate).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(), static snapshot => snapshot.InboxJobId is null);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(3 + blocked.PrecedingWrites, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        Assert.Equal(1, GetGate(extension).CurrentCount);
        Assert.Empty(GetPendingOwners(extension));
        Assert.False(((DurableMessagingTestGrain)context.GrainInstance!).Faulted.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false, "admission")]
    [InlineData(true, "admission")]
    [InlineData(false, "storage")]
    [InlineData(true, "storage")]
    public async Task CanceledCaller_LateFailureIsLoggedAndReleasesOwnership(bool proxy, string phase)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var extension = GetExtension(context);
        using var envelope = CreateEnvelope(receiver, NewMessage(151, phase));
        using var logs = new DeliveryLogProbe(envelope.Value.MessageId);
        Fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        using var blocked = await BlockAsync(receiver, phase);
        using var cancellation = new CancellationTokenSource();
        var delivery = StartDelivery(proxy, receiver, context, extension, envelope.Value, cancellation.Token);
        await blocked.Entered();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(0, GetGate(extension).CurrentCount);
        Assert.Single(GetPendingOwners(extension));

        Assert.IsType<Action>(blocked.Fail)();
        await blocked.Preceding;
        var failure = await grain.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var logged = await logs.Failure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.IsType<IOException>(failure);
        Assert.Same(failure, logged);
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, GetGate(extension).CurrentCount);
        Assert.Empty(GetPendingOwners(extension));
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Null(recovered.InboxJobId);
        Assert.Empty(recovered.Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivationAfterCallerCancellation_DrainsOwnedDelivery(bool proxy)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var extension = GetExtension(context);
        var shutdown = (CancellationTokenSource)extension.GetType().GetField("_shutdownCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(extension)!;
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = shutdown.Token.Register(() => stopping.TrySetResult());
        using var blocked = await BlockAsync(receiver, "storage");
        using var envelope = CreateEnvelope(receiver, NewMessage(152, "deactivation"));
        using var cancellation = new CancellationTokenSource();
        var delivery = StartDelivery(proxy, receiver, context, extension, envelope.Value, cancellation.Token);
        await blocked.Entered();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        _ = await receiver.GetSnapshotAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Drain canceled delivery waiter."), TestContext.Current.CancellationToken);
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(context.Deactivated.IsCompleted);
        Assert.Equal(0, GetGate(extension).CurrentCount);
        Assert.Single(GetPendingOwners(extension));
        Assert.True(shutdown.Token.CanBeCanceled);
        blocked.Release();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, GetGate(extension).CurrentCount);
        Assert.Empty(GetPendingOwners(extension));
        Assert.Throws<ObjectDisposedException>(() => shutdown.Token);
        _ = await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
    }

    private async Task<BlockedPhase> BlockAsync(IDurableMessagingTestGrain receiver, string phase)
    {
        switch (phase)
        {
            case "schedule":
                var schedule = Fixture.JobManagerProbe.BlockNext(ReceiverTestServices.InboxJobName);
                return new(schedule.WaitUntilEnteredAsync, schedule.Continue, null);
            case "admission":
                var context = Fixture.GetGrainContext(receiver);
                var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
                var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
                var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
                await OnTurnAsync(context, () =>
                    context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "prior-admission-cohort");
                var precedingStorage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
                var preceding = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
                await precedingStorage.WaitUntilEnteredAsync();
                var captures = grain.Captures.Count;
                var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                outbox.ValidateWriting = () =>
                {
                    outbox.ValidateWriting = null;
                    admitted.TrySetResult();
                };
                return new(
                    async () =>
                    {
                        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                        await OnTurnAsync(context, () =>
                        {
                            Assert.Equal(captures, grain.Captures.Count);
                            Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
                        });
                    },
                    precedingStorage.Release,
                    () => context.Scheduler.QueueAction(() =>
                    {
                        outbox.OnFaulted(new IOException("Injected late admitted-state validation failure."));
                        precedingStorage.Release();
                    }),
                    PrecedingWrites: 1,
                    PriorWrite: preceding);
            case "storage":
                var storage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
                return new(storage.WaitUntilEnteredAsync, storage.Release, storage.Fail);
            default:
                throw new ArgumentOutOfRangeException(nameof(phase));
        }
    }

    private static Task<DeliveryResult> StartDelivery(bool proxy, IDurableMessagingTestGrain receiver, IGrainContext context,
        IDurableInboxExtension extension, DurableEnvelope envelope, CancellationToken cancellationToken)
    {
        if (proxy)
        {
            return DeliverWithCancellationAsync(receiver, envelope, cancellationToken);
        }
        var started = new TaskCompletionSource<Task<DeliveryResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { started.SetResult(extension.DeliverAsync(envelope, cancellationToken).AsTask()); }
            catch (Exception exception) { started.SetException(exception); }
        });
        return started.Task.Unwrap();
    }

    private static IDurableInboxExtension GetExtension(IGrainContext context) =>
        (IDurableInboxExtension)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
    private static SemaphoreSlim GetGate(IDurableInboxExtension extension) =>
        (SemaphoreSlim)extension.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(extension)!;
    private static HashSet<string> GetPendingOwners(IDurableInboxExtension extension) =>
        (HashSet<string>)extension.GetType().GetField("_pendingOwnershipIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(extension)!;

    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception exception) { completed.SetException(exception); }
        });
        return completed.Task;
    }

    private sealed record BlockedPhase(Func<Task> Entered, Action Release, Action? Fail,
        int PrecedingWrites = 0, Task? PriorWrite = null) : IDisposable
    {
        public Task Preceding => PriorWrite ?? Task.CompletedTask;
        public void Dispose() => Release();
    }

    private sealed class DeliveryLogProbe(Guid messageId) : ILoggerProvider
    {
        public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName, messageId.ToString());
        public void Dispose() { }
        private sealed class Logger(DeliveryLogProbe owner, string category, string messageId) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (exception is not null && category == "Orleans.DurableMessaging.DurableInboxExtension"
                    && eventId.Name == "DeliveryOperationFailed" && formatter(state, exception).Contains(messageId, StringComparison.Ordinal))
                {
                    owner.Failure.TrySetResult(exception);
                }
            }
        }
    }
}
