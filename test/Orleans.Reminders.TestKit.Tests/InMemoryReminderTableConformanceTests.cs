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
    public override Task ReminderTable_StartAsync_IsIdempotent() => base.ReminderTable_StartAsync_IsIdempotent();

    [Fact]
    public override Task ReminderTable_StopAsync_ThenRestart_ResumesService() => base.ReminderTable_StopAsync_ThenRestart_ResumesService();

    [Fact]
    public override Task ReminderTable_UpsertRow_ReturnsNewNonEmptyETag() => base.ReminderTable_UpsertRow_ReturnsNewNonEmptyETag();

    [Fact]
    public override Task ReminderTable_UpsertRow_PersistsScheduleForPointRead() => base.ReminderTable_UpsertRow_PersistsScheduleForPointRead();

    [Fact]
    public override Task ReminderTable_ReadRow_MissingReminder_ReturnsNull() => base.ReminderTable_ReadRow_MissingReminder_ReturnsNull();

    [Fact]
    public override Task ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders() => base.ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders();

    [Fact]
    public override Task ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty() => base.ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty();

    [Fact]
    public override Task ReminderTable_Identity_IsGrainIdAndReminderName() => base.ReminderTable_Identity_IsGrainIdAndReminderName();

    [Fact]
    public override Task ReminderTable_Identity_WithSpecialCharacters_RoundTrips() => base.ReminderTable_Identity_WithSpecialCharacters_RoundTrips();

    [Fact]
    public override Task ReminderTable_UpsertRow_ReplacesETagOnEachWrite() => base.ReminderTable_UpsertRow_ReplacesETagOnEachWrite();

    [Fact]
    public override Task ReminderTable_RemoveRow_WithCurrentETag_RemovesRow() => base.ReminderTable_RemoveRow_WithCurrentETag_RemovesRow();

    [Fact]
    public override Task ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow() => base.ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow();

    [Fact]
    public override Task ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse() => base.ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse();

    [Fact]
    public override Task ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess() => base.ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess();

    [Fact]
    public override Task ReminderTable_UpsertRow_UpdatesStartAtAndPeriod() => base.ReminderTable_UpsertRow_UpdatesStartAtAndPeriod();

    [Fact]
    public override Task ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows() => base.ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows();

    [Fact]
    public override Task ReminderTable_ReadRows_FullRange_ReturnsAllReminders() => base.ReminderTable_ReadRows_FullRange_ReturnsAllReminders();

    [Fact]
    public override Task ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering()
        => base.ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering();

    [Fact]
    public override Task ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd() => base.ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd();

    [Fact]
    public override Task ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment() => base.ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment();

    [Fact]
    public override Task ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder() => base.ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder();

    [Fact]
    public override Task ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder() => base.ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder();

    [Fact]
    public override Task ReminderTable_ReadRow_AfterRemoval_ReturnsNull() => base.ReminderTable_ReadRow_AfterRemoval_ReturnsNull();

    [Fact]
    public override Task ReminderTable_ConcurrentUpserts_ProduceDistinctETags() => base.ReminderTable_ConcurrentUpserts_ProduceDistinctETags();

    [Fact]
    public override Task ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated() => base.ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated();

    [Fact]
    public override Task ReminderTable_TestOnlyClearTable_RemovesAllReminders() => base.ReminderTable_TestOnlyClearTable_RemovesAllReminders();

    [Fact]
    public Task InMemoryReminderTable_FullRangeReturnsExactCardinality()
        => base.ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(32);

    [Fact, TestCategory("ModelBased")]
    public Task InMemoryReminderTable_ModelBasedGeneratedConformance()
        => new ReminderTableModelBasedTestRunner(ReminderTable, "InMemoryReminderTable").RunGeneratedConformanceTests();
}
