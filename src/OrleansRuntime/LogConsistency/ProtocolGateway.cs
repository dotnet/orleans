using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Core;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.SystemTargetInterfaces;
using Orleans.Concurrency;

namespace Orleans.Runtime.LogConsistency
{
    [Reentrant]
    internal class ProtocolGateway : SystemTarget, ILogConsistencyProtocolGateway
    {
        public ProtocolGateway(SiloAddress silo)
            : base(Constants.ProtocolGatewayId, silo)
        {
        }

        public async Task<ILogConsistencyProtocolMessage> RelayMessage(GrainId id, ILogConsistencyProtocolMessage payload)
        {
            var g = this.RuntimeClient.InternalGrainFactory.GetGrain<ILogConsistencyProtocolParticipant>(id);
            return await g.OnProtocolMessageReceived(payload);
        }

    }
}
