using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        internal string DeploymentId { get; set; } = null!;

        /// <summary>
        /// Persisted as part of the KV Key therefore not serialised.
        /// </summary>
        [JsonIgnore]
        internal SiloAddress Address { get; set; } = null!;

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
        /// <summary>
        /// Gets or sets the silo host name.
        /// </summary>
        [JsonProperty]
        public string Hostname { get; set; } = null!;

        /// <summary>
        /// Gets or sets the silo gateway port.
        /// </summary>
        [JsonProperty]
        public int ProxyPort { get; set; }

        /// <summary>
        /// Gets or sets the time when the silo started.
        /// </summary>
        [JsonProperty]
        public DateTime StartTime { get; set; }

        /// <summary>
        /// Gets or sets the silo membership status.
        /// </summary>
        [JsonProperty]
        public SiloStatus Status { get; set; }

        /// <summary>
        /// Gets or sets the silo name.
        /// </summary>
        [JsonProperty]
        public string SiloName { get; set; } = null!;

        /// <summary>
        /// Gets or sets the silos which suspect this silo.
        /// </summary>
        [JsonProperty]
        public List<SuspectingSilo>? SuspectingSilos { get; set; }

        [JsonProperty]
        public Dictionary<string, string>? Metadata { get; set; }

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
        /// <summary>
        /// Gets or sets the identifier of the suspecting silo.
        /// </summary>
        [JsonProperty]
        public string Id { get; set; } = null!;

        /// <summary>
        /// Gets or sets the time when the suspicion was recorded.
        /// </summary>
        [JsonProperty]
        public DateTime Time { get; set; }
    }

    /// <summary>
    /// Contains methods for converting a Consul KVPair to and from a MembershipEntry.  
    /// This uses ConsulSiloRegistration objects as the serializable KV.Value and minimises conversion operations.
    /// </summary>
    internal class ConsulSiloRegistrationAssembler
    {
        private const string DeploymentKVPrefix = "orleans";  //Ensures a root KV namespace for orleans in Consul
        private const char KeySeparator = '/';
        internal const string SiloIAmAliveSuffix = "iamalive";
        internal const string VersionSuffix = "version";

        internal static string FormatVersionKey(string deploymentId, string? rootKvFolder) => $"{FormatDeploymentKVPrefix(deploymentId, rootKvFolder)}{KeySeparator}{VersionSuffix}";

        internal static string FormatDeploymentKVPrefix(string deploymentId, string? rootKvFolder)
        {
            //Backward compatible
            if (string.IsNullOrEmpty(rootKvFolder))
            {
                return $"{DeploymentKVPrefix}{KeySeparator}{deploymentId}";
            }

            return $"{rootKvFolder}{KeySeparator}{DeploymentKVPrefix}{KeySeparator}{deploymentId}";
        }

        internal static string FormatDeploymentSiloKey(string deploymentId, string? rootKvFolder, SiloAddress siloAddress)
        {
            return $"{FormatDeploymentKVPrefix(deploymentId, rootKvFolder)}{KeySeparator}{siloAddress.ToParsableString()}";
        }

        internal static string FormatSiloIAmAliveKey(string siloKey)
        {
            return $"{siloKey}{KeySeparator}{SiloIAmAliveSuffix}";
        }

        internal static string FormatSiloIAmAliveKey(string deploymentId, string? rootKvFolder, SiloAddress siloAddress)
        {
            return FormatSiloIAmAliveKey(FormatDeploymentSiloKey(deploymentId, rootKvFolder, siloAddress));
        }

        internal static ConsulSiloRegistration FromKVPairs(string deploymentId, KVPair siloKV, KVPair? iAmAliveKV)
        {
            var ret = JsonConvert.DeserializeObject<ConsulSiloRegistration>(Encoding.UTF8.GetString(siloKV.Value))!;

            var keyParts = siloKV.Key.Split(KeySeparator);
            ret.Address = SiloAddress.FromParsableString(keyParts[^1]);
            ret.DeploymentId = deploymentId;
            ret.LastIndex = siloKV.ModifyIndex;

            if (iAmAliveKV == null)
                ret.IAmAliveTime = ret.StartTime;
            else
                ret.IAmAliveTime = JsonConvert.DeserializeObject<DateTime>(Encoding.UTF8.GetString(iAmAliveKV.Value));

            return ret;
        }

        internal static ConsulSiloRegistration FromMembershipEntry(string deploymentId, MembershipEntry entry, string etag)
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
                SuspectingSilos = entry.SuspectTimes?.Select(silo => new SuspectingSilo { Id = silo.Item1.ToParsableString(), Time = silo.Item2 }).ToList(),
                Metadata = entry.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };

            return ret;
        }

        internal static KVPair ToKVPair(ConsulSiloRegistration siloRegistration, string? rootKvFolder)
        {
            var ret = new KVPair(ConsulSiloRegistrationAssembler.FormatDeploymentSiloKey(siloRegistration.DeploymentId, rootKvFolder, siloRegistration.Address));
            ret.ModifyIndex = siloRegistration.LastIndex;
            ret.Value = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(siloRegistration));
            return ret;
        }

        internal static KVPair ToIAmAliveKVPair(string deploymentId, string? rootKvFolder, SiloAddress siloAddress, DateTime iAmAliveTime)
        {
            var ret = new KVPair(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(deploymentId, rootKvFolder, siloAddress));
            ret.Value = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(iAmAliveTime));
            return ret;
        }

        internal static Tuple<MembershipEntry, string> ToMembershipEntry(ConsulSiloRegistration siloRegistration)
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
                Metadata = siloRegistration.Metadata?.ToImmutableDictionary(),

                // Optional - only for Azure role so initialised here
                RoleName = string.Empty,
                UpdateZone = 0,
                FaultZone = 0
            };

            return new Tuple<MembershipEntry, string>(entry, siloRegistration.LastIndex.ToString());
        }
    }

}
