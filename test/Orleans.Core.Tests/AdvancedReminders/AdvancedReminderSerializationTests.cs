using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using TestExtensions;
using Xunit;
using ReminderData = Orleans.AdvancedReminders.ReminderData;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;
using TickStatus = Orleans.AdvancedReminders.Runtime.TickStatus;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class AdvancedReminderSerializationTests
{
    [Fact]
    public void ReminderException_PreservesMessageWithoutFormatterSerialization()
    {
        using var environment = SerializationTestEnvironment.InitializeWithDefaults();
        var original = new Orleans.AdvancedReminders.Runtime.ReminderException("Storage is unavailable.");
        var copy = environment.Serializer.Deserialize<Orleans.AdvancedReminders.Runtime.ReminderException>(
            environment.Serializer.SerializeToArray(original));

        Assert.NotNull(copy);
        Assert.Equal(original.Message, copy.Message);
    }

    [Fact]
    public void EntryAndPages_PreserveEveryFieldAndContinuationToken()
    {
        using var environment = SerializationTestEnvironment.InitializeWithDefaults();
        var entry = CreateEntry();
        var table = new ReminderTableData([entry], "table-next");
        var page = new ReminderManagementPage { Reminders = [entry], ContinuationToken = "management-next" };

        var tableCopy = environment.Serializer.Deserialize<ReminderTableData>(environment.Serializer.SerializeToArray(table));
        var pageCopy = environment.Serializer.Deserialize<ReminderManagementPage>(environment.Serializer.SerializeToArray(page));

        Assert.NotNull(tableCopy);
        Assert.NotNull(pageCopy);
        Assert.Equal(table.ContinuationToken, tableCopy.ContinuationToken);
        Assert.Equal(page.ContinuationToken, pageCopy.ContinuationToken);
        AssertEntry(entry, Assert.Single(tableCopy.Reminders));
        AssertEntry(entry, Assert.Single(pageCopy.Reminders));
    }

    [Fact]
    public void RegistrationHandleAndTickStatus_PreserveIdentityAndTiming()
    {
        using var environment = SerializationTestEnvironment.InitializeWithDefaults();
        var entry = CreateEntry();
        var handle = (ReminderData)entry.ToIGrainReminder();
        var status = new TickStatus(entry.StartAt, entry.Period, entry.NextDueUtc!.Value);

        var handleCopy = environment.Serializer.Deserialize<ReminderData>(environment.Serializer.SerializeToArray(handle));
        var statusCopy = environment.Serializer.Deserialize<TickStatus>(environment.Serializer.SerializeToArray(status));

        Assert.NotNull(handleCopy);
        Assert.Equal(handle.GrainId, handleCopy.GrainId);
        Assert.Equal(handle.ReminderName, handleCopy.ReminderName);
        Assert.Equal(handle.ETag, handleCopy.ETag);
        Assert.Equal(handle.RegistrationId, handleCopy.RegistrationId);
        Assert.Equal(handle.CronExpression, handleCopy.CronExpression);
        Assert.Equal(handle.CronTimeZone, handleCopy.CronTimeZone);
        Assert.Equal(handle.Action, handleCopy.Action);
        Assert.Equal(status.FirstTickTime, statusCopy.FirstTickTime);
        Assert.Equal(status.CurrentTickTime, statusCopy.CurrentTickTime);
        Assert.Equal(status.Period, statusCopy.Period);
    }

    [Fact]
    public void QueryFilter_PreservesAllPredicates()
    {
        using var environment = SerializationTestEnvironment.InitializeWithDefaults();
        var filter = new ReminderQueryFilter
        {
            Action = MissedReminderAction.Notify,
            DueFromUtcInclusive = DateTime.UnixEpoch.AddHours(1),
            DueToUtcInclusive = DateTime.UnixEpoch.AddHours(2),
            GrainType = GrainType.Create("filter-grain"),
            MissedBy = TimeSpan.FromMinutes(2),
            OverdueBy = TimeSpan.FromMinutes(3),
            ScheduleKind = ReminderScheduleKind.Cron,
            Status = ReminderQueryStatus.Missed | ReminderQueryStatus.Overdue,
        };

        var copy = environment.Serializer.Deserialize<ReminderQueryFilter>(environment.Serializer.SerializeToArray(filter));

        Assert.NotNull(copy);
        Assert.Equal(filter.Action, copy.Action);
        Assert.Equal(filter.DueFromUtcInclusive, copy.DueFromUtcInclusive);
        Assert.Equal(filter.DueToUtcInclusive, copy.DueToUtcInclusive);
        Assert.Equal(filter.GrainType, copy.GrainType);
        Assert.Equal(filter.MissedBy, copy.MissedBy);
        Assert.Equal(filter.OverdueBy, copy.OverdueBy);
        Assert.Equal(filter.ScheduleKind, copy.ScheduleKind);
        Assert.Equal(filter.Status, copy.Status);
    }

    private static ReminderEntry CreateEntry() => new()
    {
        Action = MissedReminderAction.FireImmediately,
        CronExpression = "15 9 * * *",
        CronTimeZoneId = "Europe/Kyiv",
        ETag = "entry-version",
        GrainId = GrainId.Create("serialization", "reminder"),
        JobId = "job-id",
        JobShardId = "job-shard",
        LastFireUtc = DateTime.UnixEpoch.AddMinutes(1),
        NextDueUtc = DateTime.UnixEpoch.AddMinutes(3),
        Period = TimeSpan.FromMinutes(2),
        ReminderName = "serialized-reminder",
        ScheduleId = "r1:registration:occurrence",
        StartAt = DateTime.UnixEpoch,
    };

    private static void AssertEntry(ReminderEntry expected, ReminderEntry actual)
    {
        Assert.Equal(expected.Action, actual.Action);
        Assert.Equal(expected.CronExpression, actual.CronExpression);
        Assert.Equal(expected.CronTimeZoneId, actual.CronTimeZoneId);
        Assert.Equal(expected.ETag, actual.ETag);
        Assert.Equal(expected.GrainId, actual.GrainId);
        Assert.Equal(expected.JobId, actual.JobId);
        Assert.Equal(expected.JobShardId, actual.JobShardId);
        Assert.Equal(expected.LastFireUtc, actual.LastFireUtc);
        Assert.Equal(expected.NextDueUtc, actual.NextDueUtc);
        Assert.Equal(expected.Period, actual.Period);
        Assert.Equal(expected.ReminderName, actual.ReminderName);
        Assert.Equal(expected.ScheduleId, actual.ScheduleId);
        Assert.Equal(expected.StartAt, actual.StartAt);
    }
}
