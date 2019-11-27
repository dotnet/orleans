﻿using Amazon.DynamoDBv2.Model;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Orleans.Runtime.MembershipService
{
    internal class SiloInstanceRecord
    {
        public const string DEPLOYMENT_ID_PROPERTY_NAME = "DeploymentId";
        public const string SILO_IDENTITY_PROPERTY_NAME = "SiloIdentity";
        public const string ETAG_PROPERTY_NAME = "ETag";
        public const string ADDRESS_PROPERTY_NAME = "Address";
        public const string PORT_PROPERTY_NAME = "Port";
        public const string GENERATION_PROPERTY_NAME = "Generation";
        public const string HOSTNAME_PROPERTY_NAME = "HostName";
        public const string STATUS_PROPERTY_NAME = "SiloStatus";
        public const string PROXY_PORT_PROPERTY_NAME = "ProxyPort";
        public const string SILO_NAME_PROPERTY_NAME = "SiloName";
        public const string INSTANCE_NAME_PROPERTY_NAME = "InstanceName";
        public const string SUSPECTING_SILOS_PROPERTY_NAME = "SuspectingSilos";
        public const string SUSPECTING_TIMES_PROPERTY_NAME = "SuspectingTimes";
        public const string START_TIME_PROPERTY_NAME = "StartTime";
        public const string I_AM_ALIVE_TIME_PROPERTY_NAME = "IAmAliveTime";
        internal const char Seperator = '-';
        internal const string TABLE_VERSION_ROW = "VersionRow"; // Range key for version row.
        public const string MEMBERSHIP_VERSION_PROPERTY_NAME = "MembershipVersion";

        public string DeploymentId { get; set; }
        public string SiloIdentity { get; set; }
        public string Address { get; set; }
        public int Port { get; set; }
        public int Generation { get; set; }
        public string HostName { get; set; }
        public int Status { get; set; }
        public int ProxyPort { get; set; }
        public string SiloName { get; set; }
        public string SuspectingSilos { get; set; }
        public string SuspectingTimes { get; set; }
        public string StartTime { get; set; }
        public string IAmAliveTime { get; set; }
        public int ETag { get; set; }

        public int MembershipVersion { get; set; }

        public SiloInstanceRecord() { }

        public SiloInstanceRecord(Dictionary<string, AttributeValue> fields)
        {
            if (fields.ContainsKey(DEPLOYMENT_ID_PROPERTY_NAME))
                DeploymentId = fields[DEPLOYMENT_ID_PROPERTY_NAME].S;

            if (fields.ContainsKey(SILO_IDENTITY_PROPERTY_NAME))
                SiloIdentity = fields[SILO_IDENTITY_PROPERTY_NAME].S;

            if (fields.ContainsKey(ADDRESS_PROPERTY_NAME))
                Address = fields[ADDRESS_PROPERTY_NAME].S;

            int port;
            if (fields.ContainsKey(PORT_PROPERTY_NAME) &&
                int.TryParse(fields[PORT_PROPERTY_NAME].N, out port))
                Port = port;

            int generation;
            if (fields.ContainsKey(GENERATION_PROPERTY_NAME) &&
                int.TryParse(fields[GENERATION_PROPERTY_NAME].N, out generation))
                Generation = generation;

            if (fields.ContainsKey(HOSTNAME_PROPERTY_NAME))
                HostName = fields[HOSTNAME_PROPERTY_NAME].S;

            int status;
            if (fields.ContainsKey(STATUS_PROPERTY_NAME) &&
                int.TryParse(fields[STATUS_PROPERTY_NAME].N, out status))
                Status = status;

            int proxyPort;
            if (fields.ContainsKey(PROXY_PORT_PROPERTY_NAME) &&
                int.TryParse(fields[PROXY_PORT_PROPERTY_NAME].N, out proxyPort))
                ProxyPort = proxyPort;

            if (fields.ContainsKey(SILO_NAME_PROPERTY_NAME))
                SiloName = fields[SILO_NAME_PROPERTY_NAME].S;

            if (fields.ContainsKey(SUSPECTING_SILOS_PROPERTY_NAME))
                SuspectingSilos = fields[SUSPECTING_SILOS_PROPERTY_NAME].S;

            if (fields.ContainsKey(SUSPECTING_TIMES_PROPERTY_NAME))
                SuspectingTimes = fields[SUSPECTING_TIMES_PROPERTY_NAME].S;

            if (fields.ContainsKey(START_TIME_PROPERTY_NAME))
                StartTime = fields[START_TIME_PROPERTY_NAME].S;

            if (fields.ContainsKey(I_AM_ALIVE_TIME_PROPERTY_NAME))
                IAmAliveTime = fields[I_AM_ALIVE_TIME_PROPERTY_NAME].S;

            int etag;
            if (fields.ContainsKey(ETAG_PROPERTY_NAME) &&
                int.TryParse(fields[ETAG_PROPERTY_NAME].N, out etag))
                ETag = etag;

            if (fields.ContainsKey(MEMBERSHIP_VERSION_PROPERTY_NAME) &&
                int.TryParse(fields[MEMBERSHIP_VERSION_PROPERTY_NAME].N, out int version))
                MembershipVersion = version;
        }

        internal static SiloAddress UnpackRowKey(string rowKey)
        {
            try
            {
                int idx1 = rowKey.IndexOf(Seperator);
                int idx2 = rowKey.LastIndexOf(Seperator);
                var addressStr = rowKey.Substring(0, idx1);
                var portStr = rowKey.Substring(idx1 + 1, idx2 - idx1 - 1);
                var genStr = rowKey.Substring(idx2 + 1);
                IPAddress address = IPAddress.Parse(addressStr);
                int port = Int32.Parse(portStr);
                int generation = Int32.Parse(genStr);
                return SiloAddress.New(new IPEndPoint(address, port), generation);
            }
            catch (Exception exc)
            {
                throw new AggregateException("Error from UnpackRowKey", exc);
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("OrleansSilo [");
            sb.Append(" Deployment=").Append(DeploymentId);
            sb.Append(" LocalEndpoint=").Append(Address);
            sb.Append(" LocalPort=").Append(Port);
            sb.Append(" Generation=").Append(Generation);

            sb.Append(" Host=").Append(HostName);
            sb.Append(" Status=").Append(Status);
            sb.Append(" ProxyPort=").Append(ProxyPort);

            sb.Append(" SiloName=").Append(SiloName);

            if (!string.IsNullOrEmpty(SuspectingSilos)) sb.Append(" SuspectingSilos=").Append(SuspectingSilos);
            if (!string.IsNullOrEmpty(SuspectingTimes)) sb.Append(" SuspectingTimes=").Append(SuspectingTimes);
            sb.Append(" StartTime=").Append(StartTime);
            sb.Append(" IAmAliveTime=").Append(IAmAliveTime);
            sb.Append("]");
            return sb.ToString();
        }

        public static string ConstructSiloIdentity(SiloAddress silo)
        {
            return string.Format("{0}-{1}-{2}", silo.Endpoint.Address, silo.Endpoint.Port, silo.Generation);
        }

        public Dictionary<string, AttributeValue> GetKeys()
        {
            var keys = new Dictionary<string, AttributeValue>();
            keys.Add(DEPLOYMENT_ID_PROPERTY_NAME, new AttributeValue(DeploymentId));
            keys.Add(SILO_IDENTITY_PROPERTY_NAME, new AttributeValue(SiloIdentity));
            return keys;
        }

        public Dictionary<string, AttributeValue> GetFields(bool includeKeys = false)
        {
            var fields = new Dictionary<string, AttributeValue>();

            if (includeKeys)
            {
                fields.Add(DEPLOYMENT_ID_PROPERTY_NAME, new AttributeValue(DeploymentId));
                fields.Add(SILO_IDENTITY_PROPERTY_NAME, new AttributeValue(SiloIdentity));
            }

            if (!string.IsNullOrWhiteSpace(Address))
                fields.Add(ADDRESS_PROPERTY_NAME, new AttributeValue(Address));

            fields.Add(PORT_PROPERTY_NAME, new AttributeValue { N = Port.ToString() });
            fields.Add(GENERATION_PROPERTY_NAME, new AttributeValue { N = Generation.ToString() });

            if (!string.IsNullOrWhiteSpace(HostName))
                fields.Add(HOSTNAME_PROPERTY_NAME, new AttributeValue(HostName));

            fields.Add(STATUS_PROPERTY_NAME, new AttributeValue { N = Status.ToString() });
            fields.Add(PROXY_PORT_PROPERTY_NAME, new AttributeValue { N = ProxyPort.ToString() });

            if (!string.IsNullOrWhiteSpace(SiloName))
                fields.Add(SILO_NAME_PROPERTY_NAME, new AttributeValue(SiloName));

            if (!string.IsNullOrWhiteSpace(SuspectingSilos))
                fields.Add(SUSPECTING_SILOS_PROPERTY_NAME, new AttributeValue(SuspectingSilos));

            if (!string.IsNullOrWhiteSpace(SuspectingTimes))
                fields.Add(SUSPECTING_TIMES_PROPERTY_NAME, new AttributeValue(SuspectingTimes));

            if (!string.IsNullOrWhiteSpace(StartTime))
                fields.Add(START_TIME_PROPERTY_NAME, new AttributeValue(StartTime));

            if (!string.IsNullOrWhiteSpace(IAmAliveTime))
                fields.Add(I_AM_ALIVE_TIME_PROPERTY_NAME, new AttributeValue(IAmAliveTime));

            fields.Add(MEMBERSHIP_VERSION_PROPERTY_NAME, new AttributeValue { N = MembershipVersion.ToString() });

            fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = ETag.ToString() });
            return fields;
        }
    }
}
