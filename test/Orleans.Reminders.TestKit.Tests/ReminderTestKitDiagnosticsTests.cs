using Orleans.Reminders.TestKit;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders")]
public sealed class ReminderTestKitDiagnosticsTests
{
    [Fact]
    public void Build_BlankNamesAndNullDetails_UsesExactPlaceholders()
    {
        var report = ReminderFailureReport.Create(string.Empty, "\r\n", " \t")
            .WithDetail("observation", null);

        var expected =
            "Reminder conformance failure [provider=<unnamed-provider>, guarantee=<unnamed-guarantee>, operation=<unnamed-operation>]" + Environment.NewLine +
            "  observation: <null>";

        Assert.Equal(expected, report.Build());
    }

    [Fact]
    public void ToException_PreservesExactReportAndInnerException()
    {
        var report = ReminderFailureReport.Create("InMemory", "ETag rotation", "UpsertRow")
            .WithDetail("attempt", "second write");
        var innerException = new InvalidOperationException("write failed");
        var expected =
            "Reminder conformance failure [provider=InMemory, guarantee=ETag rotation, operation=UpsertRow]" + Environment.NewLine +
            "  attempt: 'second write'";

        var exception = report.ToException(innerException);

        Assert.IsType<ReminderConformanceException>(exception);
        Assert.Equal(expected, report.Build());
        Assert.Equal(expected, exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }

    [Fact]
    public void Throw_PreservesExactReportAndInnerException()
    {
        var report = ReminderFailureReport.Create("AzureTable", "Remove semantics", "RemoveRow")
            .WithDetail("observation", "row remained");
        var innerException = new InvalidOperationException("conditional delete failed");
        var expected =
            "Reminder conformance failure [provider=AzureTable, guarantee=Remove semantics, operation=RemoveRow]" + Environment.NewLine +
            "  observation: 'row remained'";

        var exception = Assert.Throws<ReminderConformanceException>(() => report.Throw(innerException));

        Assert.IsType<ReminderConformanceException>(exception);
        Assert.Equal(expected, report.Build());
        Assert.Equal(expected, exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }
}
