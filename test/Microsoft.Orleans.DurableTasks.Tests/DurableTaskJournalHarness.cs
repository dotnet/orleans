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

    private readonly JournalId _journalId = JournalId.Create("durable-task-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public DurableTaskJournalHarness(string journalFormatKey, TimeProvider? timeProvider = null)
    {
        JournalFormatKey = journalFormatKey;
        StorageProvider = new VolatileJournalStorageProvider(
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = journalFormatKey }));
        var silo = new HarnessSiloBuilder();
        silo.Services.AddLogging();
        silo.Services.AddSingleton(timeProvider ?? TimeProvider.System);
        silo.Services.AddKeyedSingleton(JournalingTimeProviderNames.Journaling, TimeProvider.System);
        silo.AddJournaling();
        silo.AddDurableTasks();

        // A silo composes Durable Tasks with journaled Durable Jobs, whose registration also supplies JSON metadata for
        // the journal manager's own stream directory and retirement tracker.
        silo.UseJournaledDurableJobs();
        silo.Services.AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskJournalHarness).Assembly));
        silo.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
        silo.Services.AddSingleton<IJournalStorageProvider>(StorageProvider);
        silo.Services.AddScoped<IJournaledStateManager>(services =>
            services.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(_journalId));
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

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private sealed class HarnessSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
