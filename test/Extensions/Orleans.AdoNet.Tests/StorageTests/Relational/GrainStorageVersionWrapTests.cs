using System.Globalization;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Tests.SqlUtils;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;
using TestState1 = UnitTests.StorageTests.Relational.TestDataSets.TestState1;

namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Grain state version scenarios shared by the relational back-ends.
    /// </summary>
    internal static class GrainStorageVersionWrapTests
    {
        private static readonly string MaximumVersion = int.MaxValue.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Writing and clearing a state at the maximum version wraps the version to 1 instead of overflowing.
        /// </summary>
        public static async Task VersionWrapsAfterMaximum(IGrainStorage storage, IRelationalStorage database, bool deleteStateOnClear, CancellationToken cancellationToken)
        {
            var key = $"version-wrap-{Guid.NewGuid():N}";
            var grainId = GrainId.Create("version-wrap", key);
            const string grainType = "version-wrap-grain";
            var grainState = new GrainState<TestState1> { State = new TestState1 { A = "created" } };
            await storage.WriteStateAsync(grainType, grainId, grainState, cancellationToken);

            await SetVersionAsync(database, key, int.MaxValue - 1, cancellationToken);
            await storage.ReadStateAsync(grainType, grainId, grainState, cancellationToken);
            Assert.Equal((int.MaxValue - 1).ToString(CultureInfo.InvariantCulture), grainState.ETag);

            grainState.State = new TestState1 { A = "maximum" };
            await storage.WriteStateAsync(grainType, grainId, grainState, cancellationToken);
            Assert.Equal(MaximumVersion, grainState.ETag);

            var stale = new GrainState<TestState1>
            {
                State = new TestState1 { A = "stale" },
                ETag = (int.MaxValue - 1).ToString(CultureInfo.InvariantCulture),
                RecordExists = true
            };
            await AssertStaleStateRejectedAsync(storage, grainType, grainId, stale, cancellationToken);
            var maximum = new GrainState<TestState1>();
            await storage.ReadStateAsync(grainType, grainId, maximum, cancellationToken);
            Assert.Equal(MaximumVersion, maximum.ETag);
            Assert.Equal(new TestState1 { A = "maximum" }, maximum.State);

            grainState.State = new TestState1 { A = "wrapped" };
            await storage.WriteStateAsync(grainType, grainId, grainState, cancellationToken);
            stale.ETag = MaximumVersion;
            await AssertStaleStateRejectedAsync(storage, grainType, grainId, stale, cancellationToken);
            var read = new GrainState<TestState1>();
            await storage.ReadStateAsync(grainType, grainId, read, cancellationToken);

            Assert.Equal("1", grainState.ETag);
            Assert.Equal("1", read.ETag);
            Assert.True(read.RecordExists);
            Assert.Equal(new TestState1 { A = "wrapped" }, read.State);

            grainState.State = new TestState1 { A = "after-wrap" };
            await storage.WriteStateAsync(grainType, grainId, grainState, cancellationToken);
            await storage.ReadStateAsync(grainType, grainId, read, cancellationToken);
            Assert.Equal("2", grainState.ETag);
            Assert.Equal("2", read.ETag);
            Assert.Equal(new TestState1 { A = "after-wrap" }, read.State);

            await SetVersionAsync(database, key, int.MaxValue, cancellationToken);
            grainState.ETag = MaximumVersion;
            await storage.ClearStateAsync(grainType, grainId, grainState, cancellationToken);
            await AssertStaleStateRejectedAsync(storage, grainType, grainId, stale, cancellationToken);
            var cleared = new GrainState<TestState1>();
            await storage.ReadStateAsync(grainType, grainId, cleared, cancellationToken);

            var expectedClearedVersion = deleteStateOnClear ? null : "1";
            Assert.Equal(expectedClearedVersion, grainState.ETag);
            Assert.Equal(expectedClearedVersion, cleared.ETag);
            Assert.False(cleared.RecordExists);
            Assert.Equal(new TestState1(), cleared.State);

            grainState.State = new TestState1 { A = "after-clear" };
            await storage.WriteStateAsync(grainType, grainId, grainState, cancellationToken);
            await storage.ReadStateAsync(grainType, grainId, read, cancellationToken);
            var expectedWrittenVersion = deleteStateOnClear ? "1" : "2";
            Assert.Equal(expectedWrittenVersion, grainState.ETag);
            Assert.Equal(expectedWrittenVersion, read.ETag);
            Assert.True(read.RecordExists);
            Assert.Equal(new TestState1 { A = "after-clear" }, read.State);
        }

        private static async Task AssertStaleStateRejectedAsync(
            IGrainStorage storage, string grainType, GrainId grainId, GrainState<TestState1> stale, CancellationToken cancellationToken)
        {
            var etag = stale.ETag;
            await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync(grainType, grainId, stale, cancellationToken));
            await Assert.ThrowsAsync<InconsistentStateException>(() => storage.ClearStateAsync(grainType, grainId, stale, cancellationToken));
            Assert.Equal(etag, stale.ETag);
            Assert.True(stale.RecordExists);
        }

        private static async Task SetVersionAsync(IRelationalStorage database, string grainKey, int version, CancellationToken cancellationToken)
        {
            var updated = await database.ExecuteAsync(
                "UPDATE OrleansStorage SET Version = @Version WHERE GrainIdExtensionString = @GrainKey",
                command =>
                {
                    command.AddParameter("GrainKey", grainKey);
                    command.AddParameter("Version", version);
                },
                cancellationToken: cancellationToken);
            Assert.Equal(1, updated);
        }
    }
}
