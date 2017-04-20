using System;
using Orleans;
using System.Collections;
using System.Collections.Generic;
using Orleans.Runtime;


namespace UnitTests.StorageTests.Relational.TestDataSets
{
    public sealed class StorageDataSetGeneric<TGrainKey, TStateData>: IEnumerable<object[]>
    {
        private IEnumerable<object[]> DataSet { get; } = new[]
        {
            new object[]
            {
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data1", B = 1, C = 4 } }
            },
            new object[]
            {
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data2", B = 2, C = 5 } }
            },
            new object[]
            {
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data3", B = 3, C = 6 } }
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
