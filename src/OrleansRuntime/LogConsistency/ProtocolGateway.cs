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
    internal class ProtocolGateway : SystemTarget, IProtocolGateway
    {
        public ProtocolGateway(SiloAddress silo)
            : base(Constants.ProtocolGatewayId, silo)
        {
        }

        public async Task<IProtocolMessage> RelayMessage(GrainId id, IProtocolMessage payload)
        {
            var g = InsideRuntimeClient.Current.InternalGrainFactory.Cast<IProtocolParticipant>(GrainReference.FromGrainId(id));
            return await g.OnProtocolMessageReceived(payload);
        }

    }
}
