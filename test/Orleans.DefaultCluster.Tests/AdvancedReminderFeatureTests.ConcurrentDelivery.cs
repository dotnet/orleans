using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.Runtime;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace DefaultCluster.Tests;

public sealed partial class AdvancedReminderFeatureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentOwnerTurn_CanChangeRegistration_WhileCallbackIsQueued(bool unregister)
    {
        var barrier = new AdvancedReminderDeliveryBarrier();
        await using var cluster = await CreateClusterAsync(deliveryBarrier: barrier);
        var grain = cluster.Client.GetGrain<IAdvancedReminderConcurrentFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        var replacementDue = DateTimeOffset.UtcNow.AddDays(31);
        await grain.Register(name, DateTimeOffset.UtcNow.AddDays(30));
        var table = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderTable>();
        var original = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), name));
        original.NextDueUtc = DateTime.UtcNow.AddSeconds(-1);
        original.ETag = await table.UpsertRow(original);
        barrier.Target = grain.GetGrainId();

        // Hold a normal recipient turn before an independent delivery chain sends its callback.
        var mutation = grain.ChangeWhileCallbackIsQueued(name, replacementDue, unregister);
        try
        {
            await AwaitPhase(barrier.OwnerTurnStarted.Task, "recipient normal turn acquired");
            var delivery = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderService>()
                .ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken);
            await AwaitPhase(barrier.CallbackSent.Task, "callback sent behind the recipient normal turn");
            await AwaitPhase(mutation, "independent registration mutation completed while callback is queued");
            await AwaitPhase(delivery, "queued callback and dispatcher completion");
        }
        finally
        {
            barrier.CallbackSent.TrySetResult();
        }

        var current = await table.ReadRow(grain.GetGrainId(), name);
        if (unregister)
        {
            Assert.Null(current);
        }
        else
        {
            var replacement = Assert.IsType<ReminderEntry>(current);
            Assert.Equal(replacementDue.UtcDateTime, replacement.NextDueUtc);
            Assert.NotEqual(original.ETag, replacement.ETag);
            Assert.NotEqual(original.ScheduleId, replacement.ScheduleId);
            Assert.NotEqual(original.JobId, replacement.JobId);
            Assert.NotEmpty(replacement.JobShardId);
            Assert.Null(replacement.LastFireUtc);
            await cluster.Client.GetReminderManagementGrain().DeleteAsync(grain.GetGrainId(), name);
        }

        Assert.Equal(1, await grain.GetTickCount());
    }

    [Fact]
    public async Task ConcurrentDeliveryOfSameOccurrence_FiresOnce_AndRemovesExactRegistration()
    {
        var barrier = new AdvancedReminderDeliveryBarrier { HoldCallback = true };
        await using var cluster = await CreateClusterAsync(deliveryBarrier: barrier);
        var grain = cluster.Client.GetGrain<IAdvancedReminderConcurrentFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        await grain.Register(name, DateTimeOffset.UtcNow.AddDays(30));
        var table = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderTable>();
        var original = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), name));
        original.NextDueUtc = DateTime.UtcNow.AddSeconds(-1);
        original.ETag = await table.UpsertRow(original);
        barrier.Target = grain.GetGrainId();
        var service = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderService>();

        var first = service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken);
        try
        {
            await AwaitPhase(barrier.CallbackStarted.Task, "first callback holds recipient normal turn");
            var duplicate = service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken);
            await AwaitPhase(barrier.DuplicateWaiting.Task, "duplicate observes the admitted callback before it is released");
            barrier.ReleaseCallback.TrySetResult();
            await AwaitPhase(Task.WhenAll(first, duplicate), "both deliveries completed");
        }
        finally
        {
            barrier.ReleaseCallback.TrySetResult();
        }

        Assert.Equal(1, await grain.GetTickCount());
        Assert.Null(await table.ReadRow(grain.GetGrainId(), name));
    }

    [Fact]
    public async Task CancelledDelivery_ReleasesAdmission_WithoutAdvancingOccurrence()
    {
        var barrier = new AdvancedReminderDeliveryBarrier { HoldCallback = true };
        await using var cluster = await CreateClusterAsync(deliveryBarrier: barrier);
        var grain = cluster.Client.GetGrain<IAdvancedReminderConcurrentFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        await grain.Register(name, DateTimeOffset.UtcNow.AddDays(30));
        var table = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderTable>();
        var original = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), name));
        original.NextDueUtc = DateTime.UtcNow.AddSeconds(-1);
        original.ETag = await table.UpsertRow(original);
        barrier.Target = grain.GetGrainId();
        var service = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderService>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var delivery = service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, cancellation.Token);
        try
        {
            await AwaitPhase(barrier.CallbackStarted.Task, "callback admitted before cancellation");
            await cancellation.CancelAsync();
            await AwaitPhase(barrier.RequestCancellationObserved.Task, "dispatcher received native cancellation");
            barrier.ReleaseCallback.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitPhase(delivery, "cancelled delivery releases its exact admission"));
        }
        finally
        {
            barrier.ReleaseCallback.TrySetResult();
        }
        var retained = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), name));
        Assert.Equal(original.ETag, retained.ETag);
        Assert.Equal(original.ScheduleId, retained.ScheduleId);
        Assert.Equal(original.NextDueUtc, retained.NextDueUtc);
        Assert.Equal(original.JobId, retained.JobId);
        Assert.Null(retained.LastFireUtc);

        await AwaitPhase(service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken),
            "retry enters the released admission");
        Assert.Equal(2, await grain.GetTickCount());
        Assert.Null(await table.ReadRow(grain.GetGrainId(), name));
    }

    private static async Task AwaitPhase(Task phase, string description)
    {
        try
        {
            await phase.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Advanced reminder phase did not complete: {description}.", exception);
        }
    }
}

public sealed class AdvancedReminderDeliveryBarrier : IOutgoingGrainCallFilter, IIncomingGrainCallFilter
{
    public GrainId Target { get; set; }
    public bool HoldCallback { get; init; }
    public bool FailPreparationResponse { get; init; }
    public int FailedPreparationResponseNumber { get; init; } = 1;
    public bool PausePreparationResponse { get; init; }
    public TaskCompletionSource PreparationReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleasePreparationResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _preparationResponses;
    public TaskCompletionSource OwnerTurnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CallbackSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CallbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DuplicateWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RequestCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        var invocation = context.Invoke();
        if (context.TargetId == Target
            && context.InterfaceMethod.DeclaringType == typeof(Orleans.AdvancedReminders.IRemindable)
            && context.MethodName == nameof(Orleans.AdvancedReminders.IRemindable.ReceiveReminder))
        {
            // Invoke sends the native request before it awaits the response.
            CallbackSent.TrySetResult();
        }

        await invocation;
        if (context.InterfaceMethod.DeclaringType?.FullName == "Orleans.AdvancedReminders.Runtime.ReminderService.IAdvancedReminderDispatcherGrain"
            && context.MethodName == "PrepareDeliveryAsync")
        {
            if (PausePreparationResponse)
            {
                PreparationReady.TrySetResult();
                await ReleasePreparationResponse.Task;
            }

            if (FailPreparationResponse
                && Interlocked.Increment(ref _preparationResponses) == FailedPreparationResponseNumber)
            {
                // The owner has admitted delivery, but the caller cannot observe its reply.
                throw new InvalidOperationException("Intentional loss of the prepared delivery response.");
            }
        }
    }

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        using var registration = context.InterfaceMethod.DeclaringType?.FullName == "Orleans.AdvancedReminders.Runtime.ReminderService.IAdvancedReminderDispatcherGrain"
            && context.MethodName == "ProcessDueReminderAsync"
            ? context.Request.GetCancellationToken().Register(() => RequestCancellationObserved.TrySetResult())
            : default;
        var invocation = context.Invoke();
        if (context.InterfaceMethod.DeclaringType?.FullName == "Orleans.AdvancedReminders.Runtime.ReminderService.IAdvancedReminderDispatcherGrain"
            && context.MethodName == "ObserveDeliveryAsync")
        {
            DuplicateWaiting.TrySetResult();
        }

        await invocation;
    }
}

public interface IAdvancedReminderConcurrentFeatureGrain : IGrainWithGuidKey
{
    Task Register(string name, DateTimeOffset due);
    Task Unregister();
    Task ChangeWhileCallbackIsQueued(string name, DateTimeOffset replacementDue, bool unregister);
    Task<int> GetTickCount();
}

public sealed class AdvancedReminderConcurrentFeatureGrain(AdvancedReminderDeliveryBarrier barrier)
    : Grain, IAdvancedReminderConcurrentFeatureGrain, Orleans.AdvancedReminders.IRemindable
{
    private int _tickCount;
    private Orleans.AdvancedReminders.IGrainReminder? _handle;

    public async Task Register(string name, DateTimeOffset due)
        => _handle = await this.RegisterOrUpdateAdvancedReminder(name, due);

    public Task Unregister() => this.UnregisterAdvancedReminder(_handle!);

    public async Task ChangeWhileCallbackIsQueued(string name, DateTimeOffset replacementDue, bool unregister)
    {
        barrier.OwnerTurnStarted.TrySetResult();
        await barrier.CallbackSent.Task;
        if (unregister)
        {
            await this.UnregisterAdvancedReminder(_handle!);
        }
        else
        {
            _handle = await this.RegisterOrUpdateAdvancedReminder(name, replacementDue);
        }
    }

    public Task<int> GetTickCount() => Task.FromResult(_tickCount);

    public async Task ReceiveReminder(string reminderName, Orleans.AdvancedReminders.Runtime.TickStatus status)
    {
        _tickCount++;
        barrier.CallbackStarted.TrySetResult();
        if (barrier.HoldCallback)
        {
            await barrier.ReleaseCallback.Task;
        }
    }
}
