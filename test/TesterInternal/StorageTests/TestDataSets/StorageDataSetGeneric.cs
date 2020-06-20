using System;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.StorageTests.Relational.TestDataSets
{
    internal sealed class StorageDataSetGeneric<TGrainKey, TStateData> : TheoryData<TGrainKey, Func<IInternalGrainFactory, GrainReference>, GrainState<TestStateGeneric1<TStateData>>>
    {
        public StorageDataSetGeneric()
        {
            this.AddRow(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data1", B = 1, C = 4 } });
            this.AddRow(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data2", B = 2, C = 5 } });
            this.AddRow(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data3", B = 3, C = 6 } });
        }
    }
}
