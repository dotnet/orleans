#nullable enable
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Timers;
using Xunit;
using AdvancedReminderServiceInterface = Orleans.AdvancedReminders.IReminderService;
using IGrainReminder = Orleans.AdvancedReminders.IGrainReminder;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronBuilderTimeZoneTests
{
    [Fact]
    public void Builder_DefaultTimeZone_IsUtc()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0);

        Assert.Equal(TimeZoneInfo.Utc.Id, builder.TimeZone.Id);
    }

    [Fact]
    public void Builder_TimeZoneOverloads_ApplyTypedZone_ForCoreHelpers()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetDubaiTimeZone();

        AssertTypedTimeZoneBuilder(ReminderCronBuilder.EveryMinute(zone), "* * * * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.HourlyAt(15, zone), "15 * * * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.HourlyAt(15, 10, zone), "10 15 * * * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.HourlyAt(TimeSpan.FromMinutes(15), zone), "15 * * * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.DailyAt(9, 30, zone), "30 9 * * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.DailyAt(9, 30, 15, zone), "15 30 9 * * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.DailyAt(new TimeOnly(9, 30), zone), "30 9 * * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.DailyAt(new TimeSpan(9, 30, 15), zone), "15 30 9 * * *", zone);
    }

    [Fact]
    public void Builder_TimeZoneOverloads_ApplyTypedZone_ForCalendarHelpers()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetKyivTimeZone();

        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeekdaysAt(9, 30, zone), "30 9 * * MON-FRI", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeekdaysAt(9, 30, 15, zone), "15 30 9 * * MON-FRI", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeekdaysAt(new TimeOnly(9, 30), zone), "30 9 * * MON-FRI", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeekdaysAt(new TimeSpan(9, 30, 15), zone), "15 30 9 * * MON-FRI", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeekendsAt(9, 30, zone), "30 9 * * SAT,SUN", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeekendsAt(9, 30, 15, zone), "15 30 9 * * SAT,SUN", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeekendsAt(new TimeOnly(9, 30), zone), "30 9 * * SAT,SUN", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeekendsAt(new TimeSpan(9, 30, 15), zone), "15 30 9 * * SAT,SUN", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeeklyOn(DayOfWeek.Monday, 4, 5, zone), "5 4 * * 1", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeeklyOn(DayOfWeek.Monday, 4, 5, 6, zone), "6 5 4 * * 1", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeeklyOn(DayOfWeek.Tuesday, new TimeOnly(4, 5, 6), zone), "6 5 4 * * 2", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.WeeklyOn(DayOfWeek.Tuesday, new TimeSpan(4, 5, 6), zone), "6 5 4 * * 2", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.MonthlyOn(31, 23, 59, zone), "59 23 31 * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.MonthlyOn(31, 23, 59, 58, zone), "58 59 23 31 * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.MonthlyOn(31, new TimeOnly(23, 59, 58), zone), "58 59 23 31 * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.MonthlyOn(31, new TimeSpan(23, 59, 58), zone), "58 59 23 31 * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.MonthlyOnLastDay(23, 59, zone), "59 23 L * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.MonthlyOnLastDay(23, 59, 58, zone), "58 59 23 L * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.MonthlyOnLastDay(new TimeOnly(23, 59, 58), zone), "58 59 23 L * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.MonthlyOnLastDay(new TimeSpan(23, 59, 58), zone), "58 59 23 L * *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.YearlyOn(3, 15, 6, 45, zone), "45 6 15 3 *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.YearlyOn(3, 15, 6, 45, 30, zone), "30 45 6 15 3 *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.YearlyOn(3, 15, new TimeOnly(6, 45, 30), zone), "30 45 6 15 3 *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.YearlyOn(3, 15, new TimeSpan(6, 45, 30), zone), "30 45 6 15 3 *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), 12, 34, zone), "34 12 29 2 *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), 12, 34, 56, zone), "56 34 12 29 2 *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), new TimeOnly(12, 34, 56), zone), "56 34 12 29 2 *", zone);
        AssertTypedTimeZoneBuilder(ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), new TimeSpan(12, 34, 56), zone), "56 34 12 29 2 *", zone);
    }

    [Fact]
    public void Builder_InTimeZone_WithTimeZoneInfo_UsesLocalScheduleAndReturnsUtc()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanTimeZone());
        var fromUtc = new DateTime(2026, 1, 1, 6, 30, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Builder_InTimeZone_WithUsEasternAcrossSpringForward_PreservesNineAmLocal()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone());
        var fromUtc = new DateTime(2025, 3, 7, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 3, 12, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                new DateTime(2025, 3, 7, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 8, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 9, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 10, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 11, 13, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_InTimeZone_WithAlternatePlatformId_UsesEquivalentZone()
    {
        var alternateZoneId = AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanAlternateTimeZoneId();
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(alternateZoneId);
        var fromUtc = new DateTime(2026, 1, 1, 7, 30, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public async Task RegistryRegistrationExtensions_WithNonUtcBuilder_DelegatesEncodedSchedule()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "non-utc-builder-registry");
        var reminder = Substitute.For<IGrainReminder>();
        var zone = AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(zone);
        var expectedTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone);
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", builder);

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "0 9 * * *"
                && schedule.CronTimeZoneId == expectedTimeZoneId),
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task ServiceRegistrationExtensions_WithNonUtcBuilder_DelegatesEncodedSchedule()
    {
        var service = Substitute.For<AdvancedReminderServiceInterface>();
        var grainId = GrainId.Create("test", "non-utc-builder-service");
        var reminder = Substitute.For<IGrainReminder>();
        var zone = AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(zone);
        var expectedTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone);
        service.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var result = await service.RegisterOrUpdateReminder(grainId, "r", builder);

        Assert.Same(reminder, result);
        _ = service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "0 9 * * *"
                && schedule.CronTimeZoneId == expectedTimeZoneId),
            MissedReminderAction.Skip);
    }

    private static void AssertTypedTimeZoneBuilder(ReminderCronBuilder builder, string expectedExpression, TimeZoneInfo expectedZone)
    {
        Assert.Equal(expectedExpression, builder.ToExpressionString());
        Assert.Equal(expectedZone.Id, builder.TimeZone.Id);
    }
}
