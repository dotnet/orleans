using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.AdoNet.Core;
using Orleans.Runtime;

namespace Orleans.Clustering.AdoNet;

internal class ClusteringRelationalOrleansQueries : RelationalOrleansQueries<ClusteringStoredQueries>
{
    private ClusteringRelationalOrleansQueries(IRelationalStorage storage, ClusteringStoredQueries queries) : base(storage, queries)
    {
    }

    /// <summary>
    /// Creates an instance of a database of type <see cref="ClusteringRelationalOrleansQueries"/> and Initializes Orleans queries from the database.
    /// Orleans uses only these queries and the variables therein, nothing more.
    /// </summary>
    /// <param name="invariantName">The invariant name of the connector for this database.</param>
    /// <param name="connectionString">The connection string this database should use for database operations.</param>
    internal new static async Task<ClusteringRelationalOrleansQueries> CreateInstance(string invariantName, string connectionString)
    {
        var storage = RelationalStorage.CreateInstance(invariantName, connectionString);

        var queries = await storage.ReadAsync(DbStoredQueries.GetQueriesKey, DbStoredQueries.Converters.GetQueryKeyAndValue, null);

        return new ClusteringRelationalOrleansQueries(storage, new ClusteringStoredQueries(queries.ToDictionary(q => q.Key, q => q.Value)));
    }

    /// <summary>
    /// Lists active gateways. Used mainly by Orleans clients.
    /// </summary>
    /// <param name="deploymentId">The deployment for which to query the gateways.</param>
    /// <returns>The gateways for the silo.</returns>
    public Task<List<Uri>> ActiveGatewaysAsync(string deploymentId)
    {
        return ReadAsync(Queries.GatewaysQueryKey, DbStoredQueries.Converters.GetGatewayUri, command =>
            new DbStoredQueries.Columns(command) { DeploymentId = deploymentId, Status = SiloStatus.Active },
            ret => ret.ToList());
    }

    /// <summary>
    /// Queries Orleans membership data.
    /// </summary>
    /// <param name="deploymentId">The deployment for which to query data.</param>
    /// <param name="siloAddress">Silo data used as parameters in the query.</param>
    /// <returns>Membership table data.</returns>
    public Task<MembershipTableData> MembershipReadRowAsync(string deploymentId, SiloAddress siloAddress)
    {
        return ReadAsync(Queries.MembershipReadRowKey, DbStoredQueries.Converters.GetMembershipEntry, command =>
            new DbStoredQueries.Columns(command) { DeploymentId = deploymentId, SiloAddress = siloAddress },
            ConvertToMembershipTableData);
    }

    /// <summary>
    /// returns all membership data for a deployment id
    /// </summary>
    /// <param name="deploymentId"></param>
    /// <returns></returns>
    public Task<MembershipTableData> MembershipReadAllAsync(string deploymentId)
    {
        return ReadAsync(Queries.MembershipReadAllKey, DbStoredQueries.Converters.GetMembershipEntry, command =>
            new DbStoredQueries.Columns(command) { DeploymentId = deploymentId }, ConvertToMembershipTableData);
    }

    /// <summary>
    /// deletes all membership entries for a deployment id
    /// </summary>
    /// <param name="deploymentId"></param>
    /// <returns></returns>
    public Task DeleteMembershipTableEntriesAsync(string deploymentId)
    {
        return ExecuteAsync(Queries.DeleteMembershipTableEntriesKey, command =>
            new DbStoredQueries.Columns(command) { DeploymentId = deploymentId });
    }

    /// <summary>
    /// deletes all membership entries for inactive silos where the IAmAliveTime is before the beforeDate parameter
    /// and the silo status is <seealso cref="SiloStatus.Dead"/>.
    /// </summary>
    /// <param name="beforeDate"></param>
    /// <param name="deploymentId"></param>
    /// <returns></returns>
    public Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, string deploymentId)
    {
        return ExecuteAsync(Queries.CleanupDefunctSiloEntriesKey, command =>
            new DbStoredQueries.Columns(command) { DeploymentId = deploymentId, IAmAliveTime = beforeDate.UtcDateTime });
    }

    /// <summary>
    /// Updates IAmAlive for a silo
    /// </summary>
    /// <param name="deploymentId"></param>
    /// <param name="siloAddress"></param>
    /// <param name="iAmAliveTime"></param>
    /// <returns></returns>
    public Task UpdateIAmAliveTimeAsync(string deploymentId, SiloAddress siloAddress, DateTime iAmAliveTime)
    {
        return ExecuteAsync(Queries.UpdateIAmAlivetimeKey, command =>
            new DbStoredQueries.Columns(command)
            {
                DeploymentId = deploymentId,
                SiloAddress = siloAddress,
                IAmAliveTime = iAmAliveTime
            });
    }

    /// <summary>
    /// Inserts a version row if one does not already exist.
    /// </summary>
    /// <param name="deploymentId">The deployment for which to query data.</param>
    /// <returns><em>TRUE</em> if a row was inserted. <em>FALSE</em> otherwise.</returns>
    public Task<bool> InsertMembershipVersionRowAsync(string deploymentId)
    {
        return ReadAsync(Queries.InsertMembershipVersionKey, DbStoredQueries.Converters.GetSingleBooleanValue, command =>
            new DbStoredQueries.Columns(command) { DeploymentId = deploymentId }, ret => ret.First());
    }

    /// <summary>
    /// Inserts a membership row if one does not already exist.
    /// </summary>
    /// <param name="deploymentId">The deployment with which to insert row.</param>
    /// <param name="membershipEntry">The membership entry data to insert.</param>
    /// <param name="etag">The table expected version etag.</param>
    /// <returns><em>TRUE</em> if insert succeeds. <em>FALSE</em> otherwise.</returns>
    public Task<bool> InsertMembershipRowAsync(string deploymentId, MembershipEntry membershipEntry,
        string etag)
    {
        return ReadAsync(Queries.InsertMembershipKey, DbStoredQueries.Converters.GetSingleBooleanValue, command =>
            new DbStoredQueries.Columns(command)
            {
                DeploymentId = deploymentId,
                IAmAliveTime = membershipEntry.IAmAliveTime,
                SiloName = membershipEntry.SiloName,
                HostName = membershipEntry.HostName,
                SiloAddress = membershipEntry.SiloAddress,
                StartTime = membershipEntry.StartTime,
                Status = membershipEntry.Status,
                ProxyPort = membershipEntry.ProxyPort,
                Version = etag
            }, ret => ret.First());
    }

    /// <summary>
    /// Updates membership row data.
    /// </summary>
    /// <param name="deploymentId">The deployment with which to insert row.</param>
    /// <param name="membershipEntry">The membership data to used to update database.</param>
    /// <param name="etag">The table expected version etag.</param>
    /// <returns><em>TRUE</em> if update SUCCEEDS. <em>FALSE</em> ot</returns>
    public Task<bool> UpdateMembershipRowAsync(string deploymentId, MembershipEntry membershipEntry,
        string etag)
    {
        return ReadAsync(Queries.UpdateMembershipKey, DbStoredQueries.Converters.GetSingleBooleanValue, command =>
            new DbStoredQueries.Columns(command)
            {
                DeploymentId = deploymentId,
                SiloAddress = membershipEntry.SiloAddress,
                IAmAliveTime = membershipEntry.IAmAliveTime,
                Status = membershipEntry.Status,
                SuspectTimes = membershipEntry.SuspectTimes,
                Version = etag
            }, ret => ret.First());
    }

    public static MembershipTableData ConvertToMembershipTableData(IEnumerable<Tuple<MembershipEntry, int>> ret)
    {
        var retList = ret.ToList();
        var tableVersionEtag = retList[0].Item2;
        var membershipEntries = new List<Tuple<MembershipEntry, string>>();
        if (retList[0].Item1 != null)
        {
            membershipEntries.AddRange(retList.Select(i => new Tuple<MembershipEntry, string>(i.Item1, string.Empty)));
        }
        return new MembershipTableData(membershipEntries, new TableVersion(tableVersionEtag, tableVersionEtag.ToString()));
    }
}
