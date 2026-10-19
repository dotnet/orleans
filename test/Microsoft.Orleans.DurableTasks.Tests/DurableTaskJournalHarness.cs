using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.DurableTasks.Storage;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Serialization;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Hosts journaled durable task storage in a selected journal format over volatile journal storage.
/// </summary>
/// <remarks>
/// Services come from <c>AddJournaling</c> and <c>AddDurableTasks</c>, so the journal codecs, JSON metadata, and
/// Orleans serializer match a silo. Each activation creates a scope with a fresh journaled state manager for the
/// same journal, so state written by one activation is recovered by the next.
/// </remarks>
internal sealed class DurableTaskJournalHarness : IAsyncDisposable
{
    public const string Json = "json";
    public const string OrleansBinary = "orleans-binary";

    private readonly JournalId _journalId;
    private readonly ServiceProvider _services;

    /// <param name="journalFormatKey">The journal format which the activations write.</param>
    /// <param name="timeProvider">The time provider, or the system's.</param>
    /// <param name="storageProvider">The volatile storage which holds the journal, so harnesses can share one journal.</param>
    /// <param name="journalId">The journal's id, or a new one.</param>
    /// <param name="wrapStorage">Wraps the storage which managers use, for example to inject failures.</param>
    /// <param name="configureServices">Adds services, for example JSON metadata for a test's journaled types.</param>
    public DurableTaskJournalHarness(
        string journalFormatKey,
        TimeProvider? timeProvider = null,
        VolatileJournalStorageProvider? storageProvider = null,
        JournalId? journalId = null,
        Func<IJournalStorageProvider, IJournalStorageProvider>? wrapStorage = null,
        Action<IServiceCollection>? configureServices = null)
    {
        JournalFormatKey = journalFormatKey;
        _journalId = journalId ?? JournalId.Create("durable-task-storage-tests", Guid.NewGuid().ToString("N"));
        StorageProvider = storageProvider ?? new VolatileJournalStorageProvider(
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = journalFormatKey }));
        var silo = new HarnessSiloBuilder();
        silo.Services.AddLogging();
        silo.Services.AddSingleton(timeProvider ?? TimeProvider.System);
        silo.Services.AddKeyedSingleton(JournalingTimeProviderNames.Journaling, TimeProvider.System);

        // Like a silo, the other areas' keyed clocks, including the Durable Jobs clock, default to the unkeyed one.
        silo.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        silo.AddJournaling();
        silo.AddDurableTasks();

        // A silo composes Durable Tasks with journaled Durable Jobs, whose registration also supplies JSON metadata for
        // the journal manager's own stream directory and retirement tracker.
        silo.UseJournaledDurableJobs();
        silo.Services.AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskJournalHarness).Assembly));
        silo.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
        silo.Services.AddSingleton<IJournalStorageProvider>(wrapStorage?.Invoke(StorageProvider) ?? StorageProvider);
        silo.Services.AddScoped<IJournaledStateManager>(services =>
            services.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(_journalId));
        configureServices?.Invoke(silo.Services);
        _services = silo.Services.BuildServiceProvider();
    }

    /// <summary>Gets the journal formats which durable task state supports.</summary>
    public static TheoryData<string> Formats => new() { Json, OrleansBinary };

    public string JournalFormatKey { get; }

    public JournalId JournalId => _journalId;

    public VolatileJournalStorageProvider StorageProvider { get; }

    public Serializer Serializer => _services.GetRequiredService<Serializer>();

    /// <summary>Creates an activation scope and recovers its task state from the journal.</summary>
    public async Task<Activation> ActivateAsync(CancellationToken cancellationToken)
    {
        var scope = _services.CreateAsyncScope();
        try
        {
            var storage = scope.ServiceProvider.GetRequiredService<DurableTaskGrainStorage>();
            await storage.ReadAsync(cancellationToken);
            return new(scope, storage);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates an activation scope without recovering its state, so callers can register state first.</summary>
    public AsyncServiceScope CreateScope() => _services.CreateAsyncScope();

    /// <summary>Returns the journal format key stamped on the stored journal.</summary>
    public async Task<string?> GetStoredJournalFormatKeyAsync(CancellationToken cancellationToken)
    {
        var metadata = await StorageProvider.CreateStorage(_journalId).GetMetadataAsync(cancellationToken);
        return metadata?.FormatKey;
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    public sealed class Activation(AsyncServiceScope scope, DurableTaskGrainStorage storage) : IAsyncDisposable
    {
        public DurableTaskGrainStorage Storage { get; } = storage;

        public IServiceProvider Services => scope.ServiceProvider;

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private sealed class HarnessSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
