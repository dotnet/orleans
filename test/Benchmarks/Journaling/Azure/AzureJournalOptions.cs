using System.Globalization;
using System.Text.Json.Serialization;

namespace Benchmarks.Journaling.Azure;

public enum AzureJournalBackend
{
    AzuriteBlob,
    AzuriteTable,
    StandardBlob,
    PremiumBlob,
    Table
}

public enum AzureJournalWorkload
{
    DurableAppend,
    CheckpointReplace,
    RecoveryReplay,
    CatalogBounded,
    CatalogUnbounded
}

public sealed record AzureJournalOptions
{
    public AzureJournalBackend Backend { get; init; } = AzureJournalBackend.AzuriteBlob;
    public AzureJournalWorkload Workload { get; init; } = AzureJournalWorkload.DurableAppend;
    public int Operations { get; init; } = 8;
    public int Concurrency { get; init; } = 2;
    public int PayloadBytes { get; init; } = 256;
    public int BatchSize { get; init; } = 4;
    public int HistoryBatches { get; init; } = 4;
    public int CheckpointBytes { get; init; } = 4096;
    public int DueJournals { get; init; } = 16;
    public int FutureJournals { get; init; } = 64;
    public int Seed { get; init; } = 42;
    public bool IncludeMetadata { get; init; }
    public bool AllowAzure { get; init; }
    public int SetupTimeoutSeconds { get; init; } = 120;
    public int TimeoutSeconds { get; init; } = 60;
    public int CleanupTimeoutSeconds { get; init; } = 60;
    public long MaxWork { get; init; } = 100_000;
    public long MaxBytes { get; init; } = 256L * 1024 * 1024;
    [JsonIgnore] public string Output { get; init; } = "azure-journal-results";

    public bool IsEmulator => Backend is AzureJournalBackend.AzuriteBlob or AzureJournalBackend.AzuriteTable;
    public bool IsTable => Backend is AzureJournalBackend.Table or AzureJournalBackend.AzuriteTable;
    public bool IsCatalog => Workload is AzureJournalWorkload.CatalogBounded or AzureJournalWorkload.CatalogUnbounded;
    public int AppendBytes => checked(PayloadBytes * BatchSize);
    public long RecoveryBytes => CheckpointBytes + (long)AppendBytes * HistoryBatches;
    public long PayloadBytesPerOperation => Workload switch
    {
        AzureJournalWorkload.DurableAppend => AppendBytes,
        AzureJournalWorkload.CheckpointReplace => CheckpointBytes,
        AzureJournalWorkload.RecoveryReplay => RecoveryBytes,
        _ => 0
    };
    public long ItemsPerOperation => Workload switch
    {
        AzureJournalWorkload.DurableAppend => BatchSize,
        AzureJournalWorkload.CheckpointReplace => 1,
        AzureJournalWorkload.RecoveryReplay => 1L + (long)HistoryBatches * BatchSize,
        AzureJournalWorkload.CatalogBounded => DueJournals,
        AzureJournalWorkload.CatalogUnbounded => (long)DueJournals + FutureJournals,
        _ => throw new InvalidOperationException("Unknown workload.")
    };

    // Includes seeding, warmup, measured calls, verification, and catalog traversal work.
    public long EstimatedWork => IsCatalog
        ? 2L * (DueJournals + FutureJournals + 2) + (Operations + 1L) * (DueJournals + FutureJournals + 1L)
        : (Operations + 1L) * (HistoryBatches + 6L);
    public long EstimatedPayloadBytes => IsCatalog
        ? (DueJournals + FutureJournals + 2L) * 128 + (Operations + 1L) * (DueJournals + FutureJournals) * 128
        : (Operations + 1L) * (2 * RecoveryBytes + 2 * PayloadBytesPerOperation);

    public void Validate()
    {
        if (!Enum.IsDefined(Backend) || !Enum.IsDefined(Workload))
        {
            throw new ArgumentException("Select a named backend and workload.");
        }

        CheckRange(Operations, 1, 10_000, nameof(Operations));
        CheckRange(Concurrency, 1, Math.Min(Operations, 256), nameof(Concurrency));
        CheckRange(PayloadBytes, 1, 2 * 1024 * 1024, nameof(PayloadBytes));
        CheckRange(BatchSize, 1, 8192, nameof(BatchSize));
        CheckRange(HistoryBatches, 0, 10_000, nameof(HistoryBatches));
        CheckRange(CheckpointBytes, 1, 32 * 1024 * 1024, nameof(CheckpointBytes));
        CheckRange(DueJournals, 0, 100_000, nameof(DueJournals));
        CheckRange(FutureJournals, 0, 100_000, nameof(FutureJournals));
        CheckRange(SetupTimeoutSeconds, 1, 3600, nameof(SetupTimeoutSeconds));
        CheckRange(TimeoutSeconds, 1, 3600, nameof(TimeoutSeconds));
        CheckRange(CleanupTimeoutSeconds, 1, 600, nameof(CleanupTimeoutSeconds));
        CheckRange(MaxWork, 1, 10_000_000, nameof(MaxWork));
        CheckRange(MaxBytes, 1, 8L * 1024 * 1024 * 1024, nameof(MaxBytes));
        if ((long)PayloadBytes * BatchSize > 2 * 1024 * 1024)
        {
            throw new ArgumentException("PayloadBytes * BatchSize must fit the common 2 MiB Azure Table append limit.");
        }

        if (EstimatedWork > MaxWork || EstimatedPayloadBytes > MaxBytes)
        {
            throw new ArgumentException("Estimated setup, measurement, and verification work exceeds MaxWork or MaxBytes.");
        }

        if (!IsEmulator && !AllowAzure)
        {
            throw new ArgumentException("Real Azure requires --allow-azure true and incurs storage charges.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Output);
    }

    internal static AzureJournalOptions Parse(string[] args)
    {
        var result = new AzureJournalOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 == args.Length || !seen.Add(args[index]))
            {
                throw new ArgumentException("Every option requires one value and may occur once.");
            }

            var value = args[index + 1];
            result = args[index] switch
            {
                "--backend" => result with { Backend = ParseEnum<AzureJournalBackend>(value) },
                "--workload" => result with { Workload = ParseEnum<AzureJournalWorkload>(value) },
                "--operations" => result with { Operations = ParseInt(value) },
                "--concurrency" => result with { Concurrency = ParseInt(value) },
                "--payload-bytes" => result with { PayloadBytes = ParseInt(value) },
                "--batch-size" => result with { BatchSize = ParseInt(value) },
                "--history-batches" => result with { HistoryBatches = ParseInt(value) },
                "--checkpoint-bytes" => result with { CheckpointBytes = ParseInt(value) },
                "--due-journals" => result with { DueJournals = ParseInt(value) },
                "--future-journals" => result with { FutureJournals = ParseInt(value) },
                "--seed" => result with { Seed = ParseInt(value) },
                "--metadata" => result with { IncludeMetadata = ParseBool(value) },
                "--allow-azure" => result with { AllowAzure = ParseBool(value) },
                "--setup-timeout-seconds" => result with { SetupTimeoutSeconds = ParseInt(value) },
                "--timeout-seconds" => result with { TimeoutSeconds = ParseInt(value) },
                "--cleanup-timeout-seconds" => result with { CleanupTimeoutSeconds = ParseInt(value) },
                "--max-work" => result with { MaxWork = ParseLong(value) },
                "--max-bytes" => result with { MaxBytes = ParseLong(value) },
                "--output" => result with { Output = value },
                _ => throw new ArgumentException("Unknown option. Use Journaling.Azure --help.")
            };
        }

        result.Validate();
        return result;
    }

    internal static T ParseEnum<T>(string value) where T : struct, Enum
        => Enum.GetNames<T>().Contains(value, StringComparer.OrdinalIgnoreCase) && Enum.TryParse<T>(value, true, out var result)
            ? result : throw new ArgumentException("Expected a named enum value.");

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result : throw new ArgumentException("Expected an integer.");

    private static long ParseLong(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result : throw new ArgumentException("Expected an integer.");

    private static bool ParseBool(string value)
        => bool.TryParse(value, out var result) ? result : throw new ArgumentException("Expected true or false.");

    private static void CheckRange(long value, long min, long max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentException($"{name} must be between {min} and {max}.");
        }
    }
}
