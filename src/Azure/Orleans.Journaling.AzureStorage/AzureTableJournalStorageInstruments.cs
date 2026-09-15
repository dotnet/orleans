using System.Diagnostics.Metrics;
using Azure;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class AzureTableJournalStorageInstruments(OrleansInstruments instruments, TimeProvider? clock = null)
{
    internal JournalStorageTelemetry Telemetry { get; } = new(instruments, clock);

    internal Task<T> TrackApiCallAsync<T>(
        string api, Func<Task<T>> call, Func<T, string>? classifyResult = null, Func<T, long>? countItems = null)
        => Telemetry.TrackApiCallAsync(JournalStorageTelemetry.AzureTable, api, call, ClassifyException, classifyResult, countItems);

    internal IAsyncEnumerable<Page<T>> TrackApiPages<T>(string api, IAsyncEnumerable<Page<T>> pages)
        where T : notnull
        => Telemetry.TrackApiPages(JournalStorageTelemetry.AzureTable, api, pages, static page => page.Values.Count, ClassifyException);

    private static string ClassifyException(Exception exception)
        => exception is RequestFailedException request
            ? JournalStorageTelemetry.GetHttpStatus(request.Status)
            : JournalStorageTelemetry.GetExceptionStatus(exception);

    private const string MillisecondsUnit = "ms";
    private const string BytesUnit = "bytes";
    private const string OperationTagName = "operation";
    private const string StatusTagName = "status";
    private const string StatusOk = "ok";
    private const string StatusError = "error";

    internal const string OperationCreate = "create";
    internal const string OperationGetMetadata = "get_metadata";
    internal const string OperationUpdateMetadata = "update_metadata";
    internal const string OperationAppend = "append";
    internal const string OperationDelete = "delete";
    internal const string OperationRead = "read";
    internal const string OperationReplace = "replace";

    internal static AzureTableJournalStorageInstruments CreateForDirectConstruction()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<AzureTableJournalStorageInstruments>();
        return services.BuildServiceProvider().GetRequiredService<AzureTableJournalStorageInstruments>();
    }

    private readonly Counter<long> _operations = instruments.Meter.CreateCounter<long>("orleans-journaling-azure-table-operations");
    private readonly Counter<long> _operationBytes = instruments.Meter.CreateCounter<long>("orleans-journaling-azure-table-operation-bytes", BytesUnit);
    private readonly Histogram<double> _operationDuration = instruments.Meter.CreateHistogram<double>("orleans-journaling-azure-table-operation-duration", MillisecondsUnit);

    internal void OnOperationCompleted(string operation, TimeSpan latency, long bytes, bool succeeded)
    {
        var tags = CreateTags(operation, succeeded);
        if (_operations.Enabled)
        {
            _operations.Add(1, tags);
        }

        if (_operationDuration.Enabled)
        {
            _operationDuration.Record(Math.Max(0, latency.TotalMilliseconds), tags);
        }

        if (succeeded && bytes > 0 && _operationBytes.Enabled)
        {
            _operationBytes.Add(bytes, [new KeyValuePair<string, object?>(OperationTagName, operation)]);
        }
    }

    private static KeyValuePair<string, object?>[] CreateTags(string operation, bool succeeded) =>
        [
            new(OperationTagName, operation),
            new(StatusTagName, succeeded ? StatusOk : StatusError)
        ];
}
