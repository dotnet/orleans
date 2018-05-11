using System;
using System.Collections.Generic;
using System.Net;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.MultiClusterNetwork;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    internal class GossipGateway
    {
        public DateTime GossipTimestamp { get; }

        public string Status { get; set; }

        public int Version { get; set; }

        // Primary Key
        public string ClusterId { get; set; }

        // Primary Key
        public string SiloAddress { get; set; }

        // Primary Key
        public string ServiceId { get; set; }

        // Primary Key
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
