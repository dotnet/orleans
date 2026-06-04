using System.Diagnostics.Metrics;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class AzureBlobJournalStorageInstruments(OrleansInstruments instruments)
{
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

    private readonly Counter<long> _operations = instruments.Meter.CreateCounter<long>("orleans-journaling-azure-blob-operations");
    private readonly Counter<long> _operationBytes = instruments.Meter.CreateCounter<long>("orleans-journaling-azure-blob-operation-bytes", BytesUnit);
    private readonly Histogram<double> _operationDuration = instruments.Meter.CreateHistogram<double>("orleans-journaling-azure-blob-operation-duration", MillisecondsUnit);

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
