using System.Buffers;

namespace Orleans.Journaling;

internal sealed class InstrumentedJournalStorage(
    IJournalStorage inner,
    string provider,
    JournalStorageTelemetry telemetry) : IJournalStorage
{
    public bool IsCompactionRequested => inner.IsCompactionRequested;

    public ValueTask<bool> CreateIfNotExistsAsync(IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        => telemetry.OperationsEnabled
            ? TrackAsync("create", () => inner.CreateIfNotExistsAsync(metadata, cancellationToken),
                static created => created ? JournalStorageTelemetry.Ok : JournalStorageTelemetry.AlreadyExists)
            : inner.CreateIfNotExistsAsync(metadata, cancellationToken);

    public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
        => telemetry.OperationsEnabled
            ? TrackAsync("get_metadata", () => inner.GetMetadataAsync(cancellationToken),
                static metadata => metadata is null ? JournalStorageTelemetry.NotFound : JournalStorageTelemetry.Ok)
            : inner.GetMetadataAsync(cancellationToken);

    public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
        IReadOnlyDictionary<string, string>? set = null,
        IEnumerable<string>? remove = null,
        string? expectedETag = null,
        CancellationToken cancellationToken = default)
        => telemetry.OperationsEnabled
            ? TrackAsync("update_metadata", () => inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken),
                static metadata => metadata is null ? JournalStorageTelemetry.NotApplied : JournalStorageTelemetry.Ok)
            : inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);

    public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        => telemetry.OperationsEnabled
            ? TrackAsync("append", () => inner.AppendAsync(value, cancellationToken), value.Length)
            : inner.AppendAsync(value, cancellationToken);

    public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        => telemetry.OperationsEnabled
            ? TrackAsync("replace", () => inner.ReplaceAsync(value, cancellationToken), value.Length)
            : inner.ReplaceAsync(value, cancellationToken);

    public ValueTask DeleteAsync(CancellationToken cancellationToken)
        => telemetry.OperationsEnabled
            ? TrackAsync("delete", () => inner.DeleteAsync(cancellationToken))
            : inner.DeleteAsync(cancellationToken);

    public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        => telemetry.OperationsEnabled ? TrackReadAsync(consumer, cancellationToken) : inner.ReadAsync(consumer, cancellationToken);

    private async ValueTask TrackReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        var start = telemetry.GetTimestamp();
        var status = JournalStorageTelemetry.Ok;
        CountingConsumer? counting = null;
        try
        {
            ArgumentNullException.ThrowIfNull(consumer);
            counting = new CountingConsumer(consumer);
            await inner.ReadAsync(counting, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            status = JournalStorageTelemetry.GetExceptionStatus(exception);
            throw;
        }
        finally
        {
            telemetry.OnOperationCompleted(provider, "read", telemetry.GetElapsedTime(start), status, counting?.Bytes ?? 0);
        }
    }

    private async ValueTask<T> TrackAsync<T>(string operation, Func<ValueTask<T>> call, Func<T, string> classify)
    {
        var start = telemetry.GetTimestamp();
        var status = JournalStorageTelemetry.Error;
        try
        {
            var result = await call().ConfigureAwait(false);
            status = classify(result);
            return result;
        }
        catch (Exception exception)
        {
            status = JournalStorageTelemetry.GetExceptionStatus(exception);
            throw;
        }
        finally
        {
            telemetry.OnOperationCompleted(provider, operation, telemetry.GetElapsedTime(start), status);
        }
    }

    private async ValueTask TrackAsync(string operation, Func<ValueTask> call, long bytes = 0)
    {
        var start = telemetry.GetTimestamp();
        var status = JournalStorageTelemetry.Ok;
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            status = JournalStorageTelemetry.GetExceptionStatus(exception);
            throw;
        }
        finally
        {
            telemetry.OnOperationCompleted(provider, operation, telemetry.GetElapsedTime(start), status,
                status == JournalStorageTelemetry.Ok ? bytes : 0);
        }
    }

    private sealed class CountingConsumer(IJournalStorageConsumer innerConsumer) : IJournalStorageConsumer
    {
        public long Bytes { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            Bytes += buffer.Length;
            innerConsumer.Read(buffer, metadata);
        }
    }
}
