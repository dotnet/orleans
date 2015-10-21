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
using System.Globalization;
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
    /// <summary>
    /// A Membership Table implementation using Apache Zookeeper 3.4.6 https://zookeeper.apache.org/doc/r3.4.6/
    /// </summary>
    /// <remarks>
    /// A brief overview of ZK features used: The data is represented by a tree of nodes (similar to a file system). 
    /// Every node is addressed by a path and can hold data as a byte array and has a version. When a node is created, 
    /// its version is 0. Upon updates, the version is atomically incremented. An update can also be conditional on an 
    /// expected current version. A transaction can hold several operations, which succeed or fail atomically.
    /// when creating a zookeeper client, one can set a base path where all operations are relative to.
    /// 
    /// In this implementation:
    /// Every Orleans deployment has a node   /UniqueDeploymentId
    /// Every Silo's state is saved in        /UniqueDeploymentId/IP:Port@Gen
    /// Every Silo's IAmAlive is saved in     /UniqueDeploymentId/IP:Port@Gen/IAmAlive
    /// IAmAlive is saved in a separate node because its updates are unconditional.
    /// 
    /// a node's ZK version is its ETag:
    /// the table version is the version of /UniqueDeploymentId
    /// the silo entry version is the version of /UniqueDeploymentId/IP:Port@Gen
    /// </remarks>
    public class ZooKeeperBasedMembershipTable : IMembershipTable, IGatewayListProvider
    {
        private TraceLogger logger;

        private const int ZOOKEEPER_CONNECTION_TIMEOUT = 2000;

        private ZooKeeperWatcher watcher;

        /// <summary>
        /// The deployment connection string. for eg. "192.168.1.1,192.168.1.2/DeploymentId"
        /// </summary>
        private string deploymentConnectionString;
        /// <summary>
        /// the node name for this deployment. for eg. /DeploymentId
        /// </summary>
        private string deploymentPath;
        /// <summary>
        /// The root connection string. for eg. "192.168.1.1,192.168.1.2"
        /// </summary>
        private string rootConnectionString;

        private TimeSpan maxStaleness;

        public Task InitializeGatewayListProvider(ClientConfiguration config, TraceLogger traceLogger)
        {
            InitConfig(traceLogger,config.DataConnectionString, config.DeploymentId);
            maxStaleness = config.GatewayListRefreshPeriod;
            return TaskDone.Done;
        }

        /// <summary>
        /// Initializes the ZooKeeper based membership table.
        /// </summary>
        /// <param name="config">The configuration for this instance.</param>
        /// <param name="tryInitPath">if set to true, we'll try to create a node named "/DeploymentId"</param>
        /// <param name="traceLogger">The logger to be used by this instance</param>
        /// <returns></returns>
        public async Task InitializeMembershipTable(GlobalConfiguration config, bool tryInitPath, TraceLogger traceLogger)
        {
            InitConfig(traceLogger, config.DataConnectionString, config.DeploymentId);
            // even if I am not the one who created the path, 
            // try to insert an initial path if it is not already there,
            // so we always have the path, before this silo starts working.
            // note that when a zookeeper connection adds /DeploymentId to the connection string, the nodes are relative
            await UsingZookeeper(rootConnectionString, async zk =>
            {
                try
                {
                    await zk.createAsync(deploymentPath, null, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT);
                    await zk.sync(deploymentPath);
                    //if we got here we know that we've just created the deployment path with version=0
                    logger.Info("Created new deployment path: " + deploymentPath);
                }
                catch (KeeperException.NodeExistsException)
                {
                    logger.Verbose("Deployment path already exists: " + deploymentPath);
                }
            });
        }

        private void InitConfig(TraceLogger traceLogger, string dataConnectionString, string deploymentId)
        {
            watcher = new ZooKeeperWatcher(traceLogger);
            logger = traceLogger;
            deploymentPath = "/" + deploymentId;
            deploymentConnectionString = dataConnectionString + deploymentPath;
            rootConnectionString = dataConnectionString;
        }

        public Task<MembershipTableData> ReadRow(SiloAddress siloAddress)
        {
            return UsingZookeeper(async zk =>
            {
                var getRowTask = GetRow(zk, siloAddress);
                var getTableNodeTask = zk.getDataAsync("/");//get the current table version

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
            }, true);
        }

        public Task<MembershipTableData> ReadAll()
        {
            return UsingZookeeper(async zk =>
            {
                var childrenResult = await zk.getChildrenAsync("/");//get all the child nodes (without the data)

                var childrenTasks = //get the data from each child node
                    childrenResult.Children.Select(child => GetRow(zk, SiloAddress.FromParsableString(child))).ToList();

                var childrenTaskResults = await Task.WhenAll(childrenTasks);

                var tableVersion = ConvertToTableVersion(childrenResult.Stat);//this is the current table version

                return new MembershipTableData(childrenTaskResults.ToList(), tableVersion);
            }, true);
        }
       
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            string rowPath = ConvertToRowPath(entry.SiloAddress);
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
            byte[] newRowData = Serialize(entry);
            byte[] newRowIAmAliveData = Serialize(entry.IAmAliveTime);

            int expectedTableVersion = int.Parse(tableVersion.VersionEtag, CultureInfo.InvariantCulture);

            return TryTransaction(t => t
                .setData("/", null, expectedTableVersion)//increments the version of node "/"
                .create(rowPath, newRowData, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT)
                .create(rowIAmAlivePath, newRowIAmAliveData, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT));
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            string rowPath = ConvertToRowPath(entry.SiloAddress);
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
            var newRowData = Serialize(entry);
            var newRowIAmAliveData = Serialize(entry.IAmAliveTime);

            int expectedTableVersion = int.Parse(tableVersion.VersionEtag, CultureInfo.InvariantCulture);
            int expectedRowVersion = int.Parse(etag, CultureInfo.InvariantCulture);

            return TryTransaction(t => t
                .setData("/", null, expectedTableVersion)//increments the version of node "/"
                .setData(rowPath, newRowData, expectedRowVersion)//increments the version of node "/IP:Port@Gen"
                .setData(rowIAmAlivePath, newRowIAmAliveData));
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
            byte[] newRowIAmAliveData = Serialize(entry.IAmAliveTime);
            //update the data for IAmAlive unconditionally
            return UsingZookeeper(zk => zk.setDataAsync(rowIAmAlivePath, newRowIAmAliveData));
        }

        public async Task<IList<Uri>> GetGateways()
        {
            var membershipTableData = await ReadAll();
            return membershipTableData.Members.Select(e => e.Item1).
                                            Where(m => m.Status == SiloStatus.Active && m.ProxyPort != 0).
                                            Select(m =>
                                            {
                                                m.SiloAddress.Endpoint.Port = m.ProxyPort;
                                                return m.SiloAddress.ToGatewayUri();
                                            }).ToList();
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

        /// <summary>
        /// Reads the nodes /IP:Port@Gen and /IP:Port@Gen/IAmAlive (which together is one row)
        /// </summary>
        /// <param name="zk">The zookeeper instance used for the read</param>
        /// <param name="siloAddress">The silo address.</param>
        private static async Task<Tuple<MembershipEntry, string>> GetRow(ZooKeeper zk, SiloAddress siloAddress)
        {
            string rowPath = ConvertToRowPath(siloAddress);
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(siloAddress);

            var rowDataTask = zk.getDataAsync(rowPath);
            var rowIAmAliveDataTask = zk.getDataAsync(rowIAmAlivePath);

            await Task.WhenAll(rowDataTask, rowIAmAliveDataTask);

            MembershipEntry me = Deserialize<MembershipEntry>((await rowDataTask).Data);
            me.IAmAliveTime = Deserialize<DateTime>((await rowIAmAliveDataTask).Data);

            int rowVersion = (await rowDataTask).Stat.getVersion();

            return new Tuple<MembershipEntry, string>(me, rowVersion.ToString(CultureInfo.InvariantCulture));
        }

        private Task<T> UsingZookeeper<T>(Func<ZooKeeper, Task<T>> zkMethod, bool canBeReadOnly = false)
        {
            return ZooKeeper.Using(deploymentConnectionString, ZOOKEEPER_CONNECTION_TIMEOUT, watcher, zkMethod, canBeReadOnly);
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
            return new TableVersion(version, version.ToString(CultureInfo.InvariantCulture));
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

            public override Task process(WatchedEvent @event)
            {
                if (logger.IsVerbose)
                {
                    logger.Verbose(@event.ToString());
                }
                return TaskDone.Done;
            }
        }
    }
}
