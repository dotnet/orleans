using System.Globalization;
using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

/// <summary>
/// Runs the full reminder table conformance suite against the idealized oracle, and verifies the oracle's own
/// introspection and deterministic controls.
/// </summary>
/// <remarks>
/// The oracle is the reference implementation of the documented contract, so it must satisfy every guarantee the
/// test kit expresses. A new oracle is created for each test method because xUnit instantiates the test class once
/// per test, which keeps every guarantee independent.
/// </remarks>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders")]
public sealed class IdealizedReminderTableTests : ReminderTableTestRunner
{
    private readonly IdealizedReminderTable _oracle;

    public IdealizedReminderTableTests()
        : this(new IdealizedReminderTable("Oracle"))
    {
    }

    private IdealizedReminderTableTests(IdealizedReminderTable oracle)
        : base(oracle, "Oracle")
    {
        _oracle = oracle;
    }

    // -------------------------------------------------------------------------------------------------------------
    // The complete direct conformance suite.
    // -------------------------------------------------------------------------------------------------------------

    [Fact]
    public override Task ReminderTable_StartAsync_IsIdempotent() => base.RunReminderTable_StartAsync_IsIdempotent(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_StopAsync_ThenRestart_ResumesService() => base.RunReminderTable_StopAsync_ThenRestart_ResumesService(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_UpsertRow_ReturnsNewNonEmptyETag() => base.RunReminderTable_UpsertRow_ReturnsNewNonEmptyETag(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_UpsertRow_PersistsScheduleForPointRead() => base.RunReminderTable_UpsertRow_PersistsScheduleForPointRead(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ReadRow_MissingReminder_ReturnsNull() => base.RunReminderTable_ReadRow_MissingReminder_ReturnsNull(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders() => base.RunReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty() => base.RunReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_Identity_IsGrainIdAndReminderName() => base.RunReminderTable_Identity_IsGrainIdAndReminderName(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_Identity_WithSpecialCharacters_RoundTrips() => base.RunReminderTable_Identity_WithSpecialCharacters_RoundTrips(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_UpsertRow_ReplacesETagOnEachWrite() => base.RunReminderTable_UpsertRow_ReplacesETagOnEachWrite(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_RemoveRow_WithCurrentETag_RemovesRow() => base.RunReminderTable_RemoveRow_WithCurrentETag_RemovesRow(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow() => base.RunReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse() => base.RunReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess() => base.RunReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_UpsertRow_UpdatesStartAtAndPeriod() => base.RunReminderTable_UpsertRow_UpdatesStartAtAndPeriod(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows() => base.RunReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ReadRows_FullRange_ReturnsAllReminders() => base.RunReminderTable_ReadRows_FullRange_ReturnsAllReminders(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering()
        => base.RunReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd() => base.RunReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment() => base.RunReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder() => base.RunReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder() => base.RunReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ReadRow_AfterRemoval_ReturnsNull() => base.RunReminderTable_ReadRow_AfterRemoval_ReturnsNull(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ConcurrentUpserts_ProduceDistinctETags() => base.RunReminderTable_ConcurrentUpserts_ProduceDistinctETags(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated() => base.RunReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated(TestContext.Current.CancellationToken);

    [Fact]
    public override Task ReminderTable_TestOnlyClearTable_RemovesAllReminders() => base.RunReminderTable_TestOnlyClearTable_RemovesAllReminders(TestContext.Current.CancellationToken);

    // -------------------------------------------------------------------------------------------------------------
    // Model-based conformance.
    // -------------------------------------------------------------------------------------------------------------

    [Fact, TestCategory("ModelBased")]
    public Task Oracle_ModelBasedGeneratedConformance()
    {
        var runner = new ReminderTableModelBasedTestRunner(_oracle, "Oracle");
        return runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("ModelBased")]
    public static async Task ModelBasedRunner_WithSameSeed_ProducesIdenticalOperationTrace()
    {
        var first = new IdealizedReminderTable("First");
        var second = new IdealizedReminderTable("Second");
        var options = new ReminderTableModelBasedConformanceOptions
        {
            ProviderName = "Deterministic",
            KeyPrefix = "fixed-sequence",
            Seed = 42,
            MaxDepth = 2,
            MaxSequenceLength = 2
        };

        await new ReminderTableModelBasedTestRunner(first, options).RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
        await new ReminderTableModelBasedTestRunner(second, options).RunGeneratedConformanceTests(TestContext.Current.CancellationToken);

        Assert.NotEmpty(first.Operations);
        Assert.Equal(first.Operations.Count, second.Operations.Count);
        Assert.Equal(
            first.Operations.Select(ToDeterministicObservation),
            second.Operations.Select(ToDeterministicObservation));

        static string ToDeterministicObservation(ReminderTableOperation operation)
            => string.Join(
                "|",
                operation.Kind,
                operation.GrainId?.ToString() ?? "<none>",
                operation.ReminderName ?? "<none>",
                operation.Begin?.ToString(CultureInfo.InvariantCulture) ?? "<none>",
                operation.End?.ToString(CultureInfo.InvariantCulture) ?? "<none>",
                operation.SuppliedETag ?? "<none>",
                operation.ResultETag ?? "<none>",
                operation.Succeeded.ToString(CultureInfo.InvariantCulture),
                operation.ResultCount.ToString(CultureInfo.InvariantCulture),
                operation.Failure ?? "<none>");
    }

    // -------------------------------------------------------------------------------------------------------------
    // Introspection.
    // -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Oracle_Snapshot_ExposesPersistedRecordsWithETagLineage()
    {
        var grainId = NewGrainId("introspection");
        var first = await UpsertAsync(
            NewEntry(grainId, "introspection", BaseTime, TimeSpan.FromMinutes(2)),
            nameof(Oracle_Snapshot_ExposesPersistedRecordsWithETagLineage),
            TestContext.Current.CancellationToken);
        var second = await UpsertAsync(
            NewEntry(grainId, "introspection", BaseTime.AddMinutes(5), TimeSpan.FromMinutes(3)),
            nameof(Oracle_Snapshot_ExposesPersistedRecordsWithETagLineage),
            TestContext.Current.CancellationToken);

        var record = Assert.Single(_oracle.Snapshot());
        Assert.Equal(grainId, record.GrainId);
        Assert.Equal("introspection", record.ReminderName);
        Assert.Equal(second, record.ETag);
        Assert.Equal(first, record.PreviousETag);
        Assert.Equal(BaseTime.AddMinutes(5).Ticks, record.StartAt.Ticks);
        Assert.Equal(TimeSpan.FromMinutes(3), record.Period);
        Assert.Equal(grainId.GetUniformHashCode(), record.UniformHashCode);

        var found = _oracle.Find(grainId, "introspection");
        Assert.NotNull(found);
        Assert.Equal(record.Version, found.Version);
    }

    [Fact]
    public async Task Oracle_Operations_RecordSequenceIdentityAndOutcome()
    {
        var grainId = NewGrainId("operation-log");
        _oracle.ClearOperations();

        var etag = await UpsertAsync(
            NewEntry(grainId, "operation-log"),
            nameof(Oracle_Operations_RecordSequenceIdentityAndOutcome),
            TestContext.Current.CancellationToken);
        _ = await ReminderTable.ReadRow(grainId, "operation-log").WaitAsync(TestContext.Current.CancellationToken);
        var removed = await ReminderTable.RemoveRow(grainId, "operation-log", "not-the-current-etag").WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(removed);

        var operations = _oracle.Operations;
        Assert.Equal(3, operations.Count);
        Assert.Equal(
            new[] { ReminderTableOperationKind.UpsertRow, ReminderTableOperationKind.ReadRow, ReminderTableOperationKind.RemoveRow },
            operations.Select(operation => operation.Kind));

        // Sequence numbers are monotonic and dense, so a generated failure can name the exact failing step.
        Assert.Equal(operations.Select((_, index) => operations[0].Sequence + index), operations.Select(operation => operation.Sequence));

        var upsert = operations[0];
        Assert.Equal(grainId, upsert.GrainId);
        Assert.Equal("operation-log", upsert.ReminderName);
        Assert.Equal(etag, upsert.ResultETag);
        Assert.True(upsert.Succeeded);

        var failedRemove = operations[2];
        Assert.Equal("not-the-current-etag", failedRemove.SuppliedETag);
        Assert.Equal(etag, failedRemove.ResultETag);
        Assert.False(failedRemove.Succeeded);
        Assert.Equal(0, failedRemove.ResultCount);
        Assert.Equal(1, _oracle.OperationCount(ReminderTableOperationKind.ReadRow));
    }

    [Fact]
    public async Task Oracle_ETags_AreMonotonicAndNeverReused()
    {
        var grainId = NewGrainId("etag-sequence");
        var etags = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            etags.Add(await UpsertAsync(
                NewEntry(grainId, $"etag-sequence-{index.ToString(CultureInfo.InvariantCulture)}"),
                nameof(Oracle_ETags_AreMonotonicAndNeverReused),
                TestContext.Current.CancellationToken));
        }

        Assert.Equal(etags.Count, etags.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { "etag-000001", "etag-000002", "etag-000003", "etag-000004" }, etags);

        // Removing and re-adding must not recycle an ETag.
        Assert.True(await ReminderTable.RemoveRow(grainId, "etag-sequence-0", etags[0]).WaitAsync(TestContext.Current.CancellationToken));
        var reAdded = await UpsertAsync(
            NewEntry(grainId, "etag-sequence-0"),
            nameof(Oracle_ETags_AreMonotonicAndNeverReused),
            TestContext.Current.CancellationToken);
        Assert.Equal("etag-000005", reAdded);
        Assert.DoesNotContain(reAdded, etags);
    }

    // -------------------------------------------------------------------------------------------------------------
    // Deterministic controls: outage, injected failure, blocking barrier, stale snapshot.
    // -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Oracle_Unavailable_FailsEveryOperationUntilRecovered()
    {
        var grainId = NewGrainId("outage");
        var etag = await UpsertAsync(
            NewEntry(grainId, "outage"),
            nameof(Oracle_Unavailable_FailsEveryOperationUntilRecovered),
            TestContext.Current.CancellationToken);

        _oracle.SetAvailable(false);
        Assert.False(_oracle.IsAvailable);

        await Assert.ThrowsAsync<ReminderTableUnavailableException>(
            () => ReminderTable.ReadRow(grainId, "outage").WaitAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ReminderTableUnavailableException>(
            () => ReminderTable.ReadRows(0, 0).WaitAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ReminderTableUnavailableException>(
            () => ReminderTable.UpsertRow(NewEntry(grainId, "outage-2")).WaitAsync(TestContext.Current.CancellationToken));

        // The outage is recorded so a test can assert on what the reminder service attempted while storage was down.
        Assert.Equal(3, _oracle.Operations.Count(operation => operation.Failure == nameof(ReminderTableUnavailableException)));

        // Durable state survives the outage: it was an availability failure, not a deletion.
        _oracle.SetAvailable(true);
        var recovered = await ReminderTable.ReadRow(grainId, "outage").WaitAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(recovered);
        Assert.Equal(etag, recovered.ETag);
        Assert.Null(_oracle.Find(grainId, "outage-2"));
    }

    [Fact]
    public async Task Oracle_InjectedFailure_AppliesExactlyTheRequestedNumberOfTimes()
    {
        var grainId = NewGrainId("injected-failure");
        _oracle.InjectFailure(ReminderTableOperationKind.UpsertRow, count: 2, () => new InvalidOperationException("transient store failure"));

        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReminderTable.UpsertRow(NewEntry(grainId, "injected-failure")).WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("transient store failure", first.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReminderTable.UpsertRow(NewEntry(grainId, "injected-failure")).WaitAsync(TestContext.Current.CancellationToken));

        // The third attempt is not affected, and the failures did not create partial state.
        var etag = await ReminderTable.UpsertRow(NewEntry(grainId, "injected-failure")).WaitAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(etag);
        Assert.Equal("etag-000001", etag);
        Assert.Equal(2, _oracle.Operations.Count(operation => operation.Failure == nameof(InvalidOperationException)));
        Assert.Single(_oracle.Snapshot());

        // Reads were never targeted, so they were never failed.
        Assert.NotNull(await ReminderTable.ReadRow(grainId, "injected-failure").WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Oracle_ClearInjectedFailures_CancelsEveryQueuedFailure()
    {
        var grainId = NewGrainId("clear-injected-failures");
        _oracle.InjectFailure(ReminderTableOperationKind.ReadRow, count: 2);

        _oracle.ClearInjectedFailures();
        var result = await ReminderTable.ReadRow(grainId, "missing").WaitAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Single(_oracle.Operations, operation =>
            operation.Kind == ReminderTableOperationKind.ReadRow
            && operation.GrainId == grainId
            && operation.ReminderName == "missing"
            && operation.Succeeded);
        Assert.DoesNotContain(_oracle.Operations, operation => operation.Failure is not null);
    }

    [Fact]
    public async Task Oracle_BlockNext_ProvidesADeterministicBarrierWithoutSleeping()
    {
        var grainId = NewGrainId("barrier");
        var etag = await UpsertAsync(
            NewEntry(grainId, "barrier", BaseTime, TimeSpan.FromMinutes(1)),
            nameof(Oracle_BlockNext_ProvidesADeterministicBarrierWithoutSleeping),
            TestContext.Current.CancellationToken);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        await using var gate = _oracle.BlockNext(ReminderTableOperationKind.ReadRange);

        var rangeRead = ReminderTable.ReadRows(0, 0);
        await gate.WaitUntilBlockedAsync(cancellation.Token);
        Assert.False(rangeRead.IsCompleted);

        // While the range read is parked at the barrier, a concurrent write is applied and observed by the oracle.
        var updated = await UpsertAsync(
            NewEntry(grainId, "barrier", BaseTime.AddMinutes(5), TimeSpan.FromMinutes(2)),
            nameof(Oracle_BlockNext_ProvidesADeterministicBarrierWithoutSleeping),
            cancellation.Token);
        Assert.NotEqual(etag, updated);
        Assert.False(rangeRead.IsCompleted);

        gate.Release();
        var rows = await rangeRead.WaitAsync(cancellation.Token);
        var reminder = Assert.Single(rows.Reminders, entry => entry.GrainId.Equals(grainId));
        Assert.Equal(updated, reminder.ETag);
        Assert.Equal(BaseTime.AddMinutes(5).Ticks, reminder.StartAt.Ticks);
    }

    [Fact]
    public async Task Oracle_DisposingBlockGate_ReleasesTheWaitingOperation()
    {
        var grainId = NewGrainId("disposed-barrier");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        var gate = _oracle.BlockNext(ReminderTableOperationKind.ReadRow);

        var read = ReminderTable.ReadRow(grainId, "missing");
        await gate.WaitUntilBlockedAsync(cancellation.Token);
        Assert.False(read.IsCompleted);

        await gate.DisposeAsync();
        var result = await read.WaitAsync(cancellation.Token);

        Assert.Null(result);
        Assert.Single(_oracle.Operations, operation =>
            operation.Kind == ReminderTableOperationKind.ReadRow
            && operation.GrainId == grainId
            && operation.Succeeded
            && operation.ResultCount == 0);
    }

    [Fact]
    public async Task Oracle_FreezeReads_ServesAStaleSnapshotWhileWritesStillApply()
    {
        var grainId = NewGrainId("stale-snapshot");
        var original = await UpsertAsync(
            NewEntry(grainId, "stale-snapshot", BaseTime, TimeSpan.FromMinutes(1)),
            nameof(Oracle_FreezeReads_ServesAStaleSnapshotWhileWritesStillApply),
            TestContext.Current.CancellationToken);

        using (_oracle.FreezeReads())
        {
            var updated = await UpsertAsync(
                NewEntry(grainId, "stale-snapshot", BaseTime.AddMinutes(20), TimeSpan.FromMinutes(9)),
                nameof(Oracle_FreezeReads_ServesAStaleSnapshotWhileWritesStillApply),
                TestContext.Current.CancellationToken);
            Assert.NotEqual(original, updated);

            var stale = await ReminderTable.ReadRow(grainId, "stale-snapshot").WaitAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(stale);
            Assert.Equal(original, stale.ETag);
            Assert.Equal(BaseTime.Ticks, stale.StartAt.Ticks);

            // The durable record is already the new one: only reads are frozen.
            Assert.Equal(updated, _oracle.Find(grainId, "stale-snapshot")!.ETag);
        }

        var live = await ReminderTable.ReadRow(grainId, "stale-snapshot").WaitAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(live);
        Assert.Equal(BaseTime.AddMinutes(20).Ticks, live.StartAt.Ticks);
        Assert.Equal(TimeSpan.FromMinutes(9), live.Period);
    }

    [Fact]
    public async Task Oracle_StopAsync_WithCanceledToken_IsObservable()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReminderTable.StopAsync(cancellation.Token));
        Assert.False(_oracle.IsStarted);
        Assert.Equal(0, _oracle.OperationCount(ReminderTableOperationKind.Stop));
    }

    [Theory]
    [InlineData(ReminderTableOperationKind.Start)]
    [InlineData(ReminderTableOperationKind.Stop)]
    public async Task Oracle_CancellationInterruptsBlockedLifecycleOperation(ReminderTableOperationKind operationKind)
    {
        if (operationKind == ReminderTableOperationKind.Stop)
        {
            await ReminderTable.StartAsync(TestContext.Current.CancellationToken);
        }

        var expectedStarted = operationKind == ReminderTableOperationKind.Stop;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        waitCancellation.CancelAfter(TestConstants.InitTimeout);
        await using var gate = _oracle.BlockNext(operationKind);

        var operation = operationKind == ReminderTableOperationKind.Start
            ? ReminderTable.StartAsync(operationCancellation.Token)
            : ReminderTable.StopAsync(operationCancellation.Token);
        await gate.WaitUntilBlockedAsync(waitCancellation.Token);

        await operationCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Equal(expectedStarted, _oracle.IsStarted);
        Assert.Equal(0, _oracle.OperationCount(operationKind));
    }

    [Fact]
    public static void Oracle_InRange_ImplementsExclusiveBeginInclusiveEndAndWrapAround()
    {
        // Non-wrapping: (10, 20]
        Assert.False(IdealizedReminderTable.InRange(10, 10, 20));
        Assert.True(IdealizedReminderTable.InRange(11, 10, 20));
        Assert.True(IdealizedReminderTable.InRange(20, 10, 20));
        Assert.False(IdealizedReminderTable.InRange(21, 10, 20));

        // Wrapping: (20, 10]
        Assert.True(IdealizedReminderTable.InRange(21, 20, 10));
        Assert.True(IdealizedReminderTable.InRange(10, 20, 10));
        Assert.False(IdealizedReminderTable.InRange(11, 20, 10));
        Assert.False(IdealizedReminderTable.InRange(20, 20, 10));

        // Degenerate: begin == end covers the whole ring.
        Assert.True(IdealizedReminderTable.InRange(0, 0, 0));
        Assert.True(IdealizedReminderTable.InRange(uint.MaxValue, 0, 0));
    }

    [Fact(DisplayName = nameof(ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality))]
    public async Task ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality_CountSeven()
    {
        _oracle.ClearOperations();

        await base.RunReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(7, TestContext.Current.CancellationToken);

        var upserts = _oracle.Operations.Where(operation => operation.Kind == ReminderTableOperationKind.UpsertRow).ToList();
        Assert.Equal(7, upserts.Count);
        Assert.Equal(7, upserts.Select(operation => (operation.GrainId, operation.ReminderName)).Distinct().Count());
        Assert.All(upserts, operation => Assert.False(string.IsNullOrEmpty(operation.ResultETag)));

        var fullRangeRead = Assert.Single(
            _oracle.Operations,
            operation => operation.Kind == ReminderTableOperationKind.ReadRange && operation.Begin == 0 && operation.End == 0);
        Assert.Equal(7, fullRangeRead.ResultCount);
        Assert.Empty(_oracle.Snapshot());
        Assert.Equal(7, _oracle.Operations.Count(operation => operation.Kind == ReminderTableOperationKind.RemoveRow && operation.Succeeded));
    }

}
