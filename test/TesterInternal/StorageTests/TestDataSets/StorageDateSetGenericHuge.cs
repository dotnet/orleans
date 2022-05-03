using Orleans;
using System;
using Orleans.Runtime;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace UnitTests.StorageTests.Relational.TestDataSets
{
    internal sealed class StorageDataSetGenericHuge<TGrainKey, TStateData> : IEnumerable<object[]>
    {
        private const int NumCases = 3;
        private static Range<long> CountOfCharacters { get; } = new Range<long>(1000000, 1000000);

        public record TestData(string GrainType, Func<IInternalGrainFactory, GrainReference> GrainGetter, GrainState<TestStateGeneric1<TStateData>> GrainState);

        public static TestData GetTestData(int testNum) => testNum switch
        {
            0 => new TestData(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(CountOfCharacters), A = "Data1", B = 1, C = 4 } }),
            1 => new TestData(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(CountOfCharacters), A = "Data2", B = 2, C = 5 } }),
            2 => new TestData(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(CountOfCharacters), A = "Data3", B = 3, C = 6 } }),
            _ => throw new IndexOutOfRangeException(),
        };

        public IEnumerator<object[]> GetEnumerator() => Enumerable.Range(0, NumCases).Select(n => new object[] { n }).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}