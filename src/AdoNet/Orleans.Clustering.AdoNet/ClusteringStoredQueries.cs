using System.Collections.Generic;
using Orleans.AdoNet.Core;

namespace Orleans.Clustering.AdoNet;

internal class ClusteringStoredQueries(Dictionary<string, string> queries) : DbStoredQueries(queries)
{
    /// <summary>
    /// A query template to retrieve gateway URIs.
    /// </summary>
    internal string GatewaysQueryKey => GetQuery(nameof(GatewaysQueryKey));

    /// <summary>
    /// A query template to retrieve a single row of membership data.
    /// </summary>
    internal string MembershipReadRowKey => GetQuery(nameof(MembershipReadRowKey));

    /// <summary>
    /// A query template to retrieve all membership data.
    /// </summary>
    internal string MembershipReadAllKey => GetQuery(nameof(MembershipReadAllKey));

    /// <summary>
    /// A query template to insert a membership version row.
    /// </summary>
    internal string InsertMembershipVersionKey => GetQuery(nameof(InsertMembershipVersionKey));

    /// <summary>
    /// A query template to update "I Am Alive Time".
    /// </summary>
    internal string UpdateIAmAlivetimeKey => GetQuery(nameof(UpdateIAmAlivetimeKey));

    /// <summary>
    /// A query template to insert a membership row.
    /// </summary>
    internal string InsertMembershipKey => GetQuery(nameof(InsertMembershipKey));

    /// <summary>
    /// A query template to update a membership row.
    /// </summary>
    internal string UpdateMembershipKey => GetQuery(nameof(UpdateMembershipKey));

    /// <summary>
    /// A query template to delete membership entries.
    /// </summary>
    internal string DeleteMembershipTableEntriesKey => GetQuery(nameof(DeleteMembershipTableEntriesKey));

    /// <summary>
    /// A query template to cleanup defunct silo entries.
    /// </summary>
    internal string CleanupDefunctSiloEntriesKey => GetQuery(nameof(CleanupDefunctSiloEntriesKey));
}
