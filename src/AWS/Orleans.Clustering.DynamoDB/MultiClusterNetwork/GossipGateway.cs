using System;
using System.Collections.Generic;
using System.Net;
using Amazon.DynamoDBv2.Model;
using Orleans.MultiCluster;
using Orleans.Runtime.MultiClusterNetwork;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    internal class GossipGateway
    {
        private const string STATUS_PROPERTY_NAME = "Status";
        private const string VERSION_PROPERTY_NAME = "Version";
        private const string CLUSTER_ID_PROPERTY_NAME = "ClusterId";
        private const string SILO_ADDRESS_PROPERTY_NAME = "SiloAddress";
        private const string SERVICE_ID_PROPERTY_NAME = "ServiceId";
        private const string SILO_PORT_PROPERTY_NAME = "SiloPort";
        private const string SILO_GENERATION_PROPERTY_NAME = "SiloGeneration";

        public DateTime GossipTimestamp { get; set; }

        public string Status { get; set; }

        public int Version { get; set; }

        // Primary Key
        public string ClusterId { get; set; }

        // Primary Key
        public string SiloAddress { get; set; }

        // Primary Key
        public string ServiceId { get; set; }

        public int SiloPort { get; set; }

        public int SiloGeneration { get; set; }

        public GossipGateway(Dictionary<string, AttributeValue> fields)
        {
            if (fields.ContainsKey(STATUS_PROPERTY_NAME))
                Status = fields[STATUS_PROPERTY_NAME].S;

            if (fields.ContainsKey(VERSION_PROPERTY_NAME))
                Version = int.Parse(fields[VERSION_PROPERTY_NAME].S);

            if (fields.ContainsKey(CLUSTER_ID_PROPERTY_NAME))
                ClusterId = fields[CLUSTER_ID_PROPERTY_NAME].S;

            if (fields.ContainsKey(SILO_ADDRESS_PROPERTY_NAME))
                SiloAddress = fields[SILO_ADDRESS_PROPERTY_NAME].S;

            if (fields.ContainsKey(SERVICE_ID_PROPERTY_NAME))
                ServiceId = fields[SERVICE_ID_PROPERTY_NAME].S;

            if (fields.ContainsKey(SILO_PORT_PROPERTY_NAME))
                SiloPort = int.Parse(fields[SILO_PORT_PROPERTY_NAME].S);

            if (fields.ContainsKey(SILO_GENERATION_PROPERTY_NAME))
                SiloGeneration = int.Parse(fields[SILO_GENERATION_PROPERTY_NAME].S);
        }

        internal GatewayEntry ToGatewayEntry()
        {
            return new GatewayEntry
            {
                ClusterId = ClusterId,
                SiloAddress = Runtime.SiloAddress.New(new IPEndPoint(IPAddress.Parse(SiloAddress), SiloPort), SiloGeneration),
                Status = (GatewayStatus)Enum.Parse(typeof(GatewayStatus), Status),
                HeartbeatTimestamp = GossipTimestamp
            };
        }
    }
}
