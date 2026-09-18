using Orleans.Clustering.TestKit;
using TestExtensions;
using Xunit;

namespace UnitTests.MembershipTests;

[TestCategory("Membership"), TestCategory("ClusteringConformance")]
public abstract class MembershipTableConformanceTestsBase
{
    protected abstract MembershipTableTestFixture CreateConformanceFixture();

    protected virtual int ConformanceConcurrencyRowCount => 128;

    private Task RunConformance(Func<MembershipTableTestRunner, CancellationToken, Task> scenario)
        => CreateConformanceFixture().RunAsync(
            (fixture, cancellationToken) => scenario(
                new MembershipTableTestRunner(
                    fixture,
                    seed: 17,
                    output: message => TestContext.Current.TestOutputHelper?.WriteLine(message),
                    concurrencyRowCount: ConformanceConcurrencyRowCount),
                cancellationToken),
            TestContext.Current.CancellationToken);

    [Fact]
    public Task InsertRow_CurrentTableVersion_CommitsExactlyOneVersion()
        => RunConformance((runner, ct) => runner.InsertRow_CurrentTableVersion_CommitsExactlyOneVersion(ct));

    [Fact]
    public Task UpdateRow_CurrentTokens_CommitsExactlyOneVersion()
        => RunConformance((runner, ct) => runner.UpdateRow_CurrentTokens_CommitsExactlyOneVersion(ct));

    [Fact]
    public Task Reads_SameVersion_PreservesRetainedCanonicalFields()
        => RunConformance((runner, ct) => runner.Reads_SameVersion_PreservesRetainedCanonicalFields(ct));

    [Fact]
    public Task Reads_MaySkipCommittedVersions_WithoutSkippingHistoryValidation()
        => RunConformance((runner, ct) => runner.Reads_MaySkipCommittedVersions_WithoutSkippingHistoryValidation(ct));

    [Fact]
    public Task Lifecycle_DeadRemainsTerminalAfterCompaction_SuccessorUsesNewGeneration()
        => RunConformance((runner, ct) => runner.Lifecycle_DeadRemainsTerminalAfterCompaction_SuccessorUsesNewGeneration(ct));

    [Fact]
    public Task UpdateIAmAlive_NewerThenOlderAndRepeated_PreservesMaximum()
        => RunConformance((runner, ct) => runner.UpdateIAmAlive_NewerThenOlderAndRepeated_PreservesMaximum(ct));

    [Fact]
    public Task UpdateRow_FreshTokensAndOldHeartbeat_PreservesMaximum()
        => RunConformance((runner, ct) => runner.UpdateRow_FreshTokensAndOldHeartbeat_PreservesMaximum(ct));

    [Fact]
    public Task UpdateRow_HeartbeatOnlyRowEtagConflict_AllowsOneDocumentedRereadRetry()
        => RunConformance((runner, ct) => runner.UpdateRow_HeartbeatOnlyRowEtagConflict_AllowsOneDocumentedRereadRetry(ct));

    [Fact]
    public Task Handles_IndependentlyConstructed_ShareCommittedBackingState()
        => RunConformance((runner, ct) => runner.Handles_IndependentlyConstructed_ShareCommittedBackingState(ct));

    [Fact]
    public Task InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects()
        => RunConformance((runner, ct) => runner.InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects(ct));

    [Fact]
    public Task UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects()
        => RunConformance((runner, ct) => runner.UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects(ct));

    [Fact]
    public Task UpdateRow_StaleRowTokenWithFreshTableToken_ReturnsFalseWithoutSideEffects()
        => RunConformance((runner, ct) => runner.UpdateRow_StaleRowTokenWithFreshTableToken_ReturnsFalseWithoutSideEffects(ct));

    [Fact]
    public Task InsertRow_DuplicateIdentity_ReturnsFalseWithoutSideEffects()
        => RunConformance((runner, ct) => runner.InsertRow_DuplicateIdentity_ReturnsFalseWithoutSideEffects(ct));

    [Fact]
    public Task UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects()
        => RunConformance((runner, ct) => runner.UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects(ct));

    [Fact]
    public Task ReadRow_AndReadAll_AgreeForPresentAndAbsentIdentities()
        => RunConformance((runner, ct) => runner.ReadRow_AndReadAll_AgreeForPresentAndAbsentIdentities(ct));

    [Fact]
    public Task Reads_RetainedObjectsRemainUnchangedAfterLaterWrites()
        => RunConformance((runner, ct) => runner.Reads_RetainedObjectsRemainUnchangedAfterLaterWrites(ct));

    [Fact]
    public Task InsertRow_MutatingInputAndSuspectList_DoesNotMutateStoredState()
        => RunConformance((runner, ct) => runner.InsertRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(ct));

    [Fact]
    public Task UpdateRow_MutatingInputAndSuspectList_DoesNotMutateStoredState()
        => RunConformance((runner, ct) => runner.UpdateRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(ct));

    [Fact]
    public Task Reads_MutatingReturnedEntryAndSuspectList_DoesNotMutateStoredState()
        => RunConformance((runner, ct) => runner.Reads_MutatingReturnedEntryAndSuspectList_DoesNotMutateStoredState(ct));

    [Fact]
    public Task ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner()
        => RunConformance((runner, ct) => runner.ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner(ct));

    [Fact]
    public Task ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews()
        => RunConformance((runner, ct) => runner.ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(ct));

    [Fact]
    public Task ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews()
        => RunConformance((runner, ct) => runner.ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews(ct));

    [Fact]
    public Task InitializeMembershipTable_RepeatedWithData_PreservesCommittedState()
        => RunConformance((runner, ct) => runner.InitializeMembershipTable_RepeatedWithData_PreservesCommittedState(ct));

    [Fact]
    public Task Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated()
        => RunConformance((runner, ct) => runner.Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated(ct));

    [Fact]
    public Task CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows()
        => RunConformance((runner, ct) => runner.CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(ct));

    [Fact]
    public Task DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster()
        => RunConformance((runner, ct) => runner.DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct));

    [Fact]
    public Task DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster()
        => RunConformance((runner, ct) => runner.DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(ct));

    [Fact, TestCategory("ModelBased")]
    public Task MembershipTable_ModelBased_GeneratedConformance()
        => new MembershipTableModelBasedTestRunner(
            CreateConformanceFixture,
            new MembershipTableModelBasedConformanceOptions
            {
                ProviderName = GetType().Name,
                Seed = 17,
                MaxDepth = 3,
                MaxSequenceLength = 3
            },
            output: message => TestContext.Current.TestOutputHelper?.WriteLine(message))
            .RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
}
