using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Orleans;

namespace UnitTestGrains
{
    /// <summary>
    /// A simple grain that allows to set two agruments and then multiply them.
    /// </summary>
    public class ErrorPersistentGrain : ErrorGrain, IErrorPersistentGrain
    {
    }
}
