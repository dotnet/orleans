using System.Threading.Tasks;
using Orleans.MultiCluster;
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
