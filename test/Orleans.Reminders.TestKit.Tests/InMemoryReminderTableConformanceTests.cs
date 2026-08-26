using Orleans.Hosting;
using Orleans.Reminders.TestKit;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

/// <summary>
/// Deploys the built-in in-memory (grain-based) reminder provider in an in-process cluster.
/// </summary>
public sealed class InMemoryReminderTableFixture : ReminderTableTestFixture, IAsyncLifetime
{
    protected override void ConfigureSilo(ISiloBuilder siloBuilder) => siloBuilder.UseInMemoryReminderService();

    public override ValueTask InitializeAsync() => base.InitializeAsync(TestContext.Current.CancellationToken);

    public override async ValueTask DisposeAsync()
    {
        using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await base.DisposeAsync(cleanupCancellation.Token);
    }
}

/// <summary>
/// Runs the shared direct conformance suite against the built-in in-memory reminder provider.
/// </summary>
/// <remarks>
/// This is the only built-in reminder provider which can be exercised without an external service, so it is the
/// reference integration for the TestKit. Other built-in providers adopt the same suite through their own
/// fixtures once their backing service is available.
/// </remarks>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Reminders"), TestCategory("ReminderTestKit")]
public sealed class InMemoryReminderTableConformanceTests : ReminderTableTestRunner, IClassFixture<InMemoryReminderTableFixture>
{
    private readonly InMemoryReminderTableFixture _fixture;

    public InMemoryReminderTableConformanceTests(InMemoryReminderTableFixture fixture)
        : base(fixture.ReminderTable, "InMemoryReminderTable")
    {
        _fixture = fixture;
        _fixture.EnsurePreconditionsMet();
    }

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

    [Fact]
    public Task InMemoryReminderTable_FullRangeReturnsExactCardinality()
        => base.RunReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(32, TestContext.Current.CancellationToken);

    [Fact, TestCategory("ModelBased")]
    public Task InMemoryReminderTable_ModelBasedGeneratedConformance()
        => new ReminderTableModelBasedTestRunner(ReminderTable, "InMemoryReminderTable").RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
}
