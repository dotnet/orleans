using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Starts a Garnet journal storage provider which hosts an embedded partition in a temporary directory.
/// </summary>
public sealed class GarnetPartitionFixture : IAsyncLifetime
{
    private GarnetJournalStorageProvider? _provider;
    private ConnectionMultiplexer? _admin;

    public GarnetPartitionFixture() : this(directory: null)
    {
    }

    private GarnetPartitionFixture(string? directory)
    {
        DataDirectory = directory ?? CreateTemporaryDirectory();
    }

    public string DataDirectory { get; }

    internal GarnetJournalStorageProvider Provider => _provider ?? throw new InvalidOperationException("The fixture has not started.");

    /// <summary>Gets a raw connection to the partition, for inspecting and corrupting state.</summary>
    public IDatabase Database => (_admin ?? throw new InvalidOperationException("The fixture has not started.")).GetDatabase();

    public Action<GarnetJournalStorageOptions>? Configure { get; init; }

    public static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orleans-garnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static GarnetJournalStorageProvider CreateProvider(string dataDirectory, Action<GarnetJournalStorageOptions>? configure = null, string? journalFormatKey = null)
    {
        var options = new GarnetJournalStorageOptions
        {
            DataDirectory = dataDirectory,
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
        _provider = CreateProvider(DataDirectory, Configure);
        await _provider.StartAsync(CancellationToken.None);
        _admin = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { _provider.EmbeddedEndPoint! },
            Protocol = RedisProtocol.Resp2,
            AllowAdmin = true,
        });
    }

    /// <summary>Stops the provider, including its embedded partition, and starts it again from its data directory.</summary>
    internal async Task RestartAsync()
    {
        await DisposeConnectionsAsync();
        await InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionsAsync();
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

    private async Task DisposeConnectionsAsync()
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
}
