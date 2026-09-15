using System.Buffers;
using System.Security.Cryptography;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Benchmarks.Journaling.Azure;

internal interface IAzureJournalScenario
{
    Task PrepareAsync(CancellationToken cancellationToken);
    Task ExecuteAsync(int index, CancellationToken cancellationToken);
    Task VerifyAsync(IEnumerable<int> completed, CancellationToken cancellationToken);
    void StartMetrics();
    IReadOnlyList<ProviderMetric> StopMetrics();
    Task CleanupAsync(CancellationToken cancellationToken);
}

internal sealed class AzureJournalScenario(AzureJournalOptions options, AzureJournalReport report) : IAzureJournalScenario
{
    internal const string FormatKey = "benchmark-bytes-v1";
    private readonly byte[] _append = CreatePayload(options.AppendBytes, options.Seed);
    private readonly byte[] _checkpoint = CreatePayload(options.CheckpointBytes, unchecked(options.Seed + 1));
    private readonly string _resourceName = "journalbench" + Guid.NewGuid().ToString("N");
    private ServiceProvider? _services;
    private SiloLifecycleSubject? _lifecycle;
    private IJournalStorageProvider _provider = null!;
    private IJournalStorageCatalog _catalog = null!;
    private ProviderMetrics? _metrics;
    private BlobContainerClient? _container;
    private TableServiceClient? _tableService;
    private readonly List<IJournalStorage> _journals = [];
    private OwnedBenchmarkResource? _resource;
    private bool _started;
    private byte[] _expectedHash = null!;
    private HashSet<JournalId> _expectedCatalog = [];

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        report.Resource = _resourceName;
        Console.Error.WriteLine($"Azure journal benchmark resource={_resourceName}; backend={options.Backend}");
        var expectedBatches = options.Workload switch
        {
            AzureJournalWorkload.DurableAppend => options.HistoryBatches + 1,
            AzureJournalWorkload.CheckpointReplace => 0,
            _ => options.HistoryBatches
        };
        _expectedHash = PayloadConsumer.ComputeHash(_checkpoint, _append, expectedBatches);
        _expectedCatalog = Enumerable.Range(0, options.DueJournals).Select(index => CatalogId(index, future: false)).ToHashSet();
        if (options.Workload == AzureJournalWorkload.CatalogUnbounded)
        {
            _expectedCatalog.UnionWith(Enumerable.Range(0, options.FutureJournals).Select(index => CatalogId(index, future: true)));
        }

        var builder = new ProviderSiloBuilder();
        builder.Services.AddLogging().AddMetrics().AddSingleton<OrleansInstruments>();
        if (options.IsTable)
        {
            var clientOptions = new TableClientOptions();
            ConfigureClient(clientOptions);
            _tableService = options.IsEmulator
                ? new TableServiceClient(EmulatorConnectionString(), clientOptions)
                : new TableServiceClient(ReadAzureEndpoint("JOURNAL_BENCHMARK_TABLE_ENDPOINT"), new DefaultAzureCredential(), clientOptions);
            ValidateEndpoint(_tableService.Uri, options.IsEmulator);
            report.AccountVerification = options.IsEmulator ? "emulator" : "requested-unverified";
            report.AccountKind = options.IsEmulator ? "Azurite" : null;
            _resource = new OwnedBenchmarkResource(report,
                async token => { await _tableService.CreateTableAsync(_resourceName, token); },
                async token => { await _tableService.DeleteTableAsync(_resourceName, token); });
            await _resource.CreateAsync(cancellationToken);
            builder.AddAzureTableJournalStorage(value =>
            {
                value.TableName = _resourceName;
                value.TableServiceClient = _tableService;
            });
        }
        else
        {
            var clientOptions = new BlobClientOptions();
            ConfigureClient(clientOptions);
            var client = options.IsEmulator
                ? new BlobServiceClient(EmulatorConnectionString(), clientOptions)
                : new BlobServiceClient(ReadAzureEndpoint("JOURNAL_BENCHMARK_BLOB_ENDPOINT"), new DefaultAzureCredential(), clientOptions);
            ValidateEndpoint(client.Uri, options.IsEmulator);
            if (options.IsEmulator)
            {
                report.AccountVerification = "emulator";
                report.AccountKind = "Azurite";
            }
            else
            {
                var account = (await client.GetAccountInfoAsync(cancellationToken)).Value;
                report.AccountKind = account.AccountKind.ToString();
                report.AccountSku = account.SkuName.ToString();
                VerifyAccount(options.Backend, report.AccountKind, report.AccountSku);
                report.AccountVerification = "data-plane-verified";
            }

            _container = client.GetBlobContainerClient(_resourceName);
            _resource = new OwnedBenchmarkResource(report,
                async token => { await _container.CreateAsync(cancellationToken: token); },
                async token => { await _container.DeleteAsync(cancellationToken: token); });
            await _resource.CreateAsync(cancellationToken);
            builder.AddAzureBlobJournalStorage(value =>
            {
                value.ContainerName = _resourceName;
                value.BlobServiceClient = client;
            });
        }

        builder.Services.AddKeyedSingleton<IJournalFormat>(FormatKey, new BenchmarkByteFormat());
        builder.Services.Configure<JournaledStateManagerOptions>(value => value.JournalFormatKey = FormatKey);
        _services = builder.Services.BuildServiceProvider();
        _metrics = new ProviderMetrics(_services.GetRequiredService<OrleansInstruments>().Meter);
        _provider = _services.GetRequiredService<IJournalStorageProvider>();
        _catalog = _services.GetRequiredService<IJournalStorageCatalog>();
        _lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        foreach (var participant in _services.GetServices<ILifecycleParticipant<ISiloLifecycle>>())
        {
            participant.Participate(_lifecycle);
        }

        report.Phase = "initialize";
        await _lifecycle.OnStart(cancellationToken);
        _started = true;
        report.Phase = "seed";
        if (options.IsCatalog)
        {
            await SeedCatalogAsync(cancellationToken);
        }
        else
        {
            // The last journal is reserved for warmup; measured operations each get an identical baseline.
            for (var index = 0; index <= options.Operations; index++)
            {
                var storage = _provider.CreateStorage(WorkId(index));
                await CreateJournalAsync(storage, cancellationToken);
                await storage.ReplaceAsync(new ReadOnlySequence<byte>(_checkpoint), cancellationToken);
                for (var batch = 0; batch < options.HistoryBatches; batch++)
                {
                    await storage.AppendAsync(new ReadOnlySequence<byte>(_append), cancellationToken);
                }

                _journals.Add(storage);
            }
        }

        report.Phase = "warmup";
        await ExecuteAsync(options.Operations, cancellationToken);
        await VerifyAsync([options.Operations], cancellationToken);
    }

    public async Task ExecuteAsync(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (options.Workload)
        {
            case AzureJournalWorkload.DurableAppend:
                await _journals[index].AppendAsync(new ReadOnlySequence<byte>(_append), cancellationToken);
                break;
            case AzureJournalWorkload.CheckpointReplace:
                await _journals[index].ReplaceAsync(new ReadOnlySequence<byte>(_checkpoint), cancellationToken);
                break;
            case AzureJournalWorkload.RecoveryReplay:
                await VerifyJournalAsync(index, options.HistoryBatches, cancellationToken);
                break;
            case AzureJournalWorkload.CatalogBounded:
            case AzureJournalWorkload.CatalogUnbounded:
                await VerifyCatalogAsync(cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Unknown workload.");
        }
    }

    public async Task VerifyAsync(IEnumerable<int> completed, CancellationToken cancellationToken)
    {
        if (options.IsCatalog)
        {
            return;
        }

        var batches = options.Workload == AzureJournalWorkload.DurableAppend ? options.HistoryBatches + 1 : 0;
        foreach (var index in completed)
        {
            if (options.Workload != AzureJournalWorkload.RecoveryReplay)
            {
                await VerifyJournalAsync(index, batches, cancellationToken);
            }

            ValidateMetadata(await _provider.CreateStorage(WorkId(index)).GetMetadataAsync(cancellationToken), options.IncludeMetadata);
        }
    }

    private async Task VerifyJournalAsync(int index, int batches, CancellationToken cancellationToken)
    {
        using var consumer = new PayloadConsumer(_checkpoint, _append, batches, _expectedHash);
        // Recovery uses a fresh storage object, exercising persisted state rather than a writer's cache.
        await _provider.CreateStorage(WorkId(index)).ReadAsync(consumer, cancellationToken);
        consumer.AssertComplete();
    }

    private async Task SeedCatalogAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < options.DueJournals; index++)
        {
            await CreateJournalAsync(_provider.CreateStorage(CatalogId(index, future: false)), cancellationToken);
        }

        for (var index = 0; index < options.FutureJournals; index++)
        {
            await CreateJournalAsync(_provider.CreateStorage(CatalogId(index, future: true)), cancellationToken);
        }

        await CreateJournalAsync(_provider.CreateStorage(new JournalId("catalog/00000000/expired")), cancellationToken);
        await CreateJournalAsync(_provider.CreateStorage(new JournalId("unrelated/00000000")), cancellationToken);
    }

    private async Task CreateJournalAsync(IJournalStorage storage, CancellationToken cancellationToken)
    {
        if (!await storage.CreateIfNotExistsAsync(options.IncludeMetadata ? CallerMetadata : null, cancellationToken))
        {
            throw new BenchmarkValidationException(BenchmarkValidationError.JournalCollision);
        }
    }

    internal static IReadOnlyDictionary<string, string> CallerMetadata { get; } = new Dictionary<string, string>
    {
        ["benchmark"] = "azure-journaling",
        ["version"] = "1"
    };

    internal static JournalId CatalogId(int index, bool future) => new($"catalog/{(future ? "21000101" : "20000101")}/{index:D8}");
    private static JournalId WorkId(int index) => new($"work/{index:D8}");

    internal ListOptions CatalogOptions() => new()
    {
        Prefix = new JournalId("catalog/"),
        MinId = CatalogId(0, future: false),
        MaxId = options.Workload == AzureJournalWorkload.CatalogBounded ? CatalogId(Math.Max(0, options.DueJournals - 1), future: false) : default,
        IncludeMetadata = options.IncludeMetadata
    };

    private Task VerifyCatalogAsync(CancellationToken cancellationToken)
        => ValidateCatalogAsync(_catalog.ListAsync(CatalogOptions(), cancellationToken), _expectedCatalog, options.IncludeMetadata, cancellationToken);

    internal static async Task ValidateCatalogAsync(
        IAsyncEnumerable<JournalCatalogEntry> entries, IReadOnlySet<JournalId> expectedIds, bool includeMetadata, CancellationToken cancellationToken)
    {
        var expected = new HashSet<JournalId>(expectedIds);
        await foreach (var entry in entries.WithCancellation(cancellationToken))
        {
            if (!expected.Remove(entry.Id))
            {
                throw new BenchmarkValidationException(BenchmarkValidationError.CatalogUnexpectedIdentity);
            }

            if (includeMetadata)
            {
                ValidateMetadata(entry.Metadata, includeMetadata: true);
            }
            else if (entry.Metadata is not null)
            {
                throw new BenchmarkValidationException(BenchmarkValidationError.CatalogUnrequestedMetadata);
            }
        }

        if (expected.Count != 0)
        {
            throw new BenchmarkValidationException(BenchmarkValidationError.CatalogMissingIdentity);
        }
    }

    internal static void ValidateMetadata(IJournalMetadata? metadata, bool includeMetadata)
    {
        if (metadata?.Format != FormatKey || string.IsNullOrEmpty(metadata.ETag))
        {
            throw new BenchmarkValidationException(BenchmarkValidationError.JournalFormatOrETag);
        }

        var properties = metadata.Properties.Where(pair => !pair.Key.StartsWith('$')).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (properties.Count != (includeMetadata ? CallerMetadata.Count : 0)
            || (includeMetadata && CallerMetadata.Any(pair => !properties.TryGetValue(pair.Key, out var value) || value != pair.Value)))
        {
            throw new BenchmarkValidationException(BenchmarkValidationError.JournalCallerMetadata);
        }
    }

    public void StartMetrics() => _metrics!.Start();
    public IReadOnlyList<ProviderMetric> StopMetrics() => _metrics!.Stop();

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_started)
            {
                try
                {
                    await _lifecycle!.OnStop(cancellationToken);
                }
                catch (Exception exception)
                {
                    report.Failures.Add(BenchmarkFailure.From("cleanup-lifecycle", exception));
                }

                _started = false;
            }

            if (_resource is not null)
            {
                await _resource.CleanupAsync(cancellationToken);
            }
        }
        finally
        {
            _metrics?.Dispose();
            if (_services is not null)
            {
                await _services.DisposeAsync();
                _services = null;
            }
        }
    }

    internal static void VerifyAccount(AzureJournalBackend backend, string kind, string sku)
    {
        var matches = backend switch
        {
            AzureJournalBackend.PremiumBlob => kind == "BlockBlobStorage" && sku.StartsWith("Premium_", StringComparison.Ordinal),
            AzureJournalBackend.StandardBlob => kind is "Storage" or "StorageV2" or "BlobStorage" && sku.StartsWith("Standard_", StringComparison.Ordinal),
            _ => false
        };
        if (!matches)
        {
            throw new InvalidOperationException("The Azure account kind/SKU does not match the requested backend.");
        }
    }

    private static string EmulatorConnectionString()
        => Environment.GetEnvironmentVariable("JOURNAL_BENCHMARK_AZURITE_CONNECTION_STRING") ?? "UseDevelopmentStorage=true";

    private static Uri ReadAzureEndpoint(string name)
    {
        if (!Uri.TryCreate(Environment.GetEnvironmentVariable(name), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Set {name} to the Azure service endpoint.");
        }

        ValidateEndpoint(uri, emulator: false);
        return uri;
    }

    internal static void ValidateEndpoint(Uri uri, bool emulator)
    {
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || (emulator ? !uri.IsLoopback || uri.Scheme is not ("http" or "https") : uri.IsLoopback || uri.Scheme != "https"))
        {
            throw new ArgumentException("Use loopback emulator endpoints or HTTPS Azure service endpoints authenticated with Entra ID.");
        }
    }

    private static void ConfigureClient(ClientOptions options)
    {
        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsLoggingContentEnabled = false;
    }

    internal static byte[] CreatePayload(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private sealed class ProviderSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class BenchmarkByteFormat : IJournalFormat
    {
        public string FormatKey => AzureJournalScenario.FormatKey;
        public string MimeType => "application/octet-stream";
        public JournalBufferWriter CreateWriter() => throw new NotSupportedException("The provider benchmark writes deterministic bytes directly.");
        public void Replay(JournalBufferReader input, JournalReplayContext context) => throw new NotSupportedException("The provider benchmark replays bytes through PayloadConsumer.");
    }

    internal sealed class PayloadConsumer : IJournalStorageConsumer, IDisposable
    {
        private readonly byte[] _checkpoint;
        private readonly byte[] _append;
        private readonly long _expectedLength;
        private readonly byte[] _expectedHash;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        private bool _completed;

        public PayloadConsumer(byte[] checkpoint, byte[] append, int batches, byte[]? expectedHash = null)
        {
            _checkpoint = checkpoint;
            _append = append;
            _expectedLength = checkpoint.Length + (long)append.Length * batches;
            _expectedHash = expectedHash ?? ComputeHash(checkpoint, append, batches);
        }

        internal static byte[] ComputeHash(byte[] checkpoint, byte[] append, int batches)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(checkpoint);
            for (var index = 0; index < batches; index++)
            {
                hash.AppendData(append);
            }

            return hash.GetHashAndReset();
        }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            if (_completed)
            {
                throw new BenchmarkValidationException(BenchmarkValidationError.RecoveryAfterCompletion);
            }

            if (metadata?.Format != FormatKey)
            {
                throw new BenchmarkValidationException(BenchmarkValidationError.RecoveryFormat);
            }

            Span<byte> scratch = stackalloc byte[4096];
            while (buffer.Length > 0)
            {
                var count = Math.Min(buffer.Length, scratch.Length);
                var bytes = scratch[..count];
                buffer.Read(bytes);
                if (_length + count > _expectedLength)
                {
                    throw new BenchmarkValidationException(BenchmarkValidationError.RecoveryLength);
                }

                for (var index = 0; index < count; index++)
                {
                    var position = _length + index;
                    var expected = position < _checkpoint.Length ? _checkpoint[position] : _append[(position - _checkpoint.Length) % _append.Length];
                    if (bytes[index] != expected)
                    {
                        throw new BenchmarkValidationException(BenchmarkValidationError.RecoveryPayload);
                    }
                }

                _hash.AppendData(bytes);
                _length += count;
            }

            _completed = buffer.IsCompleted;
        }

        public void AssertComplete()
        {
            if (!_completed || _length != _expectedLength || !_hash.GetHashAndReset().AsSpan().SequenceEqual(_expectedHash))
            {
                throw new BenchmarkValidationException(BenchmarkValidationError.RecoveryCompletionOrChecksum);
            }
        }

        public void Dispose() => _hash.Dispose();
    }
}
