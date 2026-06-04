using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Cassandra;
using Orleans.Runtime;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Orleans.Clustering.Cassandra;

/// <summary>
/// This class is responsible for keeping a list of prepared queries and
/// knowing their parameters (including type and conversion to the target
/// type).
/// </summary>
internal sealed class OrleansQueries
{
    private const string MetadataColumnName = "metadata_json";

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

    internal async Task EnsureTableExistsAsync(TimeSpan maxRetryDelay, int? ttl)
    {
        var tableExists = await DoesTableAlreadyExistAsync();
        if (!tableExists)
        {
            try
            {
                await MakeTableAsync(ttl);
                MetadataColumnAvailable = true;
            }
            catch (WriteTimeoutException) // If there's contention on table creation, backoff a bit and try once more
            {
                // Randomize the delay to avoid contention, preferring that more instances will wait longer
                var nextSingle = Random.Shared.NextSingle();
                await Task.Delay(maxRetryDelay * Math.Sqrt(nextSingle));

                if (!await DoesTableAlreadyExistAsync())
                {
                    await MakeTableAsync(ttl);
                    MetadataColumnAvailable = true;
                }
            }
        }

        if (!MetadataColumnAvailable)
        {
            await EnsureMetadataColumnExistsAsync();
        }
    }

    internal async Task EnsureClusterVersionExistsAsync(TimeSpan maxRetryDelay, string clusterIdentifier)
    {
        if (!await DoesClusterVersionAlreadyExistAsync(clusterIdentifier))
        {
            try
            {
                await Session.ExecuteAsync(await InsertMembershipVersion(clusterIdentifier));
            }
            catch (WriteTimeoutException) // If there's contention on table creation, backoff a bit and try once more
            {
                // Randomize the delay to avoid contention, preferring that more instances will wait longer
                var nextSingle = Random.Shared.NextSingle();
                await Task.Delay(maxRetryDelay * Math.Sqrt(nextSingle));

                if (!await DoesClusterVersionAlreadyExistAsync(clusterIdentifier))
                {
                    await Session.ExecuteAsync(await InsertMembershipVersion(clusterIdentifier));
                }
            }
        }
    }

    private async Task<bool> DoesClusterVersionAlreadyExistAsync(string clusterIdentifier)
    {
        try
        {
            var resultSet = await Session.ExecuteAsync(CheckIfClusterVersionExists(clusterIdentifier, ConsistencyLevel.LocalOne));
            return resultSet.Any();
        }
        catch (UnavailableException)
        {
            var resultSet = await Session.ExecuteAsync(CheckIfClusterVersionExists(clusterIdentifier, ConsistencyLevel.One));
            return resultSet.Any();
        }
    }

    private async Task<bool> DoesTableAlreadyExistAsync()
    {
        try
        {
            var resultSet = await Session.ExecuteAsync(CheckIfTableExists(Session.Keyspace, ConsistencyLevel.LocalOne));
            return resultSet.Any();
        }
        catch (UnavailableException)
        {
            var resultSet = await Session.ExecuteAsync(CheckIfTableExists(Session.Keyspace, ConsistencyLevel.One));
            return resultSet.Any();
        }
        catch (UnauthorizedException)
        {
            return false;
        }
    }

    private async Task<bool> DoesMetadataColumnAlreadyExistAsync()
    {
        try
        {
            var resultSet = await Session.ExecuteAsync(CheckIfMetadataColumnExists(Session.Keyspace, ConsistencyLevel.LocalOne));
            return resultSet.Any();
        }
        catch (UnavailableException)
        {
            var resultSet = await Session.ExecuteAsync(CheckIfMetadataColumnExists(Session.Keyspace, ConsistencyLevel.One));
            return resultSet.Any();
        }
        catch (UnauthorizedException)
        {
            return false;
        }
    }

    private async Task EnsureMetadataColumnExistsAsync()
    {
        if (await DoesMetadataColumnAlreadyExistAsync())
        {
            MetadataColumnAvailable = true;
            return;
        }

        try
        {
            await Session.ExecuteAsync(AddMetadataColumn());
            MetadataColumnAvailable = true;
        }
        catch (InvalidQueryException)
        {
            MetadataColumnAvailable = await DoesMetadataColumnAlreadyExistAsync();
        }
        catch (UnauthorizedException)
        {
            MetadataColumnAvailable = false;
        }
    }

    private async Task MakeTableAsync(int? ttlSeconds)
    {
        await Session.ExecuteAsync(EnsureTableExists(ttlSeconds));
        await Session.ExecuteAsync(EnsureIndexExists);
    }

    public ConsistencyLevel MembershipWriteConsistencyLevel { get; set; }

    public ConsistencyLevel MembershipReadConsistencyLevel { get; set; }

    public bool MetadataColumnAvailable { get; private set; }

    public IStatement CheckIfClusterVersionExists(string clusterIdentifier, ConsistencyLevel consistencyLevel) =>
        new SimpleStatement(
                $"SELECT version FROM membership WHERE partition_key = '{clusterIdentifier}';")
            .SetConsistencyLevel(consistencyLevel);

    public IStatement CheckIfTableExists(string keyspace, ConsistencyLevel consistencyLevel) =>
        new SimpleStatement(
                $"SELECT * FROM system_schema.tables WHERE keyspace_name = '{keyspace}' AND table_name = 'membership';")
            .SetConsistencyLevel(consistencyLevel);

    public IStatement CheckIfMetadataColumnExists(string keyspace, ConsistencyLevel consistencyLevel) =>
        new SimpleStatement(
                $"SELECT column_name FROM system_schema.columns WHERE keyspace_name = '{keyspace}' AND table_name = 'membership' AND column_name = '{MetadataColumnName}';")
            .SetConsistencyLevel(consistencyLevel);

    public IStatement AddMetadataColumn() => new SimpleStatement($"ALTER TABLE membership ADD {MetadataColumnName} text;");

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
              metadata_json text,
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

    public async ValueTask<IStatement> InsertMembership(string clusterIdentifier, MembershipEntry membershipEntry, int version)
    {
        _insertMembershipPreparedStatement ??= await PrepareStatementAsync(MetadataColumnAvailable ? """
           UPDATE membership
           SET
             version = :new_version,
             status = :status,
             start_time = :start_time,
             silo_name = :silo_name,
             host_name = :host_name,
             proxy_port = :proxy_port,
             metadata_json = :metadata_json,
             i_am_alive_time = :i_am_alive_time
           WHERE
             partition_key = :partition_key
             AND address = :address
             AND port = :port
             AND generation = :generation
           IF
                version = :expected_version;
           """ : """
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

        return MetadataColumnAvailable
           ? _insertMembershipPreparedStatement.Bind(new
           {
               partition_key = clusterIdentifier,
               address = membershipEntry.SiloAddress.Endpoint.Address.ToString(),
               port = membershipEntry.SiloAddress.Endpoint.Port,
               generation = membershipEntry.SiloAddress.Generation,
               silo_name = membershipEntry.SiloName,
               host_name = membershipEntry.HostName,
               status = (int)membershipEntry.Status,
               proxy_port = membershipEntry.ProxyPort,
               metadata_json = SerializeMetadata(membershipEntry),
               start_time = membershipEntry.StartTime,
               i_am_alive_time = membershipEntry.IAmAliveTime,
               new_version = version + 1,
               expected_version = version
           })
           : _insertMembershipPreparedStatement.Bind(new
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
        TableVersion existingVersion)
    {
        _updateIAmAliveWithTtlPreparedStatement ??= await PrepareStatementAsync(
            MetadataColumnAvailable ? """
            UPDATE membership
            SET
                version = :same_version,
                silo_name = :silo_name,
                host_name = :host_name,
                status = :status,
                proxy_port = :proxy_port,
                suspect_times = :suspect_times,
                metadata_json = :metadata_json,
                start_time = :start_time,
                i_am_alive_time = :i_am_alive_time
            WHERE
                partition_key = :partition_key
                AND address = :address
                AND port = :port
                AND generation = :generation
            IF
                version = :expected_version;
            """ : """
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
            MembershipWriteConsistencyLevel);

        BoundStatement updateIAmAliveTimeWithTtL = MetadataColumnAvailable
            ? _updateIAmAliveWithTtlPreparedStatement.Bind(new
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
                metadata_json = SerializeMetadata(existingEntry),
                start_time = existingEntry.StartTime,
                i_am_alive_time = iAmAliveEntry.IAmAliveTime,
                // But we still check that the version was the same during the update so we don't stomp on another update
                expected_version = existingVersion.Version,
            })
            : _updateIAmAliveWithTtlPreparedStatement.Bind(new
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
        _updateMembershipPreparedStatement ??= await PrepareStatementAsync(MetadataColumnAvailable ? """
            UPDATE membership
            SET
                version = :new_version,
                status = :status,
                suspect_times = :suspect_times,
                metadata_json = :metadata_json,
                i_am_alive_time = :i_am_alive_time
            WHERE
                partition_key = :partition_key
                AND address = :address
                AND port = :port
                AND generation = :generation
            IF
                version = :expected_version;
            """ : """
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

        return MetadataColumnAvailable
            ? _updateMembershipPreparedStatement.Bind(new
            {
                partition_key = clusterIdentifier,
                new_version = version + 1,
                expected_version = version,
                status = (int)membershipEntry.Status,
                suspect_times = GetSuspectTimesString(membershipEntry),
                metadata_json = SerializeMetadata(membershipEntry),
                i_am_alive_time = membershipEntry.IAmAliveTime,
                address = membershipEntry.SiloAddress.Endpoint.Address.ToString(),
                port = membershipEntry.SiloAddress.Endpoint.Port,
                generation = membershipEntry.SiloAddress.Generation
            })
            : _updateMembershipPreparedStatement.Bind(new
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
        _membershipReadAllPreparedStatement ??= await PrepareStatementAsync(MetadataColumnAvailable ? """
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
                metadata_json,
                start_time,
                i_am_alive_time
            FROM
                membership
            WHERE
                partition_key = :partition_key;
            """ : """
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
        _membershipReadRowPreparedStatement ??= await PrepareStatementAsync(MetadataColumnAvailable ? """
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
                metadata_json,
                start_time,
                i_am_alive_time
            FROM
                membership
            WHERE
                partition_key = :partition_key
                AND address = :address
                AND port = :port
                AND generation = :generation;
            """ : """
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

    internal static string? SerializeMetadata(MembershipEntry entry)
        => entry.Metadata is not null ? JsonSerializer.Serialize(entry.Metadata) : null;

    internal static ImmutableDictionary<string, string>? DeserializeMetadata(string? metadata)
        => !string.IsNullOrEmpty(metadata)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(metadata)?.ToImmutableDictionary()
            : null;

    private async ValueTask<PreparedStatement> PrepareStatementAsync(string cql, ConsistencyLevel consistencyLevel)
    {
        var statement = await Session.PrepareAsync(cql);
        statement.SetConsistencyLevel(consistencyLevel);
        return statement;
    }

    private static string? GetSuspectTimesString(MembershipEntry entry) =>
        entry.SuspectTimes == null
            ? null
            : string.Join(
                "|",
                entry.SuspectTimes.Select((Tuple<SiloAddress, DateTime> s) =>
                    $"{s.Item1.ToParsableString()},{LogFormatter.PrintDate(s.Item2)}"));
}
