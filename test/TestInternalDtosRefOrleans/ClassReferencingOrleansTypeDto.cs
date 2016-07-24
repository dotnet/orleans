using System;
using Orleans;

namespace UnitTests.DtosRefOrleans
{
    [Serializable]
    public class ClassReferencingOrleansTypeDto
    {
        static ClassReferencingOrleansTypeDto()
        {
            typeof(IGrain).ToString();
        }

        public string MyProperty { get; set; }
    }
}