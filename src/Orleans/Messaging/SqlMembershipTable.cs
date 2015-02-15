/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Net;
using System.Text;
using System.Data;
using System.Data.SqlClient;
using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    internal class SqlMembershipTable : IMembershipTable, IGatewayListProvider
    {
        private readonly string deploymentId;
        private readonly string connectionString;
        private readonly TimeSpan maxStaleness;

        private SqlMembershipTable(GlobalConfiguration config)
        {
            deploymentId = config.DeploymentId;
            connectionString = config.DataConnectionString;
        }

        public static async Task<SqlMembershipTable> GetMembershipTable(GlobalConfiguration config, bool tryInitTableVersion)
        {
            var table = new SqlMembershipTable(config);

            // even if I am not the one who created the table, 
            // try to insert an initial table version if it is not already there,
            // so we always have a first table version row, before this silo starts working.
            if (tryInitTableVersion)
            {
                await table.InitTable();
            }
            return table;
        }

        public SqlMembershipTable(ClientConfiguration config)
        {
            deploymentId = config.DeploymentId;
            connectionString = config.DataConnectionString;
            maxStaleness = config.GatewayListRefreshPeriod;
        }

        public TimeSpan MaxStaleness
        {
            get { return maxStaleness; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        public IList<Uri> GetGateways()
        {
            var result = new List<Uri>();

            // NOTE: This method is only called from client, so blocking operations should be ok.

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var cmdStr = String.Format(CLIENT_READ_ROW_SELECT, (Int32)SiloStatus.Active);
                var command = new SqlCommand(cmdStr);
                command.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });
                command.Connection = conn;

                // Note: This does blocking read, so must not be called from grain code.
                using (var results = command.ExecuteReader())
                {
                    while (results.Read())
                    {
                        int proxyPort = 0;
                        if (!results.GetSqlInt32(1).IsNull)
                            proxyPort = results.GetInt32(1);
                        result.Add(new IPEndPoint(IPAddress.Parse(results.GetString(0)), proxyPort).ToGatewayUri());
                    }
                }
            }
            return result;
        }

        async Task<MembershipTableData> IMembershipTable.ReadRow(SiloAddress key)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var command = new SqlCommand(READ_ROW_SELECT, conn);
                command.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });
                command.Parameters.Add(new SqlParameter { ParameterName = "@address", DbType = DbType.String, Value = key.Endpoint.Address.ToString() });
                command.Parameters.Add(new SqlParameter { ParameterName = "@port", DbType = DbType.Int32, Value = key.Endpoint.Port });
                command.Parameters.Add(new SqlParameter { ParameterName = "@generation", DbType = DbType.Int32, Value = key.Generation });

                return await ProcessResults(conn, command);
            }
        }

        async Task<MembershipTableData> IMembershipTable.ReadAll()
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var command = new SqlCommand(READ_ALL_SELECT, conn);
                command.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });

                return await ProcessResults(conn, command);
            }
        }

        async Task<bool> IMembershipTable.InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    if (tableVersion == null || await UpdateTableVersion(tableVersion, conn, tx))
                    {
                        var command = new SqlCommand(INSERT_ROW, conn, tx);
                        ConvertToRow(entry, command, this.deploymentId);
                        command.Parameters.Add(new SqlParameter { ParameterName = "@newetag", DbType = DbType.String, Value = NewEtag() });

                        var result = await command.ExecuteNonQueryAsync();

                        tx.Commit();

                        return result > 0;
                    }
                }
                return false;
            }
        }

        async Task<bool> IMembershipTable.UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    if (tableVersion == null || await UpdateTableVersion(tableVersion, conn, tx))
                    {
                        var command = new SqlCommand(UPDATE_ROW, conn, tx);
                        ConvertToRow(entry, command, this.deploymentId);
                        command.Parameters.Add(new SqlParameter { ParameterName = "@etag", DbType = DbType.String, Value = etag });
                        command.Parameters.Add(new SqlParameter { ParameterName = "@newetag", DbType = DbType.String, Value = NewEtag() });

                        var result = await command.ExecuteNonQueryAsync();

                        tx.Commit();

                        return result > 0;
                    }
                }
                return false;
            }
        }

        async Task IMembershipTable.UpdateIAmAlive(MembershipEntry entry)
        {
            // Update, but only the IAmAlive column.
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var command = new SqlCommand(UPDATE_ALIVE_TIME);
                UpdateIAmAliveTime(entry, command, this.deploymentId);
                command.Connection = conn;

                await command.ExecuteNonQueryAsync();
            }
        }

        internal async Task DeleteMembershipTableEntries(string deployId)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    var command = new SqlCommand(DELETE_TABLE_ENTRIES);
                    command.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deployId });
                    command.Connection = conn;
                    command.Transaction = tx;

                    await command.ExecuteNonQueryAsync();

                    command = new SqlCommand(DELETE_VERSION_ROW);
                    command.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deployId });
                    command.Connection = conn;
                    command.Transaction = tx;

                    await command.ExecuteNonQueryAsync();

                    tx.Commit();
                }
            }
        }
        
        internal static string NewEtag()
        {
            return Guid.NewGuid().ToString();
        }

        private async Task InitTable()
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    await CreateTableVersion(new TableVersion(0, NewEtag()), conn, tx);
                    tx.Commit();
                }
            }
        }

        private async Task<MembershipTableData> ProcessResults(SqlConnection conn, SqlCommand command)
        {
            List<Tuple<MembershipEntry, string>> memEntries = new List<Tuple<MembershipEntry, string>>();

            int version = 0;
            string versionETag = null;

            using (SqlDataReader results = await command.ExecuteReaderAsync())
            {
                while (await results.ReadAsync())
                {
                    string eTag;
                    var entry = ConvertFromRow(results, out eTag, out version, out versionETag);
                    memEntries.Add(new Tuple<MembershipEntry, string>(entry, eTag));
                }
            }

            if (versionETag == null)
                return new MembershipTableData(memEntries, await GetTableVersion(conn));
            else
                return new MembershipTableData(memEntries, new TableVersion(version, versionETag));
        }

        private async Task<TableVersion> GetTableVersion(SqlConnection conn)
        {
            var read = new SqlCommand(READ_VERSION, conn);
            read.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });

            using (var results = await read.ExecuteReaderAsync())
            {
                if (await results.ReadAsync())
                    return new TableVersion((int)results.GetInt64(0), results.GetString(1));
            }

            return null;
        }

        private async Task<bool> CreateTableVersion(TableVersion version, SqlConnection conn, SqlTransaction tx)
        {
            var read = new SqlCommand(READ_VERSION, conn, tx);
            read.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });

            using (var results = await read.ExecuteReaderAsync())
            {
                if (results.HasRows)
                    return false;
            }

            var write = new SqlCommand(INSERT_VERSION_ROW, conn, tx);
            write.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });
            write.Parameters.Add(new SqlParameter { ParameterName = "@timestamp", DbType = DbType.DateTime, Value = DateTime.UtcNow });
            write.Parameters.Add(new SqlParameter { ParameterName = "@version", DbType = DbType.Int64, Value = version.Version });
            write.Parameters.Add(new SqlParameter { ParameterName = "@newetag", DbType = DbType.String, Value = version.VersionEtag });

            return (await write.ExecuteNonQueryAsync()) > 0;
        }

        private async Task<bool> UpdateTableVersion(TableVersion version, SqlConnection conn, SqlTransaction tx)
        {
            var read = new SqlCommand(READ_VERSION, conn, tx);
            read.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });

            string query;

            using (var results = await read.ExecuteReaderAsync())
            {
                query = (results.HasRows) ? UPDATE_VERSION_ROW : INSERT_VERSION_ROW;
            }

            var write = new SqlCommand(query, conn, tx);
            write.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });
            write.Parameters.Add(new SqlParameter { ParameterName = "@timestamp", DbType = DbType.DateTime, Value = DateTime.UtcNow });
            write.Parameters.Add(new SqlParameter { ParameterName = "@version", DbType = DbType.Int64, Value = version.Version });
            write.Parameters.Add(new SqlParameter { ParameterName = "@etag", DbType = DbType.String, Value = version.VersionEtag });
            write.Parameters.Add(new SqlParameter { ParameterName = "@newetag", DbType = DbType.String, Value = NewEtag() });

            return (await write.ExecuteNonQueryAsync()) > 0;
        }

        private static MembershipEntry ConvertFromRow(SqlDataReader results, out string eTag, out int tableVersion, out string versionETag)
        {
            var entry = new MembershipEntry();

            int port = results.GetInt32(PortIdx);
            int gen = results.GetInt32(GenerationIdx);
            entry.SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse(results.GetString(AddressIdx)), port), gen);

            entry.HostName = results.GetString(HostNameIdx);
            entry.Status = (SiloStatus)results.GetInt32(StatusIdx);
            if (!results.GetSqlInt32(ProxyPortIdx).IsNull)
                entry.ProxyPort = results.GetInt32(ProxyPortIdx);
            if (!results.GetSqlBoolean(PrimaryIdx).IsNull)
                entry.IsPrimary = results.GetBoolean(PrimaryIdx);

            entry.RoleName = results.GetString(RoleNameIdx);
            entry.InstanceName = results.GetString(InstanceNameIdx);
            if (!results.GetSqlInt32(UpdateZoneIdx).IsNull)
                entry.UpdateZone = results.GetInt32(UpdateZoneIdx);
            if (!results.GetSqlInt32(FaultZoneIdx).IsNull)
                entry.FaultZone = results.GetInt32(FaultZoneIdx);

            if (!results.GetSqlDateTime(StartTimeIdx).IsNull)
                entry.StartTime = results.GetDateTime(StartTimeIdx);
            if (!results.GetSqlDateTime(IAmAliveTimeIdx).IsNull)
                entry.IAmAliveTime = results.GetDateTime(IAmAliveTimeIdx);
            eTag = results.GetString(ETagIdx);
            tableVersion = (int)results.GetInt64(VersionIdx);
            versionETag = results.GetString(VersionETagIdx);

            var suspectingSilosString = results.GetSqlString(SuspectingSilosIdx);
            var suspectingTimesString = results.GetSqlString(SuspectingTimesIdx);

            List<SiloAddress> suspectingSilos = new List<SiloAddress>();
            List<DateTime> suspectingTimes = new List<DateTime>();
            if (!suspectingSilosString.IsNull && !string.IsNullOrEmpty(suspectingSilosString.Value))
            {
                string[] silos = suspectingSilosString.Value.Split('|');
                foreach (string silo in silos)
                {
                    suspectingSilos.Add(SiloAddress.FromParsableString(silo));
                }
            }

            if (!suspectingTimesString.IsNull && !string.IsNullOrEmpty(suspectingTimesString.Value))
            {
                string[] times = suspectingTimesString.Value.Split('|');
                foreach (string time in times)
                {
                    suspectingTimes.Add(TraceLogger.ParseDate(time));
                }
            }

            if (suspectingSilos.Count != suspectingTimes.Count)
                throw new OrleansException(String.Format("SuspectingSilos.Length of {0} as read from SQL table is not eqaul to SuspectingTimes.Length of {1}", suspectingSilos.Count, suspectingTimes.Count));

            for (int i = 0; i < suspectingSilos.Count; i++)
            {
                entry.AddSuspector(suspectingSilos[i], suspectingTimes[i]);
            }
            return entry;
        }

        private static void ConvertToRow(MembershipEntry memEntry, SqlCommand command, string deploymentId)
        {
            command.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });
            command.Parameters.Add(new SqlParameter { ParameterName = "@address", DbType = DbType.String, Value = memEntry.SiloAddress.Endpoint.Address.ToString() });
            command.Parameters.Add(new SqlParameter { ParameterName = "@port", DbType = DbType.Int32, Value = memEntry.SiloAddress.Endpoint.Port });
            command.Parameters.Add(new SqlParameter { ParameterName = "@generation", DbType = DbType.Int32, Value = memEntry.SiloAddress.Generation });
            command.Parameters.Add(new SqlParameter { ParameterName = "@hostname", DbType = DbType.String, Value = memEntry.HostName });
            command.Parameters.Add(new SqlParameter { ParameterName = "@status", DbType = DbType.Int32, Value = memEntry.Status });
            command.Parameters.Add(new SqlParameter { ParameterName = "@proxyport", DbType = DbType.Int32, Value = memEntry.ProxyPort });
            command.Parameters.Add(new SqlParameter { ParameterName = "@primary", DbType = DbType.Boolean, Value = memEntry.IsPrimary });
            command.Parameters.Add(new SqlParameter { ParameterName = "@rolename", DbType = DbType.String, Value = memEntry.RoleName });
            command.Parameters.Add(new SqlParameter { ParameterName = "@instancename", DbType = DbType.String, Value = memEntry.InstanceName });
            command.Parameters.Add(new SqlParameter { ParameterName = "@updatezone", DbType = DbType.Int32, Value = memEntry.UpdateZone });
            command.Parameters.Add(new SqlParameter { ParameterName = "@faultzone", DbType = DbType.Int32, Value = memEntry.FaultZone });
            command.Parameters.Add(new SqlParameter { ParameterName = "@starttime", DbType = DbType.DateTime, Value = memEntry.StartTime });
            command.Parameters.Add(new SqlParameter { ParameterName = "@iamalivetime", DbType = DbType.DateTime, Value = memEntry.IAmAliveTime });

            if (memEntry.SuspectTimes != null)
            {
                StringBuilder siloList = new StringBuilder();
                StringBuilder timeList = new StringBuilder();
                bool first = true;
                foreach (var tuple in memEntry.SuspectTimes)
                {
                    if (!first)
                    {
                        siloList.Append('|');
                        timeList.Append('|');
                    }
                    siloList.Append(tuple.Item1.ToParsableString());
                    timeList.Append(TraceLogger.PrintDate(tuple.Item2));
                    first = false;
                }

                command.Parameters.Add(new SqlParameter { ParameterName = "@suspectingsilos", DbType = DbType.String, Value = siloList.ToString() });
                command.Parameters.Add(new SqlParameter { ParameterName = "@suspectingtimes", DbType = DbType.String, Value = timeList.ToString() });
            }
            else
            {
                command.Parameters.Add(new SqlParameter { ParameterName = "@suspectingsilos", DbType = DbType.String, Value = DBNull.Value });
                command.Parameters.Add(new SqlParameter { ParameterName = "@suspectingtimes", DbType = DbType.String, Value = DBNull.Value });
            }
        }

        private static void UpdateIAmAliveTime(MembershipEntry memEntry, SqlCommand command, string deploymentId)
        {
            command.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });
            command.Parameters.Add(new SqlParameter { ParameterName = "@address", DbType = DbType.String, Value = memEntry.SiloAddress.Endpoint.Address.ToString() });
            command.Parameters.Add(new SqlParameter { ParameterName = "@port", DbType = DbType.Int32, Value = memEntry.SiloAddress.Endpoint.Port });
            command.Parameters.Add(new SqlParameter { ParameterName = "@generation", DbType = DbType.Int32, Value = memEntry.SiloAddress.Generation });
            command.Parameters.Add(new SqlParameter { ParameterName = "@iamalivetime", DbType = DbType.DateTime, Value = memEntry.IAmAliveTime });
            command.Parameters.Add(new SqlParameter { ParameterName = "@newetag", DbType = DbType.String, Value = NewEtag() });
        }

        private const string CLIENT_READ_ROW_SELECT = @"SELECT Address,ProxyPort FROM [OrleansMembershipTable] WHERE DeploymentId = @deploymentid AND Status = {0}";


        // Column offsets for result rows. Must correspond precisely to the order in which columns appear in the SELECT queries below.
        //
        private const int AddressIdx = 1;
        private const int PortIdx = 2;
        private const int GenerationIdx = 3;
        private const int HostNameIdx = 4;
        private const int StatusIdx = 5;
        private const int ProxyPortIdx = 6;
        private const int PrimaryIdx = 7;
        private const int RoleNameIdx = 8;
        private const int InstanceNameIdx = 8;
        private const int UpdateZoneIdx = 10;
        private const int FaultZoneIdx = 11;
        private const int SuspectingSilosIdx = 12;
        private const int SuspectingTimesIdx = 13;
        private const int StartTimeIdx = 14;
        private const int IAmAliveTimeIdx = 15;
        private const int ETagIdx = 16;
        private const int VersionIdx = 17;
        private const int VersionETagIdx = 18;

        private const string READ_ALL_SELECT = @"SELECT m.DeploymentId,m.[Address],m.Port,m.Generation,m.HostName,m.Status,m.ProxyPort,m.[Primary],m.RoleName,m.InstanceName," +
            "m.UpdateZone,m.FaultZone,m.SuspectingSilos,m.SuspectingTimes,m.StartTime,m.IAmAliveTime,m.ETag,v.[Version],v.ETag as VersionETag " +
            "FROM [dbo].[OrleansMembershipTable] m LEFT JOIN [dbo].[OrleansMembershipVersionTable] v ON m.DeploymentId = v.DeploymentId WHERE m.DeploymentId = @deploymentid";

        private const string READ_ROW_SELECT = @"SELECT m.DeploymentId,m.[Address],m.Port,m.Generation,m.HostName,m.Status,m.ProxyPort,m.[Primary],m.RoleName,m.InstanceName," +
            "m.UpdateZone,m.FaultZone,m.SuspectingSilos,m.SuspectingTimes,m.StartTime,m.IAmAliveTime,m.ETag,v.[Version],v.ETag as VersionETag " +
            "FROM [dbo].[OrleansMembershipTable] m LEFT JOIN [dbo].[OrleansMembershipVersionTable] v ON m.DeploymentId = v.DeploymentId WHERE m.DeploymentId = @deploymentid AND Address = @address AND Port = @port AND Generation = @generation";

        private const string READ_VERSION = @"SELECT Version,ETag FROM [OrleansMembershipVersionTable] WHERE DeploymentId = @deploymentid";

        private const string INSERT_ROW =
            "INSERT INTO [OrleansMembershipTable] " +
            "(DeploymentId,Address,Port,Generation,HostName,Status,ProxyPort,[Primary],RoleName,InstanceName,UpdateZone,FaultZone,SuspectingSilos,SuspectingTimes,StartTime,IAmAliveTime,ETag) " +
            "VALUES (@deploymentid,@address,@port,@generation,@hostname,@status,@proxyport,@primary,@rolename,@instancename,@updatezone,@faultzone,@suspectingsilos,@suspectingtimes,@starttime,@iamalivetime,@newetag)";

        private const string UPDATE_ROW =
            "UPDATE [OrleansMembershipTable] " +
            "SET Address = @address,Port = @port, Generation = @generation, HostName = @hostname, Status = @status, ProxyPort = @proxyport, [Primary] = @primary, RoleName = @rolename, InstanceName = @instancename, " +
            "UpdateZone = @updatezone, FaultZone = @faultzone, SuspectingSilos = @suspectingsilos, SuspectingTimes = @suspectingtimes, StartTime = @starttime, IAmAliveTime = @iamalivetime, ETag = @newetag " +
            "WHERE DeploymentId = @deploymentid AND Address = @address AND Port = @port AND Generation = @generation AND ETag = @etag";

        private const string INSERT_VERSION_ROW =
            "INSERT INTO [OrleansMembershipVersionTable] " +
            "(DeploymentId,TimeStamp,Version,ETag) " +
            "VALUES (@deploymentid,@timestamp,@version,@newetag)";

        private const string UPDATE_VERSION_ROW =
            "UPDATE [OrleansMembershipVersionTable] " +
            "SET TimeStamp = @timestamp,Version = @version,ETag = @newetag " +
            "WHERE DeploymentId = @deploymentid AND ETag = @etag";

        private const string UPDATE_ALIVE_TIME =
            "UPDATE [OrleansMembershipTable] " +
            "SET IAmAliveTime = @iamalivetime, ETag = @newetag " +
            "WHERE DeploymentId = @deploymentid AND Address = @address AND Port = @port AND Generation = @generation";

        private const string DELETE_TABLE_ENTRIES =
            "DELETE FROM [OrleansMembershipTable] " +
            "WHERE DeploymentId = @deploymentid";

        private const string DELETE_VERSION_ROW =
            "DELETE FROM [OrleansMembershipVersionTable] " +
            "WHERE DeploymentId = @deploymentid";
    }
}
