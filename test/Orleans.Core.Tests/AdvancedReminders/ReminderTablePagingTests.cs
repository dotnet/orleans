using System.Text;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class ReminderTablePagingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public async Task InMemoryPaging_PreservesOrderAcrossHashCollisionsAndWrappedRange(int pageSize)
    {
        var table = new AdvancedReminderTableGrain();
        var type = GrainType.Create("paging");
        var collisionA = GrainId.Create(type, IdSpan.UnsafeCreate(Encoding.UTF8.GetBytes("first"), hashCode: 10));
        var collisionB = GrainId.Create(type, IdSpan.UnsafeCreate(Encoding.UTF8.GetBytes("second"), hashCode: 10));
        var before = GrainId.Create(type, IdSpan.UnsafeCreate(Encoding.UTF8.GetBytes("before"), hashCode: 9));
        var after = GrainId.Create(type, IdSpan.UnsafeCreate(Encoding.UTF8.GetBytes("after"), hashCode: 11));
        (GrainId Grain, string Name)[] expected =
        [
            (after, "last"),
            (before, "only"),
            (collisionA, "A"),
            (collisionA, "a"),
            (collisionB, "b"),
            (collisionB, "c"),
        ];
        for (var index = expected.Length - 1; index >= 0; index--)
        {
            var (grain, name) = expected[index];
            await table.UpsertRow(new ReminderEntry { GrainId = grain, ReminderName = name });
        }

        var begin = after.GetUniformHashCode();
        var observed = new List<(GrainId, string)>();
        string? cursor = null;
        do
        {
            var page = await table.ReadRows(begin, begin, pageSize, cursor);
            observed.AddRange(page.Reminders.Select(static entry => (entry.GrainId, entry.ReminderName)));
            Assert.InRange(observed.Count, 1, expected.Length);
            Assert.True(page.ContinuationToken is null || page.ContinuationToken != cursor);
            cursor = page.ContinuationToken;
        } while (cursor is not null);

        Assert.Equal(expected, observed);
    }
}
