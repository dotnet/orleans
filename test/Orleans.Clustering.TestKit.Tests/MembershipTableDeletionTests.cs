using Xunit;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableDeletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_VerifiesOriginalBackendBeforeDisposingAnyOwner(bool terminal)
    {
        var backend = new IdealizedMembershipBackend { TerminalDeletion = terminal, DestructiveDisposal = true };
        var probes = new List<(string Cluster, bool Deleted)>();
        var fixture = new MembershipTableTestFixture("native-probe",
            (_, cluster, _) => ValueTask.FromResult(new MembershipTableTestHandle(backend.Create(cluster),
                () => backend.DisposeHandleAsync(cluster))), async (cluster, ct) =>
            {
                Assert.Equal(0, backend.DisposedHandles);
                var deleted = await backend.IsDeletedAsync(cluster, ct);
                probes.Add((cluster, deleted));
                return deleted;
            });
        await fixture.RunAsync(async (f, ct) =>
        {
            await new MembershipTableTestRunner(f).DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct);
            Assert.Equal(new[] { (f.ClusterId, false), (f.ClusterId, true), (f.OtherClusterId, false) }, probes);
            Assert.Equal(3, backend.CreatedHandles);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.InitializeAsync(ct).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.CreateAdditionalHandleAsync(f.ClusterId, ct).AsTask());
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2, backend.Deletes);
        Assert.Equal(3, backend.DisposedHandles);
        Assert.Equal(0, backend.OperationsAfterDeletion);
        Assert.Empty(backend.Partitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_NoOpFailsBeforeDestructiveOwnerDisposal(bool foreign)
    {
        var backend = new IdealizedMembershipBackend { TerminalDeletion = true, DestructiveDisposal = true };
        var control = new MembershipFaultController(MembershipFault.DeleteNoOp) { Backend = backend };
        var failure = await Assert.ThrowsAsync<ClusteringConformanceException>(() =>
            control.Fixture().RunAsync(async (fixture, ct) =>
            {
                try
                {
                    var runner = new MembershipTableTestRunner(fixture);
                    if (foreign) await runner.DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(ct);
                    else await runner.DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct);
                }
                catch (ClusteringConformanceException)
                {
                    Assert.Equal(0, backend.DisposedHandles);
                    Assert.Equal(2, backend.Partitions.Count);
                    Assert.All(backend.Partitions.Values, partition => Assert.NotEmpty(partition.Rows));
                    throw;
                }
            }, TestContext.Current.CancellationToken));
        Assert.Contains("deletion left populated history", failure.Message);
        Assert.Equal(0, backend.Deletes);
        Assert.Equal(3, backend.DisposedHandles);
        Assert.Empty(backend.Partitions);
    }

    [Fact]
    public async Task Probe_ConstantDeletedResultFailsAgainstSeededHistory()
    {
        var backend = new IdealizedMembershipBackend();
        var fixture = new MembershipTableTestFixture("invalid-probe", backend.Create, (_, _) => ValueTask.FromResult(true));
        var failure = await Assert.ThrowsAsync<ClusteringConformanceException>(() => fixture.RunAsync(
            (f, ct) => new MembershipTableTestRunner(f).DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct),
            TestContext.Current.CancellationToken));
        Assert.Contains("deletion probe reported deleted populated history", failure.Message);
        Assert.Equal(1, backend.Deletes);
        Assert.Equal(2, Assert.Single(backend.Partitions).Value.Rows.Count);
    }

    [Fact]
    public async Task Probe_InfrastructureFailurePropagatesBeforeDisposalAndRetiresDeletedHandles()
    {
        var backend = new IdealizedMembershipBackend { TerminalDeletion = true };
        var expected = new InvalidOperationException("native-probe-failed");
        var fixture = new MembershipTableTestFixture("probe-error",
            (_, cluster, _) => ValueTask.FromResult(new MembershipTableTestHandle(backend.Create(cluster),
                () => backend.DisposeHandleAsync(cluster))),
            (cluster, ct) =>
            {
                if (backend.DeletedScopes.Contains(cluster))
                {
                    Assert.Equal(0, backend.DisposedHandles);
                    throw expected;
                }
                return backend.IsDeletedAsync(cluster, ct);
            });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync(
            (f, ct) => new MembershipTableTestRunner(f).DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct),
            TestContext.Current.CancellationToken));
        Assert.Same(expected, failure);
        Assert.Equal(2, backend.Deletes);
        Assert.Equal(3, backend.DisposedHandles);
        Assert.Equal(0, backend.OperationsAfterDeletion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_InfrastructureFailureIsNeverSuccessfulDeletion(bool committed)
    {
        var backend = new IdealizedMembershipBackend { TerminalDeletion = true, DestructiveDisposal = true };
        var control = new MembershipFaultController(committed ? MembershipFault.DeleteCommitThenFailure : MembershipFault.DeleteStorageFailure)
        {
            Backend = backend
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => control.Fixture().RunAsync(
            (f, ct) => new MembershipTableTestRunner(f).DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct),
            TestContext.Current.CancellationToken));
        Assert.Same(control.DeletionFailure, failure);
        Assert.Equal(committed ? 2 : 0, backend.Deletes);
        Assert.Equal(0, backend.OperationsAfterDeletion);
        Assert.Equal(3, backend.DisposedHandles);
        Assert.Empty(backend.Partitions);
    }

    [Fact]
    public async Task Model_DeleteIsTerminalAndFurtherRequestsNeverReachProvider()
    {
        var backend = new IdealizedMembershipBackend { TerminalDeletion = true };
        await backend.Fixture().RunAsync(async (fixture, ct) =>
        {
            var executed = new List<MembershipOperationKind>();
            var context = new MembershipModelExecutionContext(fixture, 1, 0, ct, executed.Add);
            Assert.Null((await context.ExecuteAsync(new(MembershipOperationKind.InsertNew))).Failure);
            var deleted = await context.ExecuteAsync(new(MembershipOperationKind.DeleteCluster));
            Assert.Null(deleted.Failure);
            Assert.True(deleted.HistoryDeleted);
            Assert.Null(deleted.Observation);
            foreach (var kind in Enum.GetValues<MembershipOperationKind>())
            {
                var rejected = await context.ExecuteAsync(new(kind));
                Assert.Contains("generated illegal operation", rejected.Failure);
                Assert.False(rejected.HistoryDeleted);
                Assert.Null(rejected.Observation);
            }
            Assert.Equal(new[] { MembershipOperationKind.InsertNew, MembershipOperationKind.DeleteCluster }, executed);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2, backend.Deletes);
        Assert.Equal(0, backend.OperationsAfterDeletion);
        Assert.Empty(backend.Partitions);
    }

    [Fact]
    public async Task GeneratedHistories_UseFreshOwnersAndFinishDeletionBeforeDisposal()
    {
        var owners = new List<IdealizedMembershipBackend>();
        await new MembershipTableModelBasedTestRunner(() =>
        {
            if (owners.Count > 0)
            {
                Assert.Empty(owners[^1].Partitions);
                Assert.Equal(owners[^1].CreatedHandles, owners[^1].DisposedHandles);
            }
            var owner = new IdealizedMembershipBackend { TerminalDeletion = true, DestructiveDisposal = true };
            owners.Add(owner);
            return owner.Fixture();
        }, "terminal-owners").RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
        Assert.True(owners.Count > 1);
        Assert.Contains(owners, owner => owner.DeletionProbes > 0);
        Assert.All(owners, owner =>
        {
            Assert.Equal(2, owner.Deletes);
            Assert.Equal(3, owner.CreatedHandles);
            Assert.Equal(3, owner.DisposedHandles);
            Assert.Equal(0, owner.OperationsAfterDeletion);
            Assert.Empty(owner.Partitions);
        });
    }

    [Fact]
    public async Task GeneratedDeletion_NoOpFailsEvenWhenDisposalDestroysBackingState()
    {
        var backend = new IdealizedMembershipBackend { DestructiveDisposal = true };
        var control = new MembershipFaultController(MembershipFault.DeleteNoOp) { Backend = backend };
        var failure = await Assert.ThrowsAsync<ClusteringConformanceException>(() =>
            new MembershipTableModelBasedTestRunner(control.Fixture, "no-op-deletion")
                .RunGeneratedConformanceTests(TestContext.Current.CancellationToken));
        Assert.Contains("DeleteCluster", failure.Message);
        Assert.Contains("deletion left populated history", failure.Message);
        Assert.Equal(0, backend.Deletes);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
        Assert.Empty(backend.Partitions);
    }
}
