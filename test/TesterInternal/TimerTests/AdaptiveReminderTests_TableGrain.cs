using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.Internal;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.TimerTests
{
    [TestCategory("Functional"), TestCategory("Reminders")]
    public class AdaptiveReminderTests_TableGrain : ReminderTests_Base, IClassFixture<AdaptiveReminderTests_TableGrain.Fixture>
    {
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan PollStep = TimeSpan.FromMilliseconds(100);

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }

            private class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddMemoryGrainStorageAsDefault()
                        .UseInMemoryReminderService()
                        .AddAdaptiveReminderService()
                        .Configure<ReminderOptions>(options =>
                        {
                            options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100);
                            options.PollInterval = TimeSpan.FromMilliseconds(100);
                            options.LookAheadWindow = TimeSpan.FromSeconds(2);
                            options.BaseBucketSize = 1;
                            options.EnablePriority = true;
                        });
                }
            }
        }

        public AdaptiveReminderTests_TableGrain(Fixture fixture) : base(fixture)
        {
            var controlProxy = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitAsync(TestConstants.InitTimeout).Wait();
        }

        [Fact]
        public async Task Adaptive_IntervalReminder_UsesIntervalStatusAndPersistsAdaptiveFields()
        {
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            const string reminderName = "interval-meta";
            var period = TimeSpan.FromMilliseconds(700);

            await grain.StartReminderWithOptions(
                reminderName,
                dueTime: TimeSpan.FromMilliseconds(300),
                period: period,
                priority: ReminderPriority.Normal,
                action: MissedReminderAction.Skip);

            var firstTick = await WaitForFirstTick(grain, reminderName, WaitTimeout);

            Assert.Equal(ReminderScheduleKind.Interval, firstTick.ScheduleKind);
            Assert.Equal(period, firstTick.Period);

            var entry = await grain.GetReminderEntry(reminderName);
            Assert.NotNull(entry);
            Assert.Null(entry.CronExpression);
            Assert.Equal(ReminderPriority.Normal, entry.Priority);
            Assert.Equal(MissedReminderAction.Skip, entry.Action);
            Assert.NotNull(entry.LastFireUtc);
            Assert.NotNull(entry.NextDueUtc);
            Assert.True(entry.NextDueUtc > entry.LastFireUtc);

            await grain.StopReminder(reminderName);
        }

        [Fact]
        public async Task Adaptive_AbsoluteUtcReminder_FiresAndPersistsStartAt()
        {
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            const string reminderName = "absolute-meta";
            var period = TimeSpan.FromMilliseconds(700);
            var dueAtUtc = DateTime.UtcNow.AddSeconds(2);

            await grain.StartReminderAtUtc(
                reminderName,
                dueAtUtc,
                period,
                priority: ReminderPriority.High,
                action: MissedReminderAction.Skip);

            var firstTick = await WaitForFirstTick(grain, reminderName, WaitTimeout);
            Assert.Equal(ReminderScheduleKind.Interval, firstTick.ScheduleKind);
            Assert.Equal(period, firstTick.Period);
            Assert.True(firstTick.FirstTickTime >= dueAtUtc.AddSeconds(-1));
            Assert.True(firstTick.FirstTickTime <= dueAtUtc.AddSeconds(1));

            var entry = await grain.GetReminderEntry(reminderName);
            Assert.NotNull(entry);
            Assert.Equal(ReminderPriority.High, entry.Priority);
            Assert.Equal(MissedReminderAction.Skip, entry.Action);
            Assert.True(entry.StartAt >= dueAtUtc.AddSeconds(-1));
            Assert.True(entry.StartAt <= dueAtUtc.AddSeconds(1));

            await grain.StopReminder(reminderName);
        }

        [Fact]
        public async Task Adaptive_CronReminder_UsesCronStatusAndPersistsCronExpression()
        {
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            const string reminderName = "cron-meta";
            const string cron = "*/1 * * * * *";

            await grain.StartCronReminder(
                reminderName,
                cron,
                priority: ReminderPriority.High,
                action: MissedReminderAction.Skip);

            var firstTick = await WaitForFirstTick(grain, reminderName, WaitTimeout);

            Assert.Equal(ReminderScheduleKind.Cron, firstTick.ScheduleKind);
            Assert.Equal(TimeSpan.Zero, firstTick.Period);

            var entry = await grain.GetReminderEntry(reminderName);
            Assert.NotNull(entry);
            Assert.Equal(cron, entry.CronExpression);
            Assert.Equal(TimeSpan.Zero, entry.Period);
            Assert.NotNull(entry.LastFireUtc);
            Assert.NotNull(entry.NextDueUtc);
            Assert.True(entry.NextDueUtc > entry.LastFireUtc);
            Assert.Equal(ReminderPriority.High, entry.Priority);

            await grain.StopReminder(reminderName);
        }

        [Fact]
        public async Task Adaptive_MissedAction_OverdueRowsFireOnlyWhenConfigured()
        {
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var now = DateTime.UtcNow;
            var startAt = now.AddSeconds(-30);
            var overdue = now.AddSeconds(-15);
            var longPeriod = TimeSpan.FromMinutes(10);

            const string fireName = "missed-fire";
            const string skipName = "missed-skip";
            const string notifyName = "missed-notify";

            await grain.UpsertRawReminderEntry(
                fireName,
                startAt,
                longPeriod,
                string.Empty,
                overdue,
                ReminderPriority.High,
                MissedReminderAction.FireImmediately);

            await grain.UpsertRawReminderEntry(
                skipName,
                startAt,
                longPeriod,
                string.Empty,
                overdue,
                ReminderPriority.Normal,
                MissedReminderAction.Skip);

            await grain.UpsertRawReminderEntry(
                notifyName,
                startAt,
                longPeriod,
                string.Empty,
                overdue,
                ReminderPriority.Normal,
                MissedReminderAction.Notify);

            _ = await WaitForFirstTick(grain, fireName, WaitTimeout);
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            var fireTicks = await grain.GetTickRecords(fireName);
            var skipTicks = await grain.GetTickRecords(skipName);
            var notifyTicks = await grain.GetTickRecords(notifyName);

            Assert.True(fireTicks.Count >= 1);
            Assert.Empty(skipTicks);
            Assert.Empty(notifyTicks);

            var fireEntry = await grain.GetReminderEntry(fireName);
            var skipEntry = await grain.GetReminderEntry(skipName);
            var notifyEntry = await grain.GetReminderEntry(notifyName);

            Assert.NotNull(fireEntry.LastFireUtc);
            Assert.Null(skipEntry.LastFireUtc);
            Assert.Null(notifyEntry.LastFireUtc);

            Assert.NotNull(fireEntry.NextDueUtc);
            Assert.NotNull(skipEntry.NextDueUtc);
            Assert.NotNull(notifyEntry.NextDueUtc);

            Assert.True(fireEntry.NextDueUtc > now);
            Assert.True(skipEntry.NextDueUtc > now);
            Assert.True(notifyEntry.NextDueUtc > now);

            await grain.StopReminder(fireName);
            await grain.StopReminder(skipName);
            await grain.StopReminder(notifyName);
        }

        [Fact]
        public async Task Adaptive_PriorityAndBucketSize_ProcessesMixedPriorityReminders()
        {
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var dueAtUtc = DateTime.UtcNow.AddSeconds(5);
            var longPeriod = TimeSpan.FromMinutes(10);

            const string normalSecondaryName = "priority-normal-secondary";
            const string normalName = "priority-normal";
            const string highName = "priority-high";

            // Insert all rows with identical NextDueUtc to remove due-time skew and verify pure priority ordering.
            await grain.UpsertRawReminderEntry(normalSecondaryName, dueAtUtc, longPeriod, string.Empty, dueAtUtc, ReminderPriority.Normal, MissedReminderAction.FireImmediately);
            await grain.UpsertRawReminderEntry(normalName, dueAtUtc, longPeriod, string.Empty, dueAtUtc, ReminderPriority.Normal, MissedReminderAction.FireImmediately);
            await grain.UpsertRawReminderEntry(highName, dueAtUtc, longPeriod, string.Empty, dueAtUtc, ReminderPriority.High, MissedReminderAction.FireImmediately);

            var records = await WaitForRemindersToTick(
                grain,
                new[] { highName, normalName, normalSecondaryName },
                WaitTimeout);

            Assert.Contains(records, record => record.ReminderName == highName);
            Assert.Contains(records, record => record.ReminderName == normalName);
            Assert.Contains(records, record => record.ReminderName == normalSecondaryName);

            var highEntry = await grain.GetReminderEntry(highName);
            var normalEntry = await grain.GetReminderEntry(normalName);
            var normalSecondaryEntry = await grain.GetReminderEntry(normalSecondaryName);

            Assert.Equal(ReminderPriority.High, highEntry.Priority);
            Assert.Equal(ReminderPriority.Normal, normalEntry.Priority);
            Assert.Equal(ReminderPriority.Normal, normalSecondaryEntry.Priority);

            await grain.StopReminder(highName);
            await grain.StopReminder(normalName);
            await grain.StopReminder(normalSecondaryName);
        }

        [Fact]
        public async Task Adaptive_LookAheadWindow_QueuesReminderOnlyWhenEnteringWindow()
        {
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            const string reminderName = "lookahead";
            await grain.StartReminderWithOptions(
                reminderName,
                dueTime: TimeSpan.FromSeconds(5),
                period: TimeSpan.FromMinutes(10),
                priority: ReminderPriority.Normal,
                action: MissedReminderAction.FireImmediately);

            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Empty(await grain.GetTickRecords(reminderName));

            _ = await WaitForFirstTick(grain, reminderName, WaitTimeout);
            await grain.StopReminder(reminderName);
        }

        [Fact]
        public async Task Adaptive_MixedCronAndInterval_BothSchedulesTick()
        {
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            const string intervalName = "mixed-interval";
            const string cronName = "mixed-cron";

            await grain.StartReminderWithOptions(
                intervalName,
                dueTime: TimeSpan.FromMilliseconds(300),
                period: TimeSpan.FromMilliseconds(700),
                priority: ReminderPriority.Normal,
                action: MissedReminderAction.Skip);

            await grain.StartCronReminder(
                cronName,
                "*/1 * * * * *",
                priority: ReminderPriority.Normal,
                action: MissedReminderAction.Skip);

            await WaitForTickCount(grain, intervalName, expectedCount: 2, timeout: WaitTimeout);
            await WaitForTickCount(grain, cronName, expectedCount: 2, timeout: WaitTimeout);

            var intervalRecords = await grain.GetTickRecords(intervalName);
            var cronRecords = await grain.GetTickRecords(cronName);

            Assert.All(intervalRecords, record => Assert.Equal(ReminderScheduleKind.Interval, record.ScheduleKind));
            Assert.All(cronRecords, record => Assert.Equal(ReminderScheduleKind.Cron, record.ScheduleKind));

            await grain.StopReminder(intervalName);
            await grain.StopReminder(cronName);
        }

        [Fact]
        public async Task Adaptive_ManagementFilter_StatusAndPriority_ReturnsExpectedRows()
        {
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var management = this.GrainFactory.GetGrain<IReminderManagementGrain>(Guid.Empty);
            var now = DateTime.UtcNow;

            const string missedCritical = "mgmt-missed-critical";
            const string overdueNormal = "mgmt-overdue-normal";

            await grain.UpsertRawReminderEntry(
                missedCritical,
                now.AddMinutes(-10),
                TimeSpan.Zero,
                "*/1 * * * * *",
                now.AddMinutes(-3),
                ReminderPriority.High,
                MissedReminderAction.Skip);

            await grain.UpsertRawReminderEntry(
                overdueNormal,
                now.AddMinutes(-10),
                TimeSpan.FromMinutes(5),
                string.Empty,
                now.AddMinutes(-3),
                ReminderPriority.Normal,
                MissedReminderAction.Skip);

            var filter = new ReminderQueryFilter
            {
                Status = ReminderQueryStatus.Overdue | ReminderQueryStatus.Missed,
                OverdueBy = TimeSpan.FromSeconds(1),
                MissedBy = TimeSpan.FromSeconds(1),
                Priority = ReminderPriority.High,
            };

            var firstPage = await management.ListFilteredAsync(filter, pageSize: 10);
            Assert.Contains(firstPage.Reminders, reminder => reminder.ReminderName == missedCritical);
            Assert.DoesNotContain(firstPage.Reminders, reminder => reminder.ReminderName == overdueNormal);

            var iteratorNames = new List<string>();
            await foreach (var reminder in management.CreateIterator().EnumerateFilteredAsync(filter, pageSize: 1))
            {
                iteratorNames.Add(reminder.ReminderName);
            }

            Assert.Contains(missedCritical, iteratorNames);
            Assert.DoesNotContain(overdueNormal, iteratorNames);

            await grain.StopReminder(missedCritical);
            await grain.StopReminder(overdueNormal);
        }

        [Fact]
        public async Task Adaptive_ManagementCanUpdateAndDeleteReminder()
        {
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var management = this.GrainFactory.GetGrain<IReminderManagementGrain>(Guid.Empty);
            const string reminderName = "mgmt-update-delete";

            await grain.StartReminderWithOptions(
                reminderName,
                dueTime: TimeSpan.FromMilliseconds(300),
                period: TimeSpan.FromSeconds(5),
                priority: ReminderPriority.Normal,
                action: MissedReminderAction.Skip);

            var created = await grain.GetReminderEntry(reminderName);
            Assert.NotNull(created);

            await management.SetPriorityAsync(created.GrainId, reminderName, ReminderPriority.High);
            await management.SetActionAsync(created.GrainId, reminderName, MissedReminderAction.FireImmediately);

            var updated = await grain.GetReminderEntry(reminderName);
            Assert.NotNull(updated);
            Assert.Equal(ReminderPriority.High, updated.Priority);
            Assert.Equal(MissedReminderAction.FireImmediately, updated.Action);

            await management.DeleteAsync(created.GrainId, reminderName);

            var deleted = await grain.GetReminderEntry(reminderName);
            Assert.Null(deleted);
        }

        private static async Task<ReminderTickRecord> WaitForFirstTick(
            IReminderTestGrain2 grain,
            string reminderName,
            TimeSpan timeout)
        {
            var result = await WaitForTickCount(grain, reminderName, expectedCount: 1, timeout: timeout);
            return result[0];
        }

        private static async Task<List<ReminderTickRecord>> WaitForTickCount(
            IReminderTestGrain2 grain,
            string reminderName,
            int expectedCount,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            List<ReminderTickRecord> records = new();

            while (DateTime.UtcNow < deadline)
            {
                records = await grain.GetTickRecords(reminderName);
                if (records.Count >= expectedCount)
                {
                    return records;
                }

                await Task.Delay(PollStep);
            }

            Assert.True(records.Count >= expectedCount, $"Reminder '{reminderName}' did not reach {expectedCount} ticks. Current count: {records.Count}.");
            return records;
        }

        private static async Task<List<ReminderTickRecord>> WaitForRemindersToTick(
            IReminderTestGrain2 grain,
            IReadOnlyCollection<string> reminderNames,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            List<ReminderTickRecord> all = new();

            while (DateTime.UtcNow < deadline)
            {
                all = await grain.GetTickRecords();
                var reached = all
                    .Select(record => record.ReminderName)
                    .Distinct(StringComparer.Ordinal)
                    .Intersect(reminderNames, StringComparer.Ordinal)
                    .Count();

                if (reached == reminderNames.Count)
                {
                    return all;
                }

                await Task.Delay(PollStep);
            }

            var seen = all.Select(record => record.ReminderName).Distinct(StringComparer.Ordinal).ToArray();
            Assert.Fail($"Not all reminders ticked within timeout. Seen: {string.Join(", ", seen)}.");
            return all;
        }
    }
}
