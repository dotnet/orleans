using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    class SpecializedSimpleGenericGrain : SimpleGenericGrain<double>
    {
        public override Task Transform()
        {
            Value = Value * 2.0;
            return TaskDone.Done;
        }
    }
}
