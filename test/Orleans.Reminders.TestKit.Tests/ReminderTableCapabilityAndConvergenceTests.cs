using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

public sealed class ReminderTableCapabilityAndConvergenceTests
{
    [Fact]
    public static void BuiltInProviderProfiles_DeclareOnlyProvenGuarantees()
    {
        AssertProfile(ReminderTableProviderProfiles.AzureStorage("Azure"), sameIdentity: false, rotation: true, unsignedRanges: true);
        var cosmos = ReminderTableProviderProfiles.Cosmos("Cosmos");
        AssertProfile(cosmos, sameIdentity: false, rotation: true, unsignedRanges: true, restart: true);
        Assert.True(cosmos.SupportsConditionalUpsert);
        AssertProfile(ReminderTableProviderProfiles.AdoNet("ADO.NET"), sameIdentity: false, rotation: true, unsignedRanges: false, restart: true);
        var firestore = ReminderTableProviderProfiles.Firestore("Firestore");
        AssertProfile(firestore, sameIdentity: false, rotation: false, unsignedRanges: true);
        Assert.True(firestore.SupportsConditionalUpsert);
        Assert.Equal(TimeSpan.FromSeconds(2), firestore.ReadConvergenceTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(25), firestore.ReadConvergenceDelay);
        AssertProfile(ReminderTableProviderProfiles.DynamoDB("DynamoDB"), sameIdentity: true, rotation: true, unsignedRanges: true, restart: true);
        AssertProfile(ReminderTableProviderProfiles.Redis("Redis"), sameIdentity: true, rotation: true, unsignedRanges: true, restart: true);

        var inMemory = ReminderTableProviderProfiles.InMemory("InMemory");
        AssertProfile(inMemory, sameIdentity: true, rotation: true, unsignedRanges: true, restart: true);
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
            bool rotation,
            bool unsignedRanges,
            bool restart = false)
        {
            Assert.Equal(restart, profile.SupportsRestartAfterStop);
            Assert.Equal(sameIdentity, profile.SupportsSameIdentityConcurrentUpserts);
            Assert.True(profile.SupportsParallelDistinctRows);
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
    public static async Task ModelRunner_ClearsRowsAfterTheFinalGeneratedCase()
    {
        var table = new IdealizedReminderTable("FinalCleanup");
        var options = new ReminderTableModelBasedConformanceOptions
        {
            Capabilities = ReminderTableProviderProfiles.Oracle("FinalCleanup"),
            KeyPrefix = "final-cleanup",
            MaxDepth = 3,
            MaxSequenceLength = 3
        };

        await new ReminderTableModelBasedTestRunner(table, options).RunGeneratedConformanceTests();

        Assert.Empty(table.Snapshot());
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
}
