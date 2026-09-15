using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class AzureTableJournalStorageProvider : ILifecycleParticipant<ISiloLifecycle>, IJournalStorageProvider, IJournalStorageCatalog
{
    private const int MaximumDefaultJournalIdLength = 512;
    private static readonly string[] JournalIdSelect = [AzureTableJournalStorage.JournalIdPropertyName];
    private static readonly string[] JournalMetadataSelect =
    [
        AzureTableJournalStorage.JournalIdPropertyName,
        AzureTableJournalStorage.FormatPropertyName,
        AzureTableJournalStorage.MetadataPropertyName,
        nameof(TableEntity.Timestamp),
    ];

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

    public async IAsyncEnumerable<JournalCatalogEntry> ListAsync(
        ListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var range = new JournalCatalogRange(options);
        if (range.IsEmpty
            || (_options.UsesDefaultPartitionKey
                && range.Prefix is { } prefix
                && (prefix.Length > MaximumDefaultJournalIdLength
                    || prefix.AsSpan().IndexOfAnyExceptInRange(' ', '~') >= 0)))
        {
            yield break;
        }

        var table = _tableClientProvider.GetTableClient();
        var filter = GetCatalogFilter(range);
        await foreach (var page in table.QueryAsync<TableEntity>(
            filter,
            maxPerPage: 1000,
            select: range.IncludeMetadata ? JournalMetadataSelect : JournalIdSelect,
            cancellationToken: cancellationToken).AsPages(pageSizeHint: 1000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entity in page.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetJournalId(entity, out var journalId)
                    && range.Contains(journalId.Value))
                {
                    yield return new(
                        journalId,
                        range.IncludeMetadata
                            ? AzureTableJournalStorage.CreateJournalMetadataSnapshot(entity.ETag, entity)
                            : null);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private string GetCatalogFilter(JournalCatalogRange range)
    {
        var filter = TableClient.CreateQueryFilter($"RowKey eq {AzureTableJournalStorage.HeaderRowKey}");
        if (_options.UsesDefaultPartitionKey)
        {
            if (range.LowerBound is { } lowerBound)
            {
                var lowerKey = GetPartitionKeyBound(lowerBound);
                // A longer bound sorts after its longest possible stored prefix.
                filter += lowerBound.Length > MaximumDefaultJournalIdLength
                    ? TableClient.CreateQueryFilter($" and PartitionKey gt {lowerKey}")
                    : TableClient.CreateQueryFilter($" and PartitionKey ge {lowerKey}");
            }

            if (range.MaxId is { } maxId)
            {
                var upperKey = GetPartitionKeyBound(maxId);
                filter += TableClient.CreateQueryFilter($" and PartitionKey le {upperKey}");
            }

            if (range.Prefix is { } prefix)
            {
                var prefixKey = AzureTableJournalStorageOptions.EncodePartitionKey(prefix);
                // At the length limit only the exact id can match; shorter prefixes can have descendants.
                filter += prefix.Length == MaximumDefaultJournalIdLength
                    ? TableClient.CreateQueryFilter($" and PartitionKey le {prefixKey}")
                    : TableClient.CreateQueryFilter($" and PartitionKey lt {prefixKey + "G"}");
            }
        }
        else
        {
            // Ordinal UTF-16 boundaries can contain unpaired surrogates. Keep those constraints local.
            if (range.LowerBound is { } lowerBound && IsWellFormedUnicode(lowerBound))
            {
                filter += TableClient.CreateQueryFilter($" and JournalId ge {lowerBound}");
            }

            if (range.UpperBound is { } upperBound && IsWellFormedUnicode(upperBound))
            {
                filter += range.Contains(upperBound)
                    ? TableClient.CreateQueryFilter($" and JournalId le {upperBound}")
                    : TableClient.CreateQueryFilter($" and JournalId lt {upperBound}");
            }
        }

        return filter;
    }

    private static string GetPartitionKeyBound(string value)
    {
        value = value[..Math.Min(value.Length, MaximumDefaultJournalIdLength)];
        var index = value.AsSpan().IndexOfAnyExceptInRange(' ', '~');
        if (index < 0)
        {
            return AzureTableJournalStorageOptions.EncodePartitionKey(value);
        }

        // No stored id can equal an unsupported bound. At its first unsupported code unit,
        // 1F sorts after the exact prefix but before any descendant; 7F sorts after all descendants.
        return AzureTableJournalStorageOptions.EncodePartitionKey(value[..index])
            + (value[index] < ' ' ? "1F" : "7F");
    }

    private static bool IsWellFormedUnicode(ReadOnlySpan<char> value)
    {
        while (!value.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(value, out _, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            value = value[consumed..];
        }

        return true;
    }

    private static bool TryGetJournalId(TableEntity entity, out JournalId journalId)
    {
        if (entity.TryGetValue(AzureTableJournalStorage.JournalIdPropertyName, out var value)
            && value is string journalIdValue)
        {
            return TryParseJournalId(journalIdValue, out journalId);
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
