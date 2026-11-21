#nullable enable
extern alias AdvancedRemindersAdoNet;

using System.Data;
using Xunit;
using AdvancedReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using AdoNetReminderTable = AdvancedRemindersAdoNet::Orleans.AdvancedReminders.Runtime.ReminderService.AdoNetReminderTable;
using DbStoredQueries = AdvancedRemindersAdoNet::Orleans.Reminders.AdoNet.Storage.DbStoredQueries;

namespace UnitTests.AdvancedRemindersTest;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class AdoNetAdvancedReminderTableTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(null)]
    public void GetNullableVersion_ReadsUpsertResult(int? version)
    {
        using var table = new DataTable();
        table.Columns.Add("Version", typeof(int));
        table.Rows.Add((object?)version ?? DBNull.Value);
        using var reader = table.CreateDataReader();

        Assert.True(reader.Read());
        Assert.Equal(version, DbStoredQueries.Converters.GetNullableVersion(reader));
    }

    [Fact]
    public void NormalizeUtcFields_SpecifiesUtcKindWithoutChangingTicks()
    {
        var startAt = new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Unspecified);
        var nextDueUtc = startAt.AddMinutes(10);
        var lastFireUtc = startAt.AddMinutes(-2);
        var reminder = new AdvancedReminderEntry
        {
            StartAt = startAt,
            NextDueUtc = nextDueUtc,
            LastFireUtc = lastFireUtc,
        };

        AdoNetReminderTable.NormalizeUtcFields(reminder);

        Assert.Equal(DateTimeKind.Utc, reminder.StartAt.Kind);
        Assert.Equal(startAt.Ticks, reminder.StartAt.Ticks);
        Assert.Equal(DateTimeKind.Utc, reminder.NextDueUtc.Value.Kind);
        Assert.Equal(nextDueUtc.Ticks, reminder.NextDueUtc.Value.Ticks);
        Assert.Equal(DateTimeKind.Utc, reminder.LastFireUtc.Value.Kind);
        Assert.Equal(lastFireUtc.Ticks, reminder.LastFireUtc.Value.Ticks);
    }
}
