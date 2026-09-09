using System.Runtime.CompilerServices;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class AzureTableJournalStorageProvider : ILifecycleParticipant<ISiloLifecycle>, IJournalStorageProvider, IJournalStorageCatalog
{
    private static readonly string[] JournalIdSelect = [AzureTableJournalStorage.JournalIdPropertyName];

    private readonly AzureTableJournalStorageOptions _options;
    private readonly AzureTableJournalStorage.InitializedTableClientProvider _tableClientProvider = new();
    private readonly AzureTableJournalStorage.AzureTableJournalStorageShared _shared;

    public AzureTableJournalStorageProvider(
        IOptions<AzureTableJournalStorageOptions> options,
        IOptions<JournaledStateManagerOptions> managerOptions,
        IServiceProvider serviceProvider,
        ILogger<AzureTableJournalStorage> logger,
        AzureTableJournalStorageInstruments? instruments = null)
    {
        _options = options.Value;
        var journalFormatKey = ValidateJournalFormatKey(managerOptions.Value.JournalFormatKey);
        ValidateJournalFormat(serviceProvider, journalFormatKey);
        _shared = new AzureTableJournalStorage.AzureTableJournalStorageShared(
            logger,
            options,
            _tableClientProvider,
            instruments ?? AzureTableJournalStorageInstruments.CreateForDirectConstruction(),
            journalFormatKey);
    }

    private async Task Initialize(CancellationToken cancellationToken)
    {
        var createClient = _options.CreateClient
            ?? throw new InvalidOperationException(
                $"No Azure Table service client was configured. Set {nameof(AzureTableJournalStorageOptions.TableServiceClient)} " +
                $"or call {nameof(AzureTableJournalStorageOptions.ConfigureTableServiceClient)}.");
        var client = await createClient(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The configured Azure Table service client factory returned null.");
        var table = client.GetTableClient(_options.TableName);
        await table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
        _tableClientProvider.SetTableClient(table);
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return new AzureTableJournalStorage(_shared, journalId);
    }

    public async IAsyncEnumerable<JournalId> ListAsync(
        ListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = options?.Prefix ?? default;
        var maxId = options?.MaxId ?? default;
        var table = _tableClientProvider.GetTableClient();
        var filter = GetCatalogFilter(prefix, maxId);
        await foreach (var page in table.QueryAsync<TableEntity>(
            filter,
            maxPerPage: 1000,
            select: JournalIdSelect,
            cancellationToken: cancellationToken).AsPages(pageSizeHint: 1000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entity in page.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetJournalId(entity, out var journalId)
                    && prefix.IsPrefixOf(journalId)
                    && (maxId.IsDefault || string.CompareOrdinal(journalId.Value, maxId.Value) <= 0))
                {
                    yield return journalId;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string GetCatalogFilter(JournalId prefix, JournalId maxId)
    {
        var headers = TableClient.CreateQueryFilter($"RowKey eq {AzureTableJournalStorage.HeaderRowKey}");
        if (prefix.IsDefault)
        {
            // Legacy headers have no JournalId property. Without a prefix, their escaped partition
            // keys cannot safely be range-filtered by an arbitrary ordinal JournalId bound.
            return headers;
        }

        var descendantPrefix = prefix.Value + "/";
        var descendantEnd = prefix.Value + "0";
        var identities = TableClient.CreateQueryFilter(
            $"(JournalId eq {prefix.Value} or (JournalId ge {descendantPrefix} and JournalId lt {descendantEnd}))");
        if (!maxId.IsDefault)
        {
            identities += TableClient.CreateQueryFilter($" and JournalId le {maxId.Value}");
        }

        var partition = Uri.EscapeDataString(prefix.Value);
        var legacyStart = partition + "%2F";
        var legacyEnd = partition + "%2G";
        var legacy = TableClient.CreateQueryFilter(
            $"(PartitionKey eq {partition} or (PartitionKey ge {legacyStart} and PartitionKey lt {legacyEnd}))");
        if (!maxId.IsDefault
            && maxId.Value.StartsWith(descendantPrefix, StringComparison.Ordinal)
            && IsUnescapedSuffix(maxId.Value.AsSpan(descendantPrefix.Length)))
        {
            // For a shared encoded prefix and an unreserved ASCII upper-bound suffix, escaping
            // any lesser suffix can only move it earlier ('%' sorts before every unreserved char).
            // This covers time-prefixed shard bounds without assuming general URI order preservation.
            var legacyMax = Uri.EscapeDataString(maxId.Value);
            legacy += TableClient.CreateQueryFilter($" and PartitionKey le {legacyMax}");
        }

        // The OR also preserves custom partition mappings with canonical JournalId properties.
        // Table queries cannot test for an absent property, so the legacy arm can over-select.
        return $"{headers} and (({identities}) or ({legacy}))";
    }

    private static bool IsUnescapedSuffix(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.' or '_' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetJournalId(TableEntity entity, out JournalId journalId)
    {
        if (entity.GetString(AzureTableJournalStorage.JournalIdPropertyName) is { } journalIdValue)
        {
            return TryParseJournalId(journalIdValue, out journalId);
        }

        // Legacy headers can only be listed when they use the reversible default partition mapping.
        var decodedPartitionKey = Uri.UnescapeDataString(entity.PartitionKey);
        if (TryParseJournalId(decodedPartitionKey, out journalId)
            && string.Equals(
                Uri.EscapeDataString(journalId.Value),
                entity.PartitionKey,
                StringComparison.Ordinal))
        {
            return true;
        }

        journalId = default;
        return false;
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(AzureTableJournalStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);
    }

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

    private static void ValidateJournalFormat(IServiceProvider serviceProvider, string journalFormatKey)
    {
        var journalFormat = serviceProvider.GetKeyedService<IJournalFormat>(journalFormatKey);
        if (journalFormat is null)
        {
            throw new InvalidOperationException(
                $"Journal format key '{journalFormatKey}' requires keyed service '{typeof(IJournalFormat).FullName}', but none was registered.");
        }

        if (!string.Equals(journalFormat.FormatKey, journalFormatKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Journal format key '{journalFormatKey}' resolved format '{journalFormat.GetType().FullName}', but its {nameof(IJournalFormat.FormatKey)} is '{journalFormat.FormatKey}'. " +
                "Register the journal format using the same key it reports.");
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
