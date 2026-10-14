using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Provides journal storage on a Garnet partition, hosting the partition in process when configured to.
/// </summary>
internal sealed class GarnetJournalStorageProvider : IJournalStorageProvider, IJournalStorageCatalog, ILifecycleParticipant<ISiloLifecycle>, IAsyncDisposable
{
    private const int ConcurrentBucketReads = 32;

    private readonly GarnetJournalStorageOptions _options;
    private readonly string _journalFormatKey;
    private readonly JournalStorageTelemetry _telemetry;
    private readonly ILoggerFactory? _loggerFactory;
    private GarnetJournalPartitionHost? _host;
    private ConnectionMultiplexer? _connection;
    private GarnetJournalPartitionClient? _client;

    public GarnetJournalStorageProvider(
        IOptions<GarnetJournalStorageOptions> options,
        IOptions<JournaledStateManagerOptions> managerOptions,
        ILoggerFactory? loggerFactory = null,
        OrleansInstruments? instruments = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(managerOptions);
        _options = options.Value;
        _journalFormatKey = JournalFormatServices.ValidateJournalFormatKey(managerOptions.Value.JournalFormatKey);
        _loggerFactory = loggerFactory;
        _telemetry = instruments is null ? JournalStorageTelemetry.CreateForDirectConstruction() : new JournalStorageTelemetry(instruments);
    }

    /// <summary>Gets the endpoint of the embedded partition, if this provider hosts one.</summary>
    internal System.Net.IPEndPoint? EmbeddedEndPoint => _host?.EndPoint;

    internal GarnetJournalPartitionClient Client => _client ?? throw new InvalidOperationException(
        $"{nameof(GarnetJournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return new GarnetJournalStorage(Client, journalId, _journalFormatKey, _options.CompactionThresholdBytes);
    }

    /// <remarks>
    /// Reads each bucket's index set as one snapshot. The index is updated in the same transaction as every create and
    /// delete, so a listing includes every journal which exists for its whole duration.
    /// </remarks>
    public async IAsyncEnumerable<JournalCatalogEntry> ListAsync(
        JournalCatalogListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var range = new JournalCatalogRange(options);
        if (range.IsEmpty)
        {
            yield break;
        }

        var client = Client;
        for (var start = 0; start < client.BucketCount; start += ConcurrentBucketReads)
        {
            var count = Math.Min(ConcurrentBucketReads, client.BucketCount - start);
            var reads = new Task<RedisValue[]>[count];
            for (var i = 0; i < count; i++)
            {
                reads[i] = client.Database.SetMembersAsync(GarnetJournalKeys.GetIndexKey(start + i));
            }

            foreach (var members in await Task.WhenAll(reads).WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                _telemetry.OnCatalogPage(GarnetJournalPartitionClient.ProviderName, members.Length);
                foreach (var member in members)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!GarnetJournalKeys.TryDecodeJournalId((byte[])member!, out var journalId))
                    {
                        throw new InvalidOperationException("A Garnet journal bucket index contains an invalid journal id.");
                    }

                    if (range.Contains(journalId.Value))
                    {
                        _telemetry.OnCatalogEntry(GarnetJournalPartitionClient.ProviderName);
                        yield return new JournalCatalogEntry(journalId);
                    }
                }
            }
        }
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(nameof(GarnetJournalStorageProvider), _options.InitStage, onStart: StartAsync, onStop: StopAsync);
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConfigurationOptions configuration;
        if (!string.IsNullOrWhiteSpace(_options.DataDirectory))
        {
            _host = GarnetJournalPartitionHost.Start(
                new GarnetJournalPartitionSettings
                {
                    DataDirectory = _options.DataDirectory,
                    ListenEndPoint = _options.ListenEndPoint,
                    LogMemorySize = _options.LogMemorySize,
                    IndexMemorySize = _options.IndexMemorySize,
                    CheckpointAofSize = _options.CheckpointAofSize,
                },
                _loggerFactory);
            var endPoint = _host.EndPoint;
            if (endPoint.Address.Equals(System.Net.IPAddress.Any) || endPoint.Address.Equals(System.Net.IPAddress.IPv6Any))
            {
                endPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, endPoint.Port);
            }

            configuration = new ConfigurationOptions { EndPoints = { endPoint } };
        }
        else
        {
            configuration = _options.ConfigurationOptions?.Clone()
                ?? throw new InvalidOperationException($"{nameof(GarnetJournalStorageOptions)}.{nameof(GarnetJournalStorageOptions.ConfigurationOptions)} is required.");
        }

        // Procedure responses are RESP2 arrays of bulk strings.
        configuration.Protocol = RedisProtocol.Resp2;
        configuration.AbortOnConnectFail = false;
        _connection = await ConnectionMultiplexer.ConnectAsync(configuration).WaitAsync(cancellationToken).ConfigureAwait(false);
        var client = new GarnetJournalPartitionClient(
            _connection.GetDatabase(),
            _options.BucketCount,
            _options.OperationTimeout,
            _telemetry,
            _loggerFactory?.CreateLogger<GarnetJournalStorageProvider>());
        await client.EnsureSinglePartitionInitializedAsync(cancellationToken).ConfigureAwait(false);
        _client = client;
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        _client = null;
        if (_connection is { } connection)
        {
            _connection = null;
            await connection.CloseAsync().ConfigureAwait(false);
            connection.Dispose();
        }

        _host?.Dispose();
        _host = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
