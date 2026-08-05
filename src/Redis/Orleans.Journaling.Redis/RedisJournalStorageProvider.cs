using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.Journaling;

internal sealed class RedisJournalStorageProvider : IJournalStorageProvider, IJournalStorageCatalog, ILifecycleParticipant<ISiloLifecycle>
{
    private const int JournalIdReadBatchSize = 128;
    private const int ScanPageSize = 250;
    private const string ReadJournalIdScript =
        """
        local journalId = redis.call('HGET', KEYS[1], '$journal-id')
        if journalId ~= false then
            return { 1, journalId }
        end
        if redis.call('EXISTS', KEYS[1]) == 0 then
            return { 0 }
        end
        return { -1 }
        """;

    private static readonly RedisValue[] NoValues = [];

    private readonly RedisJournalStorageOptions _options;
    private readonly string _keyPrefix;
    private readonly string _journalFormatKey;
    private IConnectionMultiplexer? _connection;
    private IDatabase? _database;
    private bool _isSharedConnection;

    public RedisJournalStorageProvider(
        IOptions<RedisJournalStorageOptions> options,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<JournaledStateManagerOptions> managerOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clusterOptions);
        ArgumentNullException.ThrowIfNull(managerOptions);

        _options = options.Value;
        _keyPrefix = _options.GetKeyPrefix(clusterOptions.Value.ServiceId);
        _journalFormatKey = ValidateJournalFormatKey(managerOptions.Value.JournalFormatKey);
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var keyName = _options.GetKeyNameForJournal(journalId);
        return new RedisJournalStorage(GetDatabase(), _keyPrefix, keyName, _journalFormatKey, _options, journalId);
    }

    public async IAsyncEnumerable<JournalId> ListAsync(
        JournalId prefix = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var connection = GetConnection();
        var database = GetDatabase();
        var metadataKeys = new HashSet<RedisKey>();
        var pattern = RedisJournalStorage.GetMetadataKeyPattern(_keyPrefix);
        var scannedServer = false;
        foreach (var endpoint in connection.GetEndPoints())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var server = connection.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            if (!server.IsConnected)
            {
                throw new InvalidOperationException(
                    $"Redis primary server '{endpoint}' is not connected, so journal discovery cannot produce a complete result.");
            }

            scannedServer = true;
            await foreach (var metadataKey in server.KeysAsync(
                database.Database,
                pattern,
                pageSize: ScanPageSize).WithCancellation(cancellationToken))
            {
                metadataKeys.Add(metadataKey);
            }
        }

        if (!scannedServer)
        {
            throw new InvalidOperationException("No connected primary Redis servers are available for journal discovery.");
        }

        var journalIds = new HashSet<JournalId>();
        foreach (var batch in metadataKeys.Chunk(JournalIdReadBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reads = new Task<RedisValue>[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                reads[i] = database.HashGetAsync(batch[i], RedisJournalStorage.JournalIdMetadataKey);
            }

            var values = await Task.WhenAll(reads).ConfigureAwait(false);
            for (var i = 0; i < values.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = values[i];
                if (value.IsNullOrEmpty)
                {
                    var result = (RedisResult[]?)await database.ScriptEvaluateAsync(
                        ReadJournalIdScript,
                        [batch[i]],
                        NoValues).ConfigureAwait(false);
                    if (result is not { Length: > 0 })
                    {
                        throw new InvalidOperationException("The Redis journal discovery script returned an invalid response.");
                    }

                    var status = (int)result[0];
                    if (status == 0)
                    {
                        continue;
                    }

                    if (status != 1 || result.Length != 2)
                    {
                        throw new InvalidOperationException(
                            $"Redis journal metadata '{batch[i]}' is missing '{RedisJournalStorage.JournalIdMetadataKey}'.");
                    }

                    value = (RedisValue)result[1];
                }

                if (!RedisJournalStorage.TryParseJournalId(value.ToString(), out var journalId))
                {
                    throw new InvalidOperationException(
                        $"Redis journal metadata '{batch[i]}' contains an invalid '{RedisJournalStorage.JournalIdMetadataKey}' value.");
                }

                if (prefix.IsPrefixOf(journalId))
                {
                    journalIds.Add(journalId);
                }
            }
        }

        foreach (var journalId in journalIds.OrderBy(static journalId => journalId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return journalId;
        }
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(RedisJournalStorageProvider),
            _options.InitStage,
            onStart: Initialize,
            onStop: Close);
    }

    private async Task Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (multiplexer, isShared) = await _options.CreateMultiplexer(_options).ConfigureAwait(false);
        _connection = multiplexer ?? throw new InvalidOperationException("The Redis journal storage multiplexer factory returned null.");
        _isSharedConnection = isShared;
        _database = _connection.GetDatabase();
    }

    private async Task Close(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connection is null || _isSharedConnection)
        {
            return;
        }

        await _connection.CloseAsync().ConfigureAwait(false);
        _connection.Dispose();
        _connection = null;
        _database = null;
    }

    private IDatabase GetDatabase()
        => _database ?? throw new InvalidOperationException(
            $"{nameof(RedisJournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");

    private IConnectionMultiplexer GetConnection()
        => _connection ?? throw new InvalidOperationException(
            $"{nameof(RedisJournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");

    private static string ValidateJournalFormatKey(string? journalFormatKey)
    {
        if (string.IsNullOrWhiteSpace(journalFormatKey))
        {
            throw new InvalidOperationException("The configured journal format key must be non-empty.");
        }

        return journalFormatKey;
    }
}
