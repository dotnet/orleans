using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;

namespace Benchmarks.Journaling.Azure;

internal sealed record LatencySummary(int Count, double? MeanMs, double? P50Ms, double? P95Ms, double? P99Ms)
{
    public static LatencySummary Create(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? new(0, null, null, null, null) : new(
            sorted.Length, sorted.Average(), Percentile(sorted, .50), Percentile(sorted, .95), Percentile(sorted, .99));
    }

    private static double Percentile(double[] sorted, double percentile)
        => sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
}

internal enum BenchmarkValidationError
{
    JournalCollision,
    CatalogUnexpectedIdentity,
    CatalogMissingIdentity,
    CatalogUnrequestedMetadata,
    JournalFormatOrETag,
    JournalCallerMetadata,
    RecoveryAfterCompletion,
    RecoveryFormat,
    RecoveryLength,
    RecoveryPayload,
    RecoveryCompletionOrChecksum
}

internal sealed class BenchmarkValidationException(BenchmarkValidationError code) : Exception(code.ToString())
{
    public BenchmarkValidationError Code { get; } = code;
}

internal sealed record BenchmarkFailure(string Phase, string ExceptionType, int? HttpStatus, int? Operation = null, BenchmarkValidationError? ValidationError = null)
{
    // Exception messages and URIs can contain credentials, SDK request bodies, or connection strings.
    public static BenchmarkFailure From(string phase, Exception exception, int? operation = null)
        => new(phase, exception.GetType().Name, (exception as RequestFailedException)?.Status, operation, (exception as BenchmarkValidationException)?.Code);
}

internal sealed record OperationResult(int Index, string Outcome, double LatencyMs, long PayloadBytes, long Items);

internal sealed class AzureJournalReport(AzureJournalOptions configuration)
{
    public int SchemaVersion => 1;
    public string Source => "IJournalStorage / IJournalStorageCatalog";
    public string LoadModel => "closed-loop fixed work; one independent journal per mutation";
    public string LatencyUnit => "ms; Stopwatch around each awaited operation, including recovery/catalog validation";
    public string PayloadByteUnit => "caller payload bytes committed or replayed; catalog = 0";
    public string ItemUnit => Configuration.Workload switch
    {
        AzureJournalWorkload.DurableAppend => "payload records",
        AzureJournalWorkload.CheckpointReplace => "checkpoints",
        AzureJournalWorkload.RecoveryReplay => "checkpoint plus payload records",
        _ => "catalog entries"
    };
    public string TelemetryUnit => "provider logical operations and SDK invocations/pages; Azure metrics establish billable transport requests";
    public AzureJournalOptions Configuration { get; } = configuration;
    public string Runtime { get; } = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string OperatingSystem { get; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    public string? Resource { get; set; }
    public string Ownership { get; set; } = "not-created";
    public string Cleanup { get; set; } = "not-required";
    public string AccountVerification { get; set; } = "not-checked";
    public string? AccountKind { get; set; }
    public string? AccountSku { get; set; }
    public string Phase { get; set; } = "setup";
    public double ElapsedSeconds { get; set; }
    public List<OperationResult> Operations { get; } = [];
    public List<BenchmarkFailure> Failures { get; } = [];
    public IReadOnlyList<ProviderMetric> ProviderMetrics { get; set; } = [];
    public int Completed => Operations.Count(operation => operation.Outcome == "completed");
    public int Failed => Operations.Count(operation => operation.Outcome == "failed");
    public int Cancelled => Operations.Count(operation => operation.Outcome == "cancelled");
    public int NotStarted => Configuration.Operations - Operations.Count;
    public long CompletedPayloadBytes => Operations.Where(operation => operation.Outcome == "completed").Sum(operation => operation.PayloadBytes);
    public long CompletedItems => Operations.Where(operation => operation.Outcome == "completed").Sum(operation => operation.Items);
    public double CompletedOperationsPerSecond => ElapsedSeconds > 0 ? Completed / ElapsedSeconds : 0;
    public double PayloadBytesPerSecond => ElapsedSeconds > 0 ? CompletedPayloadBytes / ElapsedSeconds : 0;
    public LatencySummary SuccessfulLatency => LatencySummary.Create(Operations.Where(operation => operation.Outcome == "completed").Select(operation => operation.LatencyMs));
    public LatencySummary FailedLatency => LatencySummary.Create(Operations.Where(operation => operation.Outcome == "failed").Select(operation => operation.LatencyMs));
    public LatencySummary CancelledLatency => LatencySummary.Create(Operations.Where(operation => operation.Outcome == "cancelled").Select(operation => operation.LatencyMs));
    public bool Success => Phase == "complete" && Failures.Count == 0 && Completed == Configuration.Operations && Cleanup == "deleted";

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task ExportAsync(string prefix)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        // JSON is the complete report; CSV carries the same nested configuration, failures and metrics as quoted JSON fields.
        string[] headers =
        [
            "schema_version", "backend", "workload", "phase", "success", "resource", "ownership", "cleanup",
            "account_verification", "account_kind", "account_sku", "completed", "failed", "cancelled", "not_started",
            "elapsed_seconds", "operations_per_second", "payload_bytes_per_second", "completed_payload_bytes", "completed_items",
            "p50_ms", "p95_ms", "p99_ms", "item_unit", "configuration_json", "operations_json", "failures_json", "provider_metrics_json"
        ];
        object?[] values =
        [
            SchemaVersion, Configuration.Backend, Configuration.Workload, Phase, Success, Resource, Ownership, Cleanup,
            AccountVerification, AccountKind, AccountSku, Completed, Failed, Cancelled, NotStarted,
            ElapsedSeconds, CompletedOperationsPerSecond, PayloadBytesPerSecond, CompletedPayloadBytes, CompletedItems,
            SuccessfulLatency.P50Ms, SuccessfulLatency.P95Ms, SuccessfulLatency.P99Ms, ItemUnit,
            JsonSerializer.Serialize(Configuration, JsonOptions), JsonSerializer.Serialize(Operations, JsonOptions),
            JsonSerializer.Serialize(Failures, JsonOptions), JsonSerializer.Serialize(ProviderMetrics, JsonOptions)
        ];
        var csv = string.Join(',', headers) + "\n" + string.Join(',', values.Select(CsvField)) + "\n";
        await WriteNewAsync(prefix + ".json", json);
        await WriteNewAsync(prefix + ".csv", csv);
    }

    private static string CsvField(object? value)
        => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task WriteNewAsync(string path, string content)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
    }
}
