using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace TestExtensions;

/// <summary>
/// Manages a singleton Azurite container for Azure Storage integration tests.
/// The container is lazily started on first use and shared across all tests in the process.
/// </summary>
public static class AzuriteContainer
{
    private const int BlobPort = 10000;
    private const int QueuePort = 10001;
    private const int TablePort = 10002;

    // Well-known Azurite development credentials.
    private const string AccountName = "devstoreaccount1";
    private const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static IContainer _container;
    private static string _connectionString;
    private static readonly object _lock = new();
    private static Task<bool> _startTask;

    /// <summary>
    /// Gets the connection string for the running Azurite container.
    /// Returns <see langword="null"/> if the container has not been started.
    /// </summary>
    public static string ConnectionString => _connectionString;

    /// <summary>
    /// Ensures Azurite is available. If the default ports are already reachable (e.g. an external
    /// Azurite process or CI service container), the existing instance is reused. Otherwise, a
    /// Testcontainer is started.
    /// </summary>
    /// <returns><see langword="true"/> if Azurite is available; <see langword="false"/> if it could not be started.</returns>
    public static Task<bool> EnsureStartedAsync()
    {
        if (_startTask is not null)
        {
            return _startTask;
        }

        lock (_lock)
        {
            _startTask ??= StartAsync();
        }

        return _startTask;
    }

    private static async Task<bool> StartAsync()
    {
        // First, check whether Azurite is already reachable on the default ports (e.g. CI service container or manual process).
        if (IsPortReachable(BlobPort) && IsPortReachable(QueuePort) && IsPortReachable(TablePort))
        {
            _connectionString = BuildConnectionString("127.0.0.1", BlobPort, QueuePort, TablePort);
            return true;
        }

        try
        {
            var container = new ContainerBuilder()
                .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
                .WithPortBinding(BlobPort, true)
                .WithPortBinding(QueuePort, true)
                .WithPortBinding(TablePort, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(BlobPort))
                .Build();

            await container.StartAsync();

            var host = container.Hostname;
            var blobPort = container.GetMappedPublicPort(BlobPort);
            var queuePort = container.GetMappedPublicPort(QueuePort);
            var tablePort = container.GetMappedPublicPort(TablePort);

            _connectionString = BuildConnectionString(host, blobPort, queuePort, tablePort);
            _container = container;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildConnectionString(string host, int blobPort, int queuePort, int tablePort)
    {
        return $"DefaultEndpointsProtocol=http;AccountName={AccountName};AccountKey={AccountKey};"
             + $"BlobEndpoint=http://{host}:{blobPort}/{AccountName};"
             + $"QueueEndpoint=http://{host}:{queuePort}/{AccountName};"
             + $"TableEndpoint=http://{host}:{tablePort}/{AccountName};";
    }

    private static bool IsPortReachable(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
