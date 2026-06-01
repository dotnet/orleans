using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.Journaling;

internal sealed class RedisJournalStorageProvider : IJournalStorageProvider, IJournalStorageCatalog, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly RedisJournalStorageOptions _options;
    private readonly string _keyPrefix;
    private readonly string _journalFormatKey;
    private readonly RedisKey _catalogKey;
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
        _catalogKey = RedisJournalStorage.GetCatalogKey(_keyPrefix);
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var keyName = _options.GetKeyNameForJournal(journalId);
        return new RedisJournalStorage(GetDatabase(), _catalogKey, _keyPrefix, keyName, _journalFormatKey, _options, journalId);
    }

    public async IAsyncEnumerable<JournalId> ListAsync(
        JournalId prefix = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var database = GetDatabase();
        var members = await database.SetMembersAsync(_catalogKey).ConfigureAwait(false);
        var journalIds = new List<JournalId>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = member.ToString();
            if (!TryParseJournalId(value, out var journalId) || !prefix.IsPrefixOf(journalId))
            {
                continue;
            }

            var metadataKey = RedisJournalStorage.GetMetadataKey(_keyPrefix, _options.GetKeyNameForJournal(journalId));
            if (await database.KeyExistsAsync(metadataKey).ConfigureAwait(false))
            {
                journalIds.Add(journalId);
            }
        }

        journalIds.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
        foreach (var journalId in journalIds)
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

    private static bool TryParseJournalId(string value, out JournalId journalId)
    {
        try
        {
            journalId = new JournalId(value);
            return true;
        }
        catch (ArgumentException)
        {
            journalId = default;
            return false;
        }
    }

    private static string ValidateJournalFormatKey(string? journalFormatKey)
    {
        if (string.IsNullOrWhiteSpace(journalFormatKey))
        {
            throw new InvalidOperationException("The configured journal format key must be non-empty.");
        }

        return journalFormatKey;
    }
}
