using System;
using System.Collections.Generic;

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    public class TestTypeA
    {
        public ICollection<TestTypeA> Collection { get; set; }
    }
}
