using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Orleans.Runtime;

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
    private PreparedStatement? _updateIAmAliveWithTtlPreparedStatement;
    private PreparedStatement? _deleteMembershipEntryPreparedStatement;
    private PreparedStatement? _updateMembershipPreparedStatement;
    private PreparedStatement? _membershipReadRowPreparedStatement;
    private PreparedStatement? _membershipGatewaysQueryPreparedStatement;

    public static Task<OrleansQueries> CreateInstance(ISession session)
    {
        return Task.FromResult(new OrleansQueries(session));
    }

    private OrleansQueries(ISession session)
    {
        MembershipReadConsistencyLevel = ConsistencyLevel.Quorum;
        MembershipWriteConsistencyLevel = ConsistencyLevel.Quorum;

        Session = session;
    }

    internal async Task EnsureTableExistsAsync(TimeSpan maxRetryDelay, int? ttl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await DoesTableAlreadyExistAsync(cancellationToken))
        {
            try
            {
                await MakeTableAsync(ttl, cancellationToken);
            }
            catch (WriteTimeoutException) // If there's contention on table creation, backoff a bit and try once more
            {
                // Randomize the delay to avoid contention, preferring that more instances will wait longer
                var nextSingle = Random.Shared.NextSingle();
                await Task.Delay(maxRetryDelay * Math.Sqrt(nextSingle), cancellationToken);

                if (!await DoesTableAlreadyExistAsync(cancellationToken))
                {
                    await MakeTableAsync(ttl, cancellationToken);
                }
            }
        }
    }

    internal async Task EnsureClusterVersionExistsAsync(TimeSpan maxRetryDelay, string clusterIdentifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await DoesClusterVersionAlreadyExistAsync(clusterIdentifier, cancellationToken))
        {
            try
            {
                await ExecuteAsync(await InsertMembershipVersion(clusterIdentifier, cancellationToken), cancellationToken);
            }
            catch (WriteTimeoutException) // If there's contention on table creation, backoff a bit and try once more
            {
                // Randomize the delay to avoid contention, preferring that more instances will wait longer
                var nextSingle = Random.Shared.NextSingle();
                await Task.Delay(maxRetryDelay * Math.Sqrt(nextSingle), cancellationToken);

                if (!await DoesClusterVersionAlreadyExistAsync(clusterIdentifier, cancellationToken))
                {
                    await ExecuteAsync(await InsertMembershipVersion(clusterIdentifier, cancellationToken), cancellationToken);
                }
            }
        }
    }

    private async Task<bool> DoesClusterVersionAlreadyExistAsync(string clusterIdentifier, CancellationToken cancellationToken)
    {
        try
        {
            var resultSet = await ExecuteAsync(CheckIfClusterVersionExists(clusterIdentifier, ConsistencyLevel.LocalOne), cancellationToken);
            return await ReadFirstRowAsync(resultSet, cancellationToken) is not null;
        }
        catch (UnavailableException)
        {
            var resultSet = await ExecuteAsync(CheckIfClusterVersionExists(clusterIdentifier, ConsistencyLevel.One), cancellationToken);
            return await ReadFirstRowAsync(resultSet, cancellationToken) is not null;
        }
    }

    private async Task<bool> DoesTableAlreadyExistAsync(CancellationToken cancellationToken)
    {
        try
        {
            var resultSet = await ExecuteAsync(CheckIfTableExists(Session.Keyspace, ConsistencyLevel.LocalOne), cancellationToken);
            return await ReadFirstRowAsync(resultSet, cancellationToken) is not null;
        }
        catch (UnavailableException)
        {
            var resultSet = await ExecuteAsync(CheckIfTableExists(Session.Keyspace, ConsistencyLevel.One), cancellationToken);
            return await ReadFirstRowAsync(resultSet, cancellationToken) is not null;
        }
        catch (UnauthorizedException)
        {
            return false;
        }
    }

    private async Task MakeTableAsync(int? ttlSeconds, CancellationToken cancellationToken)
    {
        await ExecuteAsync(EnsureTableExists(ttlSeconds), cancellationToken);
        await ExecuteAsync(EnsureIndexExists, cancellationToken);
    }

    public ConsistencyLevel MembershipWriteConsistencyLevel { get; set; }

    public ConsistencyLevel MembershipReadConsistencyLevel { get; set; }

    public IStatement CheckIfClusterVersionExists(string clusterIdentifier, ConsistencyLevel consistencyLevel) =>
        new SimpleStatement(
                $"SELECT version FROM membership WHERE partition_key = '{clusterIdentifier}';")
            .SetConsistencyLevel(consistencyLevel);

    public IStatement CheckIfTableExists(string keyspace, ConsistencyLevel consistencyLevel) =>
        new SimpleStatement(
                $"SELECT * FROM system_schema.tables WHERE keyspace_name = '{keyspace}' AND table_name = 'membership';")
            .SetConsistencyLevel(consistencyLevel);

    /// <remarks>
    /// In Cassandra, a table-level <c>default_time_to_live</c> of <c>0</c> is treated as <c>disabled</c>.
    /// <para/>
    /// See https://docs.datastax.com/en/cql-oss/3.3/cql/cql_reference/cqlCreateTable.html#tabProp__cqlTableDefaultTTL
    /// </remarks>
    public IStatement EnsureTableExists(int? defaultTimeToLiveSeconds) => new SimpleStatement(
        $$"""
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
          )
          WITH compression = { 'class' : 'LZ4Compressor', 'enabled' : true }
            AND default_time_to_live = {{defaultTimeToLiveSeconds.GetValueOrDefault(0)}};
          """);

    public IStatement EnsureIndexExists => new SimpleStatement("""
            CREATE INDEX IF NOT EXISTS ix_membership_status ON membership(status);
            """);

    public async ValueTask<IStatement> InsertMembership(string clusterIdentifier, MembershipEntry membershipEntry, int version, CancellationToken cancellationToken = default)
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
           """, MembershipWriteConsistencyLevel, cancellationToken);
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

    public async ValueTask<IStatement> InsertMembershipVersion(string clusterIdentifier, CancellationToken cancellationToken = default)
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
            """, MembershipWriteConsistencyLevel, cancellationToken);
        return _insertMembershipVersionPreparedStatement.Bind(clusterIdentifier);
    }

    public async ValueTask<IStatement> DeleteMembershipTableEntries(string clusterIdentifier, CancellationToken cancellationToken = default)
    {
        _deleteMembershipTablePreparedStatement ??= await PrepareStatementAsync("""
                DELETE FROM membership WHERE partition_key = :partition_key;
                """,
            MembershipWriteConsistencyLevel, cancellationToken);
        return _deleteMembershipTablePreparedStatement.Bind(clusterIdentifier);
    }

    public async ValueTask<IStatement> UpdateIAmAliveTime(string clusterIdentifier, MembershipEntry membershipEntry, CancellationToken cancellationToken = default)
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
            ConsistencyLevel.Any, cancellationToken);

        return _updateIAmAlivePreparedStatement.Bind(new
        {
            partition_key = clusterIdentifier,
            i_am_alive_time = membershipEntry.IAmAliveTime,
            address = membershipEntry.SiloAddress.Endpoint.Address.ToString(),
            port = membershipEntry.SiloAddress.Endpoint.Port,
            generation = membershipEntry.SiloAddress.Generation
        });
    }

    /// <remarks>
    /// When the user has opted in to Cassandra TTL behavior, the entire membership row needs to be read and written
    /// back so that each cell is updated with the table's default TTL.
    /// <para/>
    /// Cassandra TTLs are cell-based, not row-based, which is why all the data needs to be re-inserted in order to
    /// update the TTLs for all cells in the row.
    /// <para/>
    /// https://docs.datastax.com/en/cql-oss/3.x/cql/cql_reference/cqlInsert.html
    /// </remarks>
    public async ValueTask<IStatement> UpdateIAmAliveTimeWithTtL(
        string clusterIdentifier,
        MembershipEntry iAmAliveEntry,
        MembershipEntry existingEntry,
        TableVersion existingVersion,
        CancellationToken cancellationToken = default)
    {
        _updateIAmAliveWithTtlPreparedStatement ??= await PrepareStatementAsync(
            """
            UPDATE membership
            SET
                version = :same_version,
                silo_name = :silo_name,
                host_name = :host_name,
                status = :status,
                proxy_port = :proxy_port,
                suspect_times = :suspect_times,
                start_time = :start_time,
                i_am_alive_time = :i_am_alive_time
            WHERE
                partition_key = :partition_key
                AND address = :address
                AND port = :port
                AND generation = :generation
            IF
            	version = :expected_version;
            """,
            // This is ignored because we're creating a LWT
            MembershipWriteConsistencyLevel, cancellationToken);

        BoundStatement updateIAmAliveTimeWithTtL = _updateIAmAliveWithTtlPreparedStatement.Bind(new
        {
            partition_key = clusterIdentifier,
            // The same version still needs to be written, to update its cell-level TTL
            same_version = existingVersion.Version,
            address = existingEntry.SiloAddress.Endpoint.Address.ToString(),
            port = existingEntry.SiloAddress.Endpoint.Port,
            generation = existingEntry.SiloAddress.Generation,
            silo_name = existingEntry.SiloName,
            host_name = existingEntry.HostName,
            status = (int)existingEntry.Status,
            proxy_port = existingEntry.ProxyPort,
            suspect_times = GetSuspectTimesString(existingEntry),
            start_time = existingEntry.StartTime,
            i_am_alive_time = iAmAliveEntry.IAmAliveTime,
            // But we still check that the version was the same during the update so we don't stomp on another update
            expected_version = existingVersion.Version,
        });

        // To improve performance, we allow IAmAlive updates to be LocalSerial
        updateIAmAliveTimeWithTtL.SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial);
        return updateIAmAliveTimeWithTtL;
    }

    public async ValueTask<IStatement> DeleteMembershipEntry(string clusterIdentifier, MembershipEntry membershipEntry, CancellationToken cancellationToken = default)
    {
        _deleteMembershipEntryPreparedStatement ??= await PrepareStatementAsync("""
            DELETE FROM
            	membership
            WHERE
            	partition_key = :partition_key
            	AND address = :address
            	AND port = :port
            	AND generation = :generation;
            """, MembershipWriteConsistencyLevel, cancellationToken);
        return _deleteMembershipEntryPreparedStatement.Bind(new
        {
            partition_key = clusterIdentifier,
            address = membershipEntry.SiloAddress.Endpoint.Address.ToString(),
            port = membershipEntry.SiloAddress.Endpoint.Port,
            generation = membershipEntry.SiloAddress.Generation
        });
    }

    public async ValueTask<IStatement> UpdateMembership(string clusterIdentifier, MembershipEntry membershipEntry, int version, CancellationToken cancellationToken = default)
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
            """, MembershipWriteConsistencyLevel, cancellationToken);
        return _updateMembershipPreparedStatement.Bind(new
        {
            partition_key = clusterIdentifier,
            new_version = version + 1,
            expected_version = version,
            status = (int)membershipEntry.Status,
            suspect_times = GetSuspectTimesString(membershipEntry),
            i_am_alive_time = membershipEntry.IAmAliveTime,
            address = membershipEntry.SiloAddress.Endpoint.Address.ToString(),
            port = membershipEntry.SiloAddress.Endpoint.Port,
            generation = membershipEntry.SiloAddress.Generation
        });
    }

    public async ValueTask<IStatement> MembershipReadVersion(string clusterIdentifier, CancellationToken cancellationToken = default)
    {
        _membershipReadVersionPreparedStatement ??= await PrepareStatementAsync("""
                SELECT
                	version
                FROM
                	membership
                WHERE
                	partition_key = :partition_key;
                """,
            MembershipReadConsistencyLevel, cancellationToken);
        return _membershipReadVersionPreparedStatement.Bind(clusterIdentifier);
    }

    public async ValueTask<IStatement> MembershipReadAll(string clusterIdentifier, CancellationToken cancellationToken = default)
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
            MembershipReadConsistencyLevel, cancellationToken);
        return _membershipReadAllPreparedStatement.Bind(clusterIdentifier);
    }

    public async ValueTask<IStatement> MembershipReadRow(string clusterIdentifier, SiloAddress siloAddress, CancellationToken cancellationToken = default)
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
            MembershipReadConsistencyLevel, cancellationToken);
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

    private async ValueTask<PreparedStatement> PrepareStatementAsync(string cql, ConsistencyLevel consistencyLevel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var statement = await AwaitAsync(Session.PrepareAsync(cql), cancellationToken);
        statement.SetConsistencyLevel(consistencyLevel);
        return statement;
    }

    internal async Task<RowSet> ExecuteAsync(IStatement statement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        statement.SetAutoPage(true);
        var rows = await AwaitAsync(Session.ExecuteAsync(statement), cancellationToken);
        return rows;
    }

    internal static async Task<Row?> ReadFirstRowAsync(RowSet rows, CancellationToken cancellationToken)
    {
        await foreach (var row in ReadRowsAsync(rows, cancellationToken))
        {
            return row;
        }

        return null;
    }

    internal static async IAsyncEnumerable<Row> ReadRowsAsync(RowSet rows, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Consume buffered rows so that fetching each subsequent page stays on the cancellable await path.
        while (true)
        {
            var available = rows.GetAvailableWithoutFetching();
            using (var buffered = rows.GetEnumerator())
            {
                for (var i = 0; i < available; i++)
                {
                    if (!buffered.MoveNext())
                    {
                        break;
                    }

                    yield return buffered.Current;
                }
            }

            if (rows.IsFullyFetched)
            {
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await AwaitAsync(rows.FetchMoreResultsAsync(), cancellationToken);
        }
    }

    // Cassandra's session and paging APIs are tokenless. Observe eventual faults when cancellation ends the wait.
    internal static async Task<T> AwaitAsync<T>(Task<T> operation, CancellationToken cancellationToken)
    {
        operation.Ignore();
        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task AwaitAsync(Task operation, CancellationToken cancellationToken)
    {
        operation.Ignore();
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? GetSuspectTimesString(MembershipEntry entry) =>
        entry.SuspectTimes == null
            ? null
            : string.Join(
                "|",
                entry.SuspectTimes.Select((Tuple<SiloAddress, DateTime> s) =>
                    $"{s.Item1.ToParsableString()},{LogFormatter.PrintDate(s.Item2)}"));
}
