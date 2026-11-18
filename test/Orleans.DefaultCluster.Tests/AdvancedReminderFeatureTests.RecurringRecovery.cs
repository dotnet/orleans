using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace DefaultCluster.Tests;

public sealed partial class AdvancedReminderFeatureTests
{
    private const int RecurringRegistrationLeadSeconds = 10;

    [Theory]
    [InlineData("one-shot")]
    [InlineData("interval")]
    [InlineData("cron")]
    public async Task LostDurableJobIsRecreatedByRecovery_WithoutChangingTheSchedule(string kind)
    {
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        var gracePeriod = TimeSpan.FromMilliseconds(1);
        await using var cluster = await CreateClusterAsync(reminderClock: clock, retryPolicy: ReminderRetryPolicy.UntilSuccess,
            missedReminderGracePeriod: gracePeriod);
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        while (grain.GetGrainId().GetUniformHashCode() >= (1u << 28))
        {
            // Use the first group of hash ranges visited by reconciliation.
            grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var services = cluster.GetSiloServiceProvider();
        var name = Guid.NewGuid().ToString("N");
        var firstDue = DateTimeOffset.FromUnixTimeSeconds(
            TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() + RecurringRegistrationLeadSeconds);
        using var completion = new ReminderCompletions(name);
        if (kind == "one-shot")
        {
            await grain.RegisterOneShot(name, firstDue, MissedReminderAction.FireImmediately, failures: 0);
        }
        else
        {
            await grain.RegisterDailyReminder(name, kind == "cron", firstDue.UtcDateTime, failFirstAttempt: false);
        }
        var original = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
        Assert.Equal(firstDue.UtcDateTime, original.NextDueUtc);
        Assert.True(await services.GetRequiredService<ILocalDurableJobManager>().CancelAsync(new DurableJob
        {
            Id = original.JobId,
            Name = $"advanced-reminder:{name}",
            ShardId = original.JobShardId,
            DueTime = firstDue,
            TargetGrainId = cluster.Client.GetGrain<IAdvancedReminderDispatcherGrain>(grain.GetGrainId().ToString()).GetGrainId()
        }, cancellationToken));

        var stranded = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
        Assert.Equal(original.JobId, stranded.JobId);
        Assert.Equal(original.ScheduleId, stranded.ScheduleId);
        Assert.Equal(0, await grain.GetTickCount());

        clock.SetUtcNow(firstDue.Add(gracePeriod * 2));
        var recovery = new AdvancedReminderRecoveryGrain(
            services.GetRequiredService<Orleans.AdvancedReminders.IReminderTable>(),
            cluster.Client,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            services.GetRequiredService<IOptions<Orleans.AdvancedReminders.ReminderOptions>>(),
            clock);
        await recovery.ReconcileAsync(force: false, cancellationToken);
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());
        Assert.NotEqual(original.JobId, Assert.Single(completion.JobIds));
        Assert.Equal(1, await grain.GetTickCount());

        var following = await GetEntryAsync(cluster, grain, name);
        if (kind == "one-shot")
        {
            Assert.Null(following);
        }
        else
        {
            Assert.NotNull(following);
            Assert.Equal(original.StartAt, following.StartAt);
            Assert.Equal(firstDue.UtcDateTime.AddDays(1), following.NextDueUtc);
        }
    }

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
        await grain.RegisterDailyReminder(name, cron, firstDue, failFirstAttempt: true);
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
