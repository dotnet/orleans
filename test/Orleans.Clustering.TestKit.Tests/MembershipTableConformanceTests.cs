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
    public Task UpdateIAmAlive_NewerThenOlderAndRepeated_PreservesMaximum() => Run((r, ct) => r.UpdateIAmAlive_NewerThenOlderAndRepeated_PreservesMaximum(ct));
    [Fact]
    public Task UpdateRow_FreshTokensAndOldHeartbeat_PreservesMaximum() => Run((r, ct) => r.UpdateRow_FreshTokensAndOldHeartbeat_PreservesMaximum(ct));
    [Fact]
    public Task UpdateRow_HeartbeatOnlyRowEtagConflict_AllowsOneDocumentedRereadRetry() => Run((r, ct) => r.UpdateRow_HeartbeatOnlyRowEtagConflict_AllowsOneDocumentedRereadRetry(ct));
    [Fact]
    public Task Handles_IndependentlyConstructed_ShareCommittedBackingState() => Run((r, ct) => r.Handles_IndependentlyConstructed_ShareCommittedBackingState(ct));
    [Fact]
    public Task InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects() => Run((r, ct) => r.InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects(ct));
    [Fact]
    public Task UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects() => Run((r, ct) => r.UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects(ct));
    [Fact]
    public Task UpdateRow_StaleRowTokenWithFreshTableToken_ReturnsFalseWithoutSideEffects() => Run((r, ct) => r.UpdateRow_StaleRowTokenWithFreshTableToken_ReturnsFalseWithoutSideEffects(ct));
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
    [Fact]
    public Task InitializeMembershipTable_RepeatedWithData_PreservesCommittedState() => Run((r, ct) => r.InitializeMembershipTable_RepeatedWithData_PreservesCommittedState(ct));
    [Fact]
    public Task Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated() => Run((r, ct) => r.Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated(ct));
    [Fact]
    public Task CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows() => Run((r, ct) => r.CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(ct));

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
    [InlineData(false)]
    [InlineData(true)]
    public Task MembershipTable_ModelBased_GeneratedConformance(bool versionedCleanup)
    {
        var backend = new IdealizedMembershipBackend { RequireInitializationAfterDeletion = true, VersionedCleanup = versionedCleanup, CleanupBatchSize = 1 };
        return new MembershipTableModelBasedTestRunner(() => backend.Fixture(), "Idealized").RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
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
            (runner, ct) => runner.UpdateRow_FreshTokensAndOldHeartbeat_PreservesMaximum(ct),
            (runner, ct) => runner.UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects(ct),
            (runner, ct) => runner.ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(ct),
            (runner, ct) => runner.ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews(ct),
            (runner, ct) => runner.CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(ct)
        ];
        foreach (var scenario in scenarios)
            await backend.Fixture().RunAsync(
                (fixture, ct) => scenario(new MembershipTableTestRunner(fixture, concurrencyRowCount: 5), ct),
                TestContext.Current.CancellationToken);
        Assert.Equal(14, backend.CleanupBatches);
        Assert.Empty(backend.Partitions);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
    }

    [Fact]
    public async Task AdministrativeDeletion_InitializesNewHistoryBeforeReading()
    {
        var backend = new IdealizedMembershipBackend { RequireInitializationAfterDeletion = true };
        await backend.Fixture().RunAsync(
            (fixture, ct) => new MembershipTableTestRunner(fixture)
                .DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct),
            TestContext.Current.CancellationToken);
        await backend.Fixture().RunAsync(
            (fixture, ct) => new MembershipTableTestRunner(fixture)
                .DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(ct),
            TestContext.Current.CancellationToken);
        Assert.Empty(backend.Partitions);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
    }

    [Fact]
    public async Task ForeignClusterDeletion_ExplicitScopeRejection_PreservesBothClusters()
    {
        var backend = new IdealizedMembershipBackend { RejectForeignClusterDeletion = true };
        await backend.Fixture().RunAsync(
            (fixture, ct) => new MembershipTableTestRunner(fixture)
                .DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(ct),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, backend.ForeignClusterDeletionRejections);
        Assert.Empty(backend.Partitions);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatEtagVariants_OldPayloadAndSingleRereadRetryAreUnconditional(bool changesRowEtag)
    {
        var backend = new IdealizedMembershipBackend { ChangeHeartbeatEtag = changesRowEtag };
        var messages = new List<string>();
        await backend.Fixture().RunAsync((f, ct) => new MembershipTableTestRunner(f, output: messages.Add)
            .UpdateRow_HeartbeatOnlyRowEtagConflict_AllowsOneDocumentedRereadRetry(ct), TestContext.Current.CancellationToken);
        Assert.Equal(changesRowEtag ? 2 : 1, backend.VersionedUpdates);
        if (changesRowEtag) Assert.Contains(messages, m => m.Contains("refresh only row ETag", StringComparison.Ordinal));
        else Assert.Empty(messages);
        await backend.Fixture().RunAsync((f, ct) => new MembershipTableTestRunner(f)
            .UpdateRow_FreshTokensAndOldHeartbeat_PreservesMaximum(ct), TestContext.Current.CancellationToken);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
    }

    [Fact]
    public async Task TableVersionDerivedRowEtags_PreserveIndependentConflictAndAtomicViewChecks()
    {
        var backend = new IdealizedMembershipBackend { TableVersionRowEtags = true };
        Func<MembershipTableTestRunner, CancellationToken, Task>[] scenarios =
        [
            (runner, ct) => runner.InsertRow_CurrentTableVersion_CommitsExactlyOneVersion(ct),
            (runner, ct) => runner.UpdateRow_CurrentTokens_CommitsExactlyOneVersion(ct),
            (runner, ct) => runner.UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects(ct),
            (runner, ct) => runner.UpdateRow_StaleRowTokenWithFreshTableToken_ReturnsFalseWithoutSideEffects(ct),
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
}
