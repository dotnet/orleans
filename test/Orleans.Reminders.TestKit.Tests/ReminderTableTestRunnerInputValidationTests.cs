using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "xUnit",
    "xUnit1051",
    Justification = "These tests intentionally exercise both protected overloads and explicit cancellation precedence.")]
public class ReminderTableTestRunnerInputValidationTests
{
    private const string ETag = "etag";
    private const string Guarantee = "Guarantee";
    private const string Operation = "Operation";
    private const string ReminderName = "reminder";

    public static TheoryData<ProtectedStringParameter, string> ProtectedStringParameters { get; } = new()
    {
        { ProtectedStringParameter.ReportGuarantee, "guarantee" },
        { ProtectedStringParameter.ReportOperation, "operation" },
        { ProtectedStringParameter.NewGrainIdLabel, "label" },
        { ProtectedStringParameter.NewEntryReminderName, "reminderName" },
        { ProtectedStringParameter.UpsertGuarantee, "guarantee" },
        { ProtectedStringParameter.UpsertGuaranteeWithCancellation, "guarantee" },
        { ProtectedStringParameter.ReadRequiredReminderName, "reminderName" },
        { ProtectedStringParameter.ReadRequiredReminderNameWithCancellation, "reminderName" },
        { ProtectedStringParameter.ReadRequiredGuarantee, "guarantee" },
        { ProtectedStringParameter.ReadRequiredGuaranteeWithCancellation, "guarantee" },
        { ProtectedStringParameter.RemoveReminderName, "reminderName" },
        { ProtectedStringParameter.RemoveReminderNameWithCancellation, "reminderName" },
        { ProtectedStringParameter.RemoveETag, "etag" },
        { ProtectedStringParameter.RemoveETagWithCancellation, "etag" },
        { ProtectedStringParameter.RequireRowsGuarantee, "guarantee" },
        { ProtectedStringParameter.RequireRowsOperation, "operation" },
        { ProtectedStringParameter.AssertEntryGuarantee, "guarantee" },
        { ProtectedStringParameter.AssertEntryOperation, "operation" },
        { ProtectedStringParameter.AssertEntryExpectedETag, "expectedETag" },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpsertAsync_NullEntry_ThrowsBeforeTableOperation(bool useCancellationOverload)
    {
        var table = new RecordingReminderTable();
        var runner = new TestRunner(table);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            useCancellationOverload
                ? runner.InvokeUpsertAsync(null!, Guarantee, TestContext.Current.CancellationToken)
                : runner.InvokeUpsertAsync(null!, Guarantee));

        Assert.Equal("entry", exception.ParamName);
        Assert.Equal(0, table.OperationCount);
    }

    [Theory]
    [InlineData(EntryParameter.Expected, "expected")]
    [InlineData(EntryParameter.Actual, "actual")]
    public void AssertEntry_NullEntry_ThrowsBeforeComparisonOrTableOperation(
        EntryParameter parameter,
        string expectedParamName)
    {
        var table = new RecordingReminderTable();
        var runner = new TestRunner(table);
        var entry = CreateEntry();

        var exception = Assert.Throws<ArgumentNullException>(() => runner.InvokeAssertEntry(
            Guarantee,
            Operation,
            parameter == EntryParameter.Expected ? null! : entry,
            ETag,
            parameter == EntryParameter.Actual ? null! : entry));

        Assert.Equal(expectedParamName, exception.ParamName);
        Assert.Equal(0, table.OperationCount);
    }

    [Fact]
    public void Describe_NullEntry_ThrowsWithExactParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => TestRunner.InvokeDescribe(null!));

        Assert.Equal("entry", exception.ParamName);
    }

    [Theory]
    [MemberData(nameof(ProtectedStringParameters))]
    public async Task ProtectedStringBoundary_NullInput_ThrowsBeforeTableOperation(
        ProtectedStringParameter parameter,
        string expectedParamName)
    {
        var table = new RecordingReminderTable();
        var runner = new TestRunner(table);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => InvokeNullStringBoundaryAsync(runner, parameter));

        Assert.Equal(expectedParamName, exception.ParamName);
        Assert.Equal(0, table.OperationCount);
    }

    [Theory]
    [InlineData(CancellationAwareOperation.Upsert)]
    [InlineData(CancellationAwareOperation.ReadRequired)]
    [InlineData(CancellationAwareOperation.Remove)]
    public async Task CancellationAwareBoundary_CanceledToken_TakesPrecedenceOverNullInput(
        CancellationAwareOperation operation)
    {
        var table = new RecordingReminderTable();
        var runner = new TestRunner(table);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeCanceledBoundaryAsync(runner, operation, cancellation.Token));

        Assert.Equal(0, table.OperationCount);
    }

    private static async Task InvokeNullStringBoundaryAsync(
        TestRunner runner,
        ProtectedStringParameter parameter)
    {
        var entry = CreateEntry();
        switch (parameter)
        {
            case ProtectedStringParameter.ReportGuarantee:
                runner.InvokeReport(null!, Operation);
                return;
            case ProtectedStringParameter.ReportOperation:
                runner.InvokeReport(Guarantee, null!);
                return;
            case ProtectedStringParameter.NewGrainIdLabel:
                runner.InvokeNewGrainId(null!);
                return;
            case ProtectedStringParameter.NewEntryReminderName:
                runner.InvokeNewEntry(null!);
                return;
            case ProtectedStringParameter.UpsertGuarantee:
                await runner.InvokeUpsertAsync(entry, null!);
                return;
            case ProtectedStringParameter.UpsertGuaranteeWithCancellation:
                await runner.InvokeUpsertAsync(entry, null!, CancellationToken.None);
                return;
            case ProtectedStringParameter.ReadRequiredReminderName:
                await runner.InvokeReadRequiredAsync(null!, Guarantee);
                return;
            case ProtectedStringParameter.ReadRequiredReminderNameWithCancellation:
                await runner.InvokeReadRequiredAsync(null!, Guarantee, CancellationToken.None);
                return;
            case ProtectedStringParameter.ReadRequiredGuarantee:
                await runner.InvokeReadRequiredAsync(ReminderName, null!);
                return;
            case ProtectedStringParameter.ReadRequiredGuaranteeWithCancellation:
                await runner.InvokeReadRequiredAsync(ReminderName, null!, CancellationToken.None);
                return;
            case ProtectedStringParameter.RemoveReminderName:
                await runner.InvokeRemoveAsync(null!, ETag);
                return;
            case ProtectedStringParameter.RemoveReminderNameWithCancellation:
                await runner.InvokeRemoveAsync(null!, ETag, CancellationToken.None);
                return;
            case ProtectedStringParameter.RemoveETag:
                await runner.InvokeRemoveAsync(ReminderName, null!);
                return;
            case ProtectedStringParameter.RemoveETagWithCancellation:
                await runner.InvokeRemoveAsync(ReminderName, null!, CancellationToken.None);
                return;
            case ProtectedStringParameter.RequireRowsGuarantee:
                runner.InvokeRequireRows(null!, Operation, new ReminderTableData([]));
                return;
            case ProtectedStringParameter.RequireRowsOperation:
                runner.InvokeRequireRows(Guarantee, null!, new ReminderTableData([]));
                return;
            case ProtectedStringParameter.AssertEntryGuarantee:
                runner.InvokeAssertEntry(null!, Operation, entry, ETag, entry);
                return;
            case ProtectedStringParameter.AssertEntryOperation:
                runner.InvokeAssertEntry(Guarantee, null!, entry, ETag, entry);
                return;
            case ProtectedStringParameter.AssertEntryExpectedETag:
                runner.InvokeAssertEntry(Guarantee, Operation, entry, null!, entry);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(parameter));
        }
    }

    private static Task InvokeCanceledBoundaryAsync(
        TestRunner runner,
        CancellationAwareOperation operation,
        CancellationToken cancellationToken)
        => operation switch
        {
            CancellationAwareOperation.Upsert => runner.InvokeUpsertAsync(null!, Guarantee, cancellationToken),
            CancellationAwareOperation.ReadRequired => runner.InvokeReadRequiredAsync(null!, Guarantee, cancellationToken),
            CancellationAwareOperation.Remove => runner.InvokeRemoveAsync(null!, ETag, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static ReminderEntry CreateEntry() => new()
    {
        GrainId = GrainId.Create("test", "key"),
        ReminderName = ReminderName,
        StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Period = TimeSpan.FromMinutes(1),
        ETag = ETag,
    };

    public enum EntryParameter
    {
        Expected,
        Actual,
    }

    public enum ProtectedStringParameter
    {
        ReportGuarantee,
        ReportOperation,
        NewGrainIdLabel,
        NewEntryReminderName,
        UpsertGuarantee,
        UpsertGuaranteeWithCancellation,
        ReadRequiredReminderName,
        ReadRequiredReminderNameWithCancellation,
        ReadRequiredGuarantee,
        ReadRequiredGuaranteeWithCancellation,
        RemoveReminderName,
        RemoveReminderNameWithCancellation,
        RemoveETag,
        RemoveETagWithCancellation,
        RequireRowsGuarantee,
        RequireRowsOperation,
        AssertEntryGuarantee,
        AssertEntryOperation,
        AssertEntryExpectedETag,
    }

    public enum CancellationAwareOperation
    {
        Upsert,
        ReadRequired,
        Remove,
    }

    private sealed class TestRunner(IReminderTable table)
        : ReminderTableTestRunner(table, nameof(RecordingReminderTable))
    {
        public ReminderFailureReport InvokeReport(string guarantee, string operation)
            => Report(guarantee, operation);

        public GrainId InvokeNewGrainId(string label) => NewGrainId(label);

        public ReminderEntry InvokeNewEntry(string reminderName)
            => NewEntry(GrainId.Create("test", "key"), reminderName);

        public Task<string> InvokeUpsertAsync(ReminderEntry entry, string guarantee)
            => UpsertAsync(entry, guarantee);

        public Task<string> InvokeUpsertAsync(
            ReminderEntry entry,
            string guarantee,
            CancellationToken cancellationToken)
            => UpsertAsync(entry, guarantee, cancellationToken);

        public Task<ReminderEntry> InvokeReadRequiredAsync(string reminderName, string guarantee)
            => ReadRequiredAsync(GrainId.Create("test", "key"), reminderName, guarantee);

        public Task<ReminderEntry> InvokeReadRequiredAsync(
            string reminderName,
            string guarantee,
            CancellationToken cancellationToken)
            => ReadRequiredAsync(GrainId.Create("test", "key"), reminderName, guarantee, cancellationToken);

        public Task InvokeRemoveAsync(string reminderName, string etag)
            => RemoveAsync(GrainId.Create("test", "key"), reminderName, etag);

        public Task InvokeRemoveAsync(
            string reminderName,
            string etag,
            CancellationToken cancellationToken)
            => RemoveAsync(GrainId.Create("test", "key"), reminderName, etag, cancellationToken);

        public ReminderTableData InvokeRequireRows(
            string guarantee,
            string operation,
            ReminderTableData? rows)
            => RequireRows(guarantee, operation, rows);

        public void InvokeAssertEntry(
            string guarantee,
            string operation,
            ReminderEntry expected,
            string expectedETag,
            ReminderEntry actual)
            => AssertEntry(guarantee, operation, expected, expectedETag, actual);

        public static string InvokeDescribe(ReminderEntry entry) => Describe(entry);
    }

    private sealed class RecordingReminderTable : IReminderTable
    {
        public int OperationCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            OperationCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            OperationCount++;
            return Task.CompletedTask;
        }

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
        {
            OperationCount++;
            return Task.FromResult<ReminderEntry?>(null);
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            OperationCount++;
            return Task.FromResult(new ReminderTableData([]));
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            OperationCount++;
            return Task.FromResult(new ReminderTableData([]));
        }

        public Task<string?> UpsertRow(ReminderEntry entry)
        {
            OperationCount++;
            return Task.FromResult<string?>(ETag);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            OperationCount++;
            return Task.FromResult(true);
        }

        public Task TestOnlyClearTable()
        {
            OperationCount++;
            return Task.CompletedTask;
        }
    }
}
