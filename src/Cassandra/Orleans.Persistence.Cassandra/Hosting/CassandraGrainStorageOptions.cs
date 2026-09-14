using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Cassandra;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Providers;
using Orleans.Persistence.Cassandra;
using Orleans.Cassandra;

namespace Orleans.Configuration;

/// <summary>
/// Options for the Cassandra grain storage provider.
/// </summary>
public sealed class CassandraGrainStorageOptions : IStorageProviderSerializerOptions
{
    /// <summary>The Cassandra keyspace.</summary>
    public string Keyspace { get; set; } = "orleans";

    /// <summary>The Cassandra table. The default provider uses <c>grain_state</c> when this is not set.</summary>
    public string? TableName { get; set; }

    /// <summary>The connection string used to create a provider-owned Cassandra session.</summary>
    [Redact]
    public string? ConnectionString { get; set; }

    /// <summary>Whether to create the table during provider initialization.</summary>
    public bool CreateTableIfNotExists { get; set; }

    /// <summary>Whether clearing state deletes the Cassandra row instead of retaining a tombstone.</summary>
    public bool DeleteStateOnClear { get; set; }

    /// <summary>The consistency level used for ordinary reads and writes.</summary>
    /// <remarks>
    /// The default is Quorum for correctness across datacenters. Lower consistency levels are opt-in and provide
    /// weaker guarantees.
    /// </remarks>
    public ConsistencyLevel ConsistencyLevel { get; set; } = ConsistencyLevel.Quorum;

    /// <summary>The serial consistency level used for lightweight transactions.</summary>
    /// <remarks>
    /// The default is Serial for correctness. LocalSerial is available as an opt-in weaker guarantee.
    /// </remarks>
    public ConsistencyLevel SerialConsistencyLevel { get; set; } = ConsistencyLevel.Serial;

    /// <summary>The lifecycle stage at which the provider is initialized.</summary>
    public int InitStage { get; set; } = ServiceLifecycleStage.ApplicationServices;

    /// <inheritdoc />
    public IGrainStorageSerializer GrainStorageSerializer { get; set; } = null!;

    internal bool OwnsSession { get; private set; }

    [NotNull]
    internal Func<IServiceProvider, Task<ISession>> CreateSessionAsync { get; private set; } = default!;

    /// <summary>
    /// Configures a provider-owned Cassandra session.
    /// </summary>
    /// <param name="connectionString">The Cassandra connection string.</param>
    /// <param name="keyspace">The keyspace to connect to.</param>
    public void ConfigureClient(string connectionString, string keyspace = "orleans")
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        var normalizedKeyspace = CassandraIdentifier.Normalize(keyspace);
        ConnectionString = connectionString;
        Keyspace = normalizedKeyspace;
        OwnsSession = true;
        CreateSessionAsync = async _ =>
        {
            var cluster = Cluster.Builder().WithConnectionString(connectionString).Build();
            var connected = false;
            try
            {
                var session = await cluster.ConnectAsync(normalizedKeyspace).ConfigureAwait(false);
                connected = true;
                return session;
            }
            finally
            {
                if (!connected)
                {
                    cluster.Dispose();
                }
            }
        };
    }

    /// <summary>
    /// Configures an externally owned Cassandra session.
    /// </summary>
    /// <param name="sessionProvider">A delegate which returns a shared session.</param>
    public void ConfigureClient(Func<IServiceProvider, Task<ISession>> sessionProvider)
        => ConfigureClient(sessionProvider, ownsSession: false);

    /// <summary>
    /// Configures a Cassandra session and specifies whether the provider owns it.
    /// </summary>
    /// <param name="sessionProvider">A delegate which returns a Cassandra session.</param>
    /// <param name="ownsSession">Whether the provider should dispose the session's cluster.</param>
    public void ConfigureClient(Func<IServiceProvider, Task<ISession>> sessionProvider, bool ownsSession)
    {
        ArgumentNullException.ThrowIfNull(sessionProvider);
        OwnsSession = ownsSession;
        CreateSessionAsync = sessionProvider;
    }
}

/// <summary>Validates Cassandra grain storage options.</summary>
public sealed class CassandraGrainStorageOptionsValidator : IConfigurationValidator
{
    private readonly CassandraGrainStorageOptions _options;
    private readonly string _name;

    /// <summary>Initializes a validator.</summary>
    /// <param name="options">The options to validate.</param>
    /// <param name="name">The provider name.</param>
    public CassandraGrainStorageOptionsValidator(CassandraGrainStorageOptions options, string name)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _name = name;
    }

    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        if (_options.ConsistencyLevel is ConsistencyLevel.Serial or ConsistencyLevel.LocalSerial)
        {
            throw new OrleansConfigurationException(
                $"Invalid {_name} Cassandra grain storage configuration: {nameof(_options.ConsistencyLevel)} must be an ordinary consistency level, not {nameof(ConsistencyLevel.Serial)} or {nameof(ConsistencyLevel.LocalSerial)}.");
        }

        if (_options.SerialConsistencyLevel is not (ConsistencyLevel.Serial or ConsistencyLevel.LocalSerial))
        {
            throw new OrleansConfigurationException(
                $"Invalid {_name} Cassandra grain storage configuration: {nameof(_options.SerialConsistencyLevel)} must be {nameof(ConsistencyLevel.Serial)} or {nameof(ConsistencyLevel.LocalSerial)}.");
        }

        if (!CassandraGrainStorage.ValidateIdentifier(_options.Keyspace, nameof(_options.Keyspace)))
        {
            throw new OrleansConfigurationException($"Invalid {_name} Cassandra grain storage configuration: {nameof(_options.Keyspace)} is not a valid Cassandra identifier.");
        }

        if (_options.TableName is null && !string.Equals(_name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal))
        {
            throw new OrleansConfigurationException($"A table name is required for named Cassandra grain storage provider '{_name}'.");
        }

        if (_options.TableName is not null && !CassandraGrainStorage.ValidateIdentifier(_options.TableName, nameof(_options.TableName)))
        {
            throw new OrleansConfigurationException($"Invalid {_name} Cassandra grain storage configuration: {nameof(_options.TableName)} is not a valid Cassandra identifier.");
        }

        if (_options.CreateSessionAsync is null && string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new OrleansConfigurationException($"Configure a Cassandra session or {nameof(_options.ConnectionString)} for provider '{_name}'.");
        }
    }
}
