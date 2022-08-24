using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.StorageTests.Relational.TestDataSets
{
    internal sealed class StorageDataSetGeneric<TGrainKey, TStateData> : IEnumerable<object[]>
    {
        private const int NumCases = 3;

        public static (string, GrainId, GrainState<TestStateGeneric1<TStateData>>) GetTestData(int testNum) => testNum switch
        {
            0 => (
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                RandomUtilities.GetRandomGrainId<TGrainKey>(),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data1", B = 1, C = 4 } }),
            1 => (
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                RandomUtilities.GetRandomGrainId<TGrainKey>(),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data2", B = 2, C = 5 } }),
            2 => (
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                RandomUtilities.GetRandomGrainId<TGrainKey>(),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data3", B = 3, C = 6 } }),
            _ => throw new IndexOutOfRangeException()
        };

        public IEnumerator<object[]> GetEnumerator() => Enumerable.Range(0, NumCases).Select(n => new object[] { n }).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
