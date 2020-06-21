using Orleans;
using System;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.StorageTests.Relational.TestDataSets
{
    internal sealed class StorageDataSetGenericHuge<TGrainKey, TStateData> : TheoryData<TGrainKey, Func<IInternalGrainFactory, GrainReference>, GrainState<TestStateGeneric1<TStateData>>>
    {
        private static Range<long> CountOfCharacters { get; } = new Range<long>(1000000, 1000000);

        public StorageDataSetGenericHuge()
        {
            this.AddRow(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(CountOfCharacters), A = "Data1", B = 1, C = 4 } });
            this.AddRow(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(CountOfCharacters), A = "Data2", B = 2, C = 5 } });
            this.AddRow(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                (Func<IInternalGrainFactory, GrainReference>)(grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory)),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(CountOfCharacters), A = "Data3", B = 3, C = 6 } });
        }
    }
}