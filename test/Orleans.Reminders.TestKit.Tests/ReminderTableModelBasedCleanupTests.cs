using Orleans.Reminders.TestKit;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders")]
public sealed class ReminderTableModelBasedCleanupTests
{
    [Fact]
    public async Task ExecuteWithCleanup_OperationAndCleanupFail_PreservesOperationFailureAndRecordsCleanup()
    {
        var operationException = new InvalidOperationException("operation failed");
        var cleanupException = new InvalidOperationException("cleanup failed");
        var cleanupAttempted = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReminderTableModelBasedConformance.ExecuteWithCleanup(
                () => Task.FromException<int>(operationException),
                _ => null,
                () =>
                {
                    cleanupAttempted = true;
                    return Task.FromException(cleanupException);
                },
                TestContext.Current.CancellationToken));

        Assert.Same(operationException, exception);
        Assert.True(cleanupAttempted);
        Assert.Same(
            cleanupException,
            exception.Data[ReminderTableModelBasedConformance.FinalCleanupExceptionDataKey]);
    }

    [Fact]
    public async Task ExecuteWithCleanup_GeneratedFailureAndCleanupFail_PreservesGeneratedDiagnosticAndRecordsCleanup()
    {
        var generatedException = new ReminderConformanceException("oracle failure diagnostic");
        var cleanupException = new InvalidOperationException("cleanup failed");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(() =>
            ReminderTableModelBasedConformance.ExecuteWithCleanup(
                () => Task.FromResult(42),
                _ => generatedException,
                () => Task.FromException(cleanupException),
                TestContext.Current.CancellationToken));

        Assert.Same(generatedException, exception);
        Assert.Equal("oracle failure diagnostic", exception.Message);
        Assert.Same(
            cleanupException,
            exception.Data[ReminderTableModelBasedConformance.FinalCleanupExceptionDataKey]);
    }

    [Fact]
    public async Task ExecuteWithCleanup_OnlyCleanupFails_SurfacesCleanupFailure()
    {
        var cleanupException = new InvalidOperationException("cleanup failed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReminderTableModelBasedConformance.ExecuteWithCleanup(
                () => Task.FromResult(42),
                _ => null,
                () => Task.FromException(cleanupException),
                TestContext.Current.CancellationToken));

        Assert.Same(cleanupException, exception);
    }

    [Fact]
    public async Task ExecuteWithCleanup_CancellationAndCleanupFail_PreservesCancellationAndRecordsCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cleanupException = new InvalidOperationException("cleanup failed");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReminderTableModelBasedConformance.ExecuteWithCleanup(
                () => Task.FromResult(42),
                _ => null,
                () => Task.FromException(cleanupException),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Same(
            cleanupException,
            exception.Data[ReminderTableModelBasedConformance.FinalCleanupExceptionDataKey]);
    }

    [Fact]
    public async Task ExecuteWithCleanup_Succeeds_ReturnsOperationResultAndAttemptsCleanup()
    {
        var cleanupAttempted = false;

        var result = await ReminderTableModelBasedConformance.ExecuteWithCleanup(
            () => Task.FromResult(42),
            _ => null,
            () =>
            {
                cleanupAttempted = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.True(cleanupAttempted);
    }

    [Fact]
    public async Task Cleanup_ClearFails_SurfacesCleanupFailure()
    {
        var cleanupException = new InvalidOperationException("cleanup failed");
        var table = new CleanupReminderTable(cleanupException);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReminderTableModelBasedConformance.Cleanup(table, new ReminderTableModelBasedConformanceOptions()));

        Assert.Same(cleanupException, exception);
        Assert.Equal(1, table.ClearAttempts);
    }

    [Fact]
    public async Task Cleanup_ClearSucceeds_ConfirmsTableIsEmpty()
    {
        var table = new CleanupReminderTable();

        await ReminderTableModelBasedConformance.Cleanup(
            table,
            new ReminderTableModelBasedConformanceOptions());

        Assert.Equal(1, table.ClearAttempts);
        Assert.Equal(1, table.RangeReadAttempts);
    }

    private sealed class CleanupReminderTable(Exception? cleanupException = null) : IReminderTable
    {
        private bool _hasRows = true;

        public int ClearAttempts { get; private set; }

        public int RangeReadAttempts { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

        public Task<ReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            RangeReadAttempts++;
            return Task.FromResult(new ReminderTableData(_hasRows ? [new ReminderEntry()] : []));
        }

        public Task<string?> UpsertRow(ReminderEntry entry) => throw new NotSupportedException();

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();

        public Task TestOnlyClearTable()
        {
            ClearAttempts++;
            if (cleanupException is not null)
            {
                return Task.FromException(cleanupException);
            }

            _hasRows = false;
            return Task.CompletedTask;
        }
    }
}
