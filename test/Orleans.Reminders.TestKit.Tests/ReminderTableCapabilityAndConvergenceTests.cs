using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

public sealed class ReminderTableCapabilityAndConvergenceTests
{
    [Fact]
    public static void BuiltInProviderProfiles_DeclareOnlyProvenGuarantees()
    {
        AssertProfile(ReminderTableProviderProfiles.AzureStorage("Azure"), sameIdentity: false, parallelDistinctRows: true, rotation: true, unsignedRanges: true);
        var cosmos = ReminderTableProviderProfiles.Cosmos("Cosmos");
        AssertProfile(cosmos, sameIdentity: false, parallelDistinctRows: true, rotation: true, unsignedRanges: true, restart: true);
        Assert.True(cosmos.SupportsConditionalUpsert);
        AssertProfile(ReminderTableProviderProfiles.AdoNet("ADO.NET"), sameIdentity: false, parallelDistinctRows: false, rotation: true, unsignedRanges: false, restart: true);
        var firestore = ReminderTableProviderProfiles.Firestore("Firestore");
        AssertProfile(firestore, sameIdentity: false, parallelDistinctRows: true, rotation: false, unsignedRanges: true);
        Assert.True(firestore.SupportsConditionalUpsert);
        Assert.Equal(TimeSpan.FromSeconds(2), firestore.ReadConvergenceTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(25), firestore.ReadConvergenceDelay);
        AssertProfile(ReminderTableProviderProfiles.DynamoDB("DynamoDB"), sameIdentity: true, parallelDistinctRows: true, rotation: true, unsignedRanges: true, restart: true);
        AssertProfile(ReminderTableProviderProfiles.Redis("Redis"), sameIdentity: true, parallelDistinctRows: true, rotation: true, unsignedRanges: true, restart: true);

        var inMemory = ReminderTableProviderProfiles.InMemory("InMemory");
        AssertProfile(inMemory, sameIdentity: true, parallelDistinctRows: true, rotation: true, unsignedRanges: true, restart: true);
        Assert.True(inMemory.SupportsRestartAfterStop);
        Assert.True(inMemory.SupportsSubSecondPrecision);

        var oracle = ReminderTableProviderProfiles.Oracle("Oracle");
        Assert.True(oracle.SupportsRestartAfterStop);
        Assert.True(oracle.SupportsSameIdentityConcurrentUpserts);
        Assert.True(oracle.SupportsParallelDistinctRows);
        Assert.True(oracle.SupportsETagRotation);
        Assert.True(oracle.SupportsUnsignedHashRangeBoundaries);
        Assert.False(oracle.SupportsConditionalUpsert);

        var dynamo = ReminderTableProviderProfiles.DynamoDB("DynamoDB");
        Assert.Equal(TimeSpan.FromSeconds(10), dynamo.ReadConvergenceTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(100), dynamo.ReadConvergenceDelay);
        Assert.Equal(1, ReminderTableProviderProfiles.AdoNet("ADO.NET").CardinalityMutationBatchSize);
        Assert.All(
            new[]
            {
                ReminderTableProviderProfiles.AzureStorage("Azure"),
                ReminderTableProviderProfiles.Cosmos("Cosmos"),
                ReminderTableProviderProfiles.AdoNet("ADO.NET"),
                ReminderTableProviderProfiles.Redis("Redis")
            },
            profile => Assert.Equal(TimeSpan.Zero, profile.ReadConvergenceTimeout));

        static void AssertProfile(
            ReminderTableCapabilities profile,
            bool sameIdentity,
            bool parallelDistinctRows,
            bool rotation,
            bool unsignedRanges,
            bool restart = false)
        {
            Assert.Equal(restart, profile.SupportsRestartAfterStop);
            Assert.Equal(sameIdentity, profile.SupportsSameIdentityConcurrentUpserts);
            Assert.Equal(parallelDistinctRows, profile.SupportsParallelDistinctRows);
            Assert.Equal(rotation, profile.SupportsETagRotation);
            Assert.Equal(unsignedRanges, profile.SupportsUnsignedHashRangeBoundaries);
        }
    }

    [Fact]
    public static async Task PortableProfile_ProducesExplicitSkipReasonsForOptionalGuarantees()
    {
        var runner = new TestRunner(
            new IdealizedReminderTable("Portable"),
            ReminderTableCapabilities.Portable("Portable"));

        var reason = Assert.Contains(
            nameof(ReminderTableTestRunner.ReminderTable_StopAsync_ThenRestart_ResumesService),
            (IDictionary<string, string>)runner.SkippedGuarantees);
        Assert.Equal(
            $"Portable does not declare {nameof(ReminderTableCapabilities)}.{nameof(ReminderTableCapabilities.SupportsRestartAfterStop)}.",
            reason);

        var exception = await Assert.ThrowsAsync<Xunit.Sdk.SkipException>(async () =>
            await XunitReminderTableTestAdapter.RunAsync(
                runner,
                nameof(ReminderTableTestRunner.ReminderTable_StopAsync_ThenRestart_ResumesService),
                runner.ReminderTable_StopAsync_ThenRestart_ResumesService));
        Assert.Equal(reason, exception.Message);
    }

    [Fact]
    public static void AdoNetProfile_SkipsUnsignedRangesButRetainsFullRangeAndCardinality()
    {
        var runner = new TestRunner(
            new IdealizedReminderTable("ADO.NET"),
            ReminderTableProviderProfiles.AdoNet("ADO.NET"));

        Assert.DoesNotContain(
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_FullRange_ReturnsAllReminders),
            runner.SkippedGuarantees.Keys);
        Assert.DoesNotContain(
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality),
            runner.SkippedGuarantees.Keys);
        Assert.Contains(
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering),
            runner.SkippedGuarantees.Keys);
        Assert.Contains(
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd),
            runner.SkippedGuarantees.Keys);
        Assert.Contains(
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment),
            runner.SkippedGuarantees.Keys);
    }

    [Fact]
    public static async Task AdoNetProfile_DisablesParallelDistinctRowsAndBothInvocationPathsSkip()
    {
        var table = new IdealizedReminderTable("ADO.NET");
        var runner = new TestRunner(table, ReminderTableProviderProfiles.AdoNet("ADO.NET"));
        var guarantee = nameof(ReminderTableTestRunner.ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated);
        var expectedReason =
            $"ADO.NET does not declare {nameof(ReminderTableCapabilities)}.{nameof(ReminderTableCapabilities.SupportsParallelDistinctRows)}.";

        Assert.False(runner.Capabilities.SupportsParallelDistinctRows);
        Assert.Equal(expectedReason, runner.SkippedGuarantees[guarantee]);

        await runner.ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated();
        Assert.Empty(table.Operations);
        Assert.Empty(table.Snapshot());

        var exception = await Assert.ThrowsAsync<Xunit.Sdk.SkipException>(
            () => XunitReminderTableTestAdapter.RunAsync(
                runner,
                guarantee,
                runner.ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated));
        Assert.Equal(expectedReason, exception.Message);
        Assert.Empty(table.Operations);
    }

    [Fact]
    public static async Task DirectRunner_RetriesUntilEventuallyConsistentPointReadConverges()
    {
        var table = new EventuallyConsistentReminderTable(readsHiddenAfterMutation: 2);
        var capabilities = ReminderTableProviderProfiles.DynamoDB("EventuallyConsistent");
        capabilities.ReadConvergenceTimeout = TimeSpan.FromSeconds(1);
        capabilities.ReadConvergenceDelay = TimeSpan.FromMilliseconds(1);
        var runner = new TestRunner(table, capabilities);

        await runner.ReminderTable_UpsertRow_PersistsScheduleForPointRead();

        Assert.True(table.HiddenPointReadCount >= 2);
        Assert.True(table.VisiblePointReadCount >= 1);
    }

    [Fact]
    public static async Task DirectRunner_ReportsPreciseConvergenceTimeoutDiagnostics()
    {
        var table = new EventuallyConsistentReminderTable(readsHiddenAfterMutation: int.MaxValue);
        var capabilities = ReminderTableProviderProfiles.DynamoDB("NeverConverges");
        capabilities.ReadConvergenceTimeout = TimeSpan.FromMilliseconds(30);
        capabilities.ReadConvergenceDelay = TimeSpan.FromMilliseconds(5);
        var runner = new TestRunner(table, capabilities);

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            runner.ReminderTable_UpsertRow_PersistsScheduleForPointRead);

        Assert.Contains("Reminder read convergence timed out", exception.Message, StringComparison.Ordinal);
        Assert.Contains("provider=NeverConverges", exception.Message, StringComparison.Ordinal);
        Assert.Contains("operation=ReadRow", exception.Message, StringComparison.Ordinal);
        Assert.Contains("attempts=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Last observation: null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public static async Task DirectRunner_ConvergenceTimeoutBoundsTheInitialRead()
    {
        var table = new SlowReadReminderTable(TimeSpan.FromSeconds(2));
        var capabilities = ReminderTableProviderProfiles.DynamoDB("SlowRead");
        capabilities.ReadConvergenceTimeout = TimeSpan.FromMilliseconds(30);
        capabilities.ReadConvergenceDelay = TimeSpan.FromMilliseconds(5);
        var runner = new TestRunner(table, capabilities);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
            runner.ReminderTable_UpsertRow_PersistsScheduleForPointRead);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"The bounded read took {stopwatch.Elapsed}.");
        Assert.Contains("attempts=0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Last observation: <no completed read>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public static async Task AdoNetProfile_SerializesExactCardinalityMutations()
    {
        var table = new ConcurrencyTrackingReminderTable();
        var runner = new TestRunner(table, ReminderTableProviderProfiles.AdoNet("ADO.NET"));

        await runner.ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(7);

        Assert.Equal(1, table.MaximumConcurrentMutations);
    }

    [Fact, TestCategory("ModelBased")]
    public static async Task ModelRunner_RetriesEventuallyConsistentReadsUsingCapabilities()
    {
        var table = new EventuallyConsistentReminderTable(readsHiddenAfterMutation: 1);
        var capabilities = ReminderTableProviderProfiles.DynamoDB("EventuallyConsistentModel");
        capabilities.ReadConvergenceTimeout = TimeSpan.FromSeconds(1);
        capabilities.ReadConvergenceDelay = TimeSpan.FromMilliseconds(1);
        var options = new ReminderTableModelBasedConformanceOptions
        {
            Capabilities = capabilities,
            KeyPrefix = "eventual-model",
            MaxDepth = 3,
            MaxSequenceLength = 3
        };

        await new ReminderTableModelBasedTestRunner(table, options).RunGeneratedConformanceTests();

        Assert.True(table.HiddenPointReadCount + table.HiddenEnumerationCount > 0);
    }

    [Fact, TestCategory("ModelBased")]
    public static async Task ModelRunner_MissingRowRemovalUsesAnExistingValidETag()
    {
        var table = new ValidatingMissingRowETagTable();
        var options = new ReminderTableModelBasedConformanceOptions
        {
            Capabilities = ReminderTableProviderProfiles.Oracle("ValidETag"),
            KeyPrefix = "valid-missing-etag",
            MaxDepth = 3,
            MaxSequenceLength = 3
        };

        await new ReminderTableModelBasedTestRunner(table, options).RunGeneratedConformanceTests();

        Assert.True(table.MissingRowRemovalCount > 0);
    }

    [Fact, TestCategory("ModelBased")]
    public static async Task ModelRunner_ConditionalUpsertsUseResolvedETagsAndPreserveStateOnStaleRejection()
    {
        var nullRejecting = new ConditionalUpsertReminderTable(throwOnStale: false);
        var throwing = new ConditionalUpsertReminderTable(throwOnStale: true);

        await RunAsync(nullRejecting, "conditional-null");
        await RunAsync(throwing, "conditional-throw");

        AssertConditionalCoverage(nullRejecting, expectExceptions: false);
        AssertConditionalCoverage(throwing, expectExceptions: true);

        static Task RunAsync(ConditionalUpsertReminderTable table, string prefix)
        {
            var options = new ReminderTableModelBasedConformanceOptions
            {
                Capabilities = ReminderTableCapabilities.Strict(prefix),
                KeyPrefix = prefix,
                MaxDepth = 3,
                MaxSequenceLength = 3
            };
            return new ReminderTableModelBasedTestRunner(table, options).RunGeneratedConformanceTests();
        }

        static void AssertConditionalCoverage(ConditionalUpsertReminderTable table, bool expectExceptions)
        {
            Assert.True(table.CurrentConditionalUpsertCount > 0);
            Assert.True(table.StaleConditionalUpsertCount > 0);
            Assert.Equal(table.StaleConditionalUpsertCount, table.VerifiedUnchangedReadbackCount);
            Assert.Equal(expectExceptions ? table.StaleConditionalUpsertCount : 0, table.StaleExceptionCount);
            Assert.All(table.CurrentSuppliedETags, etag => Assert.Contains(etag, table.IssuedETags));
            Assert.All(table.StaleSuppliedETags, etag => Assert.Contains(etag, table.IssuedETags));
            Assert.Contains(table.OperationTrace, operation => operation.StartsWith("Current:", StringComparison.Ordinal));
            Assert.Contains(table.OperationTrace, operation => operation.StartsWith("Stale:", StringComparison.Ordinal));
        }
    }

    [Fact, TestCategory("ModelBased")]
    public static async Task ModelRunner_BlindProfileNeverSuppliesETagOnUpsert()
    {
        var table = new BlindUpsertTrackingReminderTable();
        var options = new ReminderTableModelBasedConformanceOptions
        {
            Capabilities = ReminderTableProviderProfiles.Oracle("Blind"),
            KeyPrefix = "blind",
            MaxDepth = 3,
            MaxSequenceLength = 3
        };

        await new ReminderTableModelBasedTestRunner(table, options).RunGeneratedConformanceTests();

        Assert.NotEmpty(table.SuppliedETags);
        Assert.All(table.SuppliedETags, Assert.Null);
    }

    [Fact, TestCategory("ModelBased")]
    public static async Task AdoNetProfile_ModelRunnerExecutesOnlyFullRangeMode()
    {
        var table = new RangeTrackingReminderTable();
        var options = new ReminderTableModelBasedConformanceOptions
        {
            Capabilities = ReminderTableProviderProfiles.AdoNet("ADO.NET"),
            KeyPrefix = "ado-range-model",
            MaxDepth = 3,
            MaxSequenceLength = 3
        };

        await new ReminderTableModelBasedTestRunner(table, options).RunGeneratedConformanceTests();

        Assert.NotEmpty(table.RangeReads);
        Assert.All(table.RangeReads, range => Assert.Equal((0u, 0u), range));
        Assert.Empty(table.Snapshot());
    }

    [Fact, TestCategory("ModelBased")]
    public static async Task ModelRunner_ClearsRowsAfterTheFinalGeneratedCase()
    {
        var table = new IdealizedReminderTable("FinalCleanup");
        var capabilities = ReminderTableProviderProfiles.Oracle("FinalCleanup");
        capabilities.ReadConvergenceTimeout = TimeSpan.FromMilliseconds(100);
        capabilities.ReadConvergenceDelay = TimeSpan.FromMilliseconds(1);
        var options = new ReminderTableModelBasedConformanceOptions
        {
            Capabilities = capabilities,
            KeyPrefix = "final-cleanup",
            MaxDepth = 3,
            MaxSequenceLength = 3
        };

        await new ReminderTableModelBasedTestRunner(table, options).RunGeneratedConformanceTests();

        Assert.Empty(table.Snapshot());
        var finalOperations = table.Operations.TakeLast(2).ToArray();
        Assert.Collection(
            finalOperations,
            operation => Assert.Equal(ReminderTableOperationKind.ClearTable, operation.Kind),
            operation =>
            {
                Assert.Equal(ReminderTableOperationKind.ReadRange, operation.Kind);
                Assert.Equal(0u, operation.Begin);
                Assert.Equal(0u, operation.End);
                Assert.Equal(0, operation.ResultCount);
            });
    }

    private sealed class TestRunner(IReminderTable table, ReminderTableCapabilities capabilities)
        : ReminderTableTestRunner(table, capabilities);

    private sealed class EventuallyConsistentReminderTable(int readsHiddenAfterMutation) : IReminderTable
    {
        private readonly IdealizedReminderTable _inner = new("EventuallyConsistent");
        private int _remainingHiddenReads;

        public int HiddenPointReadCount { get; private set; }

        public int VisiblePointReadCount { get; private set; }

        public int HiddenEnumerationCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

        public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
        {
            if (_remainingHiddenReads > 0)
            {
                _remainingHiddenReads--;
                HiddenPointReadCount++;
                return null;
            }

            VisiblePointReadCount++;
            return await _inner.ReadRow(grainId, reminderName);
        }

        public async Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            if (_remainingHiddenReads > 0)
            {
                _remainingHiddenReads--;
                HiddenEnumerationCount++;
                return new ReminderTableData([]);
            }

            return await _inner.ReadRows(grainId);
        }

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            if (_remainingHiddenReads > 0)
            {
                _remainingHiddenReads--;
                HiddenEnumerationCount++;
                return new ReminderTableData([]);
            }

            return await _inner.ReadRows(begin, end);
        }

        public async Task<string?> UpsertRow(ReminderEntry entry)
        {
            var result = await _inner.UpsertRow(entry);
            _remainingHiddenReads = readsHiddenAfterMutation;
            return result;
        }

        public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            var result = await _inner.RemoveRow(grainId, reminderName, eTag);
            if (result)
            {
                _remainingHiddenReads = readsHiddenAfterMutation;
            }

            return result;
        }

        public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();
    }

    private sealed class ValidatingMissingRowETagTable : IReminderTable
    {
        private readonly IdealizedReminderTable _inner = new("ValidatingMissingRowETag");
        private readonly HashSet<string> _issuedETags = new(StringComparer.Ordinal);

        public int MissingRowRemovalCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => _inner.ReadRow(grainId, reminderName);

        public Task<ReminderTableData> ReadRows(GrainId grainId) => _inner.ReadRows(grainId);

        public Task<ReminderTableData> ReadRows(uint begin, uint end) => _inner.ReadRows(begin, end);

        public async Task<string?> UpsertRow(ReminderEntry entry)
        {
            var etag = await _inner.UpsertRow(entry);
            Assert.False(string.IsNullOrEmpty(etag));
            _issuedETags.Add(etag!);
            return etag;
        }

        public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            if (await _inner.ReadRow(grainId, reminderName) is null)
            {
                Assert.Contains(eTag, _issuedETags);
                MissingRowRemovalCount++;
            }

            return await _inner.RemoveRow(grainId, reminderName, eTag);
        }

        public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();
    }

    private sealed class SlowReadReminderTable(TimeSpan readDelay) : IReminderTable
    {
        private readonly IdealizedReminderTable _inner = new("SlowRead");

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

        public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
        {
            await Task.Delay(readDelay);
            return await _inner.ReadRow(grainId, reminderName);
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId) => _inner.ReadRows(grainId);

        public Task<ReminderTableData> ReadRows(uint begin, uint end) => _inner.ReadRows(begin, end);

        public Task<string?> UpsertRow(ReminderEntry entry) => _inner.UpsertRow(entry);

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => _inner.RemoveRow(grainId, reminderName, eTag);

        public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();
    }

    private sealed class ConcurrencyTrackingReminderTable : IReminderTable
    {
        private readonly IdealizedReminderTable _inner = new("ConcurrencyTracking");
        private int _activeMutations;
        private int _maximumConcurrentMutations;

        public int MaximumConcurrentMutations => Volatile.Read(ref _maximumConcurrentMutations);

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => _inner.ReadRow(grainId, reminderName);

        public Task<ReminderTableData> ReadRows(GrainId grainId) => _inner.ReadRows(grainId);

        public Task<ReminderTableData> ReadRows(uint begin, uint end) => _inner.ReadRows(begin, end);

        public async Task<string?> UpsertRow(ReminderEntry entry)
        {
            EnterMutation();
            try
            {
                await Task.Delay(5);
                return await _inner.UpsertRow(entry);
            }
            finally
            {
                Interlocked.Decrement(ref _activeMutations);
            }

        }

        public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            EnterMutation();
            try
            {
                await Task.Delay(5);
                return await _inner.RemoveRow(grainId, reminderName, eTag);
            }
            finally
            {
                Interlocked.Decrement(ref _activeMutations);
            }
        }

        public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();

        private void EnterMutation()
        {
            var active = Interlocked.Increment(ref _activeMutations);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumConcurrentMutations);
                if (observed >= active)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maximumConcurrentMutations, active, observed) != observed);
        }
    }

    private sealed class ConditionalUpsertReminderTable(bool throwOnStale) : IReminderTable
    {
        private readonly IdealizedReminderTable _inner = new("ConditionalUpsert");
        private (GrainId GrainId, string ReminderName, DateTime StartAt, TimeSpan Period, string? ETag)? _expectedAfterRejection;

        public int CurrentConditionalUpsertCount { get; private set; }

        public int StaleConditionalUpsertCount { get; private set; }

        public int StaleExceptionCount { get; private set; }

        public int VerifiedUnchangedReadbackCount { get; private set; }

        public List<string> CurrentSuppliedETags { get; } = [];

        public List<string> StaleSuppliedETags { get; } = [];

        public HashSet<string> IssuedETags { get; } = new(StringComparer.Ordinal);

        public List<string> OperationTrace { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

        public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
        {
            var result = await _inner.ReadRow(grainId, reminderName);
            if (_expectedAfterRejection is { } expected)
            {
                Assert.NotNull(result);
                Assert.Equal(expected.GrainId, result.GrainId);
                Assert.Equal(expected.ReminderName, result.ReminderName);
                Assert.Equal(expected.StartAt, result.StartAt);
                Assert.Equal(expected.Period, result.Period);
                Assert.Equal(expected.ETag, result.ETag);
                VerifiedUnchangedReadbackCount++;
                _expectedAfterRejection = null;
            }

            return result;
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId) => _inner.ReadRows(grainId);

        public Task<ReminderTableData> ReadRows(uint begin, uint end) => _inner.ReadRows(begin, end);

        public async Task<string?> UpsertRow(ReminderEntry entry)
        {
            var current = await _inner.ReadRow(entry.GrainId, entry.ReminderName);
            if (current is null || string.IsNullOrEmpty(entry.ETag))
            {
                OperationTrace.Add($"Blind:{entry.ReminderName}:{entry.Period}");
                return RecordIssuedETag(await _inner.UpsertRow(entry));
            }

            if (string.Equals(entry.ETag, current.ETag, StringComparison.Ordinal))
            {
                CurrentConditionalUpsertCount++;
                CurrentSuppliedETags.Add(entry.ETag);
                OperationTrace.Add($"Current:{entry.ReminderName}:{entry.ETag}:{entry.Period}");
                return RecordIssuedETag(await _inner.UpsertRow(entry));
            }

            Assert.Contains(entry.ETag, IssuedETags);
            StaleConditionalUpsertCount++;
            StaleSuppliedETags.Add(entry.ETag);
            OperationTrace.Add($"Stale:{entry.ReminderName}:{entry.ETag}:{entry.Period}");
            _expectedAfterRejection = (current.GrainId, current.ReminderName, current.StartAt, current.Period, current.ETag);
            if (throwOnStale)
            {
                StaleExceptionCount++;
                throw new InvalidOperationException("The conditional ETag is stale.");
            }

            return null;
        }

        private string? RecordIssuedETag(string? etag)
        {
            Assert.False(string.IsNullOrEmpty(etag));
            IssuedETags.Add(etag!);
            return etag;
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
            => _inner.RemoveRow(grainId, reminderName, eTag);

        public async Task TestOnlyClearTable()
        {
            _expectedAfterRejection = null;
            await _inner.TestOnlyClearTable();
        }
    }

    private sealed class BlindUpsertTrackingReminderTable : IReminderTable
    {
        private readonly IdealizedReminderTable _inner = new("BlindUpsertTracking");

        public List<string?> SuppliedETags { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => _inner.ReadRow(grainId, reminderName);

        public Task<ReminderTableData> ReadRows(GrainId grainId) => _inner.ReadRows(grainId);

        public Task<ReminderTableData> ReadRows(uint begin, uint end) => _inner.ReadRows(begin, end);

        public Task<string?> UpsertRow(ReminderEntry entry)
        {
            SuppliedETags.Add(entry.ETag);
            return _inner.UpsertRow(entry);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
            => _inner.RemoveRow(grainId, reminderName, eTag);

        public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();
    }

    private sealed class RangeTrackingReminderTable : IReminderTable
    {
        private readonly IdealizedReminderTable _inner = new("RangeTracking");

        public List<(uint Begin, uint End)> RangeReads { get; } = [];

        public IReadOnlyList<ReminderTableRecord> Snapshot() => _inner.Snapshot();

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => _inner.ReadRow(grainId, reminderName);

        public Task<ReminderTableData> ReadRows(GrainId grainId) => _inner.ReadRows(grainId);

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            RangeReads.Add((begin, end));
            return _inner.ReadRows(begin, end);
        }

        public Task<string?> UpsertRow(ReminderEntry entry) => _inner.UpsertRow(entry);

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
            => _inner.RemoveRow(grainId, reminderName, eTag);

        public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();
    }
}
