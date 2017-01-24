using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Consul;
using Newtonsoft.Json;

namespace Orleans.Runtime.Host
{
    /// <summary>
    /// JSON Serializable Object that when serialized and Base64 encoded, forms the Value part of a Silo's Consul KVPair
    /// </summary>
    [JsonObject]
    public class ConsulSiloRegistration
    {
        /// <summary>
        /// Persisted as part of the KV Key therefore not serialised.
        /// </summary>
        [JsonIgnore]
        internal String DeploymentId { get; set; }

        /// <summary>
        /// Persisted as part of the KV Key therefore not serialised.
        /// </summary>
        [JsonIgnore]
        internal SiloAddress Address { get; set; }

        /// <summary>
        /// Persisted in a separate KV Subkey, therefore not serialised but held here to enable cleaner assembly to MembershipEntry.
        /// </summary>
        /// <remarks>
        /// Stored in a separate KV otherwise the regular updates to IAmAlive cause the Silo's KV.ModifyIndex to change 
        /// which in turn cause UpdateRow operations to fail.
        /// </remarks>
        [JsonIgnore]
        internal DateTime IAmAliveTime { get; set; }

        /// <summary>
        /// Used to compare CAS value on update, persisted as KV.ModifyIndex therefore not serialised.
        /// </summary>
        [JsonIgnore]
        internal ulong LastIndex { get; set; }

        //Public properties are serialized to the KV.Value
        [JsonProperty]
        public String Hostname { get; set; }

        [JsonProperty]
        public Int32 ProxyPort { get; set; }

        [JsonProperty]
        public DateTime StartTime { get; set; }

        [JsonProperty]
        public SiloStatus Status { get; set; }

        [JsonProperty]
        public String SiloName { get; set; }

        [JsonProperty]
        public List<SuspectingSilo> SuspectingSilos { get; set; }

        [JsonConstructor]
        internal ConsulSiloRegistration()
        {
            SuspectingSilos = new List<SuspectingSilo>();
        }
    }

    /// <summary>
    /// JSON Serializable Object that when serialized and Base64 encoded, forms each entry in the SuspectingSilos list
    /// </summary>
    [JsonObject]
    public class SuspectingSilo
    {
        [JsonProperty]
        public String Id { get; set; }

        [JsonProperty]
        public DateTime Time { get; set; }
    }

    /// <summary>
    /// Contains methods for converting a Consul KVPair to and from a MembershipEntry.  
    /// This uses ConsulSiloRegistration objects as the serialisable KV.Value and minimises conversion operations.
    /// </summary>
    internal class ConsulSiloRegistrationAssembler
    {
        private static String DeploymentKVPrefix = "orleans";  //Ensures a root KV namespace for orleans in Consul
        private static Char KeySeparator = '/';
        internal static String SiloIAmAliveSuffix = "iamalive";

        internal static String ParseDeploymentKVPrefix(String deploymentId)
        {
            return String.Format("{0}{1}{2}", DeploymentKVPrefix, KeySeparator, deploymentId);
        }

        internal static String ParseDeploymentSiloKey(String deploymentId, SiloAddress siloAddress)
        {
            return String.Format("{0}{1}{2}", ParseDeploymentKVPrefix(deploymentId), KeySeparator, siloAddress.ToParsableString());
        }

        internal static String ParseSiloIAmAliveKey(String siloKey)
        {
            return String.Format("{0}{1}{2}", siloKey, KeySeparator, SiloIAmAliveSuffix);
        }

        internal static String ParseSiloIAmAliveKey(String deploymentId, SiloAddress siloAddress)
        {
            return ParseSiloIAmAliveKey(ParseDeploymentSiloKey(deploymentId, siloAddress));
        }

        internal static ConsulSiloRegistration FromKVPairs(String deploymentId, KVPair siloKV, KVPair iAmAliveKV)
        {
            var ret = JsonConvert.DeserializeObject<ConsulSiloRegistration>(Encoding.UTF8.GetString(siloKV.Value));

            var keyParts = siloKV.Key.Split(KeySeparator);
            ret.Address = SiloAddress.FromParsableString(keyParts.Last());
            ret.DeploymentId = deploymentId;
            ret.LastIndex = siloKV.ModifyIndex;

            if (iAmAliveKV == null)
                ret.IAmAliveTime = ret.StartTime;
            else
                ret.IAmAliveTime = JsonConvert.DeserializeObject<DateTime>(Encoding.UTF8.GetString(iAmAliveKV.Value));

            return ret;
        }

        internal static ConsulSiloRegistration FromMembershipEntry(String deploymentId, MembershipEntry entry, String etag)
        {
            var ret = new ConsulSiloRegistration
            {
                DeploymentId = deploymentId,
                Address = entry.SiloAddress,
                IAmAliveTime = entry.IAmAliveTime,
                LastIndex = Convert.ToUInt64(etag),
                Hostname = entry.HostName,
                ProxyPort = entry.ProxyPort,
                StartTime = entry.StartTime,
                Status = entry.Status,
                SiloName = entry.SiloName,
                SuspectingSilos = entry.SuspectTimes?.Select(silo => new SuspectingSilo { Id = silo.Item1.ToParsableString(), Time = silo.Item2 }).ToList()
            };

            return ret;
        }

        internal static KVPair ToKVPair(ConsulSiloRegistration siloRegistration)
        {
            var ret = new KVPair(ConsulSiloRegistrationAssembler.ParseDeploymentSiloKey(siloRegistration.DeploymentId, siloRegistration.Address));
            ret.ModifyIndex = siloRegistration.LastIndex;
            ret.Value = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(siloRegistration));
            return ret;
        }

        internal static KVPair ToIAmAliveKVPair(String deploymentId, SiloAddress siloAddress, DateTime iAmAliveTime)
        {
            var ret = new KVPair(ConsulSiloRegistrationAssembler.ParseSiloIAmAliveKey(deploymentId, siloAddress));
            ret.Value = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(iAmAliveTime));
            return ret;
        }

        internal static Tuple<MembershipEntry, String> ToMembershipEntry(ConsulSiloRegistration siloRegistration)
        {
            var entry = new MembershipEntry
            {
                SiloAddress = siloRegistration.Address,
                HostName = siloRegistration.Hostname,
                Status = siloRegistration.Status,
                ProxyPort = siloRegistration.ProxyPort,
                StartTime = siloRegistration.StartTime,
                SuspectTimes = siloRegistration.SuspectingSilos?.Select(silo => new Tuple<SiloAddress, DateTime>(SiloAddress.FromParsableString(silo.Id), silo.Time)).ToList(),
                IAmAliveTime = siloRegistration.IAmAliveTime,
                SiloName = siloRegistration.SiloName,

                // Optional - only for Azure role so initialised here
                RoleName = String.Empty,
                UpdateZone = 0,
                FaultZone = 0
            };

            return new Tuple<MembershipEntry, String>(entry, siloRegistration.LastIndex.ToString());
        }
    }
}
