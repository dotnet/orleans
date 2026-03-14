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

    public static TimeZoneInfo GetIndiaTimeZone()
        => ResolveTimeZone("Asia/Kolkata", "India Standard Time");

    public static TimeZoneInfo GetNepalTimeZone()
        => ResolveTimeZone("Asia/Kathmandu", "Nepal Standard Time");

    public static TimeZoneInfo GetLordHoweTimeZone()
        => ResolveTimeZone("Australia/Lord_Howe", "Lord Howe Standard Time");

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
    [InlineData("@unknown")]
    public void Parse_InvalidExpression_ThrowsFormatException(string expression)
    {
        Assert.ThrowsAny<FormatException>(() => ReminderCronExpression.Parse(expression));
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
                ReminderPriority.Normal,
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
            ReminderPriority.Normal,
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
                ReminderPriority.Normal,
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
            ReminderPriority.Normal,
            MissedReminderAction.Skip);
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
            Priority = ReminderPriority.High,
            Action = MissedReminderAction.FireImmediately,
        };

        var reminder = entry.ToIGrainReminder();

        Assert.Equal(entry.ReminderName, reminder.ReminderName);
        Assert.Equal(entry.CronExpression, reminder.CronExpression);
        Assert.Equal(entry.CronTimeZoneId, reminder.CronTimeZone);
        Assert.Equal(entry.Priority, reminder.Priority);
        Assert.Equal(entry.Action, reminder.Action);
    }

    [Fact]
    public void ReminderEntry_ToIGrainReminder_NormalizesNullCronFields()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "key"),
            ReminderName = "rem",
            ETag = "etag",
            CronExpression = null!,
            CronTimeZoneId = null!,
        };

        var reminder = entry.ToIGrainReminder();

        Assert.Equal(string.Empty, reminder.CronExpression);
        Assert.Equal(string.Empty, reminder.CronTimeZone);
    }
}
