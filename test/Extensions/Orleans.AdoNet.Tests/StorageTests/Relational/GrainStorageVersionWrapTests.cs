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

            await SetVersionToMaximumAsync(database, key, cancellationToken);
            grainState.ETag = MaximumVersion;
            grainState.State = new TestState1 { A = "wrapped" };
            await storage.WriteStateAsync(grainType, grainId, grainState, cancellationToken);
            var read = new GrainState<TestState1>();
            await storage.ReadStateAsync(grainType, grainId, read, cancellationToken);

            Assert.Equal("1", grainState.ETag);
            Assert.Equal("1", read.ETag);
            Assert.True(read.RecordExists);
            Assert.Equal(grainState.State, read.State);

            await SetVersionToMaximumAsync(database, key, cancellationToken);
            grainState.ETag = MaximumVersion;
            await storage.ClearStateAsync(grainType, grainId, grainState, cancellationToken);
            var cleared = new GrainState<TestState1>();
            await storage.ReadStateAsync(grainType, grainId, cleared, cancellationToken);

            var expectedClearedVersion = deleteStateOnClear ? null : "1";
            Assert.Equal(expectedClearedVersion, grainState.ETag);
            Assert.Equal(expectedClearedVersion, cleared.ETag);
            Assert.False(cleared.RecordExists);
            Assert.Equal(new TestState1(), cleared.State);
        }

        private static async Task SetVersionToMaximumAsync(IRelationalStorage database, string grainKey, CancellationToken cancellationToken)
        {
            var updated = await database.ExecuteAsync(
                "UPDATE OrleansStorage SET Version = 2147483647 WHERE GrainIdExtensionString = @GrainKey",
                command => command.AddParameter("GrainKey", grainKey),
                cancellationToken: cancellationToken);
            Assert.Equal(1, updated);
        }
    }
}
