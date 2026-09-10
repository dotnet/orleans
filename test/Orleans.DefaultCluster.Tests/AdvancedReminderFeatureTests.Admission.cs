using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace DefaultCluster.Tests;

public sealed partial class AdvancedReminderFeatureTests
{
    [Fact]
    public async Task LostPreparationResponse_ReleasesAdmission_WithoutChangingRegistration()
    {
        var barrier = new AdvancedReminderDeliveryBarrier { FailPreparationResponse = true };
        await using var cluster = await CreateClusterAsync(deliveryBarrier: barrier);
        var grain = cluster.Client.GetGrain<IAdvancedReminderConcurrentFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        await grain.Register(name, DateTimeOffset.UtcNow.AddDays(30));
        var table = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderTable>();
        var original = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), name));
        original.NextDueUtc = DateTime.UtcNow.AddSeconds(-1);
        original.ETag = await table.UpsertRow(original);
        var service = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderService>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken));
        Assert.Equal("Intentional loss of the prepared delivery response.", failure.Message);
        Assert.Equal(0, await grain.GetTickCount());
        var retained = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), name));
        Assert.Equal(original.ETag, retained.ETag);
        Assert.Equal(original.ScheduleId, retained.ScheduleId);
        Assert.Equal(original.JobId, retained.JobId);
        Assert.Null(retained.LastFireUtc);

        await AwaitPhase(service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken),
            "retry after the preparation response was lost");
        Assert.Equal(1, await grain.GetTickCount());
        Assert.Null(await table.ReadRow(grain.GetGrainId(), name));
    }

    [Fact]
    public async Task LostDuplicatePreparationResponse_DoesNotReleaseAnotherDelivery()
    {
        var barrier = new AdvancedReminderDeliveryBarrier
        {
            HoldCallback = true,
            FailPreparationResponse = true,
            FailedPreparationResponseNumber = 2
        };
        await using var cluster = await CreateClusterAsync(deliveryBarrier: barrier);
        var grain = cluster.Client.GetGrain<IAdvancedReminderConcurrentFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        await grain.Register(name, DateTimeOffset.UtcNow.AddDays(30));
        var table = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderTable>();
        var original = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), name));
        original.NextDueUtc = DateTime.UtcNow.AddSeconds(-1);
        original.ETag = await table.UpsertRow(original);
        var service = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderService>();
        var first = service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken);
        try
        {
            await AwaitPhase(barrier.CallbackStarted.Task, "original callback holds delivery admission");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken));
            var retry = service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken);
            await AwaitPhase(barrier.DuplicateWaiting.Task, "retry still waits for the original admission");
            Assert.False(retry.IsCompleted);
            barrier.ReleaseCallback.TrySetResult();
            await AwaitPhase(Task.WhenAll(first, retry), "original delivery and retry complete");
        }
        finally
        {
            barrier.ReleaseCallback.TrySetResult();
        }

        Assert.Equal(1, await grain.GetTickCount());
        Assert.Null(await table.ReadRow(grain.GetGrainId(), name));
    }

    [Fact]
    public async Task CancellationAfterPreparation_PreventsCallback_AndReleasesAdmission()
    {
        var barrier = new AdvancedReminderDeliveryBarrier { PausePreparationResponse = true };
        await using var cluster = await CreateClusterAsync(deliveryBarrier: barrier);
        var grain = cluster.Client.GetGrain<IAdvancedReminderConcurrentFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        await grain.Register(name, DateTimeOffset.UtcNow.AddDays(30));
        var table = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderTable>();
        var original = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), name));
        original.NextDueUtc = DateTime.UtcNow.AddSeconds(-1);
        original.ETag = await table.UpsertRow(original);
        var service = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderService>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var delivery = service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, cancellation.Token);
        try
        {
            await AwaitPhase(barrier.PreparationReady.Task, "owner admitted delivery before its reply");
            await cancellation.CancelAsync();
            await AwaitPhase(barrier.RequestCancellationObserved.Task, "dispatcher observed cancellation before callback");
            barrier.ReleasePreparationResponse.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitPhase(delivery, "cancelled preparation completes cleanup"));
        }
        finally
        {
            barrier.ReleasePreparationResponse.TrySetResult();
        }

        Assert.Equal(0, await grain.GetTickCount());
        var retained = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), name));
        Assert.Equal(original.ETag, retained.ETag);
        Assert.Equal(original.ScheduleId, retained.ScheduleId);
        Assert.Null(retained.LastFireUtc);
        await AwaitPhase(service.ProcessDueReminderAsync(grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken),
            "retry after cancellation before callback");
        Assert.Equal(1, await grain.GetTickCount());
        Assert.Null(await table.ReadRow(grain.GetGrainId(), name));
    }
}
