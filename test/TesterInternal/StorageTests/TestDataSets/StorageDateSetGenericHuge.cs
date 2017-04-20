using Orleans;
using System;
using System.Collections;
using System.Collections.Generic;
using Orleans.Runtime;


namespace UnitTests.StorageTests.Relational.TestDataSets
{
    public sealed class StorageDataSetGenericHuge<TGrainKey, TStateData>: IEnumerable<object[]>
    {
        private static Range<long> CountOfCharacters { get; } = new Range<long>(1000000, 1000000);

        private IEnumerable<object[]> DataSet { get; } = new[]
        {
            new object[]
            {
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(CountOfCharacters), A = "Data1", B = 1, C = 4 } }
            },
            new object[]
            {
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(CountOfCharacters), A = "Data2", B = 2, C = 5 } }
            },
            new object[]
            {
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(CountOfCharacters), A = "Data3", B = 3, C = 6 } }
            }
        };

        public IEnumerator<object[]> GetEnumerator()
        {
            return DataSet.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
