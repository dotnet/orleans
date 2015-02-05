using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTestGrains
{
    public interface IPromiseForwardGrain : ISimpleGrain, ISimpleGrain_Async
    {
    }
}
