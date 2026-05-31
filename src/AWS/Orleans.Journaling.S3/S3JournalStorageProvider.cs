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
    private readonly S3JournalStorageOptions _options;
    private readonly S3JournalStorage.S3JournalStorageShared _shared;
    private IAmazonS3? _client;

    public S3JournalStorageProvider(
        IOptions<S3JournalStorageOptions> options,
        IOptions<JournaledStateManagerOptions> managerOptions,
        IServiceProvider serviceProvider,
        ILogger<S3JournalStorage> logger)
    {
        _options = options.Value;
        ValidateOptions(_options);
        var journalFormatKey = ValidateJournalFormatKey(managerOptions.Value.JournalFormatKey);
        var journalFormat = GetJournalFormat(serviceProvider, journalFormatKey);
        _shared = new S3JournalStorage.S3JournalStorageShared(logger, options, mimeType: journalFormat.MimeType, journalFormatKey);
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
        JournalId prefix = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        var bucketName = GetBucketName();
        var objectPrefix = prefix.IsDefault ? null : prefix.Value;
        var journalIds = new List<JournalId>();
        string? continuationToken = null;

        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    Prefix = objectPrefix,
                    ContinuationToken = continuationToken,
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var item in response.S3Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!item.Key.EndsWith("/wal", StringComparison.Ordinal))
                {
                    continue;
                }

                var storageIdValue = item.Key[..^"/wal".Length];
                var journalId = _options.TryParseJournalId(storageIdValue);
                if (journalId is { } id && prefix.IsPrefixOf(id))
                {
                    journalIds.Add(id);
                }
            }

            continuationToken = response.IsTruncated.GetValueOrDefault() ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        foreach (var journalId in journalIds.OrderBy(static journalId => journalId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return journalId;
        }
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(S3JournalStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: InitializeAsync);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _client = await _options.GetCreateClient()(cancellationToken).ConfigureAwait(false);
        await EnsureBucketAsync(_client, GetBucketName(), _options.CreateBucketIfNotExists, cancellationToken).ConfigureAwait(false);
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
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucketName }, cancellationToken).ConfigureAwait(false);
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

