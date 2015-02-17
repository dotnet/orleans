using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Storage;

namespace UnitTests.StorageTests.StorageProviders
{
    //
    // <SystemStore Type="UnitTests.StorageTests.StorageProviders.ProtoSqlServerStorage" DbFile="C:\Depot\Orleans\Code\Main\OrleansV4\UnitTests\Data\TestDb.mdf" />
    //
    public class ProtoSqlServerStorage : ISystemStorage
    {
        public string Name { get; set; }

        private string connectionString;
        private string dbFilePath;

        private string tableName;
        private bool useETag;

        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            this.Name = name;

            if (config == null || config.Properties == null)
            {
                throw new ArgumentNullException("config", this.GetType().Name + " requires provider configuration info");
            }
            if (config.Properties.ContainsKey("ConnectionString"))
            {
                this.connectionString = config.Properties["ConnectionString"];
            }
            else if (config.Properties.ContainsKey("DbFile"))
            {
                this.dbFilePath = config.Properties["DbFile"];

                FileInfo dbFile = new FileInfo(dbFilePath);
                string dbFileName = dbFile.FullName;
                Trace.TraceInformation("DbFile={0} Exists={1}", dbFileName, dbFile.Exists);
                if (!dbFile.Exists) throw new FileNotFoundException("Cannot find Db file " + dbFileName, dbFileName);

                connectionString = string.Format(
                    @"Data Source=(LocalDB)\v11.0;"
                    + @"AttachDbFilename={0};"
                    + @"Integrated Security=True;"
                    + @"Connect Timeout=30",
                    dbFileName);
            }
            else
            {
                throw new ArgumentException(string.Format(
                    "No connection info provider for {0} - Either ConnectionString of DbFile is required", this.GetType().FullName));
            }

            if ("Reminders".Equals(name))
            {
                tableName = "OrleansRemindersTable";
                useETag = true;
            }
            else if ("Membership".Equals(name))
            {
                tableName = "OrleansMembershipTable";
                useETag = true;
            }
            else if ("ClientMetrics".Equals(name))
            {
                tableName = "OrleansClientMetricsTable";
            }
            else if ("SiloMetrics".Equals(name))
            {
                tableName = "OrleansSiloMetricsTable";
            }
            else if ("ClientStats".Equals(name))
            {
                tableName = "OrleansStatisticsTable";
            }
            else if ("SiloStats".Equals(name))
            {
                tableName = "OrleansStatisticsTable";
            }
            //else if ("GrainState".Equals(name))
            //{
            //    tableName = "OrleansGrainState";
            //}
            else
            {
                throw new ArgumentException("Unrecognized data type name: " + name, "name");
            }

            return Task.FromResult(0);
        }


        public async Task<IDictionary<string, object>> ReadAsync(IList<Tuple<string, string>> keys)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // Should only be one row
                IDictionary<string, object> data = null;
                await ReadRowsInternal(null, keys, conn, null, dataReader =>
                {
                    data = GetOutputValues(dataReader);
                });

                if (data == null || data.Count <= 0)
                {
                    string error = string.Format("No row in {0} table for keys= {1}",
                        tableName, String.Join(",", keys.Select(kv => kv.Item1 + "=" + kv.Item2)));
                    Trace.TraceInformation(error);
                    //throw new KeyNotFoundException(error);
                }
                return data;
            }
        }

        public async Task<string> WriteAsync(
            IList<Tuple<string, string>> keys,
            IDictionary<string, object> data,
            string eTag)
        {
            const string operation = "Write";

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                SqlTransaction tx = conn.BeginTransaction();

                string sql = await SqlStatementUpdateOneRow(keys, data, eTag, conn, tx);
                Trace.TraceInformation("{0} SQL = {1}", operation, sql);

                SqlCommand command = new SqlCommand(sql);
                command.Connection = conn;
                command.Transaction = tx;

                // Assemble input parameters
                AddInputParams(keys, command);
                AddInputParams(data, command);

                await command.ExecuteNonQueryAsync();

                tx.Commit();

                return eTag;
            }
        }

        public Task<string> WriteBatchAsync(IList<WriteOneRecord> batchWrites)
        {
            const string operation = "WriteBatchAsync";
            throw new NotImplementedException(operation);
        }


        public async Task<bool> DeleteAsync(IList<Tuple<string, string>> keys, string eTag)
        {
            const string operation = "Delete";

            if (eTag == null || !useETag)
            {
                //keys.Remove("ETag");
            }
            else
            {
                keys.Add(Tuple.Create("ETag", eTag)); // Must also match
            }
            // Construct SQL statement
            String sql = String.Format(@"DELETE FROM [{0}] WHERE {1};", tableName, GetWhereClause(keys));
            Trace.TraceInformation("{0} SQL = {1}", operation, sql);
            var command = new SqlCommand(sql);
            // Assemble input parameters
            AddInputParams(keys, command);

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                command.Connection = conn;

                var count = await command.ExecuteNonQueryAsync();

                return count > 0;
            }
        }

        public async Task<IList<IDictionary<string, object>>> ReadMultiRowsAsync(IList<Tuple<string, string>> keys)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                var tx = conn.BeginTransaction();

                var resultData = new List<IDictionary<string, object>>();

                await ReadRowsInternal(null, keys, conn, tx, dataReader =>
                {
                    var data = GetOutputValues(dataReader);
                    resultData.Add(data);
                });

                tx.Commit();

                return resultData;
            }
        }

        public async Task<IList<IDictionary<string, object>>> ReadRingRangeAsync<T>(
            IList<Tuple<string, string>> keys, string rangeParamName, T fromValue, T toValue
        ) where T : IComparable
        {
            const string operation = "ReadRingRangeAsync";

            string where = GetWhereClause(keys);

            bool insideRange = Comparer.DefaultInvariant.Compare(fromValue, toValue) < 0;
            string compareClause;
            if (insideRange)
            {
                compareClause = string.Format("@fromvalue <= [{0}] AND [{0}] <= @tovalue", rangeParamName);
            }
            else
            {
                compareClause = string.Format("[{0}] <= @fromvalue OR [{0}] => @tovalue", rangeParamName);
            }
            if (string.IsNullOrEmpty(where))
            {
                where = compareClause;
            }
            else
            {
                where = string.Format("{0} AND ( {1} )", where, compareClause);
            }
            keys.Add(Tuple.Create("fromvalue", fromValue.ToString()));
            keys.Add(Tuple.Create("tovalue", toValue.ToString()));
            Trace.TraceInformation("{0} From={1} To={2} SQL={3}",
                operation, fromValue, toValue, where);

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                var tx = conn.BeginTransaction();

                var resultData = new List<IDictionary<string, object>>();

                await ReadRowsInternal(where, keys, conn, tx, dataReader =>
                {
                    var data = GetOutputValues(dataReader);
                    resultData.Add(data);
                });

                tx.Commit();

                return resultData;
            }
        }

        // -------------------------------------- Util methods ---------------------------------------------------------------------------

        private async Task<string> SqlStatementUpdateOneRow(
            IList<Tuple<string, string>> keys,
            IDictionary<string, object> data,
            string eTag,
            SqlConnection conn,
            SqlTransaction tx)
        {
            // Should only be one row
            IDictionary<string, object> result = null;
            await ReadRowsInternal(null, keys, conn, tx, dataReader =>
            {
                result = GetOutputValues(dataReader);
            });

            bool doUpdate = result != null && result.Count > 0;

            // Construct SQL statement
            String sql;
            if (doUpdate)
            {
                if (eTag == null || !useETag)
                {
                    //keys.Remove("ETag");
                }
                else
                {
                    keys.Add(Tuple.Create("ETag", eTag)); // Must also match
                }
                sql = String.Format(@"UPDATE [{0}] SET {1} WHERE {2};",
                    tableName,
                    GetSqlSetClause(data),
                    GetWhereClause(keys));
            }
            else
            {
                foreach (var k in keys)
                {
                    data[k.Item1] = k.Item2;
                }
                if (useETag)
                {
                    // Set new Etag for this record
                    eTag = DateTime.UtcNow.ToString(CultureInfo.InvariantCulture); // TODO: How to return?
                    data["ETag"] = eTag; // Write new ETag value
                }

                sql = String.Format(@"INSERT INTO [{0}] ({1}) VALUES ({2});",
                    tableName, GetKeyNames(data), GetParamNames(data));
            }
            return sql;
        }

        private async Task ReadRowsInternal(
            string whereClause,
            IList<Tuple<string, string>> keys,
            SqlConnection conn,
            SqlTransaction tx,
            Action<SqlDataReader> action)
        {
            const string operation = "ReadRows";

            // Construct SQL statement
            string where = whereClause ?? GetWhereClause(keys);
            String sql = String.Format(@"SELECT * FROM [{0}] WHERE {1};",
                tableName, where);
            Trace.TraceInformation("{0} SQL = {1}", operation, sql);
            var command = new SqlCommand(sql);
            command.Connection = conn;
            command.Transaction = tx;

            // Assemble input parameters
            AddInputParams(keys, command);

            // Collect output values
            using (var results = await command.ExecuteReaderAsync())
            {
                while (await results.ReadAsync())
                {

                    if (results.HasRows)
                    {
                        action(results);
                    }
                }
            }
        }

        private static string GetWhereClause(IEnumerable<Tuple<string, string>> keys)
        {
            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (var kv in keys)
            {
                if (first) first = false;
                else sb.Append(" AND ");

                sb.AppendFormat("[{0}] = @{1}", kv.Item1, kv.Item1.ToLowerInvariant());
            }
            return sb.ToString();
        }

        private static string GetSqlSetClause(IDictionary<string, object> data)
        {
            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (var kv in data)
            {
                if (first) first = false;
                else sb.Append(", ");

                sb.AppendFormat("{0} = @{1}", kv.Key, kv.Key.ToLowerInvariant());
            }
            return sb.ToString();
        }

        private static string GetKeyNames(IDictionary<string, object> data)
        {
            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (var kv in data)
            {
                if (first) first = false;
                else sb.Append(",");

                sb.Append(kv.Key);
            }
            return sb.ToString();
        }
        private static string GetParamNames(IDictionary<string, object> data)
        {
            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (var kv in data)
            {
                if (first) first = false;
                else sb.Append(",");

                sb.AppendFormat("@{0}", kv.Key.ToLowerInvariant());
            }
            return sb.ToString();
        }


        private static void AddInputParams(IEnumerable<Tuple<string, string>> keys, SqlCommand command)
        {
            foreach (var kv in keys)
            {
                var param = new SqlParameter
                {
                    ParameterName = "@" + kv.Item1.ToLowerInvariant(),
                    DbType = DbType.String, // Keys are always strings
                    Value = kv.Item2
                };

                if (!command.Parameters.Contains(param.ParameterName))
                {
                    command.Parameters.Add(param);
                }
            }
        }
        private static void AddInputParams(IDictionary<string, object> data, SqlCommand command)
        {
            foreach (var kv in data)
            {
                SqlParameter param = new SqlParameter
                {
                    ParameterName = "@" + kv.Key.ToLowerInvariant()
                };

                if (kv.Value == null)
                {
                    param.Value = null;
                }
                else
                {
                    param.DbType = GetDbType(kv.Value);
                    param.Value = GetDbTypeValue(kv.Value);
                }

                if (!command.Parameters.Contains(param.ParameterName))
                {
                    command.Parameters.Add(param);
                }
            }
        }

        private static DbType GetDbType(object obj)
        {
            if (obj == null) throw new ArgumentNullException("obj", "Null value passed to GetDbType");

            Type t = obj.GetType();
            if (typeof(TimeSpan).Equals(t)) return DbType.Int32;
            if (typeof(DateTime).Equals(t)) return DbType.DateTime;
            if (typeof(Int32).Equals(t)) return DbType.Int32;
            if (typeof(Int64).Equals(t)) return DbType.Int64;
            if (typeof(Double).Equals(t)) return DbType.Double;
            if (typeof(float).Equals(t)) return DbType.Double;
            if (typeof(Guid).Equals(t)) return DbType.Guid;
            if (typeof(string).Equals(t)) return DbType.String;
            if (typeof(Boolean).Equals(t)) return DbType.Boolean;

            throw new ArgumentException(string.Format("Did not map type {0} to DbType successfully", t));
        }
        private static object GetDbTypeValue(object obj)
        {
            Type t = obj.GetType();
            // TimeSpan not supported in SQL Server directly - Need to map to Int32 time offset
            if (typeof(TimeSpan).Equals(t)) return (Int32)((TimeSpan)obj).TotalMilliseconds;
            else
            {
                // for all others, just return original object
                return obj;
            }
        }

        private static IDictionary<string, object> GetOutputValues(SqlDataReader results)
        {
            var data = new Dictionary<string, object>();
            for (int i = 0; i < results.FieldCount; i++)
            {
                string name = results.GetName(i);
                bool isNull = results.IsDBNull(i);
                if (!isNull)
                {
                    object val = results.GetValue(i);
                    data[name] = val;
                }
            }
            return data;
        }
    }
}
