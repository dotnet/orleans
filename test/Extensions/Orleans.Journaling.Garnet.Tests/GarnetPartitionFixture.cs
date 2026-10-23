using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Starts a Garnet journal storage provider which hosts one partition, owning every bucket, in a temporary directory.
/// </summary>
public sealed class GarnetPartitionFixture : IAsyncLifetime
{
    internal const string HostId = "test-host";
    private GarnetJournalStorageProvider? _provider;
    private ConnectionMultiplexer? _admin;

    public GarnetPartitionFixture()
    {
        DataDirectory = CreateTemporaryDirectory();
    }

    public string DataDirectory { get; }

    /// <summary>Gets the store holding the range map, which outlives provider restarts.</summary>
    public InMemoryRangeMetaStore MetaStore { get; } = new();

    internal GarnetJournalStorageProvider Provider => _provider ?? throw new InvalidOperationException("The fixture has not started.");

    /// <summary>Gets the endpoint of the hosted partition.</summary>
    public System.Net.IPEndPoint PartitionEndPoint => Provider.HostedPartitions.Values.Single().EndPoint;

    /// <summary>Gets a raw connection to the partition, for invoking procedures directly and for inspecting and corrupting state.</summary>
    public IDatabase Database => (_admin ?? throw new InvalidOperationException("The fixture has not started.")).GetDatabase();

    public Action<GarnetJournalStorageOptions>? Configure { get; init; }

    public static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orleans-garnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static GarnetJournalStorageProvider CreateProvider(
        string? dataDirectory,
        IRangeMetaStore metaStore,
        Action<GarnetJournalStorageOptions>? configure = null,
        string? journalFormatKey = null)
    {
        var options = new GarnetJournalStorageOptions
        {
            DataDirectory = dataDirectory,
            MetaStore = metaStore,
            HostId = HostId,
            LogMemorySize = "64m",
            IndexMemorySize = "1m",
            OperationTimeout = TimeSpan.FromSeconds(10),
        };
        configure?.Invoke(options);
        var managerOptions = new JournaledStateManagerOptions();
        if (journalFormatKey is not null)
        {
            managerOptions.JournalFormatKey = journalFormatKey;
        }

        return new GarnetJournalStorageProvider(Options.Create(options), Options.Create(managerOptions), NullLoggerFactory.Instance);
    }

    public async ValueTask InitializeAsync()
    {
        _provider = CreateProvider(DataDirectory, MetaStore, Configure);
        await _provider.StartAsync(CancellationToken.None);
        _admin = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { PartitionEndPoint },
            Protocol = RedisProtocol.Resp2,
            AllowAdmin = true,
        });
    }

    /// <summary>Stops the provider, including its partition, and starts it again from its data directory.</summary>
    internal async Task RestartAsync()
    {
        await StopAsync();
        await InitializeAsync();
    }

    /// <summary>Stops the provider, including its partition.</summary>
    internal async Task StopAsync()
    {
        if (_admin is { } admin)
        {
            _admin = null;
            await admin.CloseAsync();
            admin.Dispose();
        }

        if (_provider is { } provider)
        {
            _provider = null;
            await provider.StopAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        TryDeleteDirectory(DataDirectory);
    }

    public static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
