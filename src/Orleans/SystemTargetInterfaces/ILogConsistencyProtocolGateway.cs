using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Concurrency;


namespace Orleans.SystemTargetInterfaces
{
    /// <summary>
    /// The  protocol gateway is a relay that forwards incoming protocol messages from other clusters
    /// to the appropriate grain in this cluster.
    /// </summary>
    internal interface ILogConsistencyProtocolGateway : ISystemTarget
    {
        Task<ILogConsistencyProtocolMessage> RelayMessage(GrainId id, ILogConsistencyProtocolMessage payload);
    }

}
