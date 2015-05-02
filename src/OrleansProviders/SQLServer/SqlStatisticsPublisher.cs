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
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Data;
using System.Data.SqlClient;

using Orleans.Runtime;

namespace Orleans.Providers.SqlServer
{
    public class SqlStatisticsPublisher : IConfigurableStatisticsPublisher, IConfigurableSiloMetricsDataPublisher, IConfigurableClientMetricsDataPublisher, IProvider
    {
        private string deploymentId;
        private IPAddress clientAddress;
        private SiloAddress siloAddress;
        private IPEndPoint gateway;
        private string clientId;
        private string siloName;
        private string myHostName;
        private bool isSilo;
        private long generation;
        private long idCounter;
        private string connectionString;

        public string Name { get; private set; }

        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            connectionString = config.Properties["ConnectionString"];
            return TaskDone.Done;
        }

        public void AddConfiguration(string deployment, string hostName, string client, IPAddress address)
        {
            deploymentId = deployment;
            isSilo = false;
            myHostName = hostName;
            clientId = client;
            clientAddress = address;
            generation = SiloAddress.AllocateNewGeneration();
        }

        public void AddConfiguration(string deployment, bool silo, string siloId, SiloAddress address, IPEndPoint gatewayAddress, string hostName)
        {
            deploymentId = deployment;
            isSilo = silo;
            siloName = siloId;
            siloAddress = address;
            gateway = gatewayAddress;
            myHostName = hostName;
            if (!isSilo)
                generation = SiloAddress.AllocateNewGeneration();
        }

        public async Task ReportMetrics(IClientPerformanceMetrics metricsData)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var command1 = new SqlCommand(CLIENT_READ_SINGLE_ROW);
                    command1.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = deploymentId });
                    command1.Parameters.Add(new SqlParameter { ParameterName = "@clientid", DbType = DbType.String, Value = clientId });
                    command1.Connection = conn;
                    command1.Transaction = tx;

                    var result = (Int32)await command1.ExecuteScalarAsync();

                    var command2 = new SqlCommand((result > 0) ? CLIENT_UPDATE_ROW : CLIENT_INSERT_ROW);
                    ConvertToClientMetricsRow(metricsData, command2);
                    command2.Connection = conn;
                    command2.Transaction = tx;

                    await command2.ExecuteNonQueryAsync();
                    tx.Commit();
                }
            }
        }

        public async Task ReportMetrics(ISiloPerformanceMetrics metricsData)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var command1 = new SqlCommand(METRICS_READ_SINGLE_ROW);
                    command1.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = deploymentId });
                    command1.Parameters.Add(new SqlParameter { ParameterName = "@siloid", DbType = DbType.String, Value = siloName });
                    command1.Connection = conn;
                    command1.Transaction = tx;

                    var result = (Int32)await command1.ExecuteScalarAsync();

                    var command2 = new SqlCommand((result > 0) ? METRICS_UPDATE_ROW : METRICS_INSERT_ROW);
                    ConvertToMetricsRow(metricsData, command2);
                    command2.Connection = conn;
                    command2.Transaction = tx;

                    await command2.ExecuteNonQueryAsync();
                    tx.Commit();
                }
            }
        }

        public async Task ReportStats(List<ICounter> statsCounters)
        {
            var bldr = new SqlConnectionStringBuilder(connectionString)
            {
                MultipleActiveResultSets = true,
                Pooling = true,
                AsynchronousProcessing = true
            };

            using (var conn = new SqlConnection(bldr.ConnectionString))
            {
                conn.Open();

                var tasks = new List<Task>();

                foreach (ICounter counter in statsCounters.Where(cs => cs.Storage == CounterStorage.LogAndTable).OrderBy(cs => cs.Name))
                {
                    var command = new SqlCommand(STATS_INSERT_ROW);
                    if (ConvertToStatisticsRow(counter, command) == null)
                        continue;
                    command.Connection = conn;

                    tasks.Add(command.ExecuteNonQueryAsync());
                }

                await Task.WhenAll(tasks);
            }
        }

        private SqlCommand ConvertToStatisticsRow(ICounter counter, SqlCommand command)
        {
            string statValue = counter.IsValueDelta ? counter.GetDeltaString() : counter.GetValueString();
            if ("0".Equals(statValue)) return null; // Skip writing empty records

            var name = (isSilo) ? siloName : clientId;
            var id = (isSilo) ? siloAddress.ToLongString() : string.Format("{0}:{1}", name, generation);
            ++idCounter;

            command.Parameters.Add(new SqlParameter { ParameterName = "@deploymentid", DbType = DbType.String, Value = deploymentId });
            command.Parameters.Add(new SqlParameter { ParameterName = "@date", DbType = DbType.Date, Value = DateTime.UtcNow });
            command.Parameters.Add(new SqlParameter { ParameterName = "@timestamp", DbType = DbType.DateTime, Value = DateTime.UtcNow });
            command.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = id });
            command.Parameters.Add(new SqlParameter { ParameterName = "@counter", DbType = DbType.Int64, Value = idCounter });
            command.Parameters.Add(new SqlParameter { ParameterName = "@hostname", DbType = DbType.String, Value = myHostName });
            command.Parameters.Add(new SqlParameter { ParameterName = "@name", DbType = DbType.String, Value = name });
            command.Parameters.Add(new SqlParameter { ParameterName = "@isdelta", DbType = DbType.Boolean, Value = counter.IsValueDelta });
            command.Parameters.Add(new SqlParameter { ParameterName = "@statvalue", DbType = DbType.String, Value = statValue });
            command.Parameters.Add(new SqlParameter { ParameterName = "@statistic", DbType = DbType.String, Value = counter.Name });

            return command;
        }

        private void ConvertToMetricsRow(ISiloPerformanceMetrics metricsData, SqlCommand command)
        {
            command.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = deploymentId });
            command.Parameters.Add(new SqlParameter { ParameterName = "@siloid", DbType = DbType.String, Value = siloName });
            command.Parameters.Add(new SqlParameter { ParameterName = "@timestamp", DbType = DbType.DateTime, Value = DateTime.UtcNow });
            command.Parameters.Add(new SqlParameter { ParameterName = "@address", DbType = DbType.String, Value = siloAddress.Endpoint.Address.ToString() });
            command.Parameters.Add(new SqlParameter { ParameterName = "@port", DbType = DbType.Int32, Value = siloAddress.Endpoint.Port });
            command.Parameters.Add(new SqlParameter { ParameterName = "@generation", DbType = DbType.Int32, Value = siloAddress.Generation });
            command.Parameters.Add(new SqlParameter { ParameterName = "@hostname", DbType = DbType.String, Value = myHostName });
            if (gateway != null)
            {
                command.Parameters.Add(new SqlParameter { ParameterName = "@gatewayaddress", DbType = DbType.String, Value = gateway.Address.ToString() });
                command.Parameters.Add(new SqlParameter { ParameterName = "@gatewayport", DbType = DbType.Int32, Value = gateway.Port });
            }
            else
            {
                command.Parameters.Add(new SqlParameter { ParameterName = "@gatewayaddress", DbType = DbType.String, Value = null });
                command.Parameters.Add(new SqlParameter { ParameterName = "@gatewayport", DbType = DbType.Int32, Value = 0 });
            }
            command.Parameters.Add(new SqlParameter { ParameterName = "@cpu", DbType = DbType.Double, Value = (double)metricsData.CpuUsage });
            command.Parameters.Add(new SqlParameter { ParameterName = "@memory", DbType = DbType.Int64, Value = metricsData.MemoryUsage });
            command.Parameters.Add(new SqlParameter { ParameterName = "@activations", DbType = DbType.Int32, Value = metricsData.ActivationCount });
            command.Parameters.Add(new SqlParameter { ParameterName = "@recentlyusedactivations", DbType = DbType.Int32, Value = metricsData.RecentlyUsedActivationCount });
            command.Parameters.Add(new SqlParameter { ParameterName = "@sendqueue", DbType = DbType.Int32, Value = metricsData.SendQueueLength });
            command.Parameters.Add(new SqlParameter { ParameterName = "@receivequeue", DbType = DbType.Int32, Value = metricsData.ReceiveQueueLength });
            command.Parameters.Add(new SqlParameter { ParameterName = "@requestqueue", DbType = DbType.Int64, Value = metricsData.RequestQueueLength });
            command.Parameters.Add(new SqlParameter { ParameterName = "@sentmessages", DbType = DbType.Int64, Value = metricsData.SentMessages });
            command.Parameters.Add(new SqlParameter { ParameterName = "@receivedmessages", DbType = DbType.Int64, Value = metricsData.ReceivedMessages });
            command.Parameters.Add(new SqlParameter { ParameterName = "@loadshedding", DbType = DbType.Boolean, Value = metricsData.IsOverloaded });
            command.Parameters.Add(new SqlParameter { ParameterName = "@clientcount", DbType = DbType.Int64, Value = metricsData.ClientCount });
        }

        private void ConvertToClientMetricsRow(IClientPerformanceMetrics metricsData, SqlCommand command)
        {
            command.Parameters.Add(new SqlParameter { ParameterName = "@id", DbType = DbType.String, Value = deploymentId });
            command.Parameters.Add(new SqlParameter { ParameterName = "@clientid", DbType = DbType.String, Value = clientId });
            command.Parameters.Add(new SqlParameter { ParameterName = "@timestamp", DbType = DbType.DateTime, Value = DateTime.UtcNow });
            command.Parameters.Add(new SqlParameter { ParameterName = "@address", DbType = DbType.String, Value = clientAddress.ToString() });
            command.Parameters.Add(new SqlParameter { ParameterName = "@hostname", DbType = DbType.String, Value = myHostName });
            command.Parameters.Add(new SqlParameter { ParameterName = "@cpu", DbType = DbType.Double, Value = (double)metricsData.CpuUsage });
            command.Parameters.Add(new SqlParameter { ParameterName = "@memory", DbType = DbType.Int64, Value = metricsData.MemoryUsage });
            command.Parameters.Add(new SqlParameter { ParameterName = "@sendqueue", DbType = DbType.Int32, Value = metricsData.SendQueueLength });
            command.Parameters.Add(new SqlParameter { ParameterName = "@receivequeue", DbType = DbType.Int32, Value = metricsData.ReceiveQueueLength });
            command.Parameters.Add(new SqlParameter { ParameterName = "@sentmessages", DbType = DbType.Int64, Value = metricsData.SentMessages });
            command.Parameters.Add(new SqlParameter { ParameterName = "@receivedmessages", DbType = DbType.Int64, Value = metricsData.ReceivedMessages });
            command.Parameters.Add(new SqlParameter { ParameterName = "@connectedgatewaycount", DbType = DbType.Int64, Value = metricsData.ConnectedGatewayCount });
        }

        private const string CLIENT_READ_SINGLE_ROW =
            "SELECT COUNT(ClientId) FROM [OrleansClientMetricsTable] WHERE [DeploymentId] = @id AND [ClientId] = @clientid";

        private const string CLIENT_INSERT_ROW =
            "INSERT INTO [OrleansClientMetricsTable] " +
            "(DeploymentId,ClientId,TimeStamp,Address,HostName,CPU,Memory,SendQueue,ReceiveQueue,SentMessages,ReceivedMessages,ConnectedGatewayCount) " +
            "VALUES (@id,@clientid,@timestamp,@address,@hostname,@cpu,@memory,@sendqueue,@receivequeue,@sentmessages,@receivedmessages,@connectedgatewaycount)";

        private const string CLIENT_UPDATE_ROW =
            "UPDATE [OrleansClientMetricsTable] " +
            "SET TimeStamp = @timestamp,Address = @address,HostName = @hostname,CPU = @cpu,Memory = @memory, " +
            "SendQueue = @sendqueue,ReceiveQueue = @receivequeue,SentMessages = @sentmessages,ReceivedMessages = @receivedmessages,ConnectedGatewayCount = @connectedgatewaycount " +
            "WHERE [DeploymentId] = @id AND [ClientId] = @clientid";

        private const string METRICS_READ_SINGLE_ROW =
            "SELECT COUNT(SiloId) FROM [OrleansSiloMetricsTable] WHERE [DeploymentId] = @id AND [SiloId] = @siloid";

        private const string METRICS_INSERT_ROW =
            "INSERT INTO [OrleansSiloMetricsTable] " +
            "(DeploymentId,SiloId,TimeStamp,Address,Port,Generation,HostName,GatewayAddress,GatewayPort,CPU,Memory,Activations,RecentlyUsedActivations,SendQueue,ReceiveQueue,RequestQueue,SentMessages,ReceivedMessages,LoadShedding,ClientCount) " +
            "VALUES (@id,@siloid,@timestamp,@address,@port,@generation,@hostname,@gatewayaddress,@gatewayport,@cpu,@memory,@activations,@recentlyusedactivations,@sendqueue,@receivequeue,@requestqueue,@sentmessages,@receivedmessages,@loadshedding,@clientcount)";

        private const string METRICS_UPDATE_ROW =
            "UPDATE [OrleansSiloMetricsTable] " +
            "SET TimeStamp = @timestamp,Address = @address,Port = @port,Generation = @generation,HostName = @hostname,GatewayAddress = @gatewayaddress,GatewayPort = @gatewayport,CPU = @cpu,Memory = @memory, " +
            "Activations = @activations,RecentlyUsedActivations = @recentlyusedactivations,SendQueue = @sendqueue,ReceiveQueue = @receivequeue,RequestQueue = @requestqueue,SentMessages = @sentmessages,ReceivedMessages = @receivedmessages,LoadShedding = @loadshedding,ClientCount = @clientcount " +
            "WHERE [DeploymentId] = @id AND [SiloId] = @siloid";

        private const string STATS_INSERT_ROW =
            "INSERT INTO [OrleansStatisticsTable] " +
            "(DeploymentId,Date,TimeStamp,Id,Counter,HostName,Name,IsDelta,StatValue,Statistic) " +
            "VALUES (@deploymentid,@date,@timestamp,@id,@counter,@hostname,@name,@isdelta,@statvalue,@statistic)";
    }
}
