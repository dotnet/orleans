using System.Diagnostics.Metrics;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class S3JournalStorageInstruments(OrleansInstruments instruments, TimeProvider? clock = null)
{
    private static readonly Lazy<S3JournalStorageInstruments> DirectConstruction = new(CreateDirectConstruction);
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

    internal JournalStorageTelemetry Telemetry { get; } = new(instruments, clock);

    internal Task<T> TrackApiCallAsync<T>(string api, Func<Task<T>> call, Func<T, long>? countItems = null)
        => Telemetry.TrackApiCallAsync(JournalStorageTelemetry.S3, api, call, ClassifyException, countItems: countItems);

    internal void OnRetry(string reason) => Telemetry.OnRetry(JournalStorageTelemetry.S3, reason);

    private static string ClassifyException(Exception exception) => exception switch
    {
        AmazonS3Exception { ErrorCode: "SlowDown" } => "throttled",
        AmazonS3Exception s3 => JournalStorageTelemetry.GetHttpStatus((int)s3.StatusCode),
        _ => JournalStorageTelemetry.GetExceptionStatus(exception),
    };

    internal static S3JournalStorageInstruments CreateForDirectConstruction() => DirectConstruction.Value;

    private static S3JournalStorageInstruments CreateDirectConstruction()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<S3JournalStorageInstruments>();
        return services.BuildServiceProvider().GetRequiredService<S3JournalStorageInstruments>();
    }

    private readonly Counter<long> _operations = instruments.Meter.CreateCounter<long>("orleans-journaling-s3-operations");
    private readonly Counter<long> _operationBytes = instruments.Meter.CreateCounter<long>("orleans-journaling-s3-operation-bytes", BytesUnit);
    private readonly Histogram<double> _operationDuration = instruments.Meter.CreateHistogram<double>("orleans-journaling-s3-operation-duration", MillisecondsUnit);

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
