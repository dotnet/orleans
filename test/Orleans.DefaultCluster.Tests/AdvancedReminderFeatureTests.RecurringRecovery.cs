using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.AdvancedReminders;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace DefaultCluster.Tests;

public sealed partial class AdvancedReminderFeatureTests
{
    private const int RecurringRegistrationLeadSeconds = 10;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recurring_FailedDeliveryRetriesBeforeNextCalendarSlot_WithoutMovingItsAnchor(bool cron)
    {
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        var gate = new RecipientFailureFilter(failures: 0);
        await using var cluster = await CreateClusterAsync(reminderClock: clock, retryPolicy: ReminderRetryPolicy.UntilSuccess,
            missedReminderGracePeriod: TimeSpan.FromMilliseconds(1),
            configureServices: services => services.AddSingleton<IIncomingGrainCallFilter>(gate));
        using var gateLifetime = gate;
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        gate.Target = grain.GetGrainId();
        var name = Guid.NewGuid().ToString("N");
        var firstDue = DateTimeOffset.FromUnixTimeSeconds(
            TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() + RecurringRegistrationLeadSeconds).UtcDateTime;
        using var completion = new ReminderCompletions(name);
        await grain.RegisterDailyReminderWithFailure(name, cron, firstDue);
        var original = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
        Assert.Equal(firstDue, original.NextDueUtc);
        clock.SetUtcNow(new DateTimeOffset(firstDue, TimeSpan.Zero));

        gate.AllowAttempt();
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());
        // The retry is beyond the grace period under Skip. It is the already
        // attempted occurrence, so it must still reach its owner before tomorrow.
        clock.Advance(TimeSpan.FromSeconds(1));
        var pending = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
        Assert.Equal(original.StartAt, pending.StartAt);
        Assert.Equal(firstDue, pending.NextDueUtc);
        Assert.True(pending.LastFireUtc >= firstDue);
        Assert.NotEqual(original.ScheduleId, pending.ScheduleId);
        Assert.NotEqual(original.JobId, pending.JobId);

        gate.AllowAttempt();
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());
        var following = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
        Assert.Equal(2, await grain.GetTickCount());
        Assert.Equal(original.StartAt, following.StartAt);
        Assert.Equal(firstDue.AddDays(1), following.NextDueUtc);
        Assert.Equal(2, completion.JobIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(completion.DequeueCounts, count => Assert.Equal(1, count));
        await cluster.Client.GetReminderManagementGrain().DeleteAsync(grain.GetGrainId(), name);
        Assert.Null(await GetEntryAsync(cluster, grain, name));
    }
}
