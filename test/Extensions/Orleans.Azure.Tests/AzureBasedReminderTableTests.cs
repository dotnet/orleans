using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Reminders.AzureStorage;
using Orleans.Runtime;
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
        var table = CreateTable();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => table.UpsertRow(null!));

        Assert.Equal("entry", exception.ParamName);
    }

    [Fact]
    public void ConvertFromTableEntry_ConversionAndServiceValidationFail_PreservesConversionFailure()
    {
        var table = CreateTable();
        var entry = CreateEntry(serviceId: "other-service", period: "not-a-period");
        var expectedMessage = Assert.Throws<FormatException>(() => TimeSpan.Parse(entry.Period!)).Message;

        var exception = Assert.Throws<FormatException>(() => table.ConvertFromTableEntry(entry, "etag"));

        Assert.Equal(expectedMessage, exception.Message);
        var validationException = Assert.IsType<OrleansException>(
            exception.Data[AzureBasedReminderTable.ServiceIdValidationExceptionDataKey]);
        Assert.Contains("other-service", validationException.Message, StringComparison.Ordinal);
        Assert.Contains("service-id", validationException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertFromTableEntry_ServiceValidationFails_SurfacesValidationFailure()
    {
        var table = CreateTable();
        var entry = CreateEntry(serviceId: "other-service");

        var exception = Assert.Throws<OrleansException>(() => table.ConvertFromTableEntry(entry, "etag"));

        Assert.Contains("other-service", exception.Message, StringComparison.Ordinal);
        Assert.Contains("service-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertFromTableEntry_ConversionFailsWithMissingServiceId_PreservesConversionFailure()
    {
        var table = CreateTable();
        var entry = CreateEntry(serviceId: null, period: "not-a-period");
        var expectedMessage = Assert.Throws<FormatException>(() => TimeSpan.Parse(entry.Period!)).Message;

        var exception = Assert.Throws<FormatException>(() => table.ConvertFromTableEntry(entry, "etag"));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.IsType<NullReferenceException>(
            exception.Data[AzureBasedReminderTable.ServiceIdValidationExceptionDataKey]);
    }

    [Fact]
    public void ConvertFromTableEntry_ValidEntry_PreservesReminderData()
    {
        var table = CreateTable();
        var entry = CreateEntry(serviceId: "service-id");

        var result = table.ConvertFromTableEntry(entry, "etag");

        Assert.Equal(GrainId.Parse(entry.GrainReference!), result.GrainId);
        Assert.Equal(entry.ReminderName, result.ReminderName);
        Assert.Equal(LogFormatter.ParseDate(entry.StartAt!), result.StartAt);
        Assert.Equal(TimeSpan.Parse(entry.Period!), result.Period);
        Assert.Equal("etag", result.ETag);
    }

    private static AzureBasedReminderTable CreateTable() => new(
        NullLoggerFactory.Instance,
        Options.Create(new ClusterOptions
        {
            ServiceId = "service-id",
            ClusterId = "cluster-id",
        }),
        Options.Create(new AzureTableReminderStorageOptions()));

    private static ReminderTableEntry CreateEntry(string? serviceId, string period = "00:05:00") => new()
    {
        PartitionKey = "partition",
        RowKey = "row",
        ServiceId = serviceId,
        DeploymentId = "cluster-id",
        GrainReference = GrainId.Create("test-grain", "test-key").ToString(),
        ReminderName = "test-reminder",
        StartAt = LogFormatter.PrintDate(new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc)),
        Period = period,
        GrainRefConsistentHash = "00000000",
    };
}
