#nullable enable
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Xunit;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderMetadataTests : AdvancedReminderServiceTestBase
{
    [Fact]
    public void Dispatcher_IsNotPinnedInMemory()
    {
        Assert.Empty(typeof(AdvancedReminderDispatcherGrain).GetCustomAttributes(typeof(KeepAliveAttribute), inherit: true));
    }

    [Fact]
    public void ReminderTableData_ToString_ClosesOuterCollection()
    {
        Assert.Equal("[0 reminders: []].", new ReminderTableData().ToString());
    }

    [Fact]
    public void TryGetReminderMetadata_ReturnsExpectedValues()
    {
        var grainId = GrainId.Create("test", "metadata");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grain-id"] = grainId.ToString(),
            ["reminder-name"] = "r",
            ["etag"] = "etag-1",
        };

        var result = AdvancedReminderService.TryGetReminderMetadata(metadata, out var parsedGrainId, out var reminderName, out var eTag);

        Assert.True(result);
        Assert.Equal(grainId, parsedGrainId);
        Assert.Equal("r", reminderName);
        Assert.Equal("etag-1", eTag);
    }

    [Fact]
    public void TryGetReminderMetadata_ReturnsFalseWhenRequiredFieldsAreMissing()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grain-id"] = GrainId.Create("test", "metadata").ToString(),
        };

        var result = AdvancedReminderService.TryGetReminderMetadata(metadata, out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryGetReminderMetadata_ReturnsFalseForNullOrWhitespaceName()
    {
        var grainId = GrainId.Create("test", "metadata");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grain-id"] = grainId.ToString(),
            ["reminder-name"] = " ",
        };

        Assert.False(AdvancedReminderService.TryGetReminderMetadata(null, out _, out _, out _));
        Assert.False(AdvancedReminderService.TryGetReminderMetadata(metadata, out _, out _, out _));
    }

    [Fact]
    public void TryGetReminderMetadata_ReturnsFalseForMalformedGrainId()
    {
        var metadata = new Dictionary<string, string>
        {
            ["grain-id"] = "not a valid grain id",
            ["reminder-name"] = "r",
            ["schedule-id"] = "schedule",
        };

        Assert.False(AdvancedReminderService.TryGetReminderMetadata(metadata, out _, out _, out _));
    }
}
