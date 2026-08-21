#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NSubstitute;
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Timers;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;
using AdvancedReminderServiceInterface = Orleans.AdvancedReminders.IReminderService;
using IGrainReminder = Orleans.AdvancedReminders.IGrainReminder;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace UnitTests.AdvancedReminders;

internal static class AdvancedReminderTimeZoneTestHelper
{
    public static TimeZoneInfo GetDubaiTimeZone()
        => ResolveTimeZone("Asia/Dubai", "Arabian Standard Time");

    public static TimeZoneInfo GetUsEasternTimeZone()
        => ResolveTimeZone("America/New_York", "Eastern Standard Time");

    public static TimeZoneInfo GetCentralEuropeanTimeZone()
        => ResolveTimeZone("Europe/Berlin", "W. Europe Standard Time");

    public static TimeZoneInfo GetParisTimeZone()
        => ResolveTimeZone("Europe/Paris", "Romance Standard Time", "W. Europe Standard Time");

    public static TimeZoneInfo GetKyivTimeZone()
        => ResolveTimeZone("Europe/Kyiv", "FLE Standard Time", "E. Europe Standard Time");

    public static TimeZoneInfo GetIndiaTimeZone()
        => ResolveTimeZone("Asia/Kolkata", "India Standard Time");

    public static TimeZoneInfo GetNepalTimeZone()
        => ResolveTimeZone("Asia/Kathmandu", "Nepal Standard Time");

    public static TimeZoneInfo GetLordHoweTimeZone()
        => ResolveTimeZone("Australia/Lord_Howe", "Lord Howe Standard Time");

    public static DateTime ToUtc(TimeZoneInfo zone, int year, int month, int day, int hour, int minute, int second)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified), zone);

    public static string GetCentralEuropeanAlternateTimeZoneId()
    {
        var zone = GetCentralEuropeanTimeZone();
        return string.Equals(zone.Id, "Europe/Berlin", StringComparison.Ordinal)
            ? "W. Europe Standard Time"
            : "Europe/Berlin";
    }

    private static TimeZoneInfo ResolveTimeZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            if (TryFindTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        throw new InvalidOperationException($"Could not resolve any of the requested time zones: {string.Join(", ", ids)}.");
    }

    private static bool TryFindTimeZoneById(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
            {
                return TryFindTimeZoneById(windowsId, out zone);
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId))
            {
                return TryFindTimeZoneById(ianaId, out zone);
            }

            zone = null!;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            zone = null!;
            return false;
        }
    }
}

[TestCategory("Reminders")]
public class ReminderCronTests
{
    [Theory]
    [InlineData("@yearly", 2027, 1, 1, 0, 0, 0)]
    [InlineData("@monthly", 2026, 2, 1, 0, 0, 0)]
    [InlineData("@daily", 2026, 1, 16, 0, 0, 0)]
    [InlineData("@hourly", 2026, 1, 15, 11, 0, 0)]
    [InlineData("@every_minute", 2026, 1, 15, 10, 1, 0)]
    public void Parse_Macros_ComputeExpectedNextOccurrence(
        string macro,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var expression = ReminderCronExpression.Parse(macro);
        var fromUtc = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_AdvancedSyntax_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("0 9 15W * *");
        var fromUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 2, 16, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_ReversedRanges_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("55-5/5 * * * * *");
        var fromUtc = new DateTime(2026, 1, 1, 10, 0, 56, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData("not-a-cron")]
    [InlineData("* * * *")]
    [InlineData("* * * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("0 0 *W * *")]
    [InlineData("0 0 1-5W * *")]
    [InlineData("0 0 L-31 * *")]
    [InlineData("0 0 * FOO *")]
    [InlineData("0 0 * * 8L")]
    [InlineData("0 0 * * MON#0")]
    [InlineData("0 0 * * MON#6")]
    [InlineData("0 0 * * MONDAY")]
    [InlineData("H * * * *")]
    [InlineData("@unknown")]
    public void Parse_InvalidExpression_ThrowsFormatException(string expression)
    {
        Assert.ThrowsAny<FormatException>(() => ReminderCronExpression.Parse(expression));
    }

    [Fact]
    public void Parse_LargeMinuteList_ComputesExpectedNextOccurrence()
    {
        var minuteField = string.Join(",", Enumerable.Repeat("0", 4_096));
        var expression = ReminderCronExpression.Parse($"{minuteField} * * * *");
        var fromUtc = new DateTime(2026, 1, 1, 10, 0, 30, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void TryParse_InvalidExpression_ReturnsFalse()
    {
        var result = ReminderCronExpression.TryParse("not-a-cron", out var expression);

        Assert.False(result);
        Assert.Null(expression);
    }

    [Fact]
    public void GetOccurrences_ReturnsExpectedRange()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = expression.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(3, occurrences.Length);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), occurrences[0]);
        Assert.Equal(new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc), occurrences[1]);
        Assert.Equal(new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Utc), occurrences[2]);
    }
}

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
                DurableJobPriority.Normal,
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
            DurableJobPriority.Normal,
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
                DurableJobPriority.Normal,
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
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);
    }

    private static void AssertTypedTimeZoneBuilder(ReminderCronBuilder builder, string expectedExpression, TimeZoneInfo expectedZone)
    {
        Assert.Equal(expectedExpression, builder.ToExpressionString());
        Assert.Equal(expectedZone.Id, builder.TimeZone.Id);
    }
}

[TestCategory("Reminders")]
public class ReminderCronExpressionTimeZoneTests
{
    [Fact]
    public void GetNextOccurrence_WithTimeZone_UsesLocalScheduleAndReturnsUtc()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 6, 30, 0, DateTimeKind.Utc);
        var zone = AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanTimeZone();

        var next = expression.GetNextOccurrence(fromUtc, zone);

        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetOccurrences_WithUsEasternAcrossFallBack_PreservesNineAmLocal()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var zone = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var fromUtc = new DateTime(2025, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 11, 4, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = expression.GetOccurrences(fromUtc, toUtc, zone).ToArray();

        Assert.Equal(
            [
                new DateTime(2025, 10, 31, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 11, 1, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 11, 2, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 11, 3, 14, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Fact]
    public void GetNextOccurrence_WithUsEastern_WhenLocalTimeIsInvalid_MovesToNextValidInstant()
    {
        var expression = ReminderCronExpression.Parse("30 2 * * *");
        var zone = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var fromUtc = new DateTime(2025, 3, 8, 13, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc, zone);

        Assert.Equal(new DateTime(2025, 3, 9, 7, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetNextOccurrence_WithLeapDaySchedule_SkipsToNextLeapYear()
    {
        var expression = ReminderCronExpression.Parse("0 9 29 2 *");
        var fromUtc = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2028, 2, 29, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetNextOccurrence_WithTimeZone_ThrowsOnNullZone()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentNullException>(() => expression.GetNextOccurrence(fromUtc, zone: null!));
    }
}

[TestCategory("Reminders")]
public class ReminderCronExpressionFuzzTests
{
    [Fact]
    public void Fuzz_InternalCanonicalRoundTrip_PreservesSchedule()
    {
        var random = new Random(138_931);

        for (var i = 0; i < 300; i++)
        {
            var expressionText =
                $"{GenerateTimeField(random, 0, 59)} {GenerateTimeField(random, 0, 59)} {GenerateTimeField(random, 0, 23)} * * ?";

            var original = ReminderCronExpression.Parse(expressionText);
            var canonicalText = GetInternalCronExpressionText(original);
            var canonical = ReminderCronExpression.Parse(canonicalText);

            for (var j = 0; j < 10; j++)
            {
                var fromUtc = GenerateUtcInstant(random);
                var inclusive = random.Next(2) == 0;

                var expected = original.GetNextOccurrence(fromUtc, inclusive);
                var actual = canonical.GetNextOccurrence(fromUtc, inclusive);

                Assert.Equal(expected, actual);
            }
        }
    }

    private static string GenerateTimeField(Random random, int min, int max)
    {
        var mode = random.Next(6);
        return mode switch
        {
            0 => "*",
            1 => random.Next(min, max + 1).ToString(CultureInfo.InvariantCulture),
            2 => GenerateListField(random, min, max),
            3 => GenerateRangeField(random, min, max),
            4 => $"*/{random.Next(1, Math.Min(max - min + 1, 12) + 1).ToString(CultureInfo.InvariantCulture)}",
            _ => GenerateSteppedRangeField(random, min, max),
        };
    }

    private static string GenerateListField(Random random, int min, int max)
        => string.Join(",", Enumerable.Range(0, random.Next(2, 7)).Select(_ => random.Next(min, max + 1).ToString(CultureInfo.InvariantCulture)));

    private static string GenerateRangeField(Random random, int min, int max)
    {
        var left = random.Next(min, max + 1);
        var right = random.Next(min, max + 1);
        return $"{left.ToString(CultureInfo.InvariantCulture)}-{right.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string GenerateSteppedRangeField(Random random, int min, int max)
    {
        var left = random.Next(min, max + 1);
        var right = random.Next(min, max + 1);
        var step = random.Next(1, Math.Min(max - min + 1, 12) + 1);
        return $"{left.ToString(CultureInfo.InvariantCulture)}-{right.ToString(CultureInfo.InvariantCulture)}/{step.ToString(CultureInfo.InvariantCulture)}";
    }

    private static DateTime GenerateUtcInstant(Random random)
    {
        var year = random.Next(2024, 2028);
        var month = random.Next(1, 13);
        var day = random.Next(1, DateTime.DaysInMonth(year, month) + 1);
        var hour = random.Next(0, 24);
        var minute = random.Next(0, 60);
        var second = random.Next(0, 60);

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
    }

    private static string GetInternalCronExpressionText(ReminderCronExpression expression)
    {
        var field = typeof(ReminderCronExpression).GetField("_expression", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var internalExpression = field!.GetValue(expression);
        Assert.NotNull(internalExpression);

        return internalExpression!.ToString()!;
    }
}

[TestCategory("Reminders")]
public class ReminderEntryConversionTests
{
    [Fact]
    public void ReminderEntry_ToIGrainReminder_ExposesCronTimeZone()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "key"),
            ReminderName = "rem",
            ETag = "etag",
            CronExpression = "0 */5 * * * *",
            CronTimeZoneId = "America/New_York",
            Priority = DurableJobPriority.High,
            Action = MissedReminderAction.FireImmediately,
        };

        var reminder = entry.ToIGrainReminder();

        Assert.Equal(entry.ReminderName, reminder.ReminderName);
        Assert.Equal(entry.CronExpression, reminder.CronExpression);
        Assert.Equal(entry.CronTimeZoneId, reminder.CronTimeZone);
        Assert.Equal(entry.Priority, reminder.Priority);
        Assert.Equal(entry.Action, reminder.Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ReminderEntry_ToIGrainReminder_NormalizesMissingCronFields(string? cronValue)
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "key"),
            ReminderName = "rem",
            ETag = "etag",
            CronExpression = cronValue!,
            CronTimeZoneId = cronValue!,
        };

        var reminder = entry.ToIGrainReminder();

        Assert.Null(reminder.CronExpression);
        Assert.Null(reminder.CronTimeZone);
    }
}

[TestCategory("Reminders")]
public class ReminderCronComplexPatternTests
{
    [Fact]
    public void Parse_FullSupportedGrammar_ComputesExpectedNextOccurrences()
    {
        var cases = new (string Expression, DateTime FromUtc, DateTime ExpectedUtc)[]
        {
            ("15 10 * * *", Utc(2026, 1, 1, 10, 14, 30), Utc(2026, 1, 1, 10, 15, 0)),
            ("10,20,30 9 * * *", Utc(2026, 1, 1, 9, 15, 0), Utc(2026, 1, 1, 9, 20, 0)),
            ("10-12 9 * * *", Utc(2026, 1, 1, 9, 10, 0), Utc(2026, 1, 1, 9, 11, 0)),
            ("*/15 * * * *", Utc(2026, 1, 1, 10, 7, 0), Utc(2026, 1, 1, 10, 15, 0)),
            ("10/20 * * * *", Utc(2026, 1, 1, 10, 15, 0), Utc(2026, 1, 1, 10, 30, 0)),
            ("5-15/5 * * * *", Utc(2026, 1, 1, 10, 6, 0), Utc(2026, 1, 1, 10, 10, 0)),
            ("0 3,5-11/3,12 1 * * *", Utc(2026, 1, 1, 1, 4, 0), Utc(2026, 1, 1, 1, 5, 0)),
            ("*/20 * * * * *", Utc(2026, 1, 1, 10, 0, 1), Utc(2026, 1, 1, 10, 0, 20)),
            ("0 9 * JAN,MAR MON-FRI", Utc(2026, 1, 2, 10, 0, 0), Utc(2026, 1, 5, 9, 0, 0)),
            ("0 9 ? * MON", Utc(2026, 1, 2, 10, 0, 0), Utc(2026, 1, 5, 9, 0, 0)),
            ("0 9 LW * *", Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 1, 30, 9, 0, 0)),
            ("0 9 L-5W * *", Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 1, 26, 9, 0, 0)),
            ("0 9 13 * FRI", Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 2, 13, 9, 0, 0)),
            ("0 0 1 DEC-FEB *", Utc(2026, 12, 1, 0, 0, 0), Utc(2027, 1, 1, 0, 0, 0)),
            ("0 9 * * FRI-MON", Utc(2026, 1, 2, 10, 0, 0), Utc(2026, 1, 3, 9, 0, 0)),
            ("0 9 * * mon", Utc(2026, 1, 2, 10, 0, 0), Utc(2026, 1, 5, 9, 0, 0)),
            ("0 0 1-15/3 * *", Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 1, 4, 0, 0, 0)),
            ("0 0 1 */3 *", Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 4, 1, 0, 0, 0)),
            ("0 0 * * */2", Utc(2026, 1, 4, 0, 0, 0), Utc(2026, 1, 6, 0, 0, 0)),
            ("0 0 ? * 1/2", Utc(2026, 1, 5, 0, 0, 0), Utc(2026, 1, 7, 0, 0, 0)),
        };

        foreach (var (expressionText, fromUtc, expectedUtc) in cases)
        {
            var expression = ReminderCronExpression.Parse(expressionText);

            Assert.Equal(expectedUtc, expression.GetNextOccurrence(fromUtc));
        }

        static DateTime Utc(int year, int month, int day, int hour, int minute, int second)
            => new(year, month, day, hour, minute, second, DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("@weekly", 2026, 1, 18, 0, 0, 0)]
    [InlineData("@midnight", 2026, 1, 16, 0, 0, 0)]
    [InlineData("@every_second", 2026, 1, 15, 10, 0, 1)]
    [InlineData("@annually", 2027, 1, 1, 0, 0, 0)]
    public void Parse_AdditionalMacros_ComputeExpectedNextOccurrence(
        string macro,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var expression = ReminderCronExpression.Parse(macro);
        var fromUtc = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_LastDayOffset_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("0 9 L-3 * *");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 28, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_LastNamedWeekday_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("0 9 ? * FRIL");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 30, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_NthNamedWeekday_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("0 9 ? * MON#2");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 12, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_NearestWeekday_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("0 9 1W 6 *");
        var fromUtc = new DateTime(2025, 5, 31, 23, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2025, 6, 2, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_SecondsMonthListAndWeekdayRange_ComputesExpectedNextOccurrence()
    {
        var expression = ReminderCronExpression.Parse("15 30 9 ? JAN,MAR MON-FRI");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 9, 30, 15, DateTimeKind.Utc), next);
    }
}

[TestCategory("Reminders")]
public class ReminderCronBuilderTests
{
    [Fact]
    public void Builder_TypedFields_CoverBaseCronGrammar()
    {
        Assert.Equal("* * * * *", Five().ToExpressionString());

        Assert.Equal("10,30 * * * *", Five(minute: ReminderCronMinute.At(30, 10, 30)).ToExpressionString());
        Assert.Equal("55-5 * * * *", Five(minute: ReminderCronMinute.Range(55, 5)).ToExpressionString());
        Assert.Equal("*/5 * * * *", Five(minute: ReminderCronMinute.Every(5)).ToExpressionString());
        Assert.Equal("10/20 * * * *", Five(minute: ReminderCronMinute.EveryFrom(10, 20)).ToExpressionString());
        Assert.Equal("5-15/5 * * * *", Five(minute: ReminderCronMinute.EveryBetween(5, 15, 5)).ToExpressionString());
        Assert.Equal(
            "3,5-11/3,12 * * * *",
            Five(minute: ReminderCronMinute.Combine(
                ReminderCronMinute.At(3),
                ReminderCronMinute.EveryBetween(5, 11, 3),
                ReminderCronMinute.At(12))).ToExpressionString());

        Assert.Equal("10,30 * * * * *", Six(second: ReminderCronSecond.At(30, 10, 30)).ToExpressionString());
        Assert.Equal("55-5 * * * * *", Six(second: ReminderCronSecond.Range(55, 5)).ToExpressionString());
        Assert.Equal("*/5 * * * * *", Six(second: ReminderCronSecond.Every(5)).ToExpressionString());
        Assert.Equal("10/20 * * * * *", Six(second: ReminderCronSecond.EveryFrom(10, 20)).ToExpressionString());
        Assert.Equal("5-15/5 * * * * *", Six(second: ReminderCronSecond.EveryBetween(5, 15, 5)).ToExpressionString());
        Assert.Equal(
            "3,5-11/3,12 * * * * *",
            Six(second: ReminderCronSecond.Combine(
                ReminderCronSecond.At(3),
                ReminderCronSecond.EveryBetween(5, 11, 3),
                ReminderCronSecond.At(12))).ToExpressionString());

        Assert.Equal("* 6,14,16 * * *", Five(hour: ReminderCronHour.At(16, 6, 14)).ToExpressionString());
        Assert.Equal("* 22-1 * * *", Five(hour: ReminderCronHour.Range(22, 1)).ToExpressionString());
        Assert.Equal("* */4 * * *", Five(hour: ReminderCronHour.Every(4)).ToExpressionString());
        Assert.Equal("* 6/4 * * *", Five(hour: ReminderCronHour.EveryFrom(6, 4)).ToExpressionString());
        Assert.Equal("* 6-18/4 * * *", Five(hour: ReminderCronHour.EveryBetween(6, 18, 4)).ToExpressionString());
        Assert.Equal(
            "* 6,9-17/4,22 * * *",
            Five(hour: ReminderCronHour.Combine(
                ReminderCronHour.At(6),
                ReminderCronHour.EveryBetween(9, 17, 4),
                ReminderCronHour.At(22))).ToExpressionString());

        Assert.Equal("* * 1,15 * *", Five(dayOfMonth: ReminderCronDayOfMonth.On(15, 1)).ToExpressionString());
        Assert.Equal("* * 28-3 * *", Five(dayOfMonth: ReminderCronDayOfMonth.Range(28, 3)).ToExpressionString());
        Assert.Equal("* * */5 * *", Five(dayOfMonth: ReminderCronDayOfMonth.Every(5)).ToExpressionString());
        Assert.Equal("* * 3/5 * *", Five(dayOfMonth: ReminderCronDayOfMonth.EveryFrom(3, 5)).ToExpressionString());
        Assert.Equal("* * 3-18/5 * *", Five(dayOfMonth: ReminderCronDayOfMonth.EveryBetween(3, 18, 5)).ToExpressionString());
        Assert.Equal(
            "* * 1,5-15/5,20 * *",
            Five(dayOfMonth: ReminderCronDayOfMonth.Combine(
                ReminderCronDayOfMonth.On(1),
                ReminderCronDayOfMonth.EveryBetween(5, 15, 5),
                ReminderCronDayOfMonth.On(20))).ToExpressionString());

        Assert.Equal("* * * 1,3 *", Five(month: ReminderCronMonth.In(3, 1, 3)).ToExpressionString());
        Assert.Equal("* * * 12-2 *", Five(month: ReminderCronMonth.Range(12, 2)).ToExpressionString());
        Assert.Equal("* * * */3 *", Five(month: ReminderCronMonth.Every(3)).ToExpressionString());
        Assert.Equal("* * * 2/3 *", Five(month: ReminderCronMonth.EveryFrom(2, 3)).ToExpressionString());
        Assert.Equal("* * * 2-11/3 *", Five(month: ReminderCronMonth.EveryBetween(2, 11, 3)).ToExpressionString());
        Assert.Equal(
            "* * * 1,3-9/3,12 *",
            Five(month: ReminderCronMonth.Combine(
                ReminderCronMonth.In(1),
                ReminderCronMonth.EveryBetween(3, 9, 3),
                ReminderCronMonth.In(12))).ToExpressionString());

        Assert.Equal(
            "* * * * 1,3,5",
            Five(dayOfWeek: ReminderCronDayOfWeek.On(DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday)).ToExpressionString());
        Assert.Equal(
            "* * * * 5-1",
            Five(dayOfWeek: ReminderCronDayOfWeek.Range(DayOfWeek.Friday, DayOfWeek.Monday)).ToExpressionString());
        Assert.Equal("* * * * */2", Five(dayOfWeek: ReminderCronDayOfWeek.Every(2)).ToExpressionString());
        Assert.Equal(
            "* * * * 1/2",
            Five(dayOfWeek: ReminderCronDayOfWeek.EveryFrom(DayOfWeek.Monday, 2)).ToExpressionString());
        Assert.Equal(
            "* * * * 1-5/2",
            Five(dayOfWeek: ReminderCronDayOfWeek.EveryBetween(DayOfWeek.Monday, DayOfWeek.Friday, 2)).ToExpressionString());
        Assert.Equal(
            "* * * * 1,3-5,0",
            Five(dayOfWeek: ReminderCronDayOfWeek.Combine(
                ReminderCronDayOfWeek.On(DayOfWeek.Monday),
                ReminderCronDayOfWeek.Range(DayOfWeek.Wednesday, DayOfWeek.Friday),
                ReminderCronDayOfWeek.On(DayOfWeek.Sunday))).ToExpressionString());

        static ReminderCronBuilder Five(
            ReminderCronMinute? minute = null,
            ReminderCronHour? hour = null,
            ReminderCronDayOfMonth? dayOfMonth = null,
            ReminderCronMonth? month = null,
            ReminderCronDayOfWeek? dayOfWeek = null)
            => ReminderCronBuilder.FromFields(
                minute ?? ReminderCronMinute.Any,
                hour ?? ReminderCronHour.Any,
                dayOfMonth ?? ReminderCronDayOfMonth.Any,
                month ?? ReminderCronMonth.Any,
                dayOfWeek ?? ReminderCronDayOfWeek.Any);

        static ReminderCronBuilder Six(
            ReminderCronSecond? second = null,
            ReminderCronMinute? minute = null,
            ReminderCronHour? hour = null,
            ReminderCronDayOfMonth? dayOfMonth = null,
            ReminderCronMonth? month = null,
            ReminderCronDayOfWeek? dayOfWeek = null)
            => ReminderCronBuilder.FromFields(
                second ?? ReminderCronSecond.Any,
                minute ?? ReminderCronMinute.Any,
                hour ?? ReminderCronHour.Any,
                dayOfMonth ?? ReminderCronDayOfMonth.Any,
                month ?? ReminderCronMonth.Any,
                dayOfWeek ?? ReminderCronDayOfWeek.Any);
    }

    [Fact]
    public void Builder_TypedFields_CoverSpecialCronGrammar()
    {
        Assert.Equal("0 9 15W * *", Build(ReminderCronDayOfMonth.NearestWeekday(15)).ToExpressionString());
        Assert.Equal("0 9 L * *", Build(ReminderCronDayOfMonth.LastDay).ToExpressionString());
        Assert.Equal("0 9 L-3 * *", Build(ReminderCronDayOfMonth.DaysBeforeLast(3)).ToExpressionString());
        Assert.Equal("0 9 LW * *", Build(ReminderCronDayOfMonth.LastWeekday).ToExpressionString());
        Assert.Equal("0 9 L-5W * *", Build(ReminderCronDayOfMonth.NearestWeekdayBeforeLast(5)).ToExpressionString());
        Assert.Equal("0 9 * * 5L", Build(dayOfWeek: ReminderCronDayOfWeek.Last(DayOfWeek.Friday)).ToExpressionString());
        Assert.Equal("0 9 * * 1#2", Build(dayOfWeek: ReminderCronDayOfWeek.Nth(DayOfWeek.Monday, 2)).ToExpressionString());
        Assert.Equal(
            "0 9 13 * 5",
            Build(ReminderCronDayOfMonth.On(13), ReminderCronDayOfWeek.On(DayOfWeek.Friday)).ToExpressionString());

        static ReminderCronBuilder Build(
            ReminderCronDayOfMonth? dayOfMonth = null,
            ReminderCronDayOfWeek? dayOfWeek = null)
            => ReminderCronBuilder.FromFields(
                ReminderCronMinute.At(0),
                ReminderCronHour.At(9),
                dayOfMonth ?? ReminderCronDayOfMonth.Any,
                ReminderCronMonth.Any,
                dayOfWeek ?? ReminderCronDayOfWeek.Any);
    }

    [Fact]
    public void Builder_TypedFields_MatchRawExpressionsAcrossOneHundredOccurrences()
    {
        var cases = new (ReminderCronBuilder Builder, string RawExpression)[]
        {
            (ReminderCronBuilder.FromFields(
                ReminderCronSecond.Every(20),
                ReminderCronMinute.Any,
                ReminderCronHour.Any,
                ReminderCronDayOfMonth.Any,
                ReminderCronMonth.Any,
                ReminderCronDayOfWeek.Any), "*/20 * * * * *"),
            (ReminderCronBuilder.FromFields(
                ReminderCronMinute.Combine(
                    ReminderCronMinute.At(3),
                    ReminderCronMinute.EveryBetween(5, 11, 3),
                    ReminderCronMinute.At(12)),
                ReminderCronHour.At(1),
                ReminderCronDayOfMonth.Any,
                ReminderCronMonth.Any,
                ReminderCronDayOfWeek.Any), "3,5-11/3,12 1 * * *"),
            (ReminderCronBuilder.FromFields(
                ReminderCronMinute.At(0),
                ReminderCronHour.At(9),
                ReminderCronDayOfMonth.NearestWeekdayBeforeLast(5),
                ReminderCronMonth.Any,
                ReminderCronDayOfWeek.Any), "0 9 L-5W * *"),
            (ReminderCronBuilder.FromFields(
                ReminderCronMinute.At(0),
                ReminderCronHour.At(9),
                ReminderCronDayOfMonth.On(13),
                ReminderCronMonth.Any,
                ReminderCronDayOfWeek.On(DayOfWeek.Friday)), "0 9 13 * 5"),
        };
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var (builder, rawExpression) in cases)
        {
            var expected = ReminderCronBuilder.FromExpression(rawExpression)
                .GetOccurrences(fromUtc, toUtc)
                .Take(100)
                .ToArray();
            var actual = builder.GetOccurrences(fromUtc, toUtc).Take(100).ToArray();

            Assert.Equal(100, actual.Length);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Builder_TypedFields_RejectInvalidDefinitions()
    {
        Assert.Throws<ArgumentException>(() => ReminderCronMinute.At([]));
        Assert.Throws<ArgumentNullException>(() => ReminderCronMinute.At(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronSecond.At(60));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronMinute.Range(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronHour.Every(24));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfMonth.On(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfMonth.DaysBeforeLast(31));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfMonth.NearestWeekdayBeforeLast(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronMonth.In(13));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.On((DayOfWeek)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronDayOfWeek.Nth(DayOfWeek.Monday, 6));
        Assert.Throws<ArgumentException>(() => ReminderCronMinute.Combine([]));
        Assert.Throws<ArgumentException>(() => ReminderCronMinute.Combine(ReminderCronMinute.Any, ReminderCronMinute.At(5)));
        Assert.Throws<ArgumentException>(() => ReminderCronDayOfMonth.Combine(ReminderCronDayOfMonth.LastDay, ReminderCronDayOfMonth.On(15)));
        Assert.Throws<ArgumentNullException>(() => ReminderCronBuilder.FromFields(
            null!,
            ReminderCronHour.Any,
            ReminderCronDayOfMonth.Any,
            ReminderCronMonth.Any,
            ReminderCronDayOfWeek.Any));
    }

    [Fact]
    public void Builder_FactoryHelpers_EmitExpectedExpressions()
    {
        Assert.Equal("* * * * * *", ReminderCronBuilder.EverySecond().ToExpressionString());
        Assert.Equal("*/5 * * * * *", ReminderCronBuilder.EverySeconds(5).ToExpressionString());
        Assert.Equal("* * * * *", ReminderCronBuilder.EveryMinute().ToExpressionString());
        Assert.Equal("15 * * * *", ReminderCronBuilder.HourlyAt(15).ToExpressionString());
        Assert.Equal("10 15 * * * *", ReminderCronBuilder.HourlyAt(15, 10).ToExpressionString());
        Assert.Equal("0 9 * * *", ReminderCronBuilder.DailyAt(9, 0).ToExpressionString());
        Assert.Equal("15 30 9 * * *", ReminderCronBuilder.DailyAt(9, 30, 15).ToExpressionString());
        Assert.Equal("30 9 * * MON-FRI", ReminderCronBuilder.WeekdaysAt(9, 30).ToExpressionString());
        Assert.Equal("15 30 9 * * MON-FRI", ReminderCronBuilder.WeekdaysAt(9, 30, 15).ToExpressionString());
        Assert.Equal("30 9 * * SAT,SUN", ReminderCronBuilder.WeekendsAt(9, 30).ToExpressionString());
        Assert.Equal("5 4 * * 1", ReminderCronBuilder.WeeklyOn(DayOfWeek.Monday, 4, 5).ToExpressionString());
        Assert.Equal("6 5 4 * * 1", ReminderCronBuilder.WeeklyOn(DayOfWeek.Monday, 4, 5, 6).ToExpressionString());
        Assert.Equal("59 23 31 * *", ReminderCronBuilder.MonthlyOn(31, 23, 59).ToExpressionString());
        Assert.Equal("58 59 23 31 * *", ReminderCronBuilder.MonthlyOn(31, 23, 59, 58).ToExpressionString());
        Assert.Equal("59 23 L * *", ReminderCronBuilder.MonthlyOnLastDay(23, 59).ToExpressionString());
        Assert.Equal("58 59 23 L * *", ReminderCronBuilder.MonthlyOnLastDay(23, 59, 58).ToExpressionString());
        Assert.Equal("45 6 15 3 *", ReminderCronBuilder.YearlyOn(3, 15, 6, 45).ToExpressionString());
        Assert.Equal("30 45 6 15 3 *", ReminderCronBuilder.YearlyOn(3, 15, 6, 45, 30).ToExpressionString());
    }

    [Fact]
    public void Builder_AdvancedFactoryHelpers_EmitValidatedExpressions()
    {
        var builders = new (ReminderCronBuilder Builder, string Expression)[]
        {
            (ReminderCronBuilder.EveryMinutes(5), "*/5 * * * *"),
            (ReminderCronBuilder.EveryMinuteAtSecond(15), "15 * * * * *"),
            (ReminderCronBuilder.WeeklyOn(
                [DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Monday],
                new TimeOnly(9, 0)), "0 9 * * 1,3,5"),
            (ReminderCronBuilder.MonthlyOnNearestWeekday(1, new TimeOnly(9, 0)), "0 9 1W * *"),
            (ReminderCronBuilder.MonthlyBeforeLastDay(3, new TimeOnly(9, 0)), "0 9 L-3 * *"),
            (ReminderCronBuilder.MonthlyOnLast(DayOfWeek.Friday, new TimeOnly(9, 0)), "0 9 ? * 5L"),
            (ReminderCronBuilder.MonthlyOnNth(DayOfWeek.Monday, 2, new TimeOnly(9, 0)), "0 9 ? * 1#2"),
            (ReminderCronBuilder.WeekdaysInMonthsAt([3, 1, 3], new TimeOnly(9, 30, 15)), "15 30 9 ? 1,3 MON-FRI"),
        };

        foreach (var (builder, expression) in builders)
        {
            Assert.Equal(expression, builder.ToExpressionString());
            Assert.Equal(expression, builder.Build().ToExpressionString());
        }
    }

    [Fact]
    public void Builder_TimeOnlyAndTimeSpanHelpers_EmitExpectedExpressions()
    {
        Assert.Equal("15 * * * *", ReminderCronBuilder.HourlyAt(TimeSpan.FromMinutes(15)).ToExpressionString());
        Assert.Equal("30 9 * * *", ReminderCronBuilder.DailyAt(new TimeOnly(9, 30)).ToExpressionString());
        Assert.Equal("15 30 9 * * MON-FRI", ReminderCronBuilder.WeekdaysAt(new TimeSpan(9, 30, 15)).ToExpressionString());
        Assert.Equal("15 30 9 * * SAT,SUN", ReminderCronBuilder.WeekendsAt(new TimeSpan(9, 30, 15)).ToExpressionString());
        Assert.Equal("6 5 4 * * 2", ReminderCronBuilder.WeeklyOn(DayOfWeek.Tuesday, new TimeOnly(4, 5, 6)).ToExpressionString());
        Assert.Equal("59 23 31 * *", ReminderCronBuilder.MonthlyOn(31, TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59)).ToExpressionString());
        Assert.Equal("58 59 23 L * *", ReminderCronBuilder.MonthlyOnLastDay(new TimeOnly(23, 59, 58)).ToExpressionString());
        Assert.Equal("34 12 29 2 *", ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), new TimeOnly(12, 34)).ToExpressionString());
        Assert.Equal("34 12 29 2 *", ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), 12, 34).ToExpressionString());
        Assert.Equal("56 34 12 29 2 *", ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), 12, 34, 56).ToExpressionString());
    }

    [Fact]
    public void Builder_YearlyOn_DateOnly_IgnoresYear()
    {
        var first = ReminderCronBuilder.YearlyOn(new DateOnly(2024, 2, 29), new TimeOnly(12, 34));
        var second = ReminderCronBuilder.YearlyOn(new DateOnly(2032, 2, 29), new TimeOnly(12, 34));

        Assert.Equal(first.ToExpressionString(), second.ToExpressionString());
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, 0)]
    [InlineData(DayOfWeek.Monday, 1)]
    [InlineData(DayOfWeek.Tuesday, 2)]
    [InlineData(DayOfWeek.Wednesday, 3)]
    [InlineData(DayOfWeek.Thursday, 4)]
    [InlineData(DayOfWeek.Friday, 5)]
    [InlineData(DayOfWeek.Saturday, 6)]
    public void Builder_WeeklyOn_MapsDayOfWeekToCronValue(DayOfWeek dayOfWeek, int expectedCronDay)
    {
        var builder = ReminderCronBuilder.WeeklyOn(dayOfWeek, 4, 5);

        Assert.Equal($"5 4 * * {expectedCronDay}", builder.ToExpressionString());
    }

    [Fact]
    public void Builder_FromExpression_TrimsAndSupportsBuildAliases()
    {
        var builder = ReminderCronBuilder.FromExpression("  0 9 * * *  ");

        Assert.Equal("0 9 * * *", builder.ToExpressionString());
        Assert.Equal(TimeZoneInfo.Utc.Id, builder.TimeZone.Id);
        Assert.Equal("0 9 * * *", builder.ToCronExpression().ToExpressionString());
        Assert.Equal("0 9 * * *", builder.Build().ToExpressionString());
    }

    [Fact]
    public void Builder_FromExpression_WithUtcZone_UsesUtcBranchForNextOccurrence()
    {
        var builder = ReminderCronBuilder.FromExpression("0 9 * * *", TimeZoneInfo.Utc);
        var fromUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Builder_FromExpression_WithUtcZone_UsesUtcBranchForOccurrences()
    {
        var builder = ReminderCronBuilder.FromExpression("0 9 * * *", TimeZoneInfo.Utc);
        var fromUtc = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc, fromInclusive: false, toInclusive: true).ToArray();

        Assert.Equal(
            [
                new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    public void Builder_HourlyAt_InvalidMinute_Throws(int minute)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.HourlyAt(minute));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(24, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 60)]
    public void Builder_DailyAt_InvalidClockValues_Throws(int hour, int minute)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(hour, minute));
    }

    [Fact]
    public void Builder_HourlyAt_InvalidOffset_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.HourlyAt(TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.HourlyAt(TimeSpan.FromMilliseconds(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void Builder_EverySeconds_InvalidInterval_Throws(int interval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.EverySeconds(interval));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void Builder_EveryMinutes_InvalidInterval_Throws(int interval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.EveryMinutes(interval));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    public void Builder_EveryMinuteAtSecond_InvalidSecond_Throws(int second)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.EveryMinuteAtSecond(second));
    }

    [Fact]
    public void Builder_WeeklyOnMultipleDays_RequiresValidNonEmptyDays()
    {
        Assert.Throws<ArgumentNullException>(() => ReminderCronBuilder.WeeklyOn(null!, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentException>(() => ReminderCronBuilder.WeeklyOn([], new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeeklyOn([(DayOfWeek)99], new TimeOnly(9, 0)));
    }

    [Fact]
    public void Builder_AdvancedMonthlyHelpers_RejectInvalidCalendarValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOnNearestWeekday(0, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyBeforeLastDay(0, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyBeforeLastDay(31, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOnLast((DayOfWeek)99, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOnNth(DayOfWeek.Monday, 0, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOnNth(DayOfWeek.Monday, 6, new TimeOnly(9, 0)));
    }

    [Fact]
    public void Builder_WeekdaysInMonthsAt_RequiresValidNonEmptyMonths()
    {
        Assert.Throws<ArgumentNullException>(() => ReminderCronBuilder.WeekdaysInMonthsAt(null!, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentException>(() => ReminderCronBuilder.WeekdaysInMonthsAt([], new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeekdaysInMonthsAt([0], new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeekdaysInMonthsAt([13], new TimeOnly(9, 0)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    public void Builder_SecondBasedHelpers_InvalidSecond_Throws(int second)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.HourlyAt(0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeekdaysAt(0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeekendsAt(0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeeklyOn(DayOfWeek.Monday, 0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOn(1, 0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOnLastDay(0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.YearlyOn(1, 1, 0, 0, second));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.YearlyOn(new DateOnly(2024, 1, 1), 0, 0, second));
    }

    [Fact]
    public void Builder_DailyAt_InvalidTimeOnlyOrTimeSpan_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(new TimeOnly(9, 0, 0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.DailyAt(TimeSpan.FromMilliseconds(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void Builder_MonthlyOn_InvalidDayOfMonth_Throws(int dayOfMonth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.MonthlyOn(dayOfMonth, 0, 0));
    }

    [Fact]
    public void Builder_WeeklyOn_InvalidDayOfWeek_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.WeeklyOn((DayOfWeek)99, 0, 0));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(13, 1)]
    [InlineData(4, 31)]
    public void Builder_YearlyOn_InvalidMonthOrDay_Throws(int month, int dayOfMonth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderCronBuilder.YearlyOn(month, dayOfMonth, 0, 0));
    }

    [Fact]
    public void Builder_InTimeZone_WithUnknownId_Throws()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0);

        Assert.Throws<TimeZoneNotFoundException>(() => builder.InTimeZone("Definitely/Not-A-TimeZone"));
    }
}

[TestCategory("Reminders")]
public class ReminderCronExpressionBehaviorTests
{
    [Fact]
    public void TryParse_BlankExpression_ReturnsFalse()
    {
        var result = ReminderCronExpression.TryParse("   ", out var expression);

        Assert.False(result);
        Assert.Null(expression);
    }

    [Fact]
    public void FromValidatedString_PreservesExpressionText()
    {
        var expression = ReminderCronExpression.FromValidatedString("0 9 * * *");

        Assert.Equal("0 9 * * *", expression.ExpressionText);
        Assert.Equal("0 9 * * *", expression.ToExpressionString());
        Assert.Equal("0 9 * * *", expression.ToString());
    }

    [Fact]
    public void Equality_UsesOrdinalExpressionText()
    {
        var first = ReminderCronExpression.Parse("0 9 * * *");
        var second = ReminderCronExpression.Parse("0 9 * * *");
        var different = ReminderCronExpression.Parse("0 10 * * *");

        Assert.True(first.Equals(second));
        Assert.True(first.Equals((object)second));
        Assert.False(first.Equals(different));
        Assert.False(first.Equals((object)"0 9 * * *"));
        Assert.False(first.Equals(null));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void GetNextOccurrence_WithNonUtcDateTime_Throws()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var local = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

        Assert.Throws<ArgumentException>(() => expression.GetNextOccurrence(local));
    }

    [Fact]
    public void GetNextOccurrence_AfterMaximumDate_ReturnsNull()
    {
        var expression = ReminderCronExpression.Parse("* * * * *");

        Assert.Null(expression.GetNextOccurrence(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)));
    }

    [Fact]
    public void GetOccurrences_WithNonUtcRange_Throws()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Local);

        Assert.Throws<ArgumentException>(() => expression.GetOccurrences(from, to).ToArray());
    }
}

[TestCategory("Reminders")]
public class ReminderCronScheduleTests
{
    [Fact]
    public void Schedule_Parse_WithoutTimeZone_DefaultsToUtc()
    {
        var schedule = ReminderCronSchedule.Parse("0 9 * * *");
        var next = schedule.GetNextOccurrence(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));

        Assert.Equal(TimeZoneInfo.Utc.Id, schedule.TimeZone.Id);
        Assert.Null(schedule.TimeZoneId);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Schedule_Parse_WithExpressionAndZone_NormalizesStorageIdAndUsesLocalSchedule()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetNepalTimeZone();
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var schedule = ReminderCronSchedule.Parse(expression, zone);
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = schedule.GetNextOccurrence(fromUtc);
        var occurrences = schedule.GetOccurrences(fromUtc, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)).ToArray();

        Assert.Equal(ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone), schedule.TimeZoneId);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 1, 9, 0, 0), next);
        Assert.Equal(
            [
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 1, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 2, 9, 0, 0),
            ],
            occurrences);
    }

    [Fact]
    public void Schedule_NormalizeTimeZoneIdForStorage_ReturnsNullForUtcAndNull()
    {
        Assert.Null(ReminderCronSchedule.NormalizeTimeZoneIdForStorage(null));
        Assert.Null(ReminderCronSchedule.NormalizeTimeZoneIdForStorage(TimeZoneInfo.Utc));
    }

    [Fact]
    public void Schedule_NormalizeTimeZoneIdForStorage_RejectsCustomRulesWhichCannotBeRestored()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Orleans/Test-Custom-Zone",
            TimeSpan.FromHours(3),
            "Orleans custom zone",
            "Orleans custom zone");

        var exception = Assert.Throws<ArgumentException>(() => ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone));

        Assert.Contains("cannot be stored", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schedule_Parse_WithUnknownTimeZone_ThrowsCronFormatException()
    {
        var exception = Assert.Throws<CronFormatException>(() => ReminderCronSchedule.Parse("0 9 * * *", "Definitely/Not-A-TimeZone"));

        Assert.Contains("Unknown time zone id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schedule_Parse_WithAlternatePlatformTimeZoneId_UsesEquivalentZone()
    {
        var zoneId = AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanAlternateTimeZoneId();
        var expectedZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanTimeZone());
        var schedule = ReminderCronSchedule.Parse("0 9 * * *", zoneId);

        Assert.Equal(expectedZoneId, schedule.TimeZoneId);
    }

    [Fact]
    public void Schedule_Parse_ReusesCachedScheduleForEquivalentInputs()
    {
        var first = ReminderCronSchedule.Parse(" 0 9 * * * ", " Europe/Berlin ");
        var second = ReminderCronSchedule.Parse("0 9 * * *", "Europe/Berlin");

        Assert.Same(first, second);
    }

    [Fact]
    public void Schedule_Parse_BoundsTheProcessWideCache()
    {
        for (var index = 1; index <= ReminderCronSchedule.MaxCacheEntries + 100; index++)
        {
            _ = ReminderCronSchedule.Parse($"0{new string(' ', index)}9 * * *");
        }

        Assert.InRange(ReminderCronSchedule.CacheCount, 1, ReminderCronSchedule.MaxCacheEntries);
    }
}

[TestCategory("Reminders")]
public class ReminderCronTimeZoneEdgeCaseTests
{
    [Fact]
    public void Builder_WithNepalTimeZone_PreservesQuarterHourOffset()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetNepalTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(zone);
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 1, 9, 0, 0), next);
    }

    [Fact]
    public void Builder_WithKyivTimeZoneOverload_AcrossEuropeanSpringForward_PreservesNineAmLocal()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetKyivTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0, zone);
        var fromUtc = new DateTime(2025, 3, 28, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 4, 2, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 28, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 29, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 30, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 31, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 4, 1, 9, 0, 0),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_WithLordHoweAcrossDstTransition_PreservesNineAmLocal()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetLordHoweTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(zone);
        var fromUtc = new DateTime(2025, 4, 4, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 4, 8, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 4, 5, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 4, 6, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 4, 7, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 4, 8, 9, 0, 0),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_WithIndiaTimeZone_AcrossNewYear_PreservesLocalMidnightSchedule()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetIndiaTimeZone();
        var builder = ReminderCronBuilder.DailyAt(0, 15).InTimeZone(zone);
        var fromUtc = new DateTime(2025, 12, 31, 18, 0, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 1, 0, 15, 0), next);
    }

    [Fact]
    public void Builder_WithDubaiTimeZoneOverload_DoesNotShiftAcrossDstWindows()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetDubaiTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0, zone);
        var fromUtc = new DateTime(2025, 3, 7, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 3, 12, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 7, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 8, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 9, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 10, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 11, 9, 0, 0),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_WithKyivAndNewYork_WhenUsAlreadyOnDst_ReturnExpectedUtcOffsets()
    {
        var kyiv = AdvancedReminderTimeZoneTestHelper.GetKyivTimeZone();
        var newYork = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var kyivBuilder = ReminderCronBuilder.DailyAt(9, 0, kyiv);
        var newYorkBuilder = ReminderCronBuilder.DailyAt(9, 0, newYork);
        var fromUtc = new DateTime(2025, 3, 10, 0, 0, 0, DateTimeKind.Utc);

        var kyivNext = kyivBuilder.GetNextOccurrence(fromUtc);
        var newYorkNext = newYorkBuilder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2025, 3, 10, 7, 0, 0, DateTimeKind.Utc), kyivNext);
        Assert.Equal(new DateTime(2025, 3, 10, 13, 0, 0, DateTimeKind.Utc), newYorkNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(kyiv, 2025, 3, 10, 9, 0, 0), kyivNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(newYork, 2025, 3, 10, 9, 0, 0), newYorkNext);
    }

    [Fact]
    public void Builder_WithParisAndNewYork_WhenEuropeAlreadyStandardButUsStillOnDst_ReturnExpectedUtcOffsets()
    {
        var paris = AdvancedReminderTimeZoneTestHelper.GetParisTimeZone();
        var newYork = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var parisBuilder = ReminderCronBuilder.DailyAt(9, 0, paris);
        var newYorkBuilder = ReminderCronBuilder.DailyAt(9, 0, newYork);
        var fromUtc = new DateTime(2025, 10, 27, 0, 0, 0, DateTimeKind.Utc);

        var parisNext = parisBuilder.GetNextOccurrence(fromUtc);
        var newYorkNext = newYorkBuilder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2025, 10, 27, 8, 0, 0, DateTimeKind.Utc), parisNext);
        Assert.Equal(new DateTime(2025, 10, 27, 13, 0, 0, DateTimeKind.Utc), newYorkNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(paris, 2025, 10, 27, 9, 0, 0), parisNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(newYork, 2025, 10, 27, 9, 0, 0), newYorkNext);
    }

    [Fact]
    public void Builder_WithDubaiAndNewYork_WhenUsAlreadyOnDst_DubaiRemainsFixed()
    {
        var dubai = AdvancedReminderTimeZoneTestHelper.GetDubaiTimeZone();
        var newYork = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var dubaiBuilder = ReminderCronBuilder.DailyAt(9, 0, dubai);
        var newYorkBuilder = ReminderCronBuilder.DailyAt(9, 0, newYork);
        var fromUtc = new DateTime(2025, 3, 10, 0, 0, 0, DateTimeKind.Utc);

        var dubaiNext = dubaiBuilder.GetNextOccurrence(fromUtc);
        var newYorkNext = newYorkBuilder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2025, 3, 10, 5, 0, 0, DateTimeKind.Utc), dubaiNext);
        Assert.Equal(new DateTime(2025, 3, 10, 13, 0, 0, DateTimeKind.Utc), newYorkNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(dubai, 2025, 3, 10, 9, 0, 0), dubaiNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(newYork, 2025, 3, 10, 9, 0, 0), newYorkNext);
    }

    [Fact]
    public void Builder_WithIndiaAndParis_WhenEuropeAlreadyStandard_IndiaRemainsFixed()
    {
        var india = AdvancedReminderTimeZoneTestHelper.GetIndiaTimeZone();
        var paris = AdvancedReminderTimeZoneTestHelper.GetParisTimeZone();
        var indiaBuilder = ReminderCronBuilder.DailyAt(9, 0, india);
        var parisBuilder = ReminderCronBuilder.DailyAt(9, 0, paris);
        var fromUtc = new DateTime(2025, 10, 27, 0, 0, 0, DateTimeKind.Utc);

        var indiaNext = indiaBuilder.GetNextOccurrence(fromUtc);
        var parisNext = parisBuilder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2025, 10, 27, 3, 30, 0, DateTimeKind.Utc), indiaNext);
        Assert.Equal(new DateTime(2025, 10, 27, 8, 0, 0, DateTimeKind.Utc), parisNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(india, 2025, 10, 27, 9, 0, 0), indiaNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(paris, 2025, 10, 27, 9, 0, 0), parisNext);
    }

    [Fact]
    public void Schedule_WithNepalTimeZone_AcrossNewYear_PreservesQuarterHourOffset()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetNepalTimeZone();
        var schedule = ReminderCronSchedule.Parse("0 9 * * *", ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone));
        var fromUtc = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = schedule.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 12, 31, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 1, 9, 0, 0),
            ],
            occurrences);
    }

    [Fact]
    public void TimeZoneHelper_InvalidAndAmbiguousTransitions_ReturnExpectedBoundaries()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var invalidLocal = new DateTime(2025, 3, 9, 2, 30, 0, DateTimeKind.Unspecified);
        var ambiguousLocal = new DateTime(2025, 11, 2, 1, 30, 0, DateTimeKind.Unspecified);

        var daylightStart = TimeZoneHelper.GetDaylightTimeStart(zone, invalidLocal);
        var daylightOffset = TimeZoneHelper.GetDaylightOffset(zone, ambiguousLocal);
        var daylightEnd = TimeZoneHelper.GetDaylightTimeEnd(zone, ambiguousLocal, daylightOffset);
        var standardStart = TimeZoneHelper.GetStandardTimeStart(zone, ambiguousLocal, daylightOffset);
        var intervalEnd = TimeZoneHelper.GetAmbiguousIntervalEnd(zone, ambiguousLocal);

        Assert.True(TimeZoneHelper.IsAmbiguousTime(zone, ambiguousLocal));
        Assert.Equal(zone.GetAmbiguousTimeOffsets(ambiguousLocal).Max(), daylightOffset);
        Assert.Equal(new DateTime(2025, 3, 9, 7, 0, 0, DateTimeKind.Utc), daylightStart.UtcDateTime);
        Assert.Equal(new DateTime(2025, 11, 2, 5, 59, 59, DateTimeKind.Utc).AddTicks(9999999), daylightEnd.UtcDateTime);
        Assert.Equal(new DateTime(2025, 11, 2, 6, 0, 0, DateTimeKind.Utc), standardStart.UtcDateTime);
        Assert.Equal(new DateTime(2025, 11, 2, 7, 0, 0, DateTimeKind.Utc), intervalEnd.UtcDateTime);
    }
}

[TestCategory("Reminders")]
public class CalendarHelperTests
{
    [Fact]
    public void MoveToNearestWeekDay_HandlesWeekendEdgesAndWeekdays()
    {
        Assert.Equal(2, CalendarHelper.MoveToNearestWeekDay(2025, 6, 1));
        Assert.Equal(29, CalendarHelper.MoveToNearestWeekDay(2024, 3, 31));
        Assert.Equal(3, CalendarHelper.MoveToNearestWeekDay(2025, 2, 1));
        Assert.Equal(14, CalendarHelper.MoveToNearestWeekDay(2025, 1, 14));
    }

    [Fact]
    public void CalendarHelper_FillDateTimeParts_AndWeekdayPredicates_WorkAsExpected()
    {
        var ticks = new DateTime(2026, 1, 1, 10, 11, 12, DateTimeKind.Utc).AddTicks(42).Ticks;

        CalendarHelper.FillDateTimeParts(ticks, out var second, out var minute, out var hour, out var day, out var month, out var year);

        Assert.Equal(13, second);
        Assert.Equal(11, minute);
        Assert.Equal(10, hour);
        Assert.Equal(1, day);
        Assert.Equal(1, month);
        Assert.Equal(2026, year);
        Assert.True(CalendarHelper.IsNthDayOfWeek(8, 2));
        Assert.False(CalendarHelper.IsNthDayOfWeek(15, 2));
        Assert.True(CalendarHelper.IsLastDayOfWeek(2025, 1, 31));
        Assert.False(CalendarHelper.IsLastDayOfWeek(2025, 1, 24));
        Assert.Equal(DayOfWeek.Thursday, CalendarHelper.GetDayOfWeek(2026, 1, 1));
        Assert.Equal(29, CalendarHelper.GetDaysInMonth(2024, 2));
        Assert.True(CalendarHelper.IsGreaterThan(2026, 1, 2, 2026, 1, 1));
        Assert.False(CalendarHelper.IsGreaterThan(2026, 1, 1, 2026, 1, 2));
        Assert.Equal(new DateTime(2026, 1, 1, 10, 11, 12, DateTimeKind.Utc).Ticks, CalendarHelper.DateTimeToTicks(2026, 1, 1, 10, 11, 12));
    }
}
