using Orleans;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TestGrainInterfaces
{
    public interface ICircularStateTestGrain : IGrainWithGuidCompoundKey
    {
        Task<CircularTest1> GetState();
    }

    [Serializable]
    public class CircularStateTestState
    {
        public CircularTest1 CircularTest1 { get; set; }
    }

    [Serializable]
    public class CircularTest1
    {
        public CircularTest2 CircularTest2 { get; set; }
    }
    [Serializable]
    public class CircularTest2
    {
        public CircularTest2()
        {
            CircularTest1List = new List<CircularTest1>();
        }
        public List<CircularTest1> CircularTest1List { get; set; }
    }
}
