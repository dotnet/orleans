using System.Text;
using Orleans.Runtime;
using Orleans.Storage;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;

namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Storage key identity scenarios shared by the relational back-ends. The storage under test should use a constant
    /// hasher so that rows are distinguished by their key columns rather than by hash values.
    /// </summary>
    internal static class GrainStorageKeyFormatTests
    {
        private const string StateName = "state";

        /// <summary>
        /// Grains of different types which share a key and state name have separate state.
        /// </summary>
        public static async Task GrainTypesSharingKeyAreIsolated(IGrainStorage storage)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var typeA = GrainType.Create($"key-format-a-{suffix}");
            var typeB = GrainType.Create($"key-format-b-{suffix}");
            var guid = Guid.NewGuid();
            IdSpan[] keys =
            [
                GrainIdKeyExtensions.CreateIntegerKey(0),
                GrainIdKeyExtensions.CreateIntegerKey(42, "ext"),
                GrainIdKeyExtensions.CreateGuidKey(guid),
                GrainIdKeyExtensions.CreateGuidKey(guid, "ext"),
                IdSpan.Create("string-key"),
            ];

            foreach (var key in keys)
            {
                await AssertIsolated(storage, (GrainId.Create(typeA, key), StateName), (GrainId.Create(typeB, key), StateName));
            }
        }

        /// <summary>
        /// Grain types and state names which concatenate to the same text, differ only in letter case, or are not
        /// valid UTF-8 have separate state.
        /// </summary>
        public static async Task GrainTypeAndStateNameBoundariesAreIsolated(IGrainStorage storage)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var key = IdSpan.Create("shared-key");
            ((GrainType Type, string StateName) First, (GrainType Type, string StateName) Second)[] pairs =
            [
                ((GrainType.Create($"tenant-{suffix}/cart"), "state"), (GrainType.Create($"tenant-{suffix}"), "cart/state")),
                ((GrainType.Create($"case-{suffix}"), StateName), (GrainType.Create($"CASE-{suffix}"), StateName)),
                ((GrainType.Create($"state-case-{suffix}"), "state"), (GrainType.Create($"state-case-{suffix}"), "STATE")),
                ((new GrainType([.. Encoding.UTF8.GetBytes(suffix), 0x61, 0xFF]), StateName), (new GrainType([.. Encoding.UTF8.GetBytes(suffix), 0x61, 0xFE]), StateName)),
            ];

            foreach (var (first, second) in pairs)
            {
                await AssertIsolated(
                    storage,
                    (GrainId.Create(first.Type, key), first.StateName),
                    (GrainId.Create(second.Type, key), second.StateName));
            }
        }

        /// <summary>
        /// States with different names on one grain have separate state.
        /// </summary>
        public static async Task StateNamesOfOneGrainAreIsolated(IGrainStorage storage)
        {
            var grainId = GrainId.Create(GrainType.Create($"key-format-states-{Guid.NewGuid():N}"), GrainIdKeyExtensions.CreateIntegerKey(7));

            await AssertIsolated(storage, (grainId, "first"), (grainId, "second"));
        }

        /// <summary>
        /// Distinct grain keys of one grain type which parse to the same integer or GUID key have separate state.
        /// </summary>
        public static async Task KeysParsingToSameValueAreIsolated(IGrainStorage storage)
        {
            var grainType = GrainType.Create($"key-format-keys-{Guid.NewGuid():N}");
            var guidA = Guid.NewGuid().ToString("N");
            var guidB = Guid.NewGuid().ToString("N");

            // Each key appears once: clearing keeps the row, so a key cannot be written again as new state.
            (string First, string Second)[] keyPairs =
            [
                ("20235", "020235"),
                ("ABC", "abc"),
                ("1+x", "01+x"),
                ("1", "1+"),
                (guidA, guidA.ToUpperInvariant()),
                (guidB, guidB + "+"),
                ("trailing", "trailing "),
                ("é", "e"),

                // Distinct keys with equal default-hasher hashes.
                ("abcdeFghiJkLMNoPqrstuvwx", "AbcdefgHiJKlmnOpQRstuvwx"),
            ];

            foreach (var (first, second) in keyPairs)
            {
                await AssertIsolated(
                    storage,
                    (GrainId.Create(grainType, IdSpan.Create(first)), StateName),
                    (GrainId.Create(grainType, IdSpan.Create(second)), StateName));
            }
        }

        /// <summary>
        /// Keys of different kinds on one grain type have separate state even when their integer, GUID, and extension
        /// parts coincide.
        /// </summary>
        public static async Task KeysOfDifferentKindsAreIsolated(IGrainStorage storage)
        {
            var grainType = GrainType.Create($"key-format-kinds-{Guid.NewGuid():N}");

            // A GUID whose first eight bytes are zero has the same N0/N1 decomposition as an integer key.
            var guidBytes = new byte[16];
            BitConverter.GetBytes(5L).CopyTo(guidBytes, 8);
            var guidMatchingInteger = new Guid(guidBytes);

            (IdSpan First, IdSpan Second)[] keyPairs =
            [
                (IdSpan.Create("string-and-extension"), GrainIdKeyExtensions.CreateIntegerKey(0, "string-and-extension")),
                (IdSpan.Create("guid-extension"), GrainIdKeyExtensions.CreateGuidKey(Guid.Empty, "guid-extension")),
                (GrainIdKeyExtensions.CreateIntegerKey(5), GrainIdKeyExtensions.CreateGuidKey(guidMatchingInteger)),

                // Invalid UTF-8 bytes which decode to the same replacement text.
                (new IdSpan([0x61, 0xFF]), new IdSpan([0x61, 0xFE])),
            ];

            foreach (var (first, second) in keyPairs)
            {
                await AssertIsolated(
                    storage,
                    (GrainId.Create(grainType, first), StateName),
                    (GrainId.Create(grainType, second), StateName));
            }
        }

        /// <summary>
        /// Two grains of different types which share a key also share a row in the legacy key format, so the second
        /// first-time write conflicts with the first.
        /// </summary>
        public static async Task LegacyFormatSharesRowAcrossGrainTypes(IGrainStorage storage)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var key = GrainIdKeyExtensions.CreateIntegerKey(0);
            var grainA = GrainId.Create(GrainType.Create($"key-format-legacy-a-{suffix}"), key);
            var grainB = GrainId.Create(GrainType.Create($"key-format-legacy-b-{suffix}"), key);

            var stateA = NewState("legacy-a");
            await storage.WriteStateAsync(StateName, grainA, stateA);

            var readB = new GrainState<TestState1> { State = new TestState1() };
            await storage.ReadStateAsync(StateName, grainB, readB);
            Assert.True(readB.RecordExists);
            Assert.Equal(stateA.State, readB.State);

            await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync(StateName, grainB, NewState("legacy-b")));
        }

        private static async Task AssertIsolated(IGrainStorage storage, (GrainId GrainId, string StateName) first, (GrainId GrainId, string StateName) second)
        {
            var firstState = NewState($"{first.GrainId}/{first.StateName}");
            var secondState = NewState($"{second.GrainId}/{second.StateName}");

            await storage.WriteStateAsync(first.StateName, first.GrainId, firstState);
            await storage.WriteStateAsync(second.StateName, second.GrainId, secondState);
            Assert.Equal("1", firstState.ETag);
            Assert.Equal("1", secondState.ETag);

            await AssertStored(storage, first, firstState.State);
            await AssertStored(storage, second, secondState.State);

            await storage.ClearStateAsync(first.StateName, first.GrainId, firstState);

            var clearedState = new GrainState<TestState1> { State = new TestState1() };
            await storage.ReadStateAsync(first.StateName, first.GrainId, clearedState);
            Assert.False(clearedState.RecordExists);
            Assert.Equal(new TestState1(), clearedState.State);
            await AssertStored(storage, second, secondState.State);
        }

        private static async Task AssertStored(IGrainStorage storage, (GrainId GrainId, string StateName) grain, TestState1? expected)
        {
            var readState = new GrainState<TestState1> { State = new TestState1() };
            await storage.ReadStateAsync(grain.StateName, grain.GrainId, readState);
            Assert.True(readState.RecordExists, $"No state was read for {grain.GrainId} ({grain.StateName}).");
            Assert.Equal("1", readState.ETag);
            Assert.Equal(expected, readState.State);
        }

        private static GrainState<TestState1> NewState(string value) => new() { State = new TestState1 { A = value, B = 1, C = 2 } };
    }
}
