using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Orleans.Runtime;

namespace Orleans.AzureUtils
{
    internal class SiloInstanceTableEntry : ITableEntity
    {
        public string DeploymentId { get; set; }    // PartitionKey
        public string Address { get; set; }         // RowKey
        public string Port { get; set; }            // RowKey
        public string Generation { get; set; }      // RowKey

        public string HostName { get; set; }        // Mandatory
        public string Status { get; set; }          // Mandatory
        public string ProxyPort { get; set; }       // Optional

        public string RoleName { get; set; }        // Optional - only for Azure role
        public string SiloName { get; set; }
        public string InstanceName { get; set; }    // For backward compatability we leave the old column, untill all clients update the code to new version.
        public string UpdateZone { get; set; }         // Optional - only for Azure role
        public string FaultZone { get; set; }          // Optional - only for Azure role

        public string SuspectingSilos { get; set; }          // For liveness
        public string SuspectingTimes { get; set; }          // For liveness

        public string StartTime       { get; set; }          // Time this silo was started. For diagnostics.
        public string IAmAliveTime    { get; set; }           // Time this silo updated it was alive. For diagnostics.
        public string MembershipVersion      { get; set; }               // Special version row (for serializing table updates). // We'll have a designated row with only MembershipVersion column.

        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        internal const string TABLE_VERSION_ROW = "VersionRow"; // Row key for version row.
        internal const char Seperator = '-';

        public static string ConstructRowKey(SiloAddress silo)
        {
            return String.Format("{0}-{1}-{2}", silo.Endpoint.Address, silo.Endpoint.Port, silo.Generation);
        }
        internal static SiloAddress UnpackRowKey(string rowKey)
        {
            var debugInfo = "UnpackRowKey";
            try
            {
#if DEBUG
                debugInfo = String.Format("UnpackRowKey: RowKey={0}", rowKey);
                Trace.TraceInformation(debugInfo);
#endif
                int idx1 = rowKey.IndexOf(Seperator);
                int idx2 = rowKey.LastIndexOf(Seperator);
#if DEBUG
                debugInfo = String.Format("UnpackRowKey: RowKey={0} Idx1={1} Idx2={2}", rowKey, idx1, idx2);
#endif
                var addressStr = rowKey.Substring(0, idx1);
                var portStr = rowKey.Substring(idx1 + 1, idx2 - idx1 - 1);
                var genStr = rowKey.Substring(idx2 + 1);
#if DEBUG
                debugInfo = String.Format("UnpackRowKey: RowKey={0} -> Address={1} Port={2} Generation={3}", rowKey, addressStr, portStr, genStr);
                Trace.TraceInformation(debugInfo);
#endif
                IPAddress address = IPAddress.Parse(addressStr);
                int port = Int32.Parse(portStr);
                int generation = Int32.Parse(genStr);
                return SiloAddress.New(new IPEndPoint(address, port), generation);
            }
            catch (Exception exc)
            {
                throw new AggregateException("Error from " + debugInfo, exc);
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (RowKey.Equals(TABLE_VERSION_ROW))
            {
                sb.Append("VersionRow [").Append(DeploymentId);
                sb.Append(" Deployment=").Append(DeploymentId);
                sb.Append(" MembershipVersion=").Append(MembershipVersion);
                sb.Append("]");
            }
            else
            {
                sb.Append("OrleansSilo [");
                sb.Append(" Deployment=").Append(DeploymentId);
                sb.Append(" LocalEndpoint=").Append(Address);
                sb.Append(" LocalPort=").Append(Port);
                sb.Append(" Generation=").Append(Generation);

                sb.Append(" Host=").Append(HostName);
                sb.Append(" Status=").Append(Status);
                sb.Append(" ProxyPort=").Append(ProxyPort);

                if (!string.IsNullOrEmpty(RoleName)) sb.Append(" RoleName=").Append(RoleName);
                sb.Append(" SiloName=").Append(SiloName);
                sb.Append(" UpgradeZone=").Append(UpdateZone);
                sb.Append(" FaultZone=").Append(FaultZone);

                if (!string.IsNullOrEmpty(SuspectingSilos)) sb.Append(" SuspectingSilos=").Append(SuspectingSilos);
                if (!string.IsNullOrEmpty(SuspectingTimes)) sb.Append(" SuspectingTimes=").Append(SuspectingTimes);
                sb.Append(" StartTime=").Append(StartTime);
                sb.Append(" IAmAliveTime=").Append(IAmAliveTime);
                sb.Append("]");
            }
            return sb.ToString();
        }
    }
}
