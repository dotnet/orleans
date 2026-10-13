using Orleans.Clustering.TestKit;
using Orleans.Clustering.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Orleans.MongoDB.Tests.Clustering;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Membership")]
public sealed class MongoSingleDocumentMembershipTests(ITestOutputHelper output) : MongoMembershipTestsBase(output)
{
    protected override MongoDBMembershipStrategy Strategy => MongoDBMembershipStrategy.SingleDocument;
}

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Membership")]
public sealed class MongoMultipleMembershipTests(ITestOutputHelper output) : MongoMembershipTestsBase(output)
{
    protected override MongoDBMembershipStrategy Strategy => MongoDBMembershipStrategy.Multiple;
}

/// <summary>Every inherited test executes one unconditional modern guarantee against a fresh owned database.</summary>
public abstract class MongoMembershipTestsBase(ITestOutputHelper output) : IAsyncLifetime
{
    private MongoDatabase? database;
    private MembershipTableTestFixture? fixture;
    private MembershipTableTestRunner? runner;
    protected abstract MongoDBMembershipStrategy Strategy { get; }
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private MembershipTableTestRunner Runner
    {
        get
        {
            MongoDatabase.RequireConnection();
            return runner!;
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MongoDatabase.ConnectionVariable)))
            return;

        database = await MongoDatabase.CreateAsync();
        fixture = new MembershipTableTestFixture($"MongoDB/{Strategy}",
            clusterId => database.CreateMembershipTable(clusterId, Strategy),
            IsClusterDeletedAsync);
        try
        {
            await fixture.InitializeAsync(Token);
            runner = new MembershipTableTestRunner(fixture, seed: 6143, output: output.WriteLine);
        }
        catch
        {
            await database.DisposeAsync();
            database = null;
            throw;
        }
    }

    private async ValueTask<bool> IsClusterDeletedAsync(string clusterId, CancellationToken token)
    {
        // Probe original native backing data, not ReadAll (which could accidentally recreate a deleted scope).
        var names = Strategy == MongoDBMembershipStrategy.SingleDocument
            ? new[] { "OrleansMembershipSingle" }
            : new[] { "OrleansMembershipV3", "OrleansMembershipV3_TableVersion" };
        foreach (var name in names)
        {
            var collection = database!.Database.GetCollection<BsonDocument>(name);
            var key = name == "OrleansMembershipV3" ? "DeploymentId" : "_id";
            if (await collection.CountDocumentsAsync(new BsonDocument(key, clusterId), cancellationToken: token) != 0)
                return false;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (fixture is not null) await fixture.DisposeAsync();
        }
        finally
        {
            if (database is not null) await database.DisposeAsync();
        }
    }

    [Fact] public Task InsertRow_CurrentTableVersion_CommitsExactlyOneVersion() => Runner.InsertRow_CurrentTableVersion_CommitsExactlyOneVersion(Token);
    [Fact] public Task UpdateRow_CurrentTokens_CommitsExactlyOneVersion() => Runner.UpdateRow_CurrentTokens_CommitsExactlyOneVersion(Token);
    [Fact] public Task Reads_SameVersion_PreservesRetainedCanonicalFields() => Runner.Reads_SameVersion_PreservesRetainedCanonicalFields(Token);
    [Fact] public Task Reads_MaySkipCommittedVersions_WithoutSkippingHistoryValidation() => Runner.Reads_MaySkipCommittedVersions_WithoutSkippingHistoryValidation(Token);
    [Fact] public Task Lifecycle_DeadRemainsTerminalAfterCompaction_SuccessorUsesNewGeneration() => Runner.Lifecycle_DeadRemainsTerminalAfterCompaction_SuccessorUsesNewGeneration(Token);
    [Fact] public Task UpdateIAmAlive_OwnerWrites_PreserveCanonicalFieldsAndTableVersion() => Runner.UpdateIAmAlive_OwnerWrites_PreserveCanonicalFieldsAndTableVersion(Token);
    [Fact] public Task UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange() => Runner.UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange(Token);
    [Fact] public Task UpdateRow_TokensCapturedBeforeHeartbeat_CommitVoteChange() => Runner.UpdateRow_TokensCapturedBeforeHeartbeat_CommitVoteChange(Token);
    [Fact] public Task Handles_IndependentlyConstructed_ShareCommittedBackingState() => Runner.Handles_IndependentlyConstructed_ShareCommittedBackingState(Token);
    [Fact] public Task InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects() => Runner.InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects(Token);
    [Fact] public Task UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects() => Runner.UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects(Token);
    [Fact] public Task UpdateRow_StaleSnapshotAfterSameRowCommit_ReturnsFalseWithoutSideEffects() => Runner.UpdateRow_StaleSnapshotAfterSameRowCommit_ReturnsFalseWithoutSideEffects(Token);
    [Fact] public Task InsertRow_DuplicateIdentity_ReturnsFalseWithoutSideEffects() => Runner.InsertRow_DuplicateIdentity_ReturnsFalseWithoutSideEffects(Token);
    [Fact] public Task UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects() => Runner.UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects(Token);
    [Fact] public Task ReadAll_SelectsPresentAndAbsentIdentities() => Runner.ReadAll_SelectsPresentAndAbsentIdentities(Token);
    [Fact] public Task Reads_RetainedObjectsRemainUnchangedAfterLaterWrites() => Runner.Reads_RetainedObjectsRemainUnchangedAfterLaterWrites(Token);
    [Fact] public Task InsertRow_MutatingInputAndSuspectList_DoesNotMutateStoredState() => Runner.InsertRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(Token);
    [Fact] public Task UpdateRow_MutatingInputAndSuspectList_DoesNotMutateStoredState() => Runner.UpdateRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(Token);
    [Fact] public Task Reads_MutatingReturnedEntryAndSuspectList_DoesNotMutateStoredState() => Runner.Reads_MutatingReturnedEntryAndSuspectList_DoesNotMutateStoredState(Token);
    [Fact] public Task ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner() => Runner.ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner(Token);
    [Fact] public Task ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews() => Runner.ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(Token);
    [Fact] public Task InitializeMembershipTable_RepeatedWithData_PreservesCommittedState() => Runner.InitializeMembershipTable_RepeatedWithData_PreservesCommittedState(Token);
    [Fact] public Task Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated() => Runner.Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated(Token);
    [Fact] public Task CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows() => Runner.CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(Token);
    [Fact] public Task DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster() => Runner.DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(Token);
    [Fact] public Task DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster() => Runner.DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(Token);
}
