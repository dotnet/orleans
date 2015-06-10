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
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using org.apache.zookeeper;
using org.apache.zookeeper.data;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Host
{
    public class ZooKeeperBasedMembershipTable : IMembershipTable, IGatewayListProvider
    {
        private TraceLogger logger;

        private const int ZOOKEEPER_CONNECTION_TIMEOUT = 2000;

        private ZooKeeperWatcher watcher;

        private string deploymentConnectionString;
        private string deploymentPath;
        private string rootConnectionString;
        private TimeSpan maxStaleness;

        public Task InitializeGatewayListProvider(ClientConfiguration config,TraceLogger traceLogger)
        {
            InitConfig(traceLogger,config.DataConnectionString, config.DeploymentId, config.GatewayListRefreshPeriod);
            return TaskDone.Done;
        }

        private void InitConfig(TraceLogger traceLogger, string dataConnectionString, string deploymentId, TimeSpan maxStale)
        {
            watcher = new ZooKeeperWatcher(traceLogger);
            logger = traceLogger;
            deploymentPath = "/" + deploymentId;
            deploymentConnectionString = dataConnectionString + deploymentPath;
            rootConnectionString = dataConnectionString;
            maxStaleness = maxStale;
        }

        public Task<MembershipTableData> ReadRow(SiloAddress siloAddress)
        {
            return UsingZookeeper(async zk =>
            {
                var getRowTask = GetRow(zk, siloAddress);
                var getTableNodeTask = zk.getDataAsync("/", false);

                List<Tuple<MembershipEntry, string>> rows = new List<Tuple<MembershipEntry, string>>(1);
                try
                {
                    await Task.WhenAll(getRowTask, getTableNodeTask);
                    rows.Add(await getRowTask);
                }
                catch (KeeperException.NoNodeException)
                {
                    //that's ok because orleans expects an empty list in case of a missing row
                }

                var tableVersion = ConvertToTableVersion((await getTableNodeTask).Stat);
                return new MembershipTableData(rows, tableVersion);
            });
        }

        public Task<MembershipTableData> ReadAll()
        {
            return UsingZookeeper(async zk =>
            {
                var childrenResult = await zk.getChildrenAsync("/", false);

                var childrenTasks =
                    childrenResult.Children.Select(child => GetRow(zk, SiloAddress.FromParsableString(child))).ToList();

                var childrenTaskResults = await Task.WhenAll(childrenTasks);

                var tableVersion = ConvertToTableVersion(childrenResult.Stat);

                return new MembershipTableData(childrenTaskResults.ToList(), tableVersion);
            });
        }

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            string rowPath = ConvertToRowPath(entry.SiloAddress);
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
            byte[] newRowData = Serialize(entry);
            byte[] newRowIAmAliveData = Serialize(entry.IAmAliveTime);

            int expectedTableVersion = tableVersion.Version - 1;

            return TryTransaction(t => t
                .setData("/", null, expectedTableVersion)
                .create(rowPath, newRowData, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT)
                .create(rowIAmAlivePath, newRowIAmAliveData, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT));
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            string rowPath = ConvertToRowPath(entry.SiloAddress);
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
            var newRowData = Serialize(entry);
            var newRowIAmAliveData = Serialize(entry.IAmAliveTime);

            int expectedTableVersion = tableVersion.Version - 1;
            int expectedRowVersion = int.Parse(etag);

            return TryTransaction(t => t
                .setData("/", null, expectedTableVersion)
                .setData(rowPath, newRowData, expectedRowVersion)
                .setData(rowIAmAlivePath, newRowIAmAliveData, -1));
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
            byte[] newRowIAmAliveData = Serialize(entry.IAmAliveTime);
            return UsingZookeeper(zk => zk.setDataAsync(rowIAmAlivePath, newRowIAmAliveData, -1));
        }

        public async Task InitializeMembershipTable(GlobalConfiguration config, bool tryInitPath, TraceLogger traceLogger)
        {
            InitConfig(traceLogger, config.DataConnectionString, config.DeploymentId, config.TableRefreshTimeout);
            // even if I am not the one who created the path, 
            // try to insert an initial path if it is not already there,
            // so we always have the path, before this silo starts working.
            await UsingZookeeper(rootConnectionString, async zk =>
            {
                try
                {
                    await zk.createAsync(deploymentPath, null, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT);
                    await zk.sync(deploymentPath);
                    logger.Info("Created new deployment path.");
                }
                catch (KeeperException.NodeExistsException)
                {
                    logger.Verbose("Deployment path already exists.");
                }
            });
        }

        public IList<Uri> GetGateways()
        {
            return ReadAll().Result.Members.Select(entryWithTag => entryWithTag.Item1.SiloAddress.Endpoint.ToGatewayUri()).ToList();
        }

        public TimeSpan MaxStaleness
        {
            get { return maxStaleness; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        public Task DeleteMembershipTableEntries(string deploymentId)
        {
            string pathToDelete = "/" + deploymentId;
            return UsingZookeeper(rootConnectionString, async zk =>
            {
                await ZKUtil.deleteRecursiveAsync(zk, pathToDelete);
                await zk.sync(pathToDelete);
            });
        }

        private async Task<bool> TryTransaction(Func<Transaction, Transaction> transactionFunc)
        {
            try
            {
                await UsingZookeeper(zk => transactionFunc(zk.transaction()).commitAsync());
                return true;
            }
            catch (KeeperException e)
            {
                //these exceptions are thrown when the transaction fails to commit due to semantical reasons
                if (e is KeeperException.NodeExistsException || e is KeeperException.NoNodeException ||
                    e is KeeperException.BadVersionException)
                {
                    return false;
                }
                throw;
            }
        }

        private static async Task<Tuple<MembershipEntry, string>> GetRow(ZooKeeper zk, SiloAddress siloAddress)
        {
            string rowPath = ConvertToRowPath(siloAddress);
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(siloAddress);

            var rowDataTask = zk.getDataAsync(rowPath, false);
            var rowIAmAliveDataTask = zk.getDataAsync(rowIAmAlivePath, false);

            await Task.WhenAll(rowDataTask, rowIAmAliveDataTask);

            MembershipEntry me = Deserialize<MembershipEntry>((await rowDataTask).Data);
            me.IAmAliveTime = Deserialize<DateTime>((await rowIAmAliveDataTask).Data);

            int rowVersion = (await rowDataTask).Stat.getVersion();

            return new Tuple<MembershipEntry, string>(me, rowVersion.ToString());
        }

        private Task<T> UsingZookeeper<T>(Func<ZooKeeper, Task<T>> zkMethod)
        {
            return ZooKeeper.Using(deploymentConnectionString, ZOOKEEPER_CONNECTION_TIMEOUT, watcher, zkMethod);
        }

        private Task UsingZookeeper(string connectString, Func<ZooKeeper, Task> zkMethod)
        {
            return ZooKeeper.Using(connectString, ZOOKEEPER_CONNECTION_TIMEOUT, watcher, zkMethod);
        }

        private static string ConvertToRowPath(SiloAddress siloAddress)
        {
            return "/" + siloAddress.ToParsableString();
        }

        private static string ConvertToRowIAmAlivePath(SiloAddress siloAddress)
        {
            return ConvertToRowPath(siloAddress) + "/IAmAlive";
        }

        private static TableVersion ConvertToTableVersion(Stat stat)
        {
            int version = stat.getVersion();
            return new TableVersion(version, version.ToString());
        }

        private static byte[] Serialize(object obj)
        {
            return
                Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj, Formatting.None,
                    MembershipSerializerSettings.Instance));
        }

        private static T Deserialize<T>(byte[] data)
        {
            return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(data), MembershipSerializerSettings.Instance);
        }

        /// <summary>
        /// the state of every ZooKeeper client and its push notifications are published using watchers.
        /// in orleans the watcher is only for debugging purposes
        /// </summary>
        private class ZooKeeperWatcher : Watcher
        {
            private readonly TraceLogger logger;
            public ZooKeeperWatcher(TraceLogger traceLogger)
            {
                logger = traceLogger;
            }

            public override void process(WatchedEvent @event)
            {
                if (logger.IsVerbose)
                {
                    logger.Verbose(@event.ToString());
                }
            }
        }
    }
}
