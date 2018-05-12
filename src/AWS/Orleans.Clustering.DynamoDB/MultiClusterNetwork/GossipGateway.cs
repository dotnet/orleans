using System;
using System.Net;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.MultiClusterNetwork;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    /// <summary>
    /// Represents a Gossip Gateway, as stored in DynamoDB
    /// <para>Primary key is the combinaison of <see cref="ServiceId"/>, <see cref="SiloAddress"/>, <see cref="SiloPort"/> and <see cref="ClusterId"/></para>
    /// </summary>
    internal class GossipGateway
    {
        public DateTime GossipTimestamp { get; set; }

        public string Status { get; set; }

        public int Version { get; set; }
        
        public string ClusterId { get; set; }
        
        public string SiloAddress { get; set; }
        
        public string ServiceId { get; set; }
        
        public int SiloPort { get; set; }

        public int SiloGeneration { get; set; }

        public SiloAddress OrleansSiloAddress => Runtime.SiloAddress.New(new IPEndPoint(IPAddress.Parse(SiloAddress), SiloPort), SiloGeneration);

        public GossipGateway()
        {

        }

        public GossipGateway(GatewayEntry gatewayInfo, string serviceId)
        {
            ClusterId = gatewayInfo.ClusterId;
            GossipTimestamp = gatewayInfo.HeartbeatTimestamp;
            ServiceId = serviceId;
            SiloAddress = gatewayInfo.SiloAddress.Endpoint.Address.ToString();
            SiloPort = gatewayInfo.SiloAddress.Endpoint.Port;
            SiloGeneration = gatewayInfo.SiloAddress.Generation;
            Status = gatewayInfo.Status.ToString();
        }

        public GatewayEntry ToGatewayEntry()
        {
            return new GatewayEntry
            {
                ClusterId = ClusterId,
                SiloAddress = OrleansSiloAddress,
                Status = (GatewayStatus)Enum.Parse(typeof(GatewayStatus), Status),
                HeartbeatTimestamp = GossipTimestamp
            };
        }
    }
}
