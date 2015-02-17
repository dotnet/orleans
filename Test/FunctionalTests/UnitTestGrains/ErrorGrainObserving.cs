using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Orleans;

#pragma warning disable 618

namespace UnitTestGrains
{
    /// <summary>
    /// A simple grain that allows to set two agruments and then multiply them.
    /// </summary>
    public class ErrorGrainObserving : ErrorGrain, IErrorGrain
    {
        public ErrorGrainObserving()    
        {
            logger = GetLogger("ErrorGrainObserving");
        }
    }
}

#pragma warning restore 618