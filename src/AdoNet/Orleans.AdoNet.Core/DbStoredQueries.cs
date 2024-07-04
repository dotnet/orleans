using System.Net;
using static System.String;

namespace Orleans.AdoNet.Core;

internal abstract class DbStoredQueries
{
    private readonly Dictionary<string, string> _queries;

    internal DbStoredQueries(Dictionary<string, string> queries)
    {
        var missingQueryKeys = typeof(DbStoredQueries)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(p => p.Name)
            .Except(queries.Keys)
            .ToList();

        if (missingQueryKeys.Count > 0)
        {
            throw new ArgumentException($"Not all required queries found. Missing are: {Join(",", missingQueryKeys)}");
        }

        _queries = queries;
    }

    /// <summary>
    /// The query that's used to get all the stored queries.
    /// this will probably be the same for all relational dbs.
    /// </summary>
    internal const string GetQueriesKey = "SELECT QueryKey, QueryText FROM OrleansQuery";

    protected string GetQuery(string key) => _queries[key];

    internal static class Converters
    {
        internal static KeyValuePair<string, string> GetQueryKeyAndValue(IDataRecord record)
        {
            return new KeyValuePair<string, string>(record.GetValue<string>("QueryKey"),
                record.GetValue<string>("QueryText"));
        }

        internal static Tuple<MembershipEntry?, int> GetMembershipEntry(IDataRecord record)
        {
            //TODO: This is a bit of hack way to check in the current version if there's membership data or not, but if there's a start time, there's member.
            var startTime = record.GetDateTimeValueOrDefault(nameof(Columns.StartTime));
            MembershipEntry? entry = null;
            if (startTime.HasValue)
            {
                entry = new MembershipEntry
                {
                    SiloAddress = GetSiloAddress(record, nameof(Columns.Port)),
                    SiloName = TryGetSiloName(record),
                    HostName = record.GetValue<string>(nameof(Columns.HostName)),
                    Status = (SiloStatus)Enum.Parse(typeof(SiloStatus), record.GetInt32(nameof(Columns.Status)).ToString()),
                    ProxyPort = record.GetInt32(nameof(Columns.ProxyPort)),
                    StartTime = startTime.Value,
                    IAmAliveTime = record.GetDateTimeValue(nameof(Columns.IAmAliveTime))
                };

                var suspectingSilos = record.GetValueOrDefault<string>(nameof(Columns.SuspectTimes));
                if (!IsNullOrWhiteSpace(suspectingSilos))
                {
                    entry.SuspectTimes =
                    [
                        .. suspectingSilos.Split('|').Select(s =>
                        {
                            var split = s.Split(',');
                            return new Tuple<SiloAddress, DateTime>(
                                SiloAddress.FromParsableString(split[0]),
                                LogFormatter.ParseDate(split[1]));
                        }),
                    ];
                }
            }

            return Tuple.Create(entry, GetVersion(record));
        }

        /// <summary>
        /// This method is for compatibility with membership tables that
        /// do not contain a SiloName field
        /// </summary>
        private static string? TryGetSiloName(IDataRecord record)
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

        internal static int GetVersion(IDataRecord record) => Convert.ToInt32(record.GetValue<object>(nameof(Version)));

        internal static Uri GetGatewayUri(IDataRecord record) => GetSiloAddress(record, nameof(Columns.ProxyPort)).ToGatewayUri();

        private static SiloAddress GetSiloAddress(IDataRecord record, string portName)
        {
            //Use the GetInt32 method instead of the generic GetValue<TValue> version to retrieve the value from the data record
            //GetValue<int> causes an InvalidCastException with oracle data provider. See https://github.com/dotnet/orleans/issues/3561
            var port = record.GetInt32(portName);
            var generation = record.GetInt32(nameof(Columns.Generation));
            var address = record.GetValue<string>(nameof(Columns.Address));
            var siloAddress = SiloAddress.New(IPAddress.Parse(address), port, generation);
            return siloAddress;
        }

        internal static bool GetSingleBooleanValue(IDataRecord record)
        {
            return record.FieldCount != 1
                ? throw new InvalidOperationException("Expected a single column")
                : Convert.ToBoolean(record.GetValue(0));
        }
    }

    internal class Columns
    {
        private readonly IDbCommand _command;

        internal Columns(IDbCommand cmd)
        {
            _command = cmd;
        }

        private void Add<T>(string paramName, T paramValue, DbType? dbType = null) => _command.AddParameter(paramName, paramValue, dbType: dbType);

        private void AddAddress(string name, IPAddress address) => Add(name, address.ToString(), dbType: DbType.AnsiString);

        private void AddGrainHash(string name, uint grainHash) => Add(name, (int)grainHash);

        internal string ClientId
        {
            set => Add(nameof(ClientId), value);
        }

        internal int GatewayPort
        {
            set => Add(nameof(GatewayPort), value);
        }

        internal IPAddress GatewayAddress
        {
            set => AddAddress(nameof(GatewayAddress), value);
        }

        internal string SiloId
        {
            set => Add(nameof(SiloId), value);
        }

        internal string Id
        {
            set => Add(nameof(Id), value);
        }

        internal string Name
        {
            set => Add(nameof(Name), value);
        }

        internal const string IsValueDelta = nameof(IsValueDelta);
        internal const string StatValue = nameof(StatValue);
        internal const string Statistic = nameof(Statistic);

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
            set => Add(nameof(Generation), value);
        }

        internal int Port
        {
            set => Add(nameof(Port), value);
        }

        internal uint BeginHash
        {
            set => AddGrainHash(nameof(BeginHash), value);
        }

        internal uint EndHash
        {
            set => AddGrainHash(nameof(EndHash), value);
        }

        internal uint GrainHash
        {
            set => AddGrainHash(nameof(GrainHash), value);
        }

        internal DateTime StartTime
        {
            set => Add(nameof(StartTime), value);
        }

        internal IPAddress Address
        {
            set => AddAddress(nameof(Address), value);
        }

        internal string ServiceId
        {
            set => Add(nameof(ServiceId), value);
        }

        internal string DeploymentId
        {
            set => Add(nameof(DeploymentId), value);
        }

        internal string SiloName
        {
            set => Add(nameof(SiloName), value);
        }

        internal string HostName
        {
            set => Add(nameof(HostName), value);
        }

        internal string Version
        {
            set => Add(nameof(Version), int.Parse(value));
        }

        internal DateTime IAmAliveTime
        {
            set => Add(nameof(IAmAliveTime), value);
        }

        internal string GrainId
        {
            set => Add(nameof(GrainId), value, dbType: DbType.AnsiString);
        }

        internal string ReminderName
        {
            set => Add(nameof(ReminderName), value);
        }

        internal TimeSpan Period
        {
            set
            {
                if (value.TotalMilliseconds <= int.MaxValue)
                {
                    // Original casting when old schema is used.  Here to maintain backwards compatibility
                    Add(nameof(Period), (int)value.TotalMilliseconds);
                }
                else
                {
                    Add(nameof(Period), (long)value.TotalMilliseconds);
                }
            }
        }

        internal SiloStatus Status
        {
            set => Add(nameof(Status), (int)value);
        }

        internal int ProxyPort
        {
            set => Add(nameof(ProxyPort), value);
        }

        internal List<Tuple<SiloAddress, DateTime>> SuspectTimes
        {
            set => Add(nameof(SuspectTimes), value == null
                ? null
                : Join("|", value.Select(s => $"{s.Item1.ToParsableString()},{LogFormatter.PrintDate(s.Item2)}")));
        }

        internal string QueueId
        {
            set => Add(nameof(QueueId), value);
        }

        internal long MessageId
        {
            set => Add(nameof(MessageId), value);
        }

        internal byte[] Payload
        {
            set => Add(nameof(Payload), value);
        }

        internal int ExpiryTimeout
        {
            set => Add(nameof(ExpiryTimeout), value);
        }

        internal int MaxCount
        {
            set => Add(nameof(MaxCount), value);
        }

        internal int MaxAttempts
        {
            set => Add(nameof(MaxAttempts), value);
        }

        internal int RemovalTimeout
        {
            set => Add(nameof(RemovalTimeout), value);
        }

        internal int VisibilityTimeout
        {
            set => Add(nameof(VisibilityTimeout), value);
        }

        internal int EvictionInterval
        {
            set => Add(nameof(EvictionInterval), value);
        }

        internal int EvictionBatchSize
        {
            set => Add(nameof(EvictionBatchSize), value);
        }

        internal string EventIds
        {
            set => Add(nameof(EventIds), value);
        }

        internal string ProviderId
        {
            set => Add(nameof(ProviderId), value);
        }

        internal string Items
        {
            set => Add(nameof(Items), value);
        }
    }
}
