using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Reflection;
using Orleans;
using Orleans.Runtime;
using Orleans.SqlUtils;

namespace OrleansSQLUtils.Storage
{
    /// <summary>
    /// This class implements the expected contract between Orleans and the underlying relational storage.
    /// It makes sure all the stored queries are present and 
    /// </summary>
    internal class DbStoredQueries
    {
        private readonly Dictionary<string, string> queries;
        internal DbStoredQueries(Dictionary<string, string> queries)
        {
            var fields = typeof (DbStoredQueries).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(p => p.Name);
            var missingQueryKeys = fields.Except(queries.Keys).ToArray();
            if (missingQueryKeys.Length > 0)
            {
                throw new ArgumentException(
                    $"Not all required queries found. Missing are: {string.Join(",", missingQueryKeys)}");
            }
            this.queries = queries;
        }

        /// <summary>
        /// The query that's used to get all the stored queries.
        /// this will probably be the same for all relational dbs.
        /// </summary>
        internal const string GetQueriesKey = "SELECT QueryKey, QueryText FROM OrleansQuery;";

        /// <summary>
        /// A query template to retrieve gateway URIs.
        /// </summary>        
        internal string GatewaysQueryKey => queries[nameof(GatewaysQueryKey)];

        /// <summary>
        /// A query template to retrieve a single row of membership data.
        /// </summary>        
        internal string MembershipReadRowKey => queries[nameof(MembershipReadRowKey)];

        /// <summary>
        /// A query template to retrieve all membership data.
        /// </summary>        
        internal string MembershipReadAllKey => queries[nameof(MembershipReadAllKey)];

        /// <summary>
        /// A query template to insert a membership version row.
        /// </summary>
        internal string InsertMembershipVersionKey => queries[nameof(InsertMembershipVersionKey)];

        /// <summary>
        /// A query template to update "I Am Alive Time".
        /// </summary>
        internal string UpdateIAmAlivetimeKey => queries[nameof(UpdateIAmAlivetimeKey)];

        /// <summary>
        /// A query template to insert a membership row.
        /// </summary>
        internal string InsertMembershipKey => queries[nameof(InsertMembershipKey)];

        /// <summary>
        /// A query template to update a membership row.
        /// </summary>
        internal string UpdateMembershipKey => queries[nameof(UpdateMembershipKey)];

        /// <summary>
        /// A query template to delete membership entries.
        /// </summary>
        internal string DeleteMembershipTableEntriesKey => queries[nameof(DeleteMembershipTableEntriesKey)];

        /// <summary>
        /// A query template to read reminder entries.
        /// </summary>
        internal string ReadReminderRowsKey => queries[nameof(ReadReminderRowsKey)];

        /// <summary>
        /// A query template to read reminder entries with ranges.
        /// </summary>
        internal string ReadRangeRows1Key => queries[nameof(ReadRangeRows1Key)];

        /// <summary>
        /// A query template to read reminder entries with ranges.
        /// </summary>
        internal string ReadRangeRows2Key => queries[nameof(ReadRangeRows2Key)];

        /// <summary>
        /// A query template to read a reminder entry with ranges.
        /// </summary>
        internal string ReadReminderRowKey => queries[nameof(ReadReminderRowKey)];

        /// <summary>
        /// A query template to upsert a reminder row.
        /// </summary>
        internal string UpsertReminderRowKey => queries[nameof(UpsertReminderRowKey)];

        /// <summary>
        /// A query template to insert Orleans statistics.
        /// </summary>
        internal string InsertOrleansStatisticsKey => queries[nameof(InsertOrleansStatisticsKey)];

        /// <summary>
        /// A query template to insert or update an Orleans client metrics key.
        /// </summary>
        internal string UpsertReportClientMetricsKey => queries[nameof(UpsertReportClientMetricsKey)];

        /// <summary>
        /// A query template to insert or update an Orleans silo metrics key.
        /// </summary>
        internal string UpsertSiloMetricsKey => queries[nameof(UpsertSiloMetricsKey)];

        /// <summary>
        /// A query template to delete a reminder row.
        /// </summary>
        internal string DeleteReminderRowKey => queries[nameof(DeleteReminderRowKey)];

        /// <summary>
        /// A query template to delete all reminder rows.
        /// </summary>
        internal string DeleteReminderRowsKey => queries[nameof(DeleteReminderRowsKey)];

        internal class Converters
        {
            internal static KeyValuePair<string, string> GetQueryKeyAndValue(IDataRecord record)
            {
                return new KeyValuePair<string, string>(record.GetValue<string>("QueryKey"),
                    record.GetValue<string>("QueryText"));
            }

            internal static ReminderEntry GetReminderEntry(IDataRecord record)
            {
                //Having non-null field, GrainId, means with the query filter options, an entry was found.
                string grainId = record.GetValueOrDefault<string>(nameof(Columns.GrainId));
                if (grainId != null)
                {
                    return new ReminderEntry
                    {
                        GrainRef = GrainReference.FromKeyString(grainId),
                        ReminderName = record.GetValue<string>(nameof(Columns.ReminderName)),
                        StartAt = record.GetValue<DateTime>(nameof(Columns.StartTime)),
                        Period = TimeSpan.FromMilliseconds(record.GetValue<int>(nameof(Columns.Period))),
                        ETag = GetVersion(record).ToString()
                    };
                }
                return null;
            }

            internal static Tuple<MembershipEntry, int> GetMembershipEntry(IDataRecord record)
            {
                //TODO: This is a bit of hack way to check in the current version if there's membership data or not, but if there's a start time, there's member.            
                DateTime? startTime = record.GetValueOrDefault<DateTime?>(nameof(Columns.StartTime));
                MembershipEntry entry = null;
                if (startTime.HasValue)
                {
                    entry = new MembershipEntry
                    {
                        SiloAddress = GetSiloAddress(record, nameof(Columns.Port)),
                        SiloName = TryGetSiloName(record),
                        HostName = record.GetValue<string>(nameof(Columns.HostName)),
                        Status = record.GetValue<SiloStatus>(nameof(Columns.Status)),
                        ProxyPort = record.GetValue<int>(nameof(Columns.ProxyPort)),
                        StartTime = startTime.Value,
                        IAmAliveTime = record.GetValue<DateTime>(nameof(Columns.IAmAliveTime))
                    };

                    string suspectingSilos = record.GetValueOrDefault<string>(nameof(Columns.SuspectTimes));
                    if (!string.IsNullOrWhiteSpace(suspectingSilos))
                    {
                        entry.SuspectTimes = new List<Tuple<SiloAddress, DateTime>>();
                        entry.SuspectTimes.AddRange(suspectingSilos.Split('|').Select(s =>
                        {
                            var split = s.Split(',');
                            return new Tuple<SiloAddress, DateTime>(SiloAddress.FromParsableString(split[0]),
                                LogFormatter.ParseDate(split[1]));
                        }));
                    }
                }

                return Tuple.Create(entry, GetVersion(record));
            }

            /// <summary>
            /// This method is for compatibility with membership tables that
            /// do not contain a SiloName field
            /// </summary>
            private static string TryGetSiloName(IDataRecord record)
            {
                int pos;
                try
                {
                    pos = record.GetOrdinal(nameof(Columns.SiloName));
                }
                catch (IndexOutOfRangeException)
                {
                    return null;
                }

                return (string)record.GetValue(pos);

            }

            internal static int GetVersion(IDataRecord record)
            {
                return Convert.ToInt32(record.GetValue<object>(nameof(Version)));
            }

            internal static Uri GetGatewayUri(IDataRecord record)
            {
                return GetSiloAddress(record, nameof(Columns.ProxyPort)).ToGatewayUri();
            }

            private static SiloAddress GetSiloAddress(IDataRecord record, string portName)
            {
                int port = record.GetValue<int>(portName);
                int generation = record.GetValue<int>(nameof(Columns.Generation));
                string address = record.GetValue<string>(nameof(Columns.Address));
                var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse(address), port), generation);
                return siloAddress;
            }

            internal static bool GetSingleBooleanValue(IDataRecord record)
            {
                if (record.FieldCount != 1) throw new InvalidOperationException("Expected a single column");
                return Convert.ToBoolean(record.GetValue(0));
            }
        }

        internal class Columns
        {
            private readonly IDbCommand command;

            internal Columns(IDbCommand cmd)
            {
                command = cmd;
            }

            private void Add<T>(string paramName, T paramValue, DbType? dbType = null)
            {
                command.AddParameter(paramName, paramValue, dbType: dbType);
            }

            private void AddCoreMetricsParams(ICorePerformanceMetrics coreMetrics)
            {
                Add(nameof(coreMetrics.CpuUsage), coreMetrics.CpuUsage);
                Add(nameof(coreMetrics.MemoryUsage), coreMetrics.MemoryUsage);
                Add(nameof(coreMetrics.SendQueueLength), coreMetrics.SendQueueLength);
                Add(nameof(coreMetrics.ReceiveQueueLength), coreMetrics.ReceiveQueueLength);
                Add(nameof(coreMetrics.SentMessages), coreMetrics.SentMessages);
                Add(nameof(coreMetrics.ReceivedMessages), coreMetrics.ReceivedMessages);
            }

            private void AddAddress(string name, IPAddress address)
            {
                Add(name, address.ToString(), dbType: DbType.AnsiString);
            }

            private void AddGrainHash(string name, uint grainHash)
            {
                Add(name, (int) grainHash);
            }

            internal string ClientId
            {
                set { Add(nameof(ClientId), value); }
            }
            
            internal int GatewayPort
            {
                set { Add(nameof(GatewayPort), value); }
            }

            internal IPAddress GatewayAddress
            {
                set { AddAddress(nameof(GatewayAddress), value); }
            }

            internal string SiloId
            {
                set { Add(nameof(SiloId), value); }
            }

            internal string Id
            {
                set { Add(nameof(Id), value); }
            }

            internal string Name
            {
                set { Add(nameof(Name), value); }
            }

            internal const string IsValueDelta = nameof(IsValueDelta);
            internal const string StatValue = nameof(StatValue);
            internal const string Statistic = nameof(Statistic);

            internal List<ICounter> Counters
            {
                set
                {
                    for (int i = 0; i < value.Count; ++i)
                    {
                        Add($"{IsValueDelta}{i}", value[i].IsValueDelta);
                        Add($"{StatValue}{i}",
                            value[i].IsValueDelta ? value[i].GetDeltaString() : value[i].GetValueString());
                        Add($"{Statistic}{i}", value[i].Name);
                    }
                }
            }

            internal ISiloPerformanceMetrics SiloMetrics
            {
                set
                {
                    AddCoreMetricsParams(value);
                    Add(nameof(value.ActivationCount), value.ActivationCount);
                    Add(nameof(value.RecentlyUsedActivationCount), value.RecentlyUsedActivationCount);
                    Add(nameof(value.RequestQueueLength), value.RequestQueueLength);
                    Add(nameof(value.IsOverloaded), value.IsOverloaded);
                    Add(nameof(value.ClientCount), value.ClientCount);
                }
            }

            internal IClientPerformanceMetrics ClientMetrics
            {
                set
                {
                    AddCoreMetricsParams(value);
                    Add(nameof(value.ConnectedGatewayCount), value.ConnectedGatewayCount);
                }
            }

            internal SiloAddress SiloAddress
            {
                set
                {
                    Address = value.Endpoint.Address;
                    Port = value.Endpoint.Port;
                    Generation = value.Generation;
                }
            }

            internal int Generation
            {
                set { Add(nameof(Generation), value); }
            }

            internal int Port
            {
                set { Add(nameof(Port), value); }
            }

            internal uint BeginHash
            {
                set { AddGrainHash(nameof(BeginHash), value); }
            }

            internal uint EndHash
            {
                set { AddGrainHash(nameof(EndHash), value); }
            }

            internal uint GrainHash
            {
                set { AddGrainHash(nameof(GrainHash), value); }
            }

            internal DateTime StartTime
            {
                set { Add(nameof(StartTime), value); }
            }

            internal IPAddress Address
            {
                set { AddAddress(nameof(Address), value); }
            }

            internal string ServiceId
            {
                set { Add(nameof(ServiceId), value); }
            }

            internal string DeploymentId
            {
                set { Add(nameof(DeploymentId), value); }
            }

            internal string SiloName
            {
                set { Add(nameof(SiloName), value); }
            }

            internal string HostName
            {
                set { Add(nameof(HostName), value); }
            }

            internal string Version
            {
                set { Add(nameof(Version), int.Parse(value)); }
            }

            internal DateTime IAmAliveTime
            {
                set { Add(nameof(IAmAliveTime), value); }
            }

            internal string GrainId
            {
                set { Add(nameof(GrainId), value, dbType: DbType.AnsiString); }
            }

            internal string ReminderName
            {
                set { Add(nameof(ReminderName), value); }
            }

            internal TimeSpan Period
            {
                set { Add(nameof(Period), (int) value.TotalMilliseconds); }
            }

            internal SiloStatus Status
            {
                set { Add(nameof(Status), (int) value); }
            }

            internal int ProxyPort
            {
                set { Add(nameof(ProxyPort), value); }
            }

            internal List<Tuple<SiloAddress, DateTime>> SuspectTimes
            {
                set
                {
                    Add(nameof(SuspectTimes), value == null
                        ? null
                        : string.Join("|", value.Select(
                            s => $"{s.Item1.ToParsableString()},{LogFormatter.PrintDate(s.Item2)}")));
                }
            }
        }
    }
}