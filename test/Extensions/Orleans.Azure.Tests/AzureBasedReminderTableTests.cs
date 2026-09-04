using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Reminders.AzureStorage;
using Orleans.Runtime.ReminderService;
using TestExtensions;
using Xunit;

namespace UnitTests.RemindersTest;

[TestCategory("AzureStorage"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Reminders")]
public class AzureBasedReminderTableTests
{
    [Fact]
    public void Constructor_NullClusterOptions_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new AzureBasedReminderTable(
            NullLoggerFactory.Instance,
            null!,
            Options.Create(new AzureTableReminderStorageOptions())));

        Assert.Equal("clusterOptions", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullStorageOptions_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new AzureBasedReminderTable(
            NullLoggerFactory.Instance,
            Options.Create(new ClusterOptions()),
            null!));

        Assert.Equal("storageOptions", exception.ParamName);
    }

    [Fact]
    public async Task UpsertRow_NullEntry_ThrowsArgumentNullException()
    {
        var table = new AzureBasedReminderTable(
            NullLoggerFactory.Instance,
            Options.Create(new ClusterOptions
            {
                ServiceId = "service-id",
                ClusterId = "cluster-id",
            }),
            Options.Create(new AzureTableReminderStorageOptions()));

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => table.UpsertRow(null!));

        Assert.Equal("entry", exception.ParamName);
    }
}
