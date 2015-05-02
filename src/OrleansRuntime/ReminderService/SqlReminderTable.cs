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
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.ReminderService
{
    internal class SqlReminderTable : IReminderTable
    {
        private readonly string serviceId;
        private readonly string connectionString;

        public SqlReminderTable(GlobalConfiguration config)
        {
            serviceId = config.ServiceId.ToString();
            connectionString = config.DataConnectionString;
        }

        public Task Init()
        {
            return TaskDone.Done;
        }

        public async Task<ReminderTableData> ReadRows(GrainReference grainRef)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var command = new SqlCommand(READ_GRAIN_ROWS);
                command.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = serviceId });
                command.Parameters.Add(new SqlParameter { ParameterName = "@grainid", DbType = DbType.String, Value = grainRef.ToKeyString() });
                command.Connection = conn;

                return await ProcessResults(command);
            }
        }

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var command = new SqlCommand((begin < end) ? READ_RANGE_ROWS_1 : READ_RANGE_ROWS_2);
                command.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = serviceId });
                command.Parameters.Add(new SqlParameter { ParameterName = "@beginhash", DbType = DbType.Int32, Value = (int)begin });
                command.Parameters.Add(new SqlParameter { ParameterName = "@endhash", DbType = DbType.Int32, Value = (int)end });
                command.Connection = conn;

                return await ProcessResults(command);
            }
        }

        public async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                return await ReadRowInternal(grainRef, reminderName, conn, null);
            }
        }

        private async Task<ReminderEntry> ReadRowInternal(GrainReference grainRef, string reminderName, SqlConnection conn, SqlTransaction tx)
        {
            var command = new SqlCommand(READ_SINGLE_ROW);
            command.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = serviceId });
            command.Parameters.Add(new SqlParameter { ParameterName = "@grainid", DbType = DbType.String, Value = grainRef.ToKeyString() });
            command.Parameters.Add(new SqlParameter { ParameterName = "@name", DbType = DbType.String, Value = reminderName });
            command.Connection = conn;
            command.Transaction = tx;

            using (var results = await command.ExecuteReaderAsync())
            {
                while (await results.ReadAsync())
                    return ConvertFromRow(results);
            }

            return null;
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    ReminderEntry result = await ReadRowInternal(entry.GrainRef, entry.ReminderName, conn, tx);
                
                    var doUpdate = result != null;
                    var command = new SqlCommand(doUpdate ? UPDATE_ROW : INSERT_ROW);
                    if (doUpdate)
                    {
                        string eTagForWhere = entry.ETag ?? result.ETag;
                        command.Parameters.Add(new SqlParameter { ParameterName = "@etag", DbType = DbType.String, Value = eTagForWhere });
                    }
                    string newEtag = ConvertToRow(entry, command, serviceId);                    
                    command.Connection = conn;
                    command.Transaction = tx;
                    await command.ExecuteNonQueryAsync();

                    tx.Commit();
                    return newEtag;
                }
            }
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var command = new SqlCommand(DELETE_ROW);
                command.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = serviceId });
                command.Parameters.Add(new SqlParameter { ParameterName = "@grainid", DbType = DbType.String, Value = grainRef.ToKeyString() });
                command.Parameters.Add(new SqlParameter { ParameterName = "@name", DbType = DbType.String, Value = reminderName });
                command.Parameters.Add(new SqlParameter { ParameterName = "@etag", DbType = DbType.String, Value = eTag });
                command.Connection = conn;

                var count = await command.ExecuteNonQueryAsync();
                return count > 0;
            }
        }

        public async Task TestOnlyClearTable()
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var command = new SqlCommand(DELETE_ALL);
                command.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = serviceId });
                command.Connection = conn;

                await command.ExecuteNonQueryAsync();
            }
        }

        private static async Task<ReminderTableData> ProcessResults(SqlCommand command)
        {
            var entries = new List<ReminderEntry>();

            using (var results = await command.ExecuteReaderAsync())
            {
                while (await results.ReadAsync())
                {
                    entries.Add(ConvertFromRow(results));
                }
            }

            return new ReminderTableData(entries);
        }

        private static ReminderEntry ConvertFromRow(IDataRecord results)
        {
            return new ReminderEntry
            {
                GrainRef = GrainReference.FromKeyString(results.GetString(GRAIN_ID_IDX)),
                ReminderName = results.GetString(REMINDER_NAME_IDX),
                StartAt = results.GetDateTime(START_TIME_IDX),
                Period = TimeSpan.FromMilliseconds(results.GetInt32(PERIOD_IDX)),
                ETag = results.GetString(E_TAG_IDX)
            };
        }

        private static string ConvertToRow(ReminderEntry entry, SqlCommand command, string serviceId)
        {
            var newETag = Guid.NewGuid().ToString();
            command.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = serviceId });
            command.Parameters.Add(new SqlParameter { ParameterName = "@grainid", DbType = DbType.String, Value = entry.GrainRef.ToKeyString() });
            command.Parameters.Add(new SqlParameter { ParameterName = "@name", DbType = DbType.String, Value = entry.ReminderName });
            command.Parameters.Add(new SqlParameter { ParameterName = "@starttime", DbType = DbType.DateTime, Value = entry.StartAt });
            command.Parameters.Add(new SqlParameter { ParameterName = "@period", DbType = DbType.Int32, Value = entry.Period.TotalMilliseconds });
            command.Parameters.Add(new SqlParameter { ParameterName = "@hash", DbType = DbType.Int32, Value = (int)entry.GrainRef.GetUniformHashCode() });
            command.Parameters.Add(new SqlParameter { ParameterName = "@newetag", DbType = DbType.String, Value = newETag });
            return newETag;
        }


        // Column offsets for result rows. Must correspond precisely to the order in which columns appear in the SELECT queries below.
        
        private const int GRAIN_ID_IDX = 0;
        private const int REMINDER_NAME_IDX = 1;
        private const int START_TIME_IDX = 2;
        private const int PERIOD_IDX = 3;
        private const int E_TAG_IDX = 4;

        private const string READ_GRAIN_ROWS =
            "SELECT GrainId,ReminderName,StartTime,Period,ETag FROM [OrleansRemindersTable] WHERE [ServiceId] = @id AND [GrainId] = @grainid";

        private const string READ_RANGE_ROWS_1 =
            "SELECT GrainId,ReminderName,StartTime,Period,ETag FROM [OrleansRemindersTable] " + 
            "WHERE [ServiceId] = @id AND [GrainIdConsistentHash] > @beginhash AND [GrainIdConsistentHash] <= @endhash";

        private const string READ_RANGE_ROWS_2 =
            "SELECT GrainId,ReminderName,StartTime,Period,ETag FROM [OrleansRemindersTable] " + 
            "WHERE [ServiceId] = @id AND ([GrainIdConsistentHash] > @beginhash OR [GrainIdConsistentHash] <= @endhash)";

        private const string READ_SINGLE_ROW =
            "SELECT GrainId,ReminderName,StartTime,Period,ETag FROM [OrleansRemindersTable] WHERE [ServiceId] = @id AND [GrainId] = @grainid AND [ReminderName] = @name";

        private const string INSERT_ROW =
            "INSERT INTO [OrleansRemindersTable] " +
            "(ServiceId,GrainId,ReminderName,StartTime,Period,GrainIdConsistentHash,ETag) " +
            "VALUES (@id,@grainid,@name,@starttime,@period,@hash,@newetag)";

        private const string UPDATE_ROW =
            "UPDATE [OrleansRemindersTable] " +
            "SET StartTime = @starttime, Period = @period, GrainIdConsistentHash = @hash, ETag = @newetag " +
            "WHERE [ServiceId] = @id AND [GrainId] = @grainid AND [ReminderName] = @name AND ETag = @etag";

        private const string DELETE_ALL =
            "DELETE FROM [OrleansRemindersTable]   WHERE [ServiceId] = @id";

        private const string DELETE_ROW =
            "DELETE FROM [OrleansRemindersTable]   WHERE [ServiceId] = @id AND [GrainId] = @grainid AND [ReminderName] = @name AND ETag = @etag";
    }
}
