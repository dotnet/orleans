using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace DefaultCluster.Tests;

public sealed partial class AdvancedReminderFeatureTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task OneShot_FailedCallbackBeforeOwnerRearm_RetainsDeliveryUntilSuccess(int failures)
    {
        var gate = new RecipientFailureFilter(failures: 0);
        await using var cluster = await CreateClusterAsync(retryPolicy: ReminderRetryPolicy.UntilSuccess,
            configureServices: services => services.AddSingleton<IIncomingGrainCallFilter>(gate));
        using var gateLifetime = gate;
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        gate.Target = grain.GetGrainId();
        var name = Guid.NewGuid().ToString("N");
        using var completion = new ReminderCompletions(name);

        // This callback throws before registering anything. Seven failures also exceed
        // the backend's three-dequeue limit, which must not strand a retained reminder.
        await grain.RegisterOneShot(name, DateTimeOffset.UtcNow, MissedReminderAction.FireImmediately, failures);
        string? previousOccurrence = null;
        for (var attempt = 0; attempt < failures; attempt++)
        {
            gate.AllowAttempt();
            Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());
            var retained = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
            Assert.Equal(TimeSpan.Zero, retained.Period);
            Assert.NotNull(retained.LastFireUtc);
            Assert.True(retained.NextDueUtc > retained.LastFireUtc);
            Assert.NotEmpty(retained.JobId);
            Assert.NotEmpty(retained.JobShardId);
            Assert.NotEqual(previousOccurrence, retained.ScheduleId);
            previousOccurrence = retained.ScheduleId;
        }

        gate.AllowAttempt();
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());
        Assert.Equal(failures + 1, await grain.GetTickCount());
        Assert.Null(await GetEntryAsync(cluster, grain, name));
        Assert.Equal(failures + 1, completion.JobIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(completion.DequeueCounts, count => Assert.Equal(1, count));
    }
}
