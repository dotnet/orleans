using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Orleans.Storage;

namespace Orleans.Journaling;

internal sealed class JournalStorageTelemetry
{
    internal const string S3 = "s3";
    internal const string AzureBlob = "azure_blob";
    internal const string AzureTable = "azure_table";
    internal const string Redis = "redis";
    internal const string Volatile = "volatile";
    internal const string Ok = "ok";
    internal const string Error = "error";
    internal const string Canceled = "canceled";
    internal const string Conflict = "conflict";
    internal const string NotFound = "not_found";
    internal const string NotApplied = "not_applied";
    internal const string AlreadyExists = "already_exists";
    internal const string Disposed = "disposed";

    private static readonly Lazy<JournalStorageTelemetry> Direct = new(
        static () => new(new Meter("Microsoft.Orleans"), TimeProvider.System));
    private readonly TimeProvider _clock;
    private readonly Counter<long> _operations;
    private readonly Histogram<double> _operationDuration;
    private readonly Counter<long> _operationBytes;
    private readonly Counter<long> _catalogEntries;
    private readonly Counter<long> _apiCalls;
    private readonly Histogram<double> _apiDuration;
    private readonly Counter<long> _apiItems;
    private readonly Counter<long> _retries;

    internal JournalStorageTelemetry(OrleansInstruments instruments, TimeProvider? clock = null)
        : this(instruments.Meter, clock ?? TimeProvider.System)
    {
    }

    private JournalStorageTelemetry(Meter meter, TimeProvider clock)
    {
        _clock = clock;
        _operations = meter.CreateCounter<long>("orleans-journaling-provider-operations");
        _operationDuration = meter.CreateHistogram<double>("orleans-journaling-provider-operation-duration", "ms");
        _operationBytes = meter.CreateCounter<long>("orleans-journaling-provider-operation-bytes", "bytes");
        _catalogEntries = meter.CreateCounter<long>("orleans-journaling-provider-catalog-entries");
        _apiCalls = meter.CreateCounter<long>("orleans-journaling-provider-api-calls");
        _apiDuration = meter.CreateHistogram<double>("orleans-journaling-provider-api-call-duration", "ms");
        _apiItems = meter.CreateCounter<long>("orleans-journaling-provider-api-items");
        _retries = meter.CreateCounter<long>("orleans-journaling-provider-retries");
    }

    internal static JournalStorageTelemetry CreateForDirectConstruction() => Direct.Value;
    internal bool OperationsEnabled => _operations.Enabled || _operationDuration.Enabled || _operationBytes.Enabled;
    internal bool ApiEnabled => _apiCalls.Enabled || _apiDuration.Enabled || _apiItems.Enabled;
    internal long GetTimestamp() => _clock.GetTimestamp();
    internal TimeSpan GetElapsedTime(long start) => _clock.GetElapsedTime(start);

    internal void OnOperationCompleted(string provider, string operation, TimeSpan elapsed, string status, long bytes = 0)
    {
        if (!OperationsEnabled)
        {
            return;
        }

        var tags = new TagList { { "provider", provider }, { "operation", operation }, { "status", status } };
        _operations.Add(1, tags);
        _operationDuration.Record(elapsed.TotalMilliseconds, tags);
        if (bytes > 0)
        {
            _operationBytes.Add(bytes, tags);
        }
    }

    internal void OnApiCallCompleted(string provider, string api, TimeSpan elapsed, string status, long items = 0)
    {
        if (!ApiEnabled)
        {
            return;
        }

        var tags = new TagList { { "provider", provider }, { "api", api }, { "status", status } };
        _apiCalls.Add(1, tags);
        _apiDuration.Record(elapsed.TotalMilliseconds, tags);
        if (items > 0)
        {
            _apiItems.Add(items, tags);
        }
    }

    internal void OnRetry(string provider, string reason)
    {
        if (_retries.Enabled)
        {
            _retries.Add(1, new TagList { { "provider", provider }, { "reason", reason } });
        }
    }

    internal static string GetExceptionStatus(Exception exception) => exception switch
    {
        OperationCanceledException => Canceled,
        TimeoutException => "timeout",
        InconsistentStateException => Conflict,
        _ => Error
    };

    internal static string GetHttpStatus(int status) => status switch
    {
        >= 200 and < 300 => Ok,
        404 => NotFound,
        408 or 504 => "timeout",
        409 or 412 => Conflict,
        429 => "throttled",
        503 => "unavailable",
        _ => Error
    };

    internal Task<T> TrackApiCallAsync<T>(
        string provider,
        string api,
        Func<Task<T>> call,
        Func<Exception, string>? classifyException = null,
        Func<T, string>? classifyResult = null,
        Func<T, long>? countItems = null)
        => ApiEnabled ? TrackApiCallCoreAsync(provider, api, call, classifyException, classifyResult, countItems) : call();

    private async Task<T> TrackApiCallCoreAsync<T>(
        string provider,
        string api,
        Func<Task<T>> call,
        Func<Exception, string>? classifyException,
        Func<T, string>? classifyResult,
        Func<T, long>? countItems)
    {
        var start = GetTimestamp();
        try
        {
            var result = await call().ConfigureAwait(false);
            var status = classifyResult?.Invoke(result) ?? Ok;
            var items = countItems?.Invoke(result) ?? 0;
            OnApiCallCompleted(provider, api, GetElapsedTime(start), status, items);
            return result;
        }
        catch (Exception exception)
        {
            OnApiCallCompleted(provider, api, GetElapsedTime(start), classifyException?.Invoke(exception) ?? GetExceptionStatus(exception));
            throw;
        }
    }

    internal Task TrackApiCallAsync(string provider, string api, Func<Task> call, Func<Exception, string>? classifyException = null)
        => ApiEnabled ? TrackApiCallCoreAsync(provider, api, call, classifyException) : call();

    private async Task TrackApiCallCoreAsync(string provider, string api, Func<Task> call, Func<Exception, string>? classifyException)
    {
        var start = GetTimestamp();
        try
        {
            await call().ConfigureAwait(false);
            OnApiCallCompleted(provider, api, GetElapsedTime(start), Ok);
        }
        catch (Exception exception)
        {
            OnApiCallCompleted(provider, api, GetElapsedTime(start), classifyException?.Invoke(exception) ?? GetExceptionStatus(exception));
            throw;
        }
    }

    internal Task TrackOperationAsync(string provider, string operation, Func<Task> call)
        => OperationsEnabled ? TrackOperationCoreAsync(provider, operation, call) : call();

    private async Task TrackOperationCoreAsync(string provider, string operation, Func<Task> call)
    {
        var start = GetTimestamp();
        try
        {
            await call().ConfigureAwait(false);
            OnOperationCompleted(provider, operation, GetElapsedTime(start), Ok);
        }
        catch (Exception exception)
        {
            OnOperationCompleted(provider, operation, GetElapsedTime(start), GetExceptionStatus(exception));
            throw;
        }
    }

    internal IAsyncEnumerable<JournalCatalogEntry> TrackCatalog(string provider, IAsyncEnumerable<JournalCatalogEntry> source)
        => OperationsEnabled || _catalogEntries.Enabled
            ? new TrackedEnumerable<JournalCatalogEntry>(this, provider, "list", source, isApi: false, classifyException: null)
            : source;

    internal IAsyncEnumerable<T> TrackApiEnumeration<T>(
        string provider, string api, IAsyncEnumerable<T> source, Func<Exception, string>? classifyException = null)
        => ApiEnabled ? new TrackedEnumerable<T>(this, provider, api, source, isApi: true, classifyException) : source;

    internal IAsyncEnumerable<T> TrackApiPages<T>(
        string provider,
        string api,
        IAsyncEnumerable<T> source,
        Func<T, long> countItems,
        Func<Exception, string>? classifyException = null)
        => ApiEnabled ? TrackApiPagesCore(provider, api, source, countItems, classifyException) : source;

    private async IAsyncEnumerable<T> TrackApiPagesCore<T>(
        string provider,
        string api,
        IAsyncEnumerable<T> source,
        Func<T, long> countItems,
        Func<Exception, string>? classifyException,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            var start = GetTimestamp();
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                OnApiCallCompleted(provider, api, GetElapsedTime(start), classifyException?.Invoke(exception) ?? GetExceptionStatus(exception));
                throw;
            }

            if (!moved)
            {
                yield break;
            }

            OnApiCallCompleted(provider, api, GetElapsedTime(start), Ok, countItems(enumerator.Current));
            yield return enumerator.Current;
        }
    }

    private sealed class TrackedEnumerable<T>(
        JournalStorageTelemetry telemetry,
        string provider,
        string operation,
        IAsyncEnumerable<T> source,
        bool isApi,
        Func<Exception, string>? classifyException) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Enumerator<T>(telemetry, provider, operation, source, isApi, classifyException, cancellationToken);
    }

    private sealed class Enumerator<T>(
        JournalStorageTelemetry telemetry,
        string provider,
        string operation,
        IAsyncEnumerable<T> source,
        bool isApi,
        Func<Exception, string>? classifyException,
        CancellationToken cancellationToken) : IAsyncEnumerator<T>
    {
        private IAsyncEnumerator<T>? _inner;
        private bool _started;
        private bool _finished;
        private TimeSpan _elapsed;
        private long _items;

        public T Current => _inner!.Current;

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_finished)
            {
                return false;
            }

            var start = telemetry.GetTimestamp();
            _started = true;
            try
            {
                _inner ??= source.GetAsyncEnumerator(cancellationToken);
                var moved = await _inner.MoveNextAsync().ConfigureAwait(false);
                _elapsed += telemetry.GetElapsedTime(start);
                if (!moved)
                {
                    Finish(Ok);
                }
                else
                {
                    _items++;
                    if (!isApi && telemetry._catalogEntries.Enabled)
                    {
                        telemetry._catalogEntries.Add(1, new KeyValuePair<string, object?>("provider", provider));
                    }
                }

                return moved;
            }
            catch (Exception exception)
            {
                _elapsed += telemetry.GetElapsedTime(start);
                Finish(classifyException?.Invoke(exception) ?? GetExceptionStatus(exception));
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_started)
            {
                _finished = true;
                return;
            }

            var start = telemetry.GetTimestamp();
            try
            {
                if (_inner is not null)
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _elapsed += telemetry.GetElapsedTime(start);
                Finish(classifyException?.Invoke(exception) ?? GetExceptionStatus(exception));
                throw;
            }

            _elapsed += telemetry.GetElapsedTime(start);
            Finish(Disposed);
        }

        private void Finish(string status)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            if (isApi)
            {
                telemetry.OnApiCallCompleted(provider, operation, _elapsed, status, _items);
            }
            else
            {
                telemetry.OnOperationCompleted(provider, operation, _elapsed, status);
            }
        }
    }
}
