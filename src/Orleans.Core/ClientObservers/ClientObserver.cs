using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Runtime;

namespace Orleans.ClientObservers
{
    public abstract class ClientObserver
    {
        public abstract IdSpan ObserverId { get; }
    }
}
