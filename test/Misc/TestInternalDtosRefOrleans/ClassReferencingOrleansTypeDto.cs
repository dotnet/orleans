using System;
using Orleans;

namespace UnitTests.DtosRefOrleans
{
    [Serializable]
    [GenerateSerializer]
    public class ClassReferencingOrleansTypeDto
    {
        static ClassReferencingOrleansTypeDto()
        {
            _ = typeof(IGrain).ToString();
        }

        [Id(0)]
        public string MyProperty { get; set; }
    }
}