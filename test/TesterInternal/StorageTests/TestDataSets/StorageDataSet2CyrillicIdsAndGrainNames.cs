using System;
using Orleans;
using System.Collections;
using System.Collections.Generic;
using Orleans.Runtime;
using System.Linq;

namespace UnitTests.StorageTests.Relational.TestDataSets
{
    /// <summary>
    /// A data set for grains with Cyrillic letters and IDs.
    /// </summary>
    /// <typeparam name="TStateData">The type of <see cref="TestStateGeneric1{T}"/>.</typeparam>
    internal class StorageDataSet2CyrillicIdsAndGrainNames<TStateData>: IEnumerable<object[]>
    {
        private const int NumCases = 3;

        /// <summary>
        /// The symbol set this data set uses.
        /// </summary>
        private static SymbolSet Symbols { get; } = new SymbolSet(SymbolSet.Cyrillic);

        /// <summary>
        /// The length of random string drawn form <see cref="Symbols"/>.
        /// </summary>
        private const long StringLength = 15L;
        
        public record TestData(string GrainType, Func<IInternalGrainFactory, GrainReference> GrainGetter, GrainState<TestStateGeneric1<TStateData>> GrainState);

        public static TestData GetTestData(int testNum) => testNum switch
        {
            0 => new TestData(
                GrainTypeGenerator.GetGrainType<string>(),
                grainFactory => RandomUtilities.GetRandomGrainReference<string, int>(grainFactory, Symbols, StringLength, keyExtension: false),
                new GrainState<TestStateGeneric1<TStateData>>
                {
                    State = new TestStateGeneric1<TStateData>
                    {
                        SomeData = RandomUtilities.GetRandom<TStateData>(),
                        A = "Data1",
                        B = 1,
                        C = 4
                    }
                }),
            1 => new TestData(
                GrainTypeGenerator.GetGrainType<string>(),
                grainFactory => RandomUtilities.GetRandomGrainReference<string, int>(grainFactory, Symbols, StringLength, true),
                new GrainState<TestStateGeneric1<TStateData>>
                {
                    State = new TestStateGeneric1<TStateData>
                    {
                        SomeData = RandomUtilities.GetRandom<TStateData>(),
                        A = "Data2",
                        B = 2,
                        C = 5
                    }
                }),
            2 => new TestData(
                GrainTypeGenerator.GetGrainType<string>(),
                grainFactory => RandomUtilities.GetRandomGrainReference<string, int>(grainFactory, Symbols, StringLength, true),
                new GrainState<TestStateGeneric1<TStateData>>
                {
                    State = new TestStateGeneric1<TStateData>
                    {
                        SomeData = RandomUtilities.GetRandom<TStateData>(),
                        A = "Data3",
                        B = 3,
                        C = 6
                    }
                }),
            _ => throw new IndexOutOfRangeException()
        };

        public IEnumerator<object[]> GetEnumerator() => Enumerable.Range(0, NumCases).Select(n => new object[] { n }).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
