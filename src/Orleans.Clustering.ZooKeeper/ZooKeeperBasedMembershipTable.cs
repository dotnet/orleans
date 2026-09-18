using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using org.apache.zookeeper;
using org.apache.zookeeper.data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Runtime.Membership
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
    /// IAmAlive is saved in a separate node so owner heartbeat writes preserve the membership row's version.
    /// 
    /// a node's ZK version is its ETag:
    /// the table version is the version of /UniqueDeploymentId
    /// the silo entry version is the version of /UniqueDeploymentId/IP:Port@Gen
    /// </remarks>
    public partial class ZooKeeperBasedMembershipTable : IMembershipTable
    {
        private readonly ILogger logger;

        private const int ZOOKEEPER_SESSION_TIMEOUT = 10_000;

        private readonly ZooKeeperWatcher watcher;

        /// <summary>
        /// The deployment connection string. for eg. "192.168.1.1,192.168.1.2/ClusterId"
        /// </summary>
        private readonly string deploymentConnectionString;

        /// <summary>
        /// the node name for this deployment. for eg. /ClusterId
        /// </summary>
        private readonly string clusterPath;

        /// <summary>
        /// The root connection string. for eg. "192.168.1.1,192.168.1.2"
        /// </summary>
        private readonly string rootConnectionString;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZooKeeperBasedMembershipTable"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="membershipTableOptions">The ZooKeeper clustering options.</param>
        /// <param name="clusterOptions">The cluster identity options.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="logger"/>, <paramref name="membershipTableOptions"/>, or <paramref name="clusterOptions"/> is
        /// <see langword="null"/>.
        /// </exception>
        public ZooKeeperBasedMembershipTable(
            ILogger<ZooKeeperBasedMembershipTable> logger,
            IOptions<ZooKeeperClusteringSiloOptions> membershipTableOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(membershipTableOptions);
            ArgumentNullException.ThrowIfNull(clusterOptions);

            this.logger = logger;
            var options = membershipTableOptions.Value;
            watcher = new ZooKeeperWatcher(logger);
            this.clusterPath = "/" + clusterOptions.Value.ClusterId;
            rootConnectionString = options.ConnectionString;
            deploymentConnectionString = options.ConnectionString + this.clusterPath;
        }

        /// <summary>
        /// Initializes the ZooKeeper based membership table.
        /// </summary>
        /// <param name="tryInitPath">if set to true, we'll try to create a node named "/ClusterId"</param>
        /// <returns></returns>
        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitPath) => InitializeMembershipTableAsync(tryInitPath, CancellationToken.None);

        /// <inheritdoc />
        public async Task InitializeMembershipTableAsync(bool tryInitPath, CancellationToken cancellationToken = default)
        {
            // even if I am not the one who created the path, 
            // try to insert an initial path if it is not already there,
            // so we always have the path, before this silo starts working.
            // note that when a zookeeper connection adds /ClusterId to the connection string, the nodes are relative
            await UsingZookeeper(rootConnectionString, async zk =>
            {
                try
                {
                    await zk.createAsync(this.clusterPath, null, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT);
                    cancellationToken.ThrowIfCancellationRequested();
                    await zk.sync(this.clusterPath);
                    //if we got here we know that we've just created the deployment path with version=0
                    LogInformationCreatedNewDeploymentPath(this.clusterPath);
                }
                catch (KeeperException.NodeExistsException)
                {
                    LogDebugDeploymentPathAlreadyExists(this.clusterPath);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Atomically reads the Membership Table information about a given silo.
        /// The returned MembershipTableData includes one MembershipEntry entry for a given silo and the 
        /// TableVersion for this table. The MembershipEntry and the TableVersion have to be read atomically.
        /// </summary>
        /// <param name="siloAddress">The address of the silo whose membership information needs to be read.</param>
        /// <returns>The membership information for a given silo: MembershipTableData consisting one MembershipEntry entry and
        /// TableVersion, read atomically.</returns>
        [Obsolete("Use ReadRowAsync instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress siloAddress) => ReadRowAsync(siloAddress, CancellationToken.None);

        /// <inheritdoc />
        public Task<MembershipTableData> ReadRowAsync(SiloAddress siloAddress, CancellationToken cancellationToken = default)
        {
            return UsingZookeeper(zk => ReadCoreAsync(zk, siloAddress, cancellationToken),
                this.deploymentConnectionString, this.watcher, cancellationToken, canBeReadOnly: true);
        }

        /// <summary>
        /// Atomically reads the full content of the Membership Table.
        /// The returned MembershipTableData includes all MembershipEntry entry for all silos in the table and the 
        /// TableVersion for this table. The MembershipEntries and the TableVersion have to be read atomically.
        /// </summary>
        /// <returns>The membership information for a given table: MembershipTableData consisting multiple MembershipEntry entries and
        /// TableVersion, all read atomically.</returns>
        [Obsolete("Use ReadAllAsync instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

        /// <inheritdoc />
        public Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            return ReadAllAsync(this.deploymentConnectionString, this.watcher, cancellationToken);
        }

        internal static Task<MembershipTableData> ReadAllAsync(string deploymentConnectionString, ZooKeeperWatcher watcher, CancellationToken cancellationToken)
        {
            return UsingZookeeper(zk => ReadCoreAsync(zk, null, cancellationToken),
                deploymentConnectionString, watcher, cancellationToken, canBeReadOnly: true);
        }

        internal static async Task<MembershipTableData> ReadCoreAsync(
            NativeOperations zk, SiloAddress? siloAddress, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await zk.Sync("/");
                cancellationToken.ThrowIfCancellationRequested();
                Stat before;
                IEnumerable<SiloAddress> addresses;
                if (siloAddress is null)
                {
                    var children = await zk.GetChildren("/");
                    before = children.Stat;
                    addresses = children.Children.Select(SiloAddress.FromParsableString);
                }
                else
                {
                    before = (await zk.GetData("/")).Stat;
                    addresses = [siloAddress];
                }

                var pendingRows = Task.WhenAll(addresses.Select(address => GetRow(zk, address, siloAddress is not null, cancellationToken)));
                Tuple<MembershipEntry, string>?[] rows;
                try
                {
                    rows = await pendingRows;
                }
                catch (KeeperException.NoNodeException)
                {
                    // Observe every parallel read: a removed row must not hide another request's failure.
                    var failure = pendingRows.Exception!.InnerExceptions.FirstOrDefault(exception => exception is not KeeperException.NoNodeException);
                    if (failure is not null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var current = await zk.GetData("/");
                    if (SameVersion(before, current.Stat))
                    {
                        throw;
                    }

                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var after = await zk.GetData("/");
                if (SameVersion(before, after.Stat))
                {
                    return new MembershipTableData(rows.OfType<Tuple<MembershipEntry, string>>().ToList(), ConvertToTableVersion(after.Stat));
                }
            }
        }

        private static bool SameVersion(Stat before, Stat after) =>
            before.getVersion() == after.getVersion() && before.getCversion() == after.getCversion();

        /// <summary>
        /// Atomically tries to insert (add) a new MembershipEntry for one silo and also update the TableVersion.
        /// If operation succeeds, the following changes would be made to the table:
        /// 1) New MembershipEntry will be added to the table.
        /// 2) The newly added MembershipEntry will also be added with the new unique automatically generated eTag.
        /// 3) TableVersion.Version in the table will be updated to the new TableVersion.Version.
        /// 4) TableVersion etag in the table will be updated to the new unique automatically generated eTag.
        /// All those changes to the table, insert of a new row and update of the table version and the associated etags, should happen atomically, or fail atomically with no side effects.
        /// The operation should fail in each of the following conditions:
        /// 1) A MembershipEntry for a given silo already exist in the table
        /// 2) Update of the TableVersion failed since the given TableVersion etag (as specified by the TableVersion.VersionEtag property) did not match the TableVersion etag in the table.
        /// </summary>
        /// <param name="entry">MembershipEntry to be inserted.</param>
        /// <param name="tableVersion">The new TableVersion for this table, along with its etag.</param>
        /// <returns>True if the insert operation succeeded and false otherwise.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="entry"/> or <paramref name="tableVersion"/> is <see langword="null"/>.
        /// </exception>
        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

        /// <inheritdoc />
        public Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(tableVersion);
            cancellationToken.ThrowIfCancellationRequested();

            return UsingZookeeper(zk => InsertRowCoreAsync(zk, entry, tableVersion, cancellationToken),
                this.deploymentConnectionString, this.watcher, cancellationToken);
        }

        internal static async Task<bool> InsertRowCoreAsync(
            NativeOperations zk, MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rowPath = ConvertToRowPath(entry.SiloAddress);
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
            byte[] newRowData = Serialize(entry);
            byte[] newRowIAmAliveData = Serialize(entry.IAmAliveTime);

            int expectedTableVersion = int.Parse(tableVersion.VersionEtag, CultureInfo.InvariantCulture);

            try
            {
                await zk.Multi(
                [
                    Op.setData("/", null, expectedTableVersion),
                    Op.create(rowPath, newRowData, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT),
                    Op.create(rowIAmAlivePath, newRowIAmAliveData, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT)
                ]);
                return true;
            }
            catch (KeeperException e) when (e is KeeperException.NodeExistsException or KeeperException.BadVersionException)
            {
                return false;
            }
            catch (KeeperException.NoNodeException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await zk.GetData("/");
                return false;
            }
        }

        /// <summary>
        /// Atomically tries to update the MembershipEntry for one silo and also update the TableVersion.
        /// If operation succeeds, the following changes would be made to the table:
        /// 1) The MembershipEntry for this silo will be updated to the new MembershipEntry (the old entry will be fully substituted by the new entry) 
        /// 2) The eTag for the updated MembershipEntry will also be eTag with the new unique automatically generated eTag.
        /// 3) TableVersion.Version in the table will be updated to the new TableVersion.Version.
        /// 4) TableVersion etag in the table will be updated to the new unique automatically generated eTag.
        /// All those changes to the table, update of a new row and update of the table version and the associated etags, should happen atomically, or fail atomically with no side effects.
        /// The operation should fail in each of the following conditions:
        /// 1) A MembershipEntry for a given silo does not exist in the table
        /// 2) A MembershipEntry for a given silo exist in the table but its etag in the table does not match the provided etag.
        /// 3) Update of the TableVersion failed since the given TableVersion etag (as specified by the TableVersion.VersionEtag property) did not match the TableVersion etag in the table.
        /// </summary>
        /// <param name="entry">MembershipEntry to be updated.</param>
        /// <param name="etag">The etag  for the given MembershipEntry.</param>
        /// <param name="tableVersion">The new TableVersion for this table, along with its etag.</param>
        /// <returns>True if the update operation succeeded and false otherwise.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="entry"/>, <paramref name="etag"/>, or <paramref name="tableVersion"/> is
        /// <see langword="null"/>.
        /// </exception>
        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

        /// <inheritdoc />
        public Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(etag);
            ArgumentNullException.ThrowIfNull(tableVersion);
            cancellationToken.ThrowIfCancellationRequested();

            return UsingZookeeper(zk => UpdateRowCoreAsync(zk, entry, etag, tableVersion, cancellationToken),
                this.deploymentConnectionString, this.watcher, cancellationToken);
        }

        internal static async Task<bool> UpdateRowCoreAsync(
            NativeOperations zk, MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken)
        {
            string rowPath = ConvertToRowPath(entry.SiloAddress);
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
            var newRowData = Serialize(entry);
            int expectedTableVersion = int.Parse(tableVersion.VersionEtag, CultureInfo.InvariantCulture);
            int expectedRowVersion = int.Parse(etag, CultureInfo.InvariantCulture);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((await zk.GetData("/")).Stat.getVersion() != expectedTableVersion)
                {
                    return false;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((await zk.GetData(rowPath)).Stat.getVersion() != expectedRowVersion)
                    {
                        return false;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var heartbeat = await zk.GetData(rowIAmAlivePath);
                    var time = new DateTime(Math.Max(entry.IAmAliveTime.Ticks, Deserialize<DateTime>(heartbeat.Data).Ticks), DateTimeKind.Utc);
                    cancellationToken.ThrowIfCancellationRequested();
                    await zk.Multi(
                    [
                        Op.setData("/", null, expectedTableVersion),
                        Op.setData(rowPath, newRowData, expectedRowVersion),
                        Op.setData(rowIAmAlivePath, Serialize(time), heartbeat.Stat.getVersion())
                    ]);
                    return true;
                }
                catch (KeeperException.BadVersionException)
                {
                    // Retry heartbeat races while retaining the caller's row and table preconditions.
                }
                catch (KeeperException.NoNodeException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await zk.GetData("/");
                    return false;
                }
            }
        }

        /// <summary>
        /// Writes the owning silo's IAmAlive timestamp to its heartbeat node using one unconditional native update.
        /// The membership row and table version are preserved. Native storage failures propagate to the caller.
        /// </summary>
        /// <param name="entry">The owning silo's membership entry containing its heartbeat timestamp.</param>
        /// <returns>Task representing the successful execution of this operation. </returns>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

        /// <inheritdoc />
        public Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            cancellationToken.ThrowIfCancellationRequested();

            return UsingZookeeper(zk => UpdateIAmAliveCoreAsync(entry, zk.SetData, cancellationToken),
                this.deploymentConnectionString, this.watcher, cancellationToken);
        }

        internal static Task<Stat> UpdateIAmAliveCoreAsync(
            MembershipEntry entry,
            Func<string, byte[], int, Task<Stat>> setData,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return setData(ConvertToRowIAmAlivePath(entry.SiloAddress), Serialize(entry.IAmAliveTime), -1);
        }

        /// <summary>
        /// Deletes all table entries of the given clusterId
        /// </summary>
        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

        /// <inheritdoc />
        public Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
        {
            string pathToDelete = "/" + clusterId;
            return UsingZookeeper(rootConnectionString, async zk =>
            {
                await DeleteRecursive(zk, pathToDelete, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await zk.sync(pathToDelete);
            }, cancellationToken);
        }

        /// <summary>
        /// Reads the nodes /IP:Port@Gen and /IP:Port@Gen/IAmAlive (which together is one row)
        /// </summary>
        /// <param name="zk">The zookeeper instance used for the read</param>
        /// <param name="siloAddress">The silo address.</param>
        /// <param name="allowMissing">Whether a missing row is represented by an empty point read.</param>
        /// <param name="cancellationToken">A token which cancels the operation.</param>
        private static async Task<Tuple<MembershipEntry, string>?> GetRow(NativeOperations zk, SiloAddress siloAddress, bool allowMissing, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rowPath = ConvertToRowPath(siloAddress);
            string rowIAmAlivePath = ConvertToRowIAmAlivePath(siloAddress);

            DataResult row;
            try
            {
                row = await zk.GetData(rowPath);
            }
            catch (KeeperException.NoNodeException) when (allowMissing)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var heartbeat = await zk.GetData(rowIAmAlivePath);
            MembershipEntry me = Deserialize<MembershipEntry>(row.Data);
            me.IAmAliveTime = Deserialize<DateTime>(heartbeat.Data);

            int rowVersion = row.Stat.getVersion();

            return new Tuple<MembershipEntry, string>(me, rowVersion.ToString(CultureInfo.InvariantCulture));
        }

        // These delegates expose the native request boundary for deterministic tests of the real operation loops.
        internal sealed class NativeOperations(
            Func<string, Task<DataResult>> getData,
            Func<string, Task<ChildrenResult>> getChildren,
            Func<string, Task> sync,
            Func<List<Op>, Task> multi,
            Func<string, byte[], int, Task<Stat>> setData)
        {
            internal Func<string, Task<DataResult>> GetData { get; } = getData;
            internal Func<string, Task<ChildrenResult>> GetChildren { get; } = getChildren;
            internal Func<string, Task> Sync { get; } = sync;
            internal Func<List<Op>, Task> Multi { get; } = multi;
            internal Func<string, byte[], int, Task<Stat>> SetData { get; } = setData;
        }

        private static async Task<T> UsingZookeeper<T>(Func<NativeOperations, Task<T>> zkMethod, string deploymentConnectionString, ZooKeeperWatcher watcher, CancellationToken cancellationToken, bool canBeReadOnly = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = ZooKeeper.Using(deploymentConnectionString, ZOOKEEPER_SESSION_TIMEOUT, watcher, zk =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return zkMethod(new NativeOperations(
                    path => zk.getDataAsync(path), path => zk.getChildrenAsync(path), zk.sync,
                    operations => zk.multiAsync(operations), zk.setDataAsync));
            }, canBeReadOnly);

            return await AwaitOperationAsync(operation, cancellationToken);
        }

        internal static async Task<T> AwaitOperationAsync<T>(Task<T> operation, CancellationToken cancellationToken)
        {
            // ZooKeeperNetEx is tokenless. Keep the client alive until pending requests and
            // asynchronous disposal finish, observing failures even if the caller stops waiting.
            operation.Ignore();
            return await operation.WaitAsync(cancellationToken);
        }

        private async Task UsingZookeeper(string connectString, Func<ZooKeeper, Task> zkMethod, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = ZooKeeper.Using(connectString, ZOOKEEPER_SESSION_TIMEOUT, watcher, zk =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return zkMethod(zk);
            });

            operation.Ignore();
            await operation.WaitAsync(cancellationToken);
        }

        private static async Task DeleteRecursive(ZooKeeper zk, string path, CancellationToken cancellationToken)
        {
            // Unlike ZKUtil.deleteRecursiveAsync, check cancellation between requests.
            var paths = new List<string> { path };
            for (var i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var children = await zk.getChildrenAsync(paths[i]);
                foreach (var child in children.Children)
                {
                    paths.Add(paths[i].TrimEnd('/') + "/" + child);
                }
            }

            for (var i = paths.Count - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await zk.deleteAsync(paths[i], -1);
            }
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

        internal static byte[] Serialize(object obj)
        {
            return
                Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj, Formatting.None,
                    MembershipSerializerSettings.Instance));
        }

        internal static T Deserialize<T>(byte[] data)
        {
            return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(data), MembershipSerializerSettings.Instance)!;
        }

        /// <inheritdoc />
        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

        /// <inheritdoc />
        public Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            return UsingZookeeper(zk => CleanupCoreAsync(zk, beforeDate, cancellationToken),
                this.deploymentConnectionString, this.watcher, cancellationToken);
        }

        internal static async Task<bool> CleanupCoreAsync(NativeOperations zk, DateTimeOffset beforeDate, CancellationToken cancellationToken)
        {
            var cutoff = beforeDate.UtcDateTime;
            while (true)
            {
                var table = await ReadCoreAsync(zk, null, cancellationToken);
                var candidates = table.Members.Where(row => row.Item1.Status == SiloStatus.Dead
                    && row.Item1.StartTime < cutoff && row.Item1.IAmAliveTime < cutoff
                    && row.Item1.SuspectTimes?.Any(vote => vote.Item2 >= cutoff) != true).ToList();
                if (candidates.Count == 0)
                {
                    return true;
                }

                foreach (var (entry, etag) in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var heartbeatPath = ConvertToRowIAmAlivePath(entry.SiloAddress);
                        var heartbeat = await zk.GetData(heartbeatPath);
                        if (Deserialize<DateTime>(heartbeat.Data) >= cutoff)
                        {
                            continue;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        await zk.Multi(
                        [
                            Op.delete(heartbeatPath, heartbeat.Stat.getVersion()),
                            Op.delete(ConvertToRowPath(entry.SiloAddress), int.Parse(etag, CultureInfo.InvariantCulture))
                        ]);
                    }
                    catch (KeeperException.BadVersionException)
                    {
                        // Re-evaluate eligibility after a concurrent row or heartbeat update.
                    }
                    catch (KeeperException.NoNodeException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await zk.GetData("/");
                    }
                }
            }
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Created new deployment path: {DeploymentPath}"
        )]
        private partial void LogInformationCreatedNewDeploymentPath(string deploymentPath);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Deployment path already exists: {DeploymentPath}"
        )]
        private partial void LogDebugDeploymentPathAlreadyExists(string deploymentPath);
    }

    /// <summary>
    /// the state of every ZooKeeper client and its push notifications are published using watchers.
    /// in orleans the watcher is only for debugging purposes
    /// </summary>
    internal partial class ZooKeeperWatcher : Watcher
    {
        private readonly ILogger logger;
        public ZooKeeperWatcher(ILogger logger)
        {
            this.logger = logger;
        }

        public override Task process(WatchedEvent @event)
        {
            LogDebugWatchedEvent(@event);
            return Task.CompletedTask;
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{EventString}"
        )]
        private partial void LogDebugWatchedEvent(WatchedEvent eventString);
    }
}
