using Cassandra;
using Orleans.Runtime;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Clustering.Cassandra;

/// <summary>
/// This class is responsible for keeping a list of prepared queries and
/// knowing their parameters (including type and conversion to the target 
/// type).
/// </summary>
internal sealed class OrleansQueries
{
    public ISession Session { get; }

    private PreparedStatement? _insertMembershipVersionPreparedStatement;
    private PreparedStatement? _deleteMembershipTablePreparedStatement;
    private PreparedStatement? _insertMembershipPreparedStatement;
    private PreparedStatement? _membershipReadAllPreparedStatement;
    private PreparedStatement? _membershipReadVersionPreparedStatement;
    private PreparedStatement? _updateIAmAlivePreparedStatement;
    private PreparedStatement? _deleteMembershipEntryPreparedStatement;
    private PreparedStatement? _updateMembershipPreparedStatement;
    private PreparedStatement? _membershipReadRowPreparedStatement;
    private PreparedStatement? _membershipGatewaysQueryPreparedStatement;

    public static Task<OrleansQueries> CreateInstance(ISession session)
    {
        string? dc = null;
        var isMultiDataCenter = false;
        foreach (var dataCenter in session.Cluster.AllHosts().Where(h => h?.Datacenter is not null).Select(h => h.Datacenter))
        {
            dc ??= dataCenter;
            if (dc != dataCenter)
            {
                isMultiDataCenter = true;
                break;
            }
        }

        return Task.FromResult(new OrleansQueries(session, isMultiDataCenter));
    }

    private OrleansQueries(ISession session, bool isMultiDataCenter)
    {
        if (isMultiDataCenter)
        {
            MembershipReadConsistencyLevel = ConsistencyLevel.LocalQuorum;
            MembershipWriteConsistencyLevel = ConsistencyLevel.EachQuorum;
        }
        else
        {
            MembershipReadConsistencyLevel = ConsistencyLevel.Quorum;
            MembershipWriteConsistencyLevel = ConsistencyLevel.Quorum;
        }
        Session = session;
    }

    public ConsistencyLevel MembershipWriteConsistencyLevel { get; set; }

    public ConsistencyLevel MembershipReadConsistencyLevel { get; set; }

    public IStatement EnsureTableExists() => new SimpleStatement("""
            CREATE TABLE IF NOT EXISTS membership
            (
                partition_key ascii,
                version int static,
                address ascii,
                port int,
                generation int,
                silo_name text,
                host_name text,
                status int,
                proxy_port int,
                suspect_times ascii,
                start_time timestamp,
                i_am_alive_time timestamp,

                PRIMARY KEY(partition_key, address, port, generation)
            ) WITH compression = {
                'class' : 'LZ4Compressor',
                'enabled' : true
            };
            """);

    public IStatement EnsureIndexExists => new SimpleStatement("""
            CREATE INDEX IF NOT EXISTS ix_membership_status ON membership(status);
            """);

    public async ValueTask<IStatement> InsertMembership(string clusterIdentifier, MembershipEntry membershipEntry, int version)
    {
        _insertMembershipPreparedStatement ??= await PrepareStatementAsync("""
           UPDATE membership
           SET
             version = :new_version,
             status = :status,
             start_time = :start_time,
             silo_name = :silo_name,
             host_name = :host_name,
             proxy_port = :proxy_port,
             i_am_alive_time = :i_am_alive_time
           WHERE
             partition_key = :partition_key
             AND address = :address
             AND port = :port
             AND generation = :generation
           IF
             version = :expected_version;
           """, MembershipWriteConsistencyLevel);
        return _insertMembershipPreparedStatement.Bind(new
        {
            partition_key = clusterIdentifier,
            address = membershipEntry.SiloAddress.Endpoint.Address.ToString(),
            port = membershipEntry.SiloAddress.Endpoint.Port,
            generation = membershipEntry.SiloAddress.Generation,
            silo_name = membershipEntry.SiloName,
            host_name = membershipEntry.HostName,
            status = (int)membershipEntry.Status,
            proxy_port = membershipEntry.ProxyPort,
            start_time = membershipEntry.StartTime,
            i_am_alive_time = membershipEntry.IAmAliveTime,
            new_version = version + 1,
            expected_version = version
        });
    }

    public async ValueTask<IStatement> InsertMembershipVersion(string clusterIdentifier)
    {
        _insertMembershipVersionPreparedStatement ??= await PrepareStatementAsync("""
            INSERT INTO membership(
            	partition_key,
            	version
            )
            VALUES (
            	:partition_key,
            	0
            )
            IF NOT EXISTS;
            """, MembershipWriteConsistencyLevel);
        return _insertMembershipVersionPreparedStatement.Bind(clusterIdentifier);
    }

    public async ValueTask<IStatement> DeleteMembershipTableEntries(string clusterIdentifier)
    {
        _deleteMembershipTablePreparedStatement ??= await PrepareStatementAsync("""
                DELETE FROM membership WHERE partition_key = :partition_key;
                """,
            MembershipWriteConsistencyLevel);
        return _deleteMembershipTablePreparedStatement.Bind(clusterIdentifier);
    }

    public async ValueTask<IStatement> UpdateIAmAliveTime(string clusterIdentifier, MembershipEntry membershipEntry)
    {
        _updateIAmAlivePreparedStatement ??= await PrepareStatementAsync("""
             UPDATE membership
             SET
                i_am_alive_time = :i_am_alive_time
             WHERE
                partition_key = :partition_key
                AND address = :address
                AND port = :port
                AND generation = :generation;
             """,
            ConsistencyLevel.Any);

        return _updateIAmAlivePreparedStatement.Bind(new
        {
            partition_key = clusterIdentifier,
            i_am_alive_time = membershipEntry.IAmAliveTime,
            address = membershipEntry.SiloAddress.Endpoint.Address.ToString(),
            port = membershipEntry.SiloAddress.Endpoint.Port,
            generation = membershipEntry.SiloAddress.Generation
        });
    }

    public async ValueTask<IStatement> DeleteMembershipEntry(string clusterIdentifier, MembershipEntry membershipEntry)
    {
        _deleteMembershipEntryPreparedStatement ??= await PrepareStatementAsync("""
            DELETE FROM 
            	membership
            WHERE
            	partition_key = :partition_key
            	AND address = :address
            	AND port = :port
            	AND generation = :generation;
            """, MembershipWriteConsistencyLevel);
        return _deleteMembershipEntryPreparedStatement.Bind(new
        {
            partition_key = clusterIdentifier,
            address = membershipEntry.SiloAddress.Endpoint.Address.ToString(),
            port = membershipEntry.SiloAddress.Endpoint.Port,
            generation = membershipEntry.SiloAddress.Generation
        });
    }

    public async ValueTask<IStatement> UpdateMembership(string clusterIdentifier, MembershipEntry membershipEntry, int version)
    {
        _updateMembershipPreparedStatement ??= await PrepareStatementAsync("""
            UPDATE membership
            SET
            	version = :new_version,
            	status = :status,
            	suspect_times = :suspect_times,
            	i_am_alive_time = :i_am_alive_time  
            WHERE
            	partition_key = :partition_key
            	AND address = :address
            	AND port = :port
            	AND generation = :generation
            IF 
            	version = :expected_version;
            """, MembershipWriteConsistencyLevel);
        return _updateMembershipPreparedStatement.Bind(new
        {
            partition_key = clusterIdentifier,
            new_version = version + 1,
            expected_version = version,
            status = (int)membershipEntry.Status,
            suspect_times =
                membershipEntry.SuspectTimes == null
                    ? null
                    : string.Join("|",
                        membershipEntry.SuspectTimes.Select(s =>
                            $"{s.Item1.ToParsableString()},{LogFormatter.PrintDate(s.Item2)}")),
            i_am_alive_time = membershipEntry.IAmAliveTime,
            address = membershipEntry.SiloAddress.Endpoint.Address.ToString(),
            port = membershipEntry.SiloAddress.Endpoint.Port,
            generation = membershipEntry.SiloAddress.Generation
        });
    }

    public async ValueTask<IStatement> MembershipReadVersion(string clusterIdentifier)
    {
        _membershipReadVersionPreparedStatement ??= await PrepareStatementAsync("""
                SELECT
                	version
                FROM
                	membership
                WHERE
                	partition_key = :partition_key;
                """,
            MembershipReadConsistencyLevel);
        return _membershipReadVersionPreparedStatement.Bind(clusterIdentifier);
    }

    public async ValueTask<IStatement> MembershipReadAll(string clusterIdentifier)
    {
        _membershipReadAllPreparedStatement ??= await PrepareStatementAsync("""
            SELECT
                version,
                address,
                port,
                generation,
                silo_name,
                host_name,
                status,
                proxy_port,
                suspect_times,
                start_time,
                i_am_alive_time
            FROM
                membership
            WHERE
                partition_key = :partition_key;
            """,
            MembershipReadConsistencyLevel);
        return _membershipReadAllPreparedStatement.Bind(clusterIdentifier);
    }

    public async ValueTask<IStatement> MembershipReadRow(string clusterIdentifier, SiloAddress siloAddress)
    {
        _membershipReadRowPreparedStatement ??= await PrepareStatementAsync("""
            SELECT
                version,
                address,
                port,
                generation,
                silo_name,
                host_name,
                status,
                proxy_port,
                suspect_times,
                start_time,
                i_am_alive_time
            FROM
                membership
            WHERE
                partition_key = :partition_key
                AND address = :address
                AND port = :port
                AND generation = :generation;
            """,
            MembershipReadConsistencyLevel);
        return _membershipReadRowPreparedStatement.Bind(new
        {
            partition_key = clusterIdentifier,
            address = siloAddress.Endpoint.Address.ToString(),
            port = siloAddress.Endpoint.Port,
            generation = siloAddress.Generation
        });
    }

    public async ValueTask<IStatement> GatewaysQuery(string clusterIdentifier, int status)
    {
        // Filtering is only for the `proxy_port` filtering. We're already hitting the partition
        // and secondary index on status which both don't need "ALLOW FILTERING"
        _membershipGatewaysQueryPreparedStatement ??= await PrepareStatementAsync("""
            SELECT
                address,
                proxy_port,
                generation
            FROM
                membership
            WHERE
                partition_key = :partition_key
                AND status = :status
                AND proxy_port > 0
            ALLOW FILTERING;
            """,
            MembershipReadConsistencyLevel);
        return _membershipGatewaysQueryPreparedStatement.Bind(new
        {
            partition_key = clusterIdentifier,
            status = status
        });
    }

    private async ValueTask<PreparedStatement> PrepareStatementAsync(string cql, ConsistencyLevel consistencyLevel)
    {
        var statement = await Session.PrepareAsync(cql);
        statement.SetConsistencyLevel(consistencyLevel);
        return statement;
    }
}
