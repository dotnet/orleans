using Orleans;
using System.Collections;
using System.Collections.Generic;


namespace UnitTests.StorageTests.Relational.TestingDataSets
{
    public class StorageDataSet1<TGrainKey>: IEnumerable<object[]>
    {
        private IEnumerable<object[]> DataSet { get; } = new[]
        {
            new object[]
            {
                RandomUtilities.GetRandomGrainReference<TGrainKey>(),
                new GrainState<TestState1> { State = new TestState1 { A = RandomUtilities.GetRandom<string>(), B = 1, C = 4 } }
            },
            new object[]
            {
                RandomUtilities.GetRandomGrainReference<TGrainKey>(),
                new GrainState<TestState1> { State = new TestState1 { A = "Data2", B = 2, C = 5 } }
            },
            new object[]
            {
                RandomUtilities.GetRandomGrainReference<TGrainKey>(),
                new GrainState<TestState1> { State = new TestState1 { A = "Data3", B = 3, C = 6 } }
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
