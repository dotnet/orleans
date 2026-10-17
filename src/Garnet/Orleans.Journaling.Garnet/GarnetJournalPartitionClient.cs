using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Invokes journal procedures on a partition.
/// </summary>
/// <remarks>
/// An operation whose partition is unreachable, or whose outcome is unknown because its reply was lost, is retried
/// with the same arguments until <see cref="GarnetJournalStorageOptions.OperationTimeout"/> elapses. Retries are safe
/// because each mutation carries the ETag it writes, and a procedure which finds that ETag already in place reports
/// the earlier attempt's success.
/// </remarks>
internal sealed class GarnetJournalPartitionClient(
    IDatabase database,
    int bucketCount,
    TimeSpan operationTimeout,
    JournalStorageTelemetry? telemetry = null,
    ILogger? logger = null)
{
    internal const string ProviderName = "garnet";
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(1);
    private const string TransactionFailedMessage = "ERR Transaction failed.";
    private const string BucketCountField = "bucket-count";
    private const string InitializedField = "initialized";
    private readonly JournalStorageTelemetry _telemetry = telemetry ?? JournalStorageTelemetry.CreateForDirectConstruction();

    public IDatabase Database { get; } = database;

    public int BucketCount { get; } = bucketCount;

    public async ValueTask<RedisResult[]> ExecuteAsync(
        string procedure,
        object[] args,
        CancellationToken cancellationToken,
        StrongBox<bool>? ambiguous = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var backoff = TimeSpan.FromMilliseconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = operationTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Garnet journal procedure {procedure} did not complete within {operationTimeout}.");
            }

            string reason;
            Exception exception;
            try
            {
                // Bound each attempt by the remaining budget, so a stalled or late reply cannot outlive the operation.
                var result = await Database.ExecuteAsync(procedure, args).WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                return (RedisResult[]?)result is { Length: > 0 } values
                    ? values
                    : throw new InvalidOperationException($"Garnet journal procedure {procedure} returned an invalid response.");
            }
            catch (RedisConnectionException ex)
            {
                reason = "connection";
                exception = ex;
            }
            catch (RedisTimeoutException ex)
            {
                reason = "timeout";
                exception = ex;
            }
            catch (TimeoutException ex) when (!cancellationToken.IsCancellationRequested)
            {
                reason = "deadline";
                exception = ex;
            }
            catch (RedisServerException ex) when (string.Equals(ex.Message, TransactionFailedMessage, StringComparison.Ordinal))
            {
                // The transaction did not run, for example because its locks were not acquired in time.
                reason = "transaction";
                exception = ex;
            }

            // Only a transaction which did not run has a known outcome; any other failure may follow a commit.
            if (ambiguous is not null && reason != "transaction")
            {
                ambiguous.Value = true;
            }

            if (stopwatch.Elapsed + backoff >= operationTimeout)
            {
                throw new TimeoutException(
                    $"Garnet journal procedure {procedure} did not complete within {operationTimeout}.", exception);
            }

            _telemetry.OnRetry(ProviderName, reason);
            logger?.LogDebug(exception, "Retrying Garnet journal procedure {Procedure} after {Reason} failure.", procedure, reason);
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>
    /// Initializes a new partition so that it owns every bucket, or verifies an initialized partition's bucket count.
    /// </summary>
    /// <remarks>
    /// The bucket count is claimed atomically before any bucket opens, so concurrent initializations with different
    /// counts cannot both succeed. Opening a bucket is idempotent, and the partition is marked initialized only after
    /// every bucket is open, so a crash during initialization is repaired by the next initialization.
    /// </remarks>
    public async Task EnsureSinglePartitionInitializedAsync(CancellationToken cancellationToken)
    {
        await Database.HashSetAsync(GarnetJournalKeys.PartitionKey, BucketCountField, BucketCount, When.NotExists)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = await Database.HashGetAllAsync(GarnetJournalKeys.PartitionKey).WaitAsync(cancellationToken).ConfigureAwait(false);
        var bucketCountValue = descriptor.FirstOrDefault(entry => entry.Name == BucketCountField).Value;
        if (!int.TryParse(bucketCountValue.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var claimed) || claimed != BucketCount)
        {
            throw new InvalidOperationException(
                $"The Garnet journal partition was initialized with {bucketCountValue} buckets, but {BucketCount} are configured. The bucket count is fixed when a partition is initialized.");
        }

        if (descriptor.Any(entry => entry.Name == InitializedField))
        {
            return;
        }

        const int BatchSize = 256;
        var tasks = new List<Task<RedisResult[]>>(BatchSize);
        for (var start = 0; start < BucketCount; start += BatchSize)
        {
            tasks.Clear();
            for (var bucket = start; bucket < Math.Min(start + BatchSize, BucketCount); bucket++)
            {
                tasks.Add(ExecuteAsync(
                    GarnetJournalProtocol.Bucket,
                    [(RedisKey)GarnetJournalKeys.GetBucketKey(bucket), GarnetJournalProtocol.BucketOpenOperation, "0", GarnetJournalProtocol.NoSource, "1"],
                    cancellationToken).AsTask());
            }

            foreach (var result in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                if (GetStatus(result) != GarnetJournalProtocol.SuccessStatus)
                {
                    throw new InvalidOperationException(
                        $"A Garnet journal bucket could not be opened: it is {result.ElementAtOrDefault(1)} at version {result.ElementAtOrDefault(2)}.");
                }
            }
        }

        await Database.HashSetAsync(
            GarnetJournalKeys.PartitionKey,
            [new(InitializedField, "1"), new("schema-version", GarnetJournalProtocol.CurrentSchemaVersion)]).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static int GetStatus(RedisResult[] result)
        => int.TryParse(result[0].ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var status)
            ? status
            : throw new InvalidOperationException("A Garnet journal procedure returned an invalid status.");
}
