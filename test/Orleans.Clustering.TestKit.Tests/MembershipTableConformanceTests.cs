using Xunit;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableConformanceTests
{
    private static Task Run(Func<MembershipTableTestRunner, CancellationToken, Task> test)
    {
        var fixture = new IdealizedMembershipBackend().Fixture();
        return fixture.RunAsync((f, ct) => test(new(f, seed: 17), ct), TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task InsertRow_CurrentTableVersion_CommitsExactlyOneVersion() => Run((r, ct) => r.InsertRow_CurrentTableVersion_CommitsExactlyOneVersion(ct));
    [Fact]
    public Task UpdateRow_CurrentTokens_CommitsExactlyOneVersion() => Run((r, ct) => r.UpdateRow_CurrentTokens_CommitsExactlyOneVersion(ct));
    [Fact]
    public Task Reads_SameVersion_PreservesRetainedCanonicalFields() => Run((r, ct) => r.Reads_SameVersion_PreservesRetainedCanonicalFields(ct));
    [Fact]
    public Task Reads_MaySkipCommittedVersions_WithoutSkippingHistoryValidation() => Run((r, ct) => r.Reads_MaySkipCommittedVersions_WithoutSkippingHistoryValidation(ct));
    [Fact]
    public Task Lifecycle_DeadRemainsTerminalAfterCompaction_SuccessorUsesNewGeneration() => Run((r, ct) => r.Lifecycle_DeadRemainsTerminalAfterCompaction_SuccessorUsesNewGeneration(ct));
    [Fact]
    public Task UpdateIAmAlive_OwnerWrites_PreserveCanonicalFieldsAndTableVersion() => Run((r, ct) => r.UpdateIAmAlive_OwnerWrites_PreserveCanonicalFieldsAndTableVersion(ct));
    [Fact]
    public Task UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange() => Run((r, ct) => r.UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange(ct));
    [Fact]
    public Task UpdateRow_TokensCapturedBeforeHeartbeat_CommitVoteChange() => Run((r, ct) => r.UpdateRow_TokensCapturedBeforeHeartbeat_CommitVoteChange(ct));
    [Fact]
    public Task Handles_IndependentlyConstructed_ShareCommittedBackingState() => Run((r, ct) => r.Handles_IndependentlyConstructed_ShareCommittedBackingState(ct));
    [Fact]
    public Task InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects() => Run((r, ct) => r.InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects(ct));
    [Fact]
    public Task UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects() => Run((r, ct) => r.UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects(ct));
    [Fact]
    public Task UpdateRow_StaleSnapshotAfterSameRowCommit_ReturnsFalseWithoutSideEffects() => Run((r, ct) => r.UpdateRow_StaleSnapshotAfterSameRowCommit_ReturnsFalseWithoutSideEffects(ct));
    [Fact]
    public Task InsertRow_DuplicateIdentity_ReturnsFalseWithoutSideEffects() => Run((r, ct) => r.InsertRow_DuplicateIdentity_ReturnsFalseWithoutSideEffects(ct));
    [Fact]
    public Task UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects() => Run((r, ct) => r.UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects(ct));
    [Fact]
    public Task ReadRow_AndReadAll_AgreeForPresentAndAbsentIdentities() => Run((r, ct) => r.ReadRow_AndReadAll_AgreeForPresentAndAbsentIdentities(ct));
    [Fact]
    public Task Reads_RetainedObjectsRemainUnchangedAfterLaterWrites() => Run((r, ct) => r.Reads_RetainedObjectsRemainUnchangedAfterLaterWrites(ct));
    [Fact]
    public Task InsertRow_MutatingInputAndSuspectList_DoesNotMutateStoredState() => Run((r, ct) => r.InsertRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(ct));
    [Fact]
    public Task UpdateRow_MutatingInputAndSuspectList_DoesNotMutateStoredState() => Run((r, ct) => r.UpdateRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(ct));
    [Fact]
    public Task Reads_MutatingReturnedEntryAndSuspectList_DoesNotMutateStoredState() => Run((r, ct) => r.Reads_MutatingReturnedEntryAndSuspectList_DoesNotMutateStoredState(ct));
    [Fact]
    public Task ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner() => Run((r, ct) => r.ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner(ct));
    [Fact]
    public Task ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews() => Run((r, ct) => r.ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(ct));
    [Fact]
    public Task ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews() => Run((r, ct) => r.ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews(ct));

    [Theory]
    [InlineData(5, false, false)]
    [InlineData(1001, false, false)]
    [InlineData(4096, false, false)]
    [InlineData(4096, true, false)]
    [InlineData(4096, false, true)]
    public async Task ConcurrentReadSetup_UsesLinearPointReadsAndIndependentFinalView(int rows, bool tableVersionRowEtags, bool physicalRowEtags)
    {
        var backend = new IdealizedMembershipBackend { TableVersionRowEtags = tableVersionRowEtags, PhysicalRowEtags = physicalRowEtags };
        await backend.Fixture().RunAsync(async (fixture, ct) =>
        {
            await new MembershipTableTestRunner(fixture, concurrencyRowCount: rows).SeedConcurrentRows(ct);
            Assert.Equal(rows, backend.Inserts);
            Assert.Equal(rows + 1, backend.PointReads);
            Assert.Equal(2, backend.FullReads);
            Assert.Equal(3 * rows, backend.RowsObserved);
            var partition = backend.Partitions[fixture.ClusterId];
            Assert.Equal(rows, partition.Version);
            Assert.Equal(rows, partition.Rows.Count);
            for (var i = 1; i <= rows; i++)
                Assert.Equal($"host-{i}", partition.Rows[MembershipTableTestData.CreateEntry(i).SiloAddress].Item1.HostName);
        }, TestContext.Current.CancellationToken);
    }
    [Fact]
    public Task InitializeMembershipTable_RepeatedWithData_PreservesCommittedState() => Run((r, ct) => r.InitializeMembershipTable_RepeatedWithData_PreservesCommittedState(ct));
    [Fact]
    public Task Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated() => Run((r, ct) => r.Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated(ct));
    [Fact]
    public Task CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows() => Run((r, ct) => r.CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(ct));

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    public async Task Cleanup_PublishedHeartbeats_PreserveEligibilityAcrossStorageModels(
        bool versionedCleanup, bool lagHeartbeatReads, bool separateHeartbeatStorage, bool preserveHeartbeatOnFullWrite)
    {
        var backend = new IdealizedMembershipBackend
        {
            SeparateHeartbeatStorage = separateHeartbeatStorage,
            PreserveHeartbeatOnFullWrite = preserveHeartbeatOnFullWrite,
            VersionedCleanup = versionedCleanup,
            CleanupBatchSize = 1,
            LagHeartbeatReads = lagHeartbeatReads
        };
        await backend.Fixture().RunAsync(
            (fixture, ct) => new MembershipTableTestRunner(fixture).CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(ct),
            TestContext.Current.CancellationToken);

        Assert.Equal(6, backend.CleanupBatches);
        Assert.Equal(14, backend.HeartbeatWrites.Count);
        Assert.Equal(9, backend.VersionedUpdates);
        Assert.All(backend.HeartbeatWrites, write => Assert.NotEqual(SiloStatus.Dead, write.Status));
        Assert.All(backend.HeartbeatWrites.GroupBy(write => (write.Cluster, write.Identity)),
            writes => Assert.Single(writes.Select(write => write.Owner).Distinct()));
        if (lagHeartbeatReads) Assert.True(backend.LaggedHeartbeatReads > 0);
        Assert.Empty(backend.Partitions);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
    }

    [Fact]
    public async Task Cleanup_SeparateAtomicBatches_AdvanceVersionForEachDeletion()
    {
        var backend = new IdealizedMembershipBackend { CleanupBatchSize = 1, TableVersionRowEtags = true };
        await backend.Fixture().RunAsync(
            (fixture, ct) => new MembershipTableTestRunner(fixture).CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(ct),
            TestContext.Current.CancellationToken);

        Assert.Equal(6, backend.CleanupBatches);
        Assert.Empty(backend.Partitions);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
    }

    [Fact]
    public Task DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster() => Run((r, ct) => r.DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct));
    [Fact]
    public Task DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster() => Run((r, ct) => r.DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(ct));
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task MembershipTable_ModelBased_GeneratedConformance(bool versionedCleanup, bool lagHeartbeatReads, bool physicalRowEtags)
    {
        var backend = new IdealizedMembershipBackend
        {
            TerminalDeletion = true,
            VersionedCleanup = versionedCleanup,
            CleanupBatchSize = 1,
            LagHeartbeatReads = lagHeartbeatReads,
            PhysicalRowEtags = physicalRowEtags
        };
        await new MembershipTableModelBasedTestRunner(() => backend.Fixture(), "Idealized").RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
        Assert.NotEmpty(backend.HeartbeatWrites);
        Assert.Contains(backend.HeartbeatWrites.GroupBy(write => (write.Cluster, write.Identity)), history =>
            history.Select(write => write.Time).SequenceEqual(new[] { MembershipTableTestData.T1, MembershipTableTestData.T2, MembershipTableTestData.T2 }));
        foreach (var history in backend.HeartbeatWrites.GroupBy(write => (write.Cluster, write.Identity)))
        {
            var owner = history.First().Owner;
            var previous = DateTime.MinValue;
            foreach (var write in history)
            {
                Assert.Same(owner, write.Owner);
                Assert.InRange(write.Status, Orleans.Runtime.SiloStatus.Created, Orleans.Runtime.SiloStatus.Stopping);
                Assert.True(write.Time >= previous);
                previous = write.Time;
            }
        }
        Assert.Equal(0, backend.OperationsAfterDeletion);
        if (lagHeartbeatReads) Assert.True(backend.LaggedHeartbeatReads > 0);
        if (physicalRowEtags)
        {
            Assert.Equal(0, backend.RowConditionChecks);
            Assert.Equal(backend.HeartbeatWrites.Count, backend.HeartbeatRowMetadataChanges);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupStrategies_PassHistoryLivenessMissingRowAndConcurrentReadGuarantees(bool versionedCleanup)
    {
        var backend = new IdealizedMembershipBackend { VersionedCleanup = versionedCleanup, CleanupBatchSize = 1, TableVersionRowEtags = true };
        Func<MembershipTableTestRunner, CancellationToken, Task>[] scenarios =
        [
            (runner, ct) => runner.Reads_SameVersion_PreservesRetainedCanonicalFields(ct),
            (runner, ct) => runner.Lifecycle_DeadRemainsTerminalAfterCompaction_SuccessorUsesNewGeneration(ct),
            (runner, ct) => runner.UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange(ct),
            (runner, ct) => runner.UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects(ct),
            (runner, ct) => runner.ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(ct),
            (runner, ct) => runner.ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews(ct),
            (runner, ct) => runner.CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(ct)
        ];
        foreach (var scenario in scenarios)
            await backend.Fixture().RunAsync(
                (fixture, ct) => scenario(new MembershipTableTestRunner(fixture, concurrencyRowCount: 5), ct),
                TestContext.Current.CancellationToken);
        Assert.Equal(13, backend.CleanupBatches);
        Assert.Empty(backend.Partitions);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
    }

    [Fact]
    public async Task AdministrativeDeletion_UsesFreshOwnersForSubsequentHistories()
    {
        var firstOwner = new IdealizedMembershipBackend { TerminalDeletion = true };
        await firstOwner.Fixture().RunAsync(
            (fixture, ct) => new MembershipTableTestRunner(fixture)
                .DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct),
            TestContext.Current.CancellationToken);
        Assert.Empty(firstOwner.Partitions);
        Assert.Equal(2, firstOwner.Deletes);
        Assert.Equal(0, firstOwner.OperationsAfterDeletion);
        var secondOwner = new IdealizedMembershipBackend { TerminalDeletion = true };
        await secondOwner.Fixture().RunAsync(
            (fixture, ct) => new MembershipTableTestRunner(fixture)
                .DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(ct),
            TestContext.Current.CancellationToken);
        Assert.Empty(secondOwner.Partitions);
        Assert.Equal(2, secondOwner.Deletes);
        Assert.Equal(0, secondOwner.OperationsAfterDeletion);
        Assert.Equal(firstOwner.CreatedHandles, firstOwner.DisposedHandles);
        Assert.Equal(secondOwner.CreatedHandles, secondOwner.DisposedHandles);
    }

    [Fact]
    public async Task ForeignClusterDeletion_ExplicitScopeRejection_PreservesBothClusters()
    {
        var backend = new IdealizedMembershipBackend { RejectForeignClusterDeletion = true, TerminalDeletion = true };
        await backend.Fixture().RunAsync(
            (fixture, ct) => new MembershipTableTestRunner(fixture)
                .DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(ct),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, backend.ForeignClusterDeletionRejections);
        Assert.Equal(0, backend.OperationsAfterDeletion);
        Assert.Empty(backend.Partitions);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerSequencedHeartbeats_PreserveCanonicalFieldsAndTableVersionDespiteReadLag(bool lagHeartbeatReads)
    {
        var backend = new IdealizedMembershipBackend { LagHeartbeatReads = lagHeartbeatReads };
        await backend.Fixture().RunAsync(async (fixture, ct) =>
        {
            await new MembershipTableTestRunner(fixture).UpdateIAmAlive_OwnerWrites_PreserveCanonicalFieldsAndTableVersion(ct);
            Assert.Equal(3, backend.HeartbeatWrites.Count);
            Assert.All(backend.HeartbeatWrites, write => Assert.Same(fixture.First, write.Owner));
            Assert.Equal(new[] { MembershipTableTestData.T1, MembershipTableTestData.T2, MembershipTableTestData.T2 },
                backend.HeartbeatWrites.Select(write => write.Time));
        }, TestContext.Current.CancellationToken);
        Assert.Empty(backend.Partitions);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreHeartbeatTokens_CommitCanonicalWritesWithLagOrHeartbeatOverwrite(bool lagHeartbeatReads, bool preserveHeartbeatOnFullWrite)
    {
        var backend = new IdealizedMembershipBackend
        {
            LagHeartbeatReads = lagHeartbeatReads,
            PreserveHeartbeatOnFullWrite = preserveHeartbeatOnFullWrite
        };
        foreach (var statusChange in new[] { false, true })
        {
            await backend.Fixture().RunAsync(async (f, ct) =>
            {
                var runner = new MembershipTableTestRunner(f);
                if (statusChange) await runner.UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange(ct);
                else await runner.UpdateRow_TokensCapturedBeforeHeartbeat_CommitVoteChange(ct);
                var stored = backend.Partitions[f.ClusterId].Rows[MembershipTableTestData.CreateEntry(1).SiloAddress].Item1;
                Assert.Equal(preserveHeartbeatOnFullWrite ? MembershipTableTestData.T2 : MembershipTableTestData.T0, stored.IAmAliveTime);
            }, TestContext.Current.CancellationToken);
        }
        Assert.Equal(2, backend.VersionedUpdates);
        if (lagHeartbeatReads) Assert.True(backend.LaggedHeartbeatReads > 0);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
    }

    [Fact]
    public async Task TableVersionDerivedRowEtags_PreserveTableConflictsAndAtomicViewChecks()
    {
        var backend = new IdealizedMembershipBackend { TableVersionRowEtags = true };
        Func<MembershipTableTestRunner, CancellationToken, Task>[] scenarios =
        [
            (runner, ct) => runner.InsertRow_CurrentTableVersion_CommitsExactlyOneVersion(ct),
            (runner, ct) => runner.UpdateRow_CurrentTokens_CommitsExactlyOneVersion(ct),
            (runner, ct) => runner.UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects(ct),
            (runner, ct) => runner.UpdateRow_StaleSnapshotAfterSameRowCommit_ReturnsFalseWithoutSideEffects(ct),
            (runner, ct) => runner.UpdateIAmAlive_OwnerWrites_PreserveCanonicalFieldsAndTableVersion(ct),
            (runner, ct) => runner.UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange(ct),
            (runner, ct) => runner.UpdateRow_TokensCapturedBeforeHeartbeat_CommitVoteChange(ct),
            (runner, ct) => runner.ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner(ct),
            (runner, ct) => runner.ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(ct),
            (runner, ct) => runner.ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews(ct),
        ];
        foreach (var scenario in scenarios)
        {
            await backend.Fixture().RunAsync(
                (fixture, ct) => scenario(new MembershipTableTestRunner(fixture, concurrencyRowCount: 5), ct),
                TestContext.Current.CancellationToken);
        }

        await new MembershipTableModelBasedTestRunner(() => backend.Fixture(), "TableVersionRowEtags")
            .RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
        Assert.Empty(backend.Partitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalRowMetadata_WithVersionOnlyCas_PassesAllDirectGuarantees(bool versionedCleanup)
    {
        var backend = new IdealizedMembershipBackend
        {
            PhysicalRowEtags = true,
            VersionedCleanup = versionedCleanup,
            CleanupBatchSize = 1,
            LagHeartbeatReads = true
        };
        Func<MembershipTableTestRunner, CancellationToken, Task>[] scenarios =
        [
            (r, ct) => r.InsertRow_CurrentTableVersion_CommitsExactlyOneVersion(ct),
            (r, ct) => r.UpdateRow_CurrentTokens_CommitsExactlyOneVersion(ct),
            (r, ct) => r.Reads_SameVersion_PreservesRetainedCanonicalFields(ct),
            (r, ct) => r.Reads_MaySkipCommittedVersions_WithoutSkippingHistoryValidation(ct),
            (r, ct) => r.Lifecycle_DeadRemainsTerminalAfterCompaction_SuccessorUsesNewGeneration(ct),
            (r, ct) => r.UpdateIAmAlive_OwnerWrites_PreserveCanonicalFieldsAndTableVersion(ct),
            (r, ct) => r.UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange(ct),
            (r, ct) => r.UpdateRow_TokensCapturedBeforeHeartbeat_CommitVoteChange(ct),
            (r, ct) => r.Handles_IndependentlyConstructed_ShareCommittedBackingState(ct),
            (r, ct) => r.InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects(ct),
            (r, ct) => r.UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects(ct),
            (r, ct) => r.UpdateRow_StaleSnapshotAfterSameRowCommit_ReturnsFalseWithoutSideEffects(ct),
            (r, ct) => r.InsertRow_DuplicateIdentity_ReturnsFalseWithoutSideEffects(ct),
            (r, ct) => r.UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects(ct),
            (r, ct) => r.ReadRow_AndReadAll_AgreeForPresentAndAbsentIdentities(ct),
            (r, ct) => r.Reads_RetainedObjectsRemainUnchangedAfterLaterWrites(ct),
            (r, ct) => r.InsertRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(ct),
            (r, ct) => r.UpdateRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(ct),
            (r, ct) => r.Reads_MutatingReturnedEntryAndSuspectList_DoesNotMutateStoredState(ct),
            (r, ct) => r.ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner(ct),
            (r, ct) => r.ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(ct),
            (r, ct) => r.ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews(ct),
            (r, ct) => r.InitializeMembershipTable_RepeatedWithData_PreservesCommittedState(ct),
            (r, ct) => r.Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated(ct),
            (r, ct) => r.CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(ct),
            (r, ct) => r.DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct),
            (r, ct) => r.DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(ct)
        ];
        foreach (var scenario in scenarios)
            await backend.Fixture().RunAsync(
                (fixture, ct) => scenario(new MembershipTableTestRunner(fixture, concurrencyRowCount: 5), ct),
                TestContext.Current.CancellationToken);

        Assert.Equal(27, scenarios.Length);
        Assert.Equal(0, backend.RowConditionChecks);
        Assert.True(backend.HeartbeatRowMetadataChanges > 0);
        Assert.True(backend.LaggedHeartbeatReads > 0);
        Assert.Empty(backend.Partitions);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
    }
}
