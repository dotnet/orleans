using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class S3JournalStorageProvider : ILifecycleParticipant<ISiloLifecycle>, IJournalStorageProvider, IJournalStorageCatalog
{
    private static readonly TimeSpan MaximumTaskDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private readonly S3JournalStorageOptions _options;
    private readonly S3JournalStorage.S3JournalStorageShared _shared;
    private IAmazonS3? _client;

    public S3JournalStorageProvider(
        IOptions<S3JournalStorageOptions> options,
        IOptions<JournaledStateManagerOptions> managerOptions,
        IServiceProvider serviceProvider,
        ILogger<S3JournalStorage> logger,
        S3JournalStorageInstruments? instruments = null)
    {
        _options = options.Value;
        ValidateOptions(_options);
        var journalFormatKey = ValidateJournalFormatKey(managerOptions.Value.JournalFormatKey);
        var journalFormat = GetJournalFormat(serviceProvider, journalFormatKey);
        _shared = new S3JournalStorage.S3JournalStorageShared(
            logger,
            options,
            instruments ?? S3JournalStorageInstruments.CreateForDirectConstruction(),
            mimeType: journalFormat.MimeType,
            journalFormatKey);
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return new S3JournalStorage(_shared, GetClient(), journalId);
    }

    public async IAsyncEnumerable<JournalId> ListAsync(
        ListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var range = new JournalCatalogRange(options);
        if (range.IsEmpty)
        {
            yield break;
        }

        var ordered = _options.UseOrderedListing;
        var identityMapping = _options.UsesDefaultObjectKey;
        var listingPrefix = range.ListingPrefix;
        if (!identityMapping
            && (string.IsNullOrWhiteSpace(listingPrefix)
                || _options.GetObjectKeyPrefix is null && range.Prefix is null))
        {
            // A common prefix inferred from bounds can be whitespace, which cannot be represented
            // as a JournalId for a custom mapper. Bounds alone also do not require a prefix mapper.
            listingPrefix = null;
        }

        var objectKeyPrefix = _options.GetObjectKeyPrefixForCatalog(listingPrefix);
        if (!ordered && objectKeyPrefix is not null)
        {
            var directoryEnd = objectKeyPrefix.LastIndexOf('/') + 1;
            objectKeyPrefix = directoryEnd == 0 ? null : objectKeyPrefix[..directoryEnd];
        }

        var startAfter = ordered && identityMapping && range.LowerBound is { } lowerBound && System.Text.Ascii.IsValid(lowerBound)
            ? lowerBound : null;
        var maxObjectKey = ordered && identityMapping ? range.GetUpperBoundForSuffix("/wal") : null;
        var client = GetClient();
        var bucketName = GetBucketName();
        string? continuationToken = null;

        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    Prefix = objectKeyPrefix,
                    StartAfter = startAfter,
                    MaxKeys = 1000,
                    ContinuationToken = continuationToken,
                },
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in response.S3Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (maxObjectKey is not null && string.CompareOrdinal(item.Key, maxObjectKey) > 0)
                {
                    yield break;
                }

                if (TryGetJournalId(item.Key, range, out var id))
                {
                    yield return id;
                }
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        cancellationToken.ThrowIfCancellationRequested();
    }

    private bool TryGetJournalId(string objectKey, JournalCatalogRange range, out JournalId journalId)
    {
        if (objectKey.EndsWith("/wal", StringComparison.Ordinal)
            && _options.TryParseJournalId(objectKey[..^"/wal".Length]) is { IsDefault: false } id
            && range.Contains(id.Value))
        {
            var journalObjectKey = _options.GetObjectKeyForJournal(id);
            var canonicalWalObjectKey = S3JournalStorageOptions.GetWalObjectKeyForJournal(id, journalObjectKey);
            if (string.Equals(objectKey, canonicalWalObjectKey, StringComparison.Ordinal))
            {
                journalId = id;
                return true;
            }
        }

        journalId = default;
        return false;
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(S3JournalStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: InitializeAsync,
            onStop: CloseAsync);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var client = await _options.GetCreateClient()(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The configured S3 client factory returned null.");
        try
        {
            await EnsureBucketAsync(client, GetBucketName(), _options.CreateBucketIfNotExists, cancellationToken).ConfigureAwait(false);
            _client = client;
        }
        catch
        {
            if (!_options.IsClientExternallyOwned)
            {
                client.Dispose();
            }

            throw;
        }
    }

    internal Task CloseAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        _client = null;
        if (client is not null && !_options.IsClientExternallyOwned)
        {
            client.Dispose();
        }

        return Task.CompletedTask;
    }

    private IAmazonS3 GetClient()
        => _client ?? throw new InvalidOperationException(
            $"{nameof(S3JournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");

    private string GetBucketName()
    {
        var bucketName = _options.BucketName;
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new InvalidOperationException($"{nameof(S3JournalStorageOptions.BucketName)} must be configured.");
        }

        return bucketName;
    }

    private static async Task EnsureBucketAsync(IAmazonS3 client, string bucketName, bool createIfMissing, CancellationToken cancellationToken)
    {
        try
        {
            await client.HeadBucketAsync(new HeadBucketRequest { BucketName = bucketName }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.NotFound && createIfMissing)
        {
            try
            {
                await client.PutBucketAsync(new PutBucketRequest { BucketName = bucketName }, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception createException) when (createException.StatusCode is HttpStatusCode.Conflict)
            {
                await client.HeadBucketAsync(new HeadBucketRequest { BucketName = bucketName }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateOptions(S3JournalStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.GetObjectKey);
        ArgumentNullException.ThrowIfNull(options.TryParseJournalId);
        if (options.MaxMetadataOnlyConflictRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(S3JournalStorageOptions.MaxMetadataOnlyConflictRetries)} must be non-negative.");
        }

        if (options.MetadataOnlyConflictInitialBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(S3JournalStorageOptions.MetadataOnlyConflictInitialBackoff)} must be non-negative.");
        }

        if (options.MetadataOnlyConflictMaxBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(S3JournalStorageOptions.MetadataOnlyConflictMaxBackoff)} must be non-negative.");
        }

        if (options.MetadataOnlyConflictInitialBackoff > MaximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"{nameof(S3JournalStorageOptions.MetadataOnlyConflictInitialBackoff)} must not exceed {MaximumTaskDelay}.");
        }

        if (options.MetadataOnlyConflictMaxBackoff > MaximumTaskDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"{nameof(S3JournalStorageOptions.MetadataOnlyConflictMaxBackoff)} must not exceed {MaximumTaskDelay}.");
        }
    }

    private static IJournalFormat GetJournalFormat(IServiceProvider serviceProvider, string journalFormatKey)
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

        return journalFormat;
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
