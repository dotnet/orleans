using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace Orleans.Runtime
{
    /// <summary>
    /// Client gateway interface for forwarding client requests to silos.
    /// </summary>
    internal interface IClientObserverRegistrar : ISystemTarget
    {
    }
}
