using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

/// <summary>
/// Proves that the conformance suite actually detects contract violations, by running it against reminder tables
/// which are intentionally wrong in exactly one way.
/// </summary>
/// <remarks>
/// Each faulty implementation is a thin decorator over a correct <see cref="IdealizedReminderTable"/> with a single
/// mutation applied, so a detected failure can only be attributed to that mutation. The assertions also pin the
/// diagnostic content required of every failure: provider, guarantee, operation, reminder identity, expected and
/// observed results, ETags, and the hash range under test.
/// </remarks>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders")]
public sealed class FaultyReminderTableTests
{
    [Fact]
    public static void FailureDiagnostics_IncludeProviderSequenceIdentityExpectedObservedETagsRangeAndWindow()
    {
        var grainId = GrainId.Create("diagnostic-grain", "diagnostic-key");
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var message = ReminderFailureReport.Create("DiagnosticProvider", "RangeAndSchedule", "ReadRows")
            .WithSequence(2, ["Upsert(alpha)", "ReadRows(10, 20)"])
            .WithIdentity(grainId, "alpha")
            .WithExpected("one owned reminder")
            .WithObserved("zero reminders")
            .WithETags("current", "previous", "supplied")
            .WithRange(10, 20)
            .WithOwnership("expected", [11])
            .WithSchedule(now.AddMinutes(5), TimeSpan.FromMinutes(3))
            .WithWindow(now, TimeSpan.FromMinutes(10))
            .Build();

        Assert.Contains("provider=DiagnosticProvider", message, StringComparison.Ordinal);
        Assert.Contains("guarantee=RangeAndSchedule", message, StringComparison.Ordinal);
        Assert.Contains("operation=ReadRows", message, StringComparison.Ordinal);
        Assert.Contains("sequence: #2 of [Upsert(alpha) -> ReadRows(10, 20)]", message, StringComparison.Ordinal);
        Assert.Contains($"GrainId={grainId}, ReminderName='alpha', UniformHash=", message, StringComparison.Ordinal);
        Assert.Contains("expected: one owned reminder", message, StringComparison.Ordinal);
        Assert.Contains("observed: zero reminders", message, StringComparison.Ordinal);
        Assert.Contains("etags: current='current', previous='previous', supplied='supplied'", message, StringComparison.Ordinal);
        Assert.Contains("range: (begin, end] = (10, 20], wrapAround=False", message, StringComparison.Ordinal);
        Assert.Contains("ownership.expected: 11", message, StringComparison.Ordinal);
        Assert.Contains("schedule: StartAt=2026-01-01T00:05:00.0000000Z", message, StringComparison.Ordinal);
        Assert.Contains("window: now=2026-01-01T00:00:00.0000000Z", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConstantETag_IsDetectedBy_DeclaredETagRotationGuarantee()
    {
        var runner = CreateRunner(new ConstantETagReminderTable(), "ConstantETag");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(runner.ReminderTable_UpsertRow_ReplacesETagOnEachWrite);

        AssertDiagnostics(exception.Message, "ConstantETag", nameof(ReminderTableTestRunner.ReminderTable_UpsertRow_ReplacesETagOnEachWrite), "UpsertRow");
        Assert.Contains("write #2 to return a fresh ETag", exception.Message, StringComparison.Ordinal);
        Assert.Contains("constant-etag", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ETagIgnoringRemoval_IsDetectedBy_StaleETagRemoval()
    {
        var runner = CreateRunner(new ETagIgnoringRemoveReminderTable(), "ETagIgnoringRemove");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(runner.ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow);

        AssertDiagnostics(exception.Message, "ETagIgnoringRemove", nameof(ReminderTableTestRunner.ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow), "RemoveRow");
        Assert.Contains("false when removing with a stale ETag", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RemoveRow returned true and deleted the row", exception.Message, StringComparison.Ordinal);
        Assert.Contains("schedule: StartAt=", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InclusiveBeginRange_IsDetectedBy_RangeBoundaryGuarantee()
    {
        var runner = CreateRunner(new InclusiveBeginRangeReminderTable(), "InclusiveBeginRange");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(runner.ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd);

        AssertDiagnostics(exception.Message, "InclusiveBeginRange", nameof(ReminderTableTestRunner.ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd), "ReadRows(low, middle)");
        Assert.Contains("expected: exact identities", exception.Message, StringComparison.Ordinal);
        Assert.Contains("wrapAround=False", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ownership.fixture:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ownership.returned:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DroppedWrapAround_IsDetectedBy_WrapAroundRangeGuarantee()
    {
        var runner = CreateRunner(new NoWrapAroundRangeReminderTable(), "NoWrapAround");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(runner.ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment);

        AssertDiagnostics(exception.Message, "NoWrapAround", nameof(ReminderTableTestRunner.ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment), "ReadRows(high, low)");
        Assert.Contains("expected: exact identities", exception.Message, StringComparison.Ordinal);
        Assert.Contains("wrapAround=True", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DroppedPeriod_IsDetectedBy_SchedulePersistenceGuarantee()
    {
        var runner = CreateRunner(new PeriodLosingReminderTable(), "PeriodLosing");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(runner.ReminderTable_UpsertRow_PersistsScheduleForPointRead);

        AssertDiagnostics(exception.Message, "PeriodLosing", nameof(ReminderTableTestRunner.ReminderTable_UpsertRow_PersistsScheduleForPointRead), "ReadRow");
        Assert.Contains("expected: Period=00:03:00", exception.Message, StringComparison.Ordinal);
        Assert.Contains("observed: Period=00:00:00", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovalWhichDoesNotDelete_IsDetectedBy_ConditionalRemovalGuarantee()
    {
        var runner = CreateRunner(new ResurrectingReminderTable(), "Resurrecting");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(runner.ReminderTable_RemoveRow_WithCurrentETag_RemovesRow);

        AssertDiagnostics(exception.Message, "Resurrecting", nameof(ReminderTableTestRunner.ReminderTable_RemoveRow_WithCurrentETag_RemovesRow), "ReadRow");
        Assert.Contains("null after a successful conditional removal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovalWhichDoesNotDelete_IsDetectedBy_ExplicitDeletionObservation()
    {
        var runner = CreateRunner(new ResurrectingReminderTable(), "Resurrecting");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(runner.ReminderTable_ReadRow_AfterRemoval_ReturnsNull);

        AssertDiagnostics(exception.Message, "Resurrecting", nameof(ReminderTableTestRunner.ReminderTable_ReadRow_AfterRemoval_ReturnsNull), "ReadRow");
        Assert.Contains("null once the row has actually been removed", exception.Message, StringComparison.Ordinal);
    }

    [Fact, TestCategory("ModelBased")]
    public async Task RemovalWhichDoesNotDelete_IsDetectedBy_ModelBasedSequences()
    {
        string? failureOutput = null;
        var runner = new ReminderTableModelBasedTestRunner(new ResurrectingReminderTable(), "Resurrecting", message => failureOutput = message);

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(runner.RunGeneratedConformanceTests);

        Assert.Contains("Model-based reminder table conformance test failed [provider=Resurrecting, seed=0]", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(failureOutput);
        Assert.Contains("The reminder is still readable after a successful removal", failureOutput, StringComparison.Ordinal);
        Assert.Contains("operation=Remove", failureOutput, StringComparison.Ordinal);
    }

    [Fact, TestCategory("ModelBased")]
    public async Task ConstantETag_IsDetectedBy_ModelBasedSequences()
    {
        string? failureOutput = null;
        var runner = new ReminderTableModelBasedTestRunner(new ConstantETagReminderTable(), "ConstantETag", message => failureOutput = message);

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(runner.RunGeneratedConformanceTests);

        Assert.Contains("Model-based reminder table conformance test failed [provider=ConstantETag, seed=0]", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(failureOutput);
        Assert.Contains("Upsert reused the previous ETag 'constant-etag'", failureOutput, StringComparison.Ordinal);
    }

    [Fact, TestCategory("ModelBased")]
    public async Task ConstantETag_IsAcceptedOnlyWhenRotationCapabilityIsNotDeclared()
    {
        var capabilities = ReminderTableProviderProfiles.Firestore("TimestampETag");
        var runner = new ReminderTableModelBasedTestRunner(new ConstantETagReminderTable(), capabilities);

        await runner.RunGeneratedConformanceTests();
    }

    [Fact, TestCategory("ModelBased")]
    public async Task CorrectImplementation_PassesTheSameGeneratedSequences()
    {
        // The negative tests above are only meaningful if the identical generated sequences pass for a correct table.
        var runner = new ReminderTableModelBasedTestRunner(new IdealizedReminderTable("Control"), "Control");

        await runner.RunGeneratedConformanceTests();
    }

    private static ReminderTableTestRunner CreateRunner(IReminderTable table, string providerName)
    {
        var capabilities = ReminderTableCapabilities.Portable(providerName);
        capabilities.SupportsSubSecondPrecision = true;
        capabilities.SupportsETagRotation = true;
        capabilities.SupportsParallelDistinctRows = true;
        capabilities.SupportsUnsignedHashRangeBoundaries = true;
        return new FaultyTableRunner(table, capabilities);
    }

    private static void AssertDiagnostics(string message, string provider, string guarantee, string operation)
    {
        Assert.Contains($"provider={provider}", message, StringComparison.Ordinal);
        Assert.Contains($"guarantee={guarantee}", message, StringComparison.Ordinal);
        Assert.Contains($"operation={operation}", message, StringComparison.Ordinal);
        Assert.Contains("reminder: GrainId=", message, StringComparison.Ordinal);
        Assert.Contains("UniformHash=", message, StringComparison.Ordinal);
        Assert.Contains("expected:", message, StringComparison.Ordinal);
        Assert.Contains("observed:", message, StringComparison.Ordinal);
    }

    private sealed class FaultyTableRunner(IReminderTable table, ReminderTableCapabilities capabilities)
        : ReminderTableTestRunner(table, capabilities);

    /// <summary>
    /// A correct in-memory reminder table which each fault below mutates in exactly one way.
    /// </summary>
    private abstract class DecoratedReminderTable : IReminderTable
    {
        protected DecoratedReminderTable(string name)
        {
            Inner = new IdealizedReminderTable(name);
        }

        protected IdealizedReminderTable Inner { get; }

        public virtual Task StartAsync(CancellationToken cancellationToken = default) => Inner.StartAsync(cancellationToken);

        public virtual Task StopAsync(CancellationToken cancellationToken = default) => Inner.StopAsync(cancellationToken);

        public virtual Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => Inner.ReadRow(grainId, reminderName);

        public virtual Task<ReminderTableData> ReadRows(GrainId grainId) => Inner.ReadRows(grainId);

        public virtual Task<ReminderTableData> ReadRows(uint begin, uint end) => Inner.ReadRows(begin, end);

        public virtual Task<string?> UpsertRow(ReminderEntry entry) => Inner.UpsertRow(entry);

        public virtual Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => Inner.RemoveRow(grainId, reminderName, eTag);

        public virtual Task TestOnlyClearTable() => Inner.TestOnlyClearTable();

        protected async Task<List<ReminderEntry>> ReadAllAsync()
        {
            var rows = await Inner.ReadRows(0, 0);
            return rows.Reminders.ToList();
        }
    }

    /// <summary>Fault: every upsert reports the same ETag, so replacement is unobservable.</summary>
    private sealed class ConstantETagReminderTable() : DecoratedReminderTable("ConstantETag")
    {
        private const string ConstantETag = "constant-etag";

        public override async Task<string?> UpsertRow(ReminderEntry entry)
        {
            await Inner.UpsertRow(entry);
            return ConstantETag;
        }

        public override async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
        {
            var entry = await Inner.ReadRow(grainId, reminderName);
            return entry is null ? null : WithConstantETag(entry);
        }

        public override async Task<ReminderTableData> ReadRows(GrainId grainId)
            => new((await Inner.ReadRows(grainId)).Reminders.Select(WithConstantETag).ToList());

        public override async Task<ReminderTableData> ReadRows(uint begin, uint end)
            => new((await Inner.ReadRows(begin, end)).Reminders.Select(WithConstantETag).ToList());

        public override async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            var current = Inner.Find(grainId, reminderName);
            return current is not null
                && string.Equals(eTag, ConstantETag, StringComparison.Ordinal)
                && await Inner.RemoveRow(grainId, reminderName, current.ETag);
        }

        private static ReminderEntry WithConstantETag(ReminderEntry entry)
        {
            entry.ETag = ConstantETag;
            return entry;
        }
    }

    /// <summary>Fault: removal ignores the supplied ETag, so stale-ETag removals succeed.</summary>
    private sealed class ETagIgnoringRemoveReminderTable() : DecoratedReminderTable("ETagIgnoringRemove")
    {
        public override async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            var current = Inner.Find(grainId, reminderName);
            return current is not null && await Inner.RemoveRow(grainId, reminderName, current.ETag);
        }
    }

    /// <summary>Fault: range reads treat <c>begin</c> as inclusive.</summary>
    private sealed class InclusiveBeginRangeReminderTable() : DecoratedReminderTable("InclusiveBeginRange")
    {
        public override async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            var all = await ReadAllAsync();
            return new ReminderTableData(all.Where(entry =>
            {
                var hash = entry.GrainId.GetUniformHashCode();
                return begin < end ? hash >= begin && hash <= end : hash >= begin || hash <= end;
            }).ToList());
        }
    }

    /// <summary>Fault: range reads never wrap around zero.</summary>
    private sealed class NoWrapAroundRangeReminderTable() : DecoratedReminderTable("NoWrapAround")
    {
        public override async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            var all = await ReadAllAsync();
            return new ReminderTableData(all.Where(entry =>
            {
                var hash = entry.GrainId.GetUniformHashCode();
                return hash > begin && hash <= end;
            }).ToList());
        }
    }

    /// <summary>Fault: the period is dropped on write.</summary>
    private sealed class PeriodLosingReminderTable() : DecoratedReminderTable("PeriodLosing")
    {
        public override Task<string?> UpsertRow(ReminderEntry entry) => Inner.UpsertRow(new ReminderEntry
        {
            GrainId = entry.GrainId,
            ReminderName = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = TimeSpan.Zero,
            ETag = entry.ETag
        });
    }

    /// <summary>Fault: removal reports success without deleting the row.</summary>
    private sealed class ResurrectingReminderTable() : DecoratedReminderTable("Resurrecting")
    {
        public override Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            var current = Inner.Find(grainId, reminderName);
            return Task.FromResult(current is not null && string.Equals(current.ETag, eTag, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ReminderTable_ReadRows_ForGrain_RejectsDuplicateIdentity()
    {
        var runner = CreateRunner(new DuplicateGrainEnumerationReminderTable(), "DuplicateGrainEnumeration");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            runner.ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders);

        AssertExactEnumerationFailure(
            exception,
            "DuplicateGrainEnumeration",
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders),
            "ReadRows(GrainId)",
            "differingField=ActualIdentityMultiplicity",
            "Observed entries contain duplicate identity");
    }

    [Fact]
    public async Task ReminderTable_ReadRows_ForRange_RejectsDuplicateIdentity()
    {
        var runner = CreateRunner(new DuplicateRangeEnumerationReminderTable(), "DuplicateRangeEnumeration");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            runner.ReminderTable_ReadRows_FullRange_ReturnsAllReminders);

        AssertExactEnumerationFailure(
            exception,
            "DuplicateRangeEnumeration",
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_FullRange_ReturnsAllReminders),
            "ReadRows(0, 0)",
            "differingField=ActualIdentityMultiplicity",
            "Observed entries contain duplicate identity");
    }

    [Fact]
    public async Task ReminderTable_ReadRows_ForGrain_RejectsCorruptedIdentity()
    {
        await AssertGrainEnumerationCorruptionAsync(
            EnumerationMutation.Identity,
            "differingField=Identity",
            "Observed unknown identity");
    }

    [Fact]
    public async Task ReminderTable_ReadRows_ForGrain_RejectsCorruptedStartAt()
    {
        await AssertGrainEnumerationCorruptionAsync(
            EnumerationMutation.StartAt,
            "differingField=StartAtTicks",
            "Reminder entry field 'StartAtTicks' differs");
    }

    [Fact]
    public async Task ReminderTable_ReadRows_ForGrain_RejectsCorruptedPeriod()
    {
        await AssertGrainEnumerationCorruptionAsync(
            EnumerationMutation.Period,
            "differingField=PeriodTicks",
            "Reminder entry field 'PeriodTicks' differs");
    }

    [Fact]
    public async Task ReminderTable_ReadRows_ForGrain_RejectsCorruptedETag()
    {
        await AssertGrainEnumerationCorruptionAsync(
            EnumerationMutation.ETag,
            "differingField=ETag",
            "Reminder entry field 'ETag' differs");
    }

    [Fact]
    public async Task ReminderTable_ReadRows_ForRange_RejectsCorruptedIdentity()
    {
        await AssertRangeEnumerationCorruptionAsync(
            EnumerationMutation.Identity,
            "differingField=Identity",
            "Observed unknown identity");
    }

    [Fact]
    public async Task ReminderTable_ReadRows_ForRange_RejectsCorruptedStartAt()
    {
        await AssertRangeEnumerationCorruptionAsync(
            EnumerationMutation.StartAt,
            "differingField=StartAtTicks",
            "Reminder entry field 'StartAtTicks' differs");
    }

    [Fact]
    public async Task ReminderTable_ReadRows_ForRange_RejectsCorruptedPeriod()
    {
        await AssertRangeEnumerationCorruptionAsync(
            EnumerationMutation.Period,
            "differingField=PeriodTicks",
            "Reminder entry field 'PeriodTicks' differs");
    }

    [Fact]
    public async Task ReminderTable_ReadRows_ForRange_RejectsCorruptedETag()
    {
        await AssertRangeEnumerationCorruptionAsync(
            EnumerationMutation.ETag,
            "differingField=ETag",
            "Reminder entry field 'ETag' differs");
    }

    [Fact]
    public async Task ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows_RejectsStaleEnumerationSchedule()
    {
        var runner = CreateRunner(new StaleLoadingWindowEnumerationReminderTable(), "StaleLoadingWindowEnumeration");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            runner.ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows);

        AssertExactEnumerationFailure(
            exception,
            "StaleLoadingWindowEnumeration",
            nameof(ReminderTableTestRunner.ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows),
            "ReadRows(0, 0)",
            "differingField=StartAtTicks",
            "Reminder entry field 'StartAtTicks' differs");
        Assert.Contains("window-move", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReminderTable_ReadRows_FullRange_RejectsDuplicateAndWrongCardinality()
    {
        var runner = CreateRunner(new DuplicateFullRangeCardinalityReminderTable(), "DuplicateFullRangeCardinality");

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            () => runner.ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(7));

        AssertExactEnumerationFailure(
            exception,
            "DuplicateFullRangeCardinality",
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality),
            "ReadRows(0, 0)",
            "differingField=ActualIdentityMultiplicity",
            "Observed entries contain duplicate identity");
        Assert.Contains("expected: exact identities and complete entries", exception.Message, StringComparison.Ordinal);
        Assert.Contains("observed:", exception.Message, StringComparison.Ordinal);
    }

    [Fact, TestCategory("ModelBased")]
    public async Task ReminderTable_ModelBasedGeneratedConformance_RejectsEnumerationOnlyPayloadMutation()
    {
        string? failureOutput = null;
        var options = new ReminderTableModelBasedConformanceOptions
        {
            ProviderName = "EnumerationOnlyPayloadMutation",
            KeyPrefix = "enumeration-only-payload",
            Seed = 17,
            MaxDepth = 3,
            MaxSequenceLength = 3
        };
        var runner = new ReminderTableModelBasedTestRunner(
            new EnumerationOnlyPayloadMutationReminderTable(),
            options,
            message => failureOutput = message);

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(runner.RunGeneratedConformanceTests);

        Assert.Contains(
            "Model-based reminder table conformance test failed [provider=EnumerationOnlyPayloadMutation, seed=17]",
            exception.Message,
            StringComparison.Ordinal);
        Assert.NotNull(failureOutput);
        Assert.Contains("ReadRows(", failureOutput, StringComparison.Ordinal);
        Assert.Contains("entry field 'StartAtTicks' differs", failureOutput, StringComparison.Ordinal);
        Assert.Contains("expected=", failureOutput, StringComparison.Ordinal);
        Assert.Contains("actual=", failureOutput, StringComparison.Ordinal);
        Assert.Contains("operation=Read", failureOutput, StringComparison.Ordinal);
    }

    private static async Task AssertGrainEnumerationCorruptionAsync(
        EnumerationMutation mutation,
        string differingField,
        string comparison)
    {
        var provider = $"CorruptGrainEnumeration{mutation}";
        var runner = CreateRunner(new CorruptGrainEnumerationReminderTable(mutation), provider);

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            runner.ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders);

        AssertExactEnumerationFailure(
            exception,
            provider,
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders),
            "ReadRows(GrainId)",
            differingField,
            comparison);
    }

    private static async Task AssertRangeEnumerationCorruptionAsync(
        EnumerationMutation mutation,
        string differingField,
        string comparison)
    {
        var provider = $"CorruptRangeEnumeration{mutation}";
        var runner = CreateRunner(new CorruptRangeEnumerationReminderTable(mutation), provider);

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            runner.ReminderTable_ReadRows_FullRange_ReturnsAllReminders);

        AssertExactEnumerationFailure(
            exception,
            provider,
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_FullRange_ReturnsAllReminders),
            "ReadRows(0, 0)",
            differingField,
            comparison);
    }

    private static void AssertExactEnumerationFailure(
        ReminderConformanceException exception,
        string provider,
        string guarantee,
        string operation,
        string differingField,
        string comparison)
    {
        AssertDiagnostics(exception.Message, provider, guarantee, operation);
        const string FieldPrefix = "differingField=";
        Assert.StartsWith(FieldPrefix, differingField, StringComparison.Ordinal);
        Assert.Contains(
            $"differingField: '{differingField[FieldPrefix.Length..]}'",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(comparison, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Expected=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Actual=", exception.Message, StringComparison.Ordinal);
    }

    private enum EnumerationMutation
    {
        Identity,
        StartAt,
        Period,
        ETag
    }

    private abstract class EnumerationMutationReminderTable(
        string name,
        EnumerationMutation mutation,
        bool mutateGrainReads,
        bool mutateRangeReads) : DecoratedReminderTable(name)
    {
        public override async Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            var rows = await Inner.ReadRows(grainId);
            return mutateGrainReads ? MutateFirst(rows, mutation) : rows;
        }

        public override async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            var rows = await Inner.ReadRows(begin, end);
            return mutateRangeReads ? MutateFirst(rows, mutation) : rows;
        }
    }

    private sealed class CorruptGrainEnumerationReminderTable(EnumerationMutation mutation)
        : EnumerationMutationReminderTable($"CorruptGrainEnumeration{mutation}", mutation, mutateGrainReads: true, mutateRangeReads: false);

    private sealed class CorruptRangeEnumerationReminderTable(EnumerationMutation mutation)
        : EnumerationMutationReminderTable($"CorruptRangeEnumeration{mutation}", mutation, mutateGrainReads: false, mutateRangeReads: true);

    private sealed class EnumerationOnlyPayloadMutationReminderTable()
        : EnumerationMutationReminderTable(
            "EnumerationOnlyPayloadMutation",
            EnumerationMutation.StartAt,
            mutateGrainReads: true,
            mutateRangeReads: true);

    private sealed class DuplicateGrainEnumerationReminderTable() : DecoratedReminderTable("DuplicateGrainEnumeration")
    {
        public override async Task<ReminderTableData> ReadRows(GrainId grainId)
            => DuplicateFirst(await Inner.ReadRows(grainId));
    }

    private sealed class DuplicateRangeEnumerationReminderTable() : DecoratedReminderTable("DuplicateRangeEnumeration")
    {
        public override async Task<ReminderTableData> ReadRows(uint begin, uint end)
            => DuplicateFirst(await Inner.ReadRows(begin, end));
    }

    private sealed class DuplicateFullRangeCardinalityReminderTable() : DecoratedReminderTable("DuplicateFullRangeCardinality")
    {
        public override async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            var rows = await Inner.ReadRows(begin, end);
            return begin == 0 && end == 0 ? DuplicateFirst(rows) : rows;
        }
    }

    private sealed class StaleLoadingWindowEnumerationReminderTable() : DecoratedReminderTable("StaleLoadingWindowEnumeration")
    {
        private DateTime? _firstStartAt;

        public override Task<string?> UpsertRow(ReminderEntry entry)
        {
            _firstStartAt ??= entry.StartAt;
            return Inner.UpsertRow(entry);
        }

        public override async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            var rows = await Inner.ReadRows(begin, end);
            if (_firstStartAt is not { } staleStartAt || rows.Reminders.Count == 0)
            {
                return rows;
            }

            var entries = rows.Reminders.ToList();
            entries[0] = Copy(entries[0], startAt: staleStartAt);
            return new ReminderTableData(entries);
        }
    }

    private static ReminderTableData DuplicateFirst(ReminderTableData rows)
    {
        var entries = rows.Reminders.ToList();
        if (entries.Count > 0)
        {
            entries.Add(Copy(entries[0]));
        }

        return new ReminderTableData(entries);
    }

    private static ReminderTableData MutateFirst(ReminderTableData rows, EnumerationMutation mutation)
    {
        var entries = rows.Reminders.ToList();
        if (entries.Count == 0)
        {
            return rows;
        }

        var original = entries[0];
        entries[0] = mutation switch
        {
            EnumerationMutation.Identity => Copy(original, reminderName: $"{original.ReminderName}-corrupted"),
            EnumerationMutation.StartAt => Copy(original, startAt: original.StartAt.AddMinutes(11)),
            EnumerationMutation.Period => Copy(original, period: original.Period.Add(TimeSpan.FromMinutes(13))),
            EnumerationMutation.ETag => Copy(original, etag: $"corrupted-{original.ETag}"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };
        return new ReminderTableData(entries);
    }

    private static ReminderEntry Copy(
        ReminderEntry entry,
        string? reminderName = null,
        DateTime? startAt = null,
        TimeSpan? period = null,
        string? etag = null)
        => new()
        {
            GrainId = entry.GrainId,
            ReminderName = reminderName ?? entry.ReminderName,
            StartAt = startAt ?? entry.StartAt,
            Period = period ?? entry.Period,
            ETag = etag ?? entry.ETag
        };
}
