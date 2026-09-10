using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.DurableJobs;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace DefaultCluster.Tests;

public sealed partial class AdvancedReminderFeatureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StalledJobCancellation_ReleasesDispatcher_AndFencesPreviousOccurrence(
        bool replace, bool honorsCancellation)
    {
        var gate = new ReminderJobCancellationGate(honorsCancellation);
        await using var cluster = await CreateClusterAsync(
            deliveryBarrier: new AdvancedReminderDeliveryBarrier(), cancellationGate: gate);
        var grain = cluster.Client.GetGrain<IAdvancedReminderConcurrentFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        var followingName = Guid.NewGuid().ToString("N");
        var replacementDue = DateTimeOffset.UtcNow.AddDays(31);
        var table = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderTable>();
        await grain.Register(name, DateTimeOffset.UtcNow.AddDays(30));
        var original = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), name));
        using var completion = new ReminderCompletions(followingName);

        try
        {
            var mutation = replace ? grain.Register(name, replacementDue) : grain.Unregister();
            var requestCancellation = await gate.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.False(mutation.IsCompleted);
            var following = grain.Register(followingName, DateTimeOffset.UtcNow);
            Assert.False(following.IsCompleted);

            await AwaitPhase(mutation, "committed reminder mutation stops waiting for obsolete-job cancellation");
            Assert.True(requestCancellation.IsCancellationRequested);
            await AwaitPhase(following, "the next recipient turn can register on the same dispatcher");
            Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());
            Assert.Equal(1, await grain.GetTickCount());
            Assert.Null(await table.ReadRow(grain.GetGrainId(), followingName));

            var retained = await table.ReadRow(grain.GetGrainId(), name);
            if (replace)
            {
                var replacement = Assert.IsType<ReminderEntry>(retained);
                Assert.Equal(replacementDue.UtcDateTime, replacement.NextDueUtc);
                Assert.NotEqual(original.ScheduleId, replacement.ScheduleId);
                Assert.NotEqual(original.JobId, replacement.JobId);
            }
            else
            {
                Assert.Null(retained);
            }

            // A stale durable job can still arrive after its best-effort cleanup
            // expires. The committed removal or replacement must fence it.
            var service = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderService>();
            await AwaitPhase(service.ProcessDueReminderAsync(
                grain.GetGrainId(), name, original.ScheduleId, TestContext.Current.CancellationToken),
                "the old occurrence is rejected after the mutation commits");
            Assert.Equal(1, await grain.GetTickCount());
        }
        finally
        {
            gate.Release.TrySetResult();
            await AwaitPhase(gate.Finished.Task, "the delayed cancellation request is observed and drained");
        }
    }

    // Delay only the first cancellation response. Scheduling, storage, owner
    // turns and delivery continue through the cluster's real Durable Jobs backend.
    private sealed class ReminderJobCancellationGate(bool honorsCancellation) : ILocalDurableJobManager
    {
        private int _cancellationCount;
        public ILocalDurableJobManager Inner { get; set; } = null!;
        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
            => Inner.ScheduleJobAsync(request, cancellationToken);

        public async Task<bool> CancelAsync(DurableJob job, CancellationToken requestCancellationToken)
        {
            if (Interlocked.Increment(ref _cancellationCount) != 1)
            {
                return await Inner.CancelAsync(job, requestCancellationToken);
            }

            Started.TrySetResult(requestCancellationToken);
            try
            {
                if (honorsCancellation)
                {
                    await Release.Task.WaitAsync(requestCancellationToken);
                }
                else
                {
                    await Release.Task;
                }

                return await Inner.CancelAsync(job, requestCancellationToken);
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }
}
