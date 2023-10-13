namespace TestGrainInterfaces
{
    public interface ICircularStateTestGrain : IGrainWithGuidCompoundKey
    {
        Task<CircularTest1> GetState();
    }

    [Serializable]
    [GenerateSerializer]
    public class CircularStateTestState
    {
        [Id(0)]
        public CircularTest1 CircularTest1 { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    public class CircularTest1
    {
        [Id(0)]
        public CircularTest2 CircularTest2 { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    public class CircularTest2
    {
        public CircularTest2()
        {
            CircularTest1List = new List<CircularTest1>();
        }

        [Id(0)]
        public List<CircularTest1> CircularTest1List { get; set; }
    }
}
