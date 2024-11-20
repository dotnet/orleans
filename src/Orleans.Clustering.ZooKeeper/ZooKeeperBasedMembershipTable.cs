using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using org.apache.zookeeper;
using org.apache.zookeeper.data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Runtime.Membership;

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
public class ZooKeeperBasedMembershipTable : IMembershipTable
{
    private ILogger _logger;
    private readonly ZooKeeperClusteringSiloOptions _options;
    private const int ZOOKEEPER_CONNECTION_TIMEOUT = 2000;

    private ZooKeeperWatcher _watcher;
    private readonly ZooKeeper _zk;

    /// <summary>
    /// the node name for this deployment. for eg. /ClusterId
    /// </summary>
    private string _clusterPath;
    /// <summary>
    /// The root connection string. for eg. "192.168.1.1,192.168.1.2"
    /// </summary>
    private string _rootConnectionString;

    public ZooKeeperBasedMembershipTable(
        ILogger<ZooKeeperBasedMembershipTable> logger,
        IOptions<ZooKeeperClusteringSiloOptions> membershipTableOptions,
        IOptions<ClusterOptions> clusterOptions)
    {
        _logger = logger;
        _options = membershipTableOptions.Value;

        _clusterPath = $"/{clusterOptions.Value.ClusterId}";
        _rootConnectionString = _options.ConnectionString;

        _watcher = new ZooKeeperWatcher(logger);
        _zk = new ZooKeeper(_rootConnectionString, ZOOKEEPER_CONNECTION_TIMEOUT, _watcher);
    }

    /// <summary>
    /// Initializes the ZooKeeper based membership table.
    /// </summary>
    /// <param name="tryInitPath">if set to true, we'll try to create a node named "/ClusterId"</param>
    /// <returns></returns>
    public async Task InitializeMembershipTable(bool tryInitPath)
    {
        // even if I am not the one who created the path, 
        // try to insert an initial path if it is not already there,
        // so we always have the path, before this silo starts working.
        // note that when a zookeeper connection adds /ClusterId to the connection string, the nodes are relative
        try
        {
            await _watcher.WaitForConnectionAsync();

            await _zk.createAsync(_clusterPath, null, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT);
            await _zk.sync(_clusterPath);
            //if we got here we know that we've just created the deployment path with version=0
            _logger.Info("Created new deployment path: " + _clusterPath);
        }
        catch (KeeperException.NodeExistsException)
        {
            _logger.Debug("Deployment path already exists: " + _clusterPath);
        }
    }


    /// <summary>
    /// Atomically reads the Membership Table information about a given silo.
    /// The returned MembershipTableData includes one MembershipEntry entry for a given silo and the 
    /// TableVersion for this table. The MembershipEntry and the TableVersion have to be read atomically.
    /// </summary>
    /// <param name="siloAddress">The address of the silo whose membership information needs to be read.</param>
    /// <returns>The membership information for a given silo: MembershipTableData consisting one MembershipEntry entry and
    /// TableVersion, read atomically.</returns>
    public async Task<MembershipTableData> ReadRow(SiloAddress siloAddress)
    {
        await _watcher.WaitForConnectionAsync();
        var getRow = await GetRow(_zk, siloAddress);

        await _watcher.WaitForConnectionAsync();
        var getTableNode = await _zk.getDataAsync(_clusterPath); //get the current table version

        var rows = new List<Tuple<MembershipEntry, string>>(1);
        try
        {
            rows.Add(getRow);
        }
        catch (KeeperException.NoNodeException)
        {
            //that's ok because orleans expects an empty list in case of a missing row
        }

        var tableVersion = ConvertToTableVersion(getTableNode.Stat);

        return new MembershipTableData(rows, tableVersion);
    }

    /// <summary>
    /// Atomically reads the full content of the Membership Table.
    /// The returned MembershipTableData includes all MembershipEntry entry for all silos in the table and the 
    /// TableVersion for this table. The MembershipEntries and the TableVersion have to be read atomically.
    /// </summary>
    /// <returns>The membership information for a given table: MembershipTableData consisting multiple MembershipEntry entries and
    /// TableVersion, all read atomically.</returns>
    public async Task<MembershipTableData> ReadAll()
    {
        await _watcher.WaitForConnectionAsync();
        var childrenResult = await _zk.getChildrenAsync(_clusterPath);//get all the child nodes (without the data)

        var childrenList = new List<Tuple<MembershipEntry, string>>(childrenResult.Children.Count);
        //get the data from each child node
        foreach (var child in childrenResult.Children)
        {
            await _watcher.WaitForConnectionAsync();
            var childData = await GetRow(_zk, SiloAddress.FromParsableString(child));
            childrenList.Add(childData);
        }

        var tableVersion = ConvertToTableVersion(childrenResult.Stat);//this is the current table version

        return new MembershipTableData(childrenList, tableVersion);
    }
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
    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        var rowPath = ConvertToRowPath(entry.SiloAddress);
        var rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
        var newRowData = Serialize(entry);
        var newRowIAmAliveData = Serialize(entry.IAmAliveTime);

        var expectedTableVersion = int.Parse(tableVersion.VersionEtag, CultureInfo.InvariantCulture);

        await _watcher.WaitForConnectionAsync();
        return await TryTransaction(tx => tx
            .setData(_clusterPath, null, expectedTableVersion)//increments the version of node "/"
            .create(rowPath, newRowData, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT)
            .create(rowIAmAlivePath, newRowIAmAliveData, ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT));
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
    public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        var rowPath = ConvertToRowPath(entry.SiloAddress);
        var rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
        var newRowData = Serialize(entry);
        var newRowIAmAliveData = Serialize(entry.IAmAliveTime);

        var expectedTableVersion = int.Parse(tableVersion.VersionEtag, CultureInfo.InvariantCulture);
        var expectedRowVersion = int.Parse(etag, CultureInfo.InvariantCulture);

        await _watcher.WaitForConnectionAsync();
        return await TryTransaction(tx => tx
            .setData(_clusterPath, null, expectedTableVersion)//increments the version of node "/"
            .setData(rowPath, newRowData, expectedRowVersion)//increments the version of node "/IP:Port@Gen"
            .setData(rowIAmAlivePath, newRowIAmAliveData));
    }

    /// <summary>
    /// Updates the IAmAlive part (column) of the MembershipEntry for this silo.
    /// This operation should only update the IAmAlive column and not change other columns.
    /// This operation is a "dirty write" or "in place update" and is performed without etag validation. 
    /// With regards to eTags update:
    /// This operation may automatically update the eTag associated with the given silo row, but it does not have to. It can also leave the etag not changed ("dirty write").
    /// With regards to TableVersion:
    /// this operation should not change the TableVersion of the table. It should leave it untouched.
    /// There is no scenario where this operation could fail due to table semantical reasons. It can only fail due to network problems or table unavailability.
    /// </summary>
    /// <param name="entry">The target MembershipEntry tp update</param>
    /// <returns>Task representing the successful execution of this operation. </returns>
    public async Task UpdateIAmAlive(MembershipEntry entry)
    {
        var rowIAmAlivePath = ConvertToRowIAmAlivePath(entry.SiloAddress);
        var newRowIAmAliveData = Serialize(entry.IAmAliveTime);

        //update the data for IAmAlive unconditionally
        await _watcher.WaitForConnectionAsync();
        await _zk.setDataAsync(rowIAmAlivePath, newRowIAmAliveData);
    }

    /// <summary>
    /// Deletes all table entries of the given clusterId
    /// </summary>
    public async Task DeleteMembershipTableEntries(string clusterId)
    {
        var pathToDelete = $"/{clusterId}";

        await _watcher.WaitForConnectionAsync();
        await ZKUtil.deleteRecursiveAsync(_zk, pathToDelete);
        await _zk.sync(pathToDelete);
    }

    /// <summary>
    /// Reads the nodes /IP:Port@Gen and /IP:Port@Gen/IAmAlive (which together is one row)
    /// </summary>
    /// <param name="zk">The zookeeper instance used for the read</param>
    /// <param name="siloAddress">The silo address.</param>
    private async Task<Tuple<MembershipEntry, string>> GetRow(ZooKeeper zk, SiloAddress siloAddress)
    {
        var rowPath = ConvertToRowPath(siloAddress);
        var rowIAmAlivePath = ConvertToRowIAmAlivePath(siloAddress);

        await _watcher.WaitForConnectionAsync();
        var rowData = await zk.getDataAsync(rowPath);

        await _watcher.WaitForConnectionAsync();
        var rowIAmAliveData = await zk.getDataAsync(rowIAmAlivePath);

        var me = Deserialize<MembershipEntry>(rowData.Data);
        me.IAmAliveTime = Deserialize<DateTime>(rowIAmAliveData.Data);

        var rowVersion = rowData.Stat.getVersion();

        return new Tuple<MembershipEntry, string>(me, rowVersion.ToString(CultureInfo.InvariantCulture));
    }

    private string ConvertToRowPath(SiloAddress siloAddress)
        => $"{_clusterPath}/{siloAddress.ToParsableString()}";

    private string ConvertToRowIAmAlivePath(SiloAddress siloAddress)
        => $"{ConvertToRowPath(siloAddress)}/IAmAlive";

    private static TableVersion ConvertToTableVersion(Stat stat)
    {
        var version = stat.getVersion();
        return new TableVersion(version, version.ToString(CultureInfo.InvariantCulture));
    }

    private static byte[] Serialize(object obj)
        => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj, Formatting.None, MembershipSerializerSettings.Instance));

    private static T Deserialize<T>(byte[] data)
        => JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(data), MembershipSerializerSettings.Instance);

    private async Task<bool> TryTransaction(Func<Transaction, Transaction> transactionFunc)
    {
        try
        {
            var trx = transactionFunc(_zk.transaction());
            await trx.commitAsync();

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

    public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        await _watcher.WaitForConnectionAsync();
        var childrenResult = await _zk.getChildrenAsync(_clusterPath);
        var deleteTasks = new List<Task>();

        foreach (var child in childrenResult.Children)
        {
            var siloData = await GetRow(_zk, SiloAddress.FromParsableString(child));
            var membershipEntry = siloData.Item1;

            if (membershipEntry.Status == SiloStatus.Dead && membershipEntry.IAmAliveTime < beforeDate)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug("Deleting defunct silo entry {silo} status: {status} last alive: {iamalivetime}.",
                        membershipEntry.SiloAddress,
                        membershipEntry.Status,
                        membershipEntry.IAmAliveTime);

                deleteTasks.Add(ZKUtil.deleteRecursiveAsync(_zk, $"{_clusterPath}/{child}"));
            }
        }

        await Task.WhenAll(deleteTasks);
    }
}