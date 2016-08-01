using Orleans;
using System.Collections;
using System.Collections.Generic;


namespace UnitTests.StorageTests.Relational.TestingDataSets
{
    public class StorageWithGenericStateDataSet1<TGrainKeyType, TState>: IEnumerable<object[]>
    {
        private IEnumerable<object[]> DataSet { get; } = new[]
        {
            new object[]
            {
                RandomUtilities.GetRandomGrainReference<TGrainKeyType>(),
                new GrainState<TestStateGeneric1<TState>> { State = new TestStateGeneric1<TState> { SomeData = default(TState), A = "Data1", B = 1, C = 4 } }
            },
            new object[]
            {
                RandomUtilities.GetRandomGrainReference<TGrainKeyType>(),
                new GrainState<TestStateGeneric1<TState>> { State = new TestStateGeneric1<TState> { SomeData = default(TState), A = "Data2", B = 2, C = 5 } }
            },
            new object[]
            {
                RandomUtilities.GetRandomGrainReference<TGrainKeyType>(),
                new GrainState<TestStateGeneric1<TState>> { State = new TestStateGeneric1<TState> { SomeData = default(TState), A = "Data3", B = 3, C = 6 } }
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
