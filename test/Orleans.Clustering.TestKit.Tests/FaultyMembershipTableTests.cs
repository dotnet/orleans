using Xunit;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class FaultyMembershipTableTests
{
    [Theory]
    [InlineData("IgnoreTableToken", "stale-table", "unexpectedly succeeded")]
    [InlineData("IgnoreRowToken", "stale-row", "unexpectedly succeeded")]
    [InlineData("FalseWriteChangesHeartbeat", "stale-row", "IAmAliveTime")]
    [InlineData("VersionJump", "insert", "commit integer")]
    [InlineData("HeartbeatRegression", "heartbeat", "IAmAliveTime")]
    [InlineData("OldPayloadRegression", "old-payload", "IAmAliveTime")]
    [InlineData("AliasInsert", "alias-insert", "Status")]
    [InlineData("AliasUpdate", "alias-update", "Status")]
    [InlineData("AliasRead", "alias-read", "Status")]
    [InlineData("MutateRetainedReads", "retained", "Status")]
    [InlineData("ClearOnInitialize", "initialize", "missing identity")]
    [InlineData("CleanupNonDead", "cleanup", "ineligible identity")]
    [InlineData("CleanupCutoffInclusive", "cleanup", "ineligible identity")]
    [InlineData("CleanupChangesRetainedFields", "cleanup", "HostName")]
    [InlineData("CleanupVersionRollback", "cleanup", "cleanup version")]
    [InlineData("CleanupRoundsExclusiveCutoff", "cleanup", "left eligible Dead rows")]
    [InlineData("DeleteConfiguredScope", "wrong-cluster", "deletion probe reported deleted populated history")]
    [InlineData("RefuseStatusWrite", "heartbeat-retry", "retry forbidden")]
    [InlineData("IgnoreNewUpdateHeartbeat", "successful-update", "IAmAliveTime")]
    [InlineData("IgnoreUpdatedVoteTime", "successful-update", "SuspectTimes")]
    [InlineData("PreserveClearedVotes", "successful-update", "SuspectTimes")]
    [InlineData("CrossClusterPointRead", "isolation", "table integer")]
    [InlineData("ResurrectCompactedRow", "missing-row", "unexpectedly succeeded")]
    [InlineData("DeletePrefixScopes", "delete-own", "deletion probe reported deleted populated history")]
    [InlineData("DeleteNoOp", "delete-own", "deletion left populated history")]
    [InlineData("DeleteNoOp", "wrong-cluster", "deletion left populated history")]
    [InlineData("DeletePartial", "delete-own", "deletion left populated history")]
    [InlineData("DeleteThenRejectForeign", "wrong-cluster", "rejected foreign deletion changed its target scope")]
    [InlineData("HeartbeatResurrectsCompactedRow", "missing-row", "unexpected identity")]
    public async Task DirectGuarantee_DeliberateMutant_FailsWithSpecificEvidence(string faultName, string scenario, string expectedMessage)
    {
        var fault = Enum.Parse<MembershipFault>(faultName);
        var control = new MembershipFaultController(fault);
        var failure = await Assert.ThrowsAsync<ClusteringConformanceException>(() => control.Fixture().RunAsync((f, ct) =>
        {
            var runner = new MembershipTableTestRunner(f);
            return scenario switch
            {
                "stale-table" => runner.UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects(ct),
                "stale-row" => runner.UpdateRow_StaleRowTokenWithFreshTableToken_ReturnsFalseWithoutSideEffects(ct),
                "insert" => runner.InsertRow_CurrentTableVersion_CommitsExactlyOneVersion(ct),
                "heartbeat" => runner.UpdateIAmAlive_NewerThenOlderAndRepeated_PreservesMaximum(ct),
                "old-payload" => runner.UpdateRow_FreshTokensAndOldHeartbeat_PreservesMaximum(ct),
                "alias-insert" => runner.InsertRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(ct),
                "alias-update" => runner.UpdateRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(ct),
                "alias-read" => runner.Reads_MutatingReturnedEntryAndSuspectList_DoesNotMutateStoredState(ct),
                "retained" => runner.Reads_RetainedObjectsRemainUnchangedAfterLaterWrites(ct),
                "initialize" => runner.InitializeMembershipTable_RepeatedWithData_PreservesCommittedState(ct),
                "cleanup" => runner.CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(ct),
                "wrong-cluster" => runner.DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(ct),
                "heartbeat-retry" => runner.UpdateRow_HeartbeatOnlyRowEtagConflict_AllowsOneDocumentedRereadRetry(ct),
                "successful-update" => runner.UpdateRow_CurrentTokens_CommitsExactlyOneVersion(ct),
                "isolation" => runner.Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated(ct),
                "missing-row" => runner.UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects(ct),
                "delete-own" => runner.DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct),
                _ => throw new InvalidOperationException(scenario)
            };
        }, TestContext.Current.CancellationToken));
        Assert.Contains(expectedMessage, failure.Message);
        Assert.Contains("provider=Deliberate-" + faultName, failure.Message);
        Assert.True(control.Injected > 0, "The intended mutant must actually execute, not fail during unrelated setup.");
        if (fault == MembershipFault.RefuseStatusWrite) Assert.Equal(1, control.UpdateCalls);
    }

    [Fact]
    public async Task HeartbeatAfterCompaction_BackendFailure_PropagatesUnchanged()
    {
        var control = new MembershipFaultController(MembershipFault.HeartbeatStorageFailure);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => control.Fixture().RunAsync(
            (fixture, ct) => new MembershipTableTestRunner(fixture)
                .UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects(ct),
            TestContext.Current.CancellationToken));

        Assert.Same(control.HeartbeatFailure, failure);
        Assert.True(control.CleanupCompleted);
        Assert.Empty(control.Backend.Partitions);
    }

    [Fact]
    public async Task CrossRowRace_IgnoredTableCondition_DetectsTwoWinners()
    {
        var control = new MembershipFaultController(MembershipFault.IgnoreTableToken);
        var failure = await Assert.ThrowsAsync<ClusteringConformanceException>(() => control.Fixture().RunAsync((f, ct) =>
            new MembershipTableTestRunner(f).ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner(ct), TestContext.Current.CancellationToken));
        Assert.Contains("exactly one winner", failure.Message);
        Assert.Equal(2, control.UpdateCalls);
        Assert.True(control.Injected > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicObservation_OldRowsWithNewVersion_IsRejectedAtReadBarrier(bool pointRead)
    {
        var control = new MembershipFaultController(pointRead ? MembershipFault.TornReadRow : MembershipFault.TornReadAll);
        var failure = await Assert.ThrowsAsync<ClusteringConformanceException>(() => control.Fixture().RunAsync((f, ct) =>
        {
            var runner = new MembershipTableTestRunner(f, concurrencyRowCount: 5);
            return pointRead ? runner.ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews(ct)
                : runner.ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(ct);
        }, TestContext.Current.CancellationToken));
        Assert.Contains("old row token", failure.Message);
        Assert.True(control.ReadStarted.Task.IsCompletedSuccessfully);
        Assert.True(control.Committed.Task.IsCompletedSuccessfully);
        Assert.True(control.Injected > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicCleanup_OldDeadRowWithNewVersion_IsRejectedAtReadBarrier(bool pointRead)
    {
        var control = new MembershipFaultController(pointRead ? MembershipFault.TornCleanupReadRow : MembershipFault.TornCleanupReadAll);
        var failure = await Assert.ThrowsAsync<ClusteringConformanceException>(() => control.Fixture().RunAsync((fixture, ct) =>
        {
            var runner = new MembershipTableTestRunner(fixture, concurrencyRowCount: 5);
            return pointRead ? runner.ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews(ct)
                : runner.ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(ct);
        }, TestContext.Current.CancellationToken));

        Assert.Contains("atomic cleanup still returned the removed identity", failure.Message);
        Assert.True(control.ReadStarted.Task.IsCompletedSuccessfully);
        Assert.True(control.Committed.Task.IsCompletedSuccessfully);
        Assert.True(control.Injected > 0);
    }

    [Fact]
    public async Task GeneratedModel_IndependentStaleRowMutant_IsDetectedWithOperationPrefix()
    {
        var control = new MembershipFaultController(MembershipFault.IgnoreRowToken);
        var runner = new MembershipTableModelBasedTestRunner(control.Fixture, "model-mutant");
        var failure = await Assert.ThrowsAsync<ClusteringConformanceException>(() => runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken));
        Assert.Contains("UpdateStaleRow", failure.Message);
        Assert.Contains("row-mode=previous", failure.Message);
        Assert.Contains("expected conditional false", failure.Message);
        Assert.True(control.Injected > 0);
    }
}
