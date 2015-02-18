using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Orleans;

namespace UnitTestGrains
{
    public interface IErrorPersistentGrain : IErrorGrain
    {
    }
}
