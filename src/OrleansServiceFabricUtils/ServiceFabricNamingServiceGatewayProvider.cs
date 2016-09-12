using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Microsoft.Orleans.ServiceFabric
{
    using System.Fabric;
    using System.Globalization;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;
    using Newtonsoft.Json.Linq;

    public class ServiceFabricNamingServiceGatewayProvider : IMembershipTable, IGatewayListProvider
    {
        private const string VersionPropertyName = "VERSION";

        private const string ETagPropertyName = "ETAG";

        private const string DefaultETag = "0";

        private const string EntryPrefix = ".ENTRY_";

        private const string AlivePrefix = ".ALIVE_";

        private const string ETagPrefix = ".ETAG_";

        private readonly FabricClient fabricClient;

        private Logger log;

        private FabricClient.PropertyManagementClient store;

        private Uri tableUri;

        public ServiceFabricNamingServiceGatewayProvider(FabricClient client)
        {
            this.fabricClient = client;
        }

        public Task InitializeGatewayListProvider(ClientConfiguration config, Logger logger)
        {
            this.Initialize(logger, config.DeploymentId);
            this.MaxStaleness = config.GatewayListRefreshPeriod;
            
            return Task.FromResult(0);
        }

        public async Task InitializeMembershipTable(GlobalConfiguration config, bool tryInitTableVersion, Logger logger)
        {
            this.Initialize(logger, config.DeploymentId);
            this.log = logger;

            if (tryInitTableVersion)
            {
                try
                {
                    await this.store.CreateNameAsync(this.tableUri);
                }
                catch (FabricElementAlreadyExistsException)
                {
                    this.log?.Verbose($"Membership table already exists in property store at {this.tableUri}");
                }

                var ops = new PropertyBatchOperation[]
                              {
                                  // Check preconditions.
                                  new CheckExistsPropertyOperation(ETagPropertyName, false),

                                  // Update version and insert rows.
                                  new PutPropertyOperation(VersionPropertyName, 0),
                                  new PutPropertyOperation(ETagPropertyName, DefaultETag)
                              };
                await this.store.SubmitPropertyBatchAsync(this.tableUri, ops);
            }
        }

        private void Initialize(Logger logger, string deploymentId)
        {
            this.tableUri = GetTableUri(deploymentId);
            this.log = logger;
            this.store = this.fabricClient.PropertyManager;
        }

        private static Uri GetTableUri(string deploymentId)
        {
            return new Uri("fabric:/silos_" + deploymentId);
        }

        public Task<MembershipTableData> ReadRow(SiloAddress siloAddress)
        {
            return this.ReadEntries(siloAddress);
        }

        public Task<MembershipTableData> ReadAll()
        {
            return this.ReadEntries();
        }

        private async Task<MembershipTableData> ReadEntries(SiloAddress siloAddress = null)
        {
            ReadResults result;
            do
            {
                // Continue attempting to read from the table until a consistent read is made.
                result = await this.TryReadEntries(siloAddress);
            }
            while (!result.IsConsistent);
            return result.Results;
        }

        private async Task<ReadResults> TryReadEntries(SiloAddress siloAddress = null)
        {
            var suffix = siloAddress == null ? string.Empty : "_" + siloAddress.ToParsableString();
            var entries = new Dictionary<string, PropertyTableEntry>();
            var tableVersion = 0;
            string tableETag = null;
            PropertyEnumerationResult result = null;
            do
            {
                result = await this.store.EnumeratePropertiesAsync(this.tableUri, true, result);
                if (!result.IsConsistent)
                {
                    // The table was modified while enumerating the properties.
                    return this.InconsistentRead;
                }

                foreach (var property in result)
                {
                    var name = property.Metadata.PropertyName;
                    if (string.Equals(VersionPropertyName, name))
                    {
                        tableVersion = (int)property.GetValue<long>();
                    }
                    else if (string.Equals(ETagPropertyName, name))
                    {
                        tableETag = property.GetValue<string>();
                    }
                    else if (name.StartsWith(".") & name.EndsWith(suffix))
                    {
                        var key = GetSiloAddress(name);
                        PropertyTableEntry entry;
                        if (!entries.TryGetValue(key, out entry))
                        {
                            entry = entries[key] = new PropertyTableEntry();
                        }

                        if (name.StartsWith(EntryPrefix))
                        {
                            entry.Entry = JsonConvert.DeserializeObject<MembershipEntry>(property.GetValue<string>(), MembershipSerializerSettings.Instance);
                        }
                        else if (name.StartsWith(AlivePrefix))
                        {
                            entry.LastIAmAliveTime = property.GetValue<long>();
                        }
                        else if (name.StartsWith(ETagPrefix))
                        {
                            entry.ETag = property.GetValue<string>();
                        }
                    }
                }
            }
            while (result.HasMoreData);

            var results = new List<Tuple<MembershipEntry, string>>(entries.Count);
            foreach (var entry in entries.Values)
            {
                if (entry.Entry == null) continue;
                entry.Entry.IAmAliveTime = new DateTime(entry.LastIAmAliveTime);
                results.Add(Tuple.Create(entry.Entry, entry.ETag ?? DefaultETag));
            }

            return new ReadResults(new MembershipTableData(results, new TableVersion(tableVersion, tableETag)));
        }

        private static string GetSiloAddress(string name) => name.Substring(name.IndexOf('_') + 1);

        private ReadResults InconsistentRead { get; } = new ReadResults(null);

        private struct RowNames
        {
            public static RowNames Create(SiloAddress siloAddress)
            {
                var key = siloAddress.ToParsableString();
                return new RowNames { Entry = EntryPrefix + key, ETag = ETagPrefix + key, Alive = AlivePrefix + key };
            }

            public static string GetAliveRowName(SiloAddress siloAddress)
                => AlivePrefix + siloAddress.ToParsableString();

            public string Entry { get; private set; }
            public string ETag { get; private set; }
            public string Alive { get; private set; }
        }

        public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            var rowNames = RowNames.Create(entry.SiloAddress);
            var newETag = (tableVersion.Version + 1).ToString(CultureInfo.InvariantCulture);
            var ops = new PropertyBatchOperation[]
                          {
                              // Check preconditions.
                              new CheckValuePropertyOperation(ETagPropertyName, tableVersion.VersionEtag),
                              new CheckExistsPropertyOperation(rowNames.Entry, false),

                              // Update version and insert rows.
                              new PutPropertyOperation(VersionPropertyName, tableVersion.Version),
                              new PutPropertyOperation(ETagPropertyName, newETag),
                              new PutPropertyOperation(rowNames.Entry, JsonConvert.SerializeObject(entry, MembershipSerializerSettings.Instance)), 
                              new PutPropertyOperation(rowNames.Alive, entry.IAmAliveTime.Ticks), 
                              new PutPropertyOperation(rowNames.ETag, newETag), 
                          };
            var result = await this.store.SubmitPropertyBatchAsync(this.tableUri, ops);
            
            // A value of -1 indicates that no operation failed.
            return result.FailedOperationIndex == -1;
        }

        public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            var rowNames = RowNames.Create(entry.SiloAddress);
            var newETag = (tableVersion.Version + 1).ToString(CultureInfo.InvariantCulture);
            var ops = new PropertyBatchOperation[]
                          {
                              // Check preconditions.
                              new CheckValuePropertyOperation(ETagPropertyName, tableVersion.VersionEtag),
                              new CheckValuePropertyOperation(rowNames.ETag, etag ?? DefaultETag),
                              new CheckExistsPropertyOperation(rowNames.Entry, true),

                              // Update version and insert rows.
                              new PutPropertyOperation(VersionPropertyName, tableVersion.Version),
                              new PutPropertyOperation(ETagPropertyName, newETag),
                              new PutPropertyOperation(rowNames.Entry, JsonConvert.SerializeObject(entry, MembershipSerializerSettings.Instance)),
                              new PutPropertyOperation(rowNames.Alive, entry.IAmAliveTime.Ticks),
                              new PutPropertyOperation(rowNames.ETag, newETag), 
                          };
            var result = await this.store.SubmitPropertyBatchAsync(this.tableUri, ops);

            // A value of -1 indicates that no operation failed.
            return result.FailedOperationIndex == -1;
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            return this.store.PutPropertyAsync(
                this.tableUri, 
                RowNames.GetAliveRowName(entry.SiloAddress), 
                entry.IAmAliveTime.Ticks);
        }

        public async Task<IList<Uri>> GetGateways()
        {
            var allSilos = await this.ReadAll();
            return
                allSilos.Members.Select(e => e.Item1)
                    .Where(m => m.Status == SiloStatus.Active && m.ProxyPort != 0)
                    .Select(
                        m =>
                            {
                                m.SiloAddress.Endpoint.Port = m.ProxyPort;
                                return m.SiloAddress.ToGatewayUri();
                            }).ToList();
        }

        public TimeSpan MaxStaleness { get; private set; }

        public bool IsUpdatable => true;

        public Task DeleteMembershipTableEntries(string deploymentId)
        {
            return this.store.DeleteNameAsync(GetTableUri(deploymentId));
        }

        private class ReadResults
        {
            public ReadResults(MembershipTableData results)
            {
                this.Results = results;
            }

            public bool IsConsistent => this.Results != null;
            public MembershipTableData Results { get; }
        }

        private class PropertyTableEntry
        {
            public MembershipEntry Entry { get; set; }
            public string ETag { get; set; }
            public long LastIAmAliveTime { get; set; }
        }
        internal class MembershipSerializerSettings : JsonSerializerSettings
        {
            public static readonly MembershipSerializerSettings Instance = new MembershipSerializerSettings();

            private MembershipSerializerSettings()
            {
                Converters.Add(new SiloAddressConverter());
                Converters.Add(new MembershipEntryConverter());
                Converters.Add(new StringEnumConverter());
            }

            private class MembershipEntryConverter : JsonConverter
            {
                public override bool CanConvert(Type objectType)
                {
                    return (objectType == typeof(MembershipEntry));
                }

                public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
                {
                    MembershipEntry me = (MembershipEntry)value;
                    writer.WriteStartObject();
                    writer.WritePropertyName("SiloAddress"); serializer.Serialize(writer, me.SiloAddress);
                    writer.WritePropertyName("HostName"); writer.WriteValue(me.HostName);
                    writer.WritePropertyName("SiloName"); writer.WriteValue(me.SiloName);
                    writer.WritePropertyName("InstanceName"); writer.WriteValue(me.SiloName);
                    writer.WritePropertyName("Status"); serializer.Serialize(writer, me.Status);
                    writer.WritePropertyName("ProxyPort"); writer.WriteValue(me.ProxyPort);
                    writer.WritePropertyName("StartTime"); writer.WriteValue(me.StartTime);
                    writer.WritePropertyName("SuspectTimes"); serializer.Serialize(writer, me.SuspectTimes);
                    writer.WriteEndObject();
                }

                public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                    JsonSerializer serializer)
                {
                    JObject jo = JObject.Load(reader);
                    return new MembershipEntry
                    {
                        SiloAddress = jo["SiloAddress"].ToObject<SiloAddress>(serializer),
                        HostName = jo["HostName"].ToObject<string>(),
                        SiloName = (jo["SiloName"] ?? jo["InstanceName"]).ToObject<string>(),
                        Status = jo["Status"].ToObject<SiloStatus>(serializer),
                        ProxyPort = jo["ProxyPort"].Value<int>(),
                        StartTime = jo["StartTime"].Value<DateTime>(),
                        SuspectTimes = jo["SuspectTimes"].ToObject<List<Tuple<SiloAddress, DateTime>>>(serializer)
                    };
                }
            }

            private class SiloAddressConverter : JsonConverter
            {
                public override bool CanConvert(Type objectType)
                {
                    return (objectType == typeof(SiloAddress));
                }

                public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
                {
                    SiloAddress se = (SiloAddress)value;
                    writer.WriteStartObject();
                    writer.WritePropertyName("SiloAddress");
                    writer.WriteValue(se.ToParsableString());
                    writer.WriteEndObject();
                }

                public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                    JsonSerializer serializer)
                {
                    JObject jo = JObject.Load(reader);
                    string seStr = jo["SiloAddress"].ToObject<string>(serializer);
                    return SiloAddress.FromParsableString(seStr);
                }
            }
        }
    }
}
