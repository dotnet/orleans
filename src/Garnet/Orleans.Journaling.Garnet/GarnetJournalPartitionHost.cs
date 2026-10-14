using System.Net;
using System.Net.Sockets;
using Garnet;
using Garnet.server;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Orleans.Journaling;

/// <summary>
/// Hosts one journal partition in an embedded Garnet server.
/// </summary>
/// <remarks>
/// <para>
/// The server writes every operation to its append-only file (AOF) and replies only after the AOF commit containing
/// the operation is flushed to the device (<c>--aof --aof-commit-freq 0 --aof-commit-wait</c>). Local devices write
/// with write-through and unbuffered I/O, so an acknowledged operation survives the loss of the process. Reads also
/// wait for the AOF commit covering every write they may have observed, so no read observes a write that a crash
/// could lose.
/// </para>
/// <para>
/// The journal procedures are registered before recovery, because recovery replays logged procedure invocations by
/// their registration ids.
/// </para>
/// </remarks>
internal sealed class GarnetJournalPartitionHost : IDisposable
{
    private readonly GarnetServer _server;

    private GarnetJournalPartitionHost(GarnetServer server, IPEndPoint endPoint)
    {
        _server = server;
        EndPoint = endPoint;
    }

    /// <summary>Gets the endpoint on which the partition accepts RESP connections.</summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>
    /// Starts the partition, recovering its state from <see cref="GarnetJournalPartitionSettings.DataDirectory"/>.
    /// </summary>
    public static GarnetJournalPartitionHost Start(GarnetJournalPartitionSettings settings, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.DataDirectory);
        var dataDirectory = Path.GetFullPath(settings.DataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var logger = loggerFactory?.CreateLogger<GarnetJournalPartitionHost>();
        var requested = settings.ListenEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
        var endPoint = requested.Port == 0 ? new IPEndPoint(requested.Address, GetFreePort(requested.Address)) : requested;
        var options = new GarnetServerOptions(logger)
        {
            EndPoints = [endPoint],
            EnableStorageTier = true,
            LogDir = Path.Combine(dataDirectory, "log"),
            CheckpointDir = Path.Combine(dataDirectory, "checkpoint"),
            Recover = true,
            FailOnRecoveryError = true,
            EnableAOF = true,
            CommitFrequencyMs = 0,
            WaitForCommit = true,
            DisablePubSub = true,
            EnableLua = false,
            QuietMode = true,
            LogMemorySize = settings.LogMemorySize,
            IndexMemorySize = settings.IndexMemorySize,

            // Checkpoint whenever the AOF exceeds this size, which bounds recovery time and lets Garnet truncate the
            // AOF; compact the hybrid log before each checkpoint, keeping live records.
            AofSizeLimit = settings.CheckpointAofSize,
            CompactionType = LogCompactionType.Lookup,
            // The RandomAccess device opens files with write-through and unbuffered I/O on every platform, so a
            // completed AOF flush is on stable storage rather than in the device's volatile cache.
            DeviceFactoryCreator = new LocalStorageNamedDeviceFactoryCreator(deviceType: DeviceType.RandomAccess, logger: logger),
        };

        var server = new GarnetServer(options, loggerFactory);
        try
        {
            GarnetJournalProcedures.Register(server);
            server.Start();
        }
        catch
        {
            server.Dispose(deleteDir: false);
            throw;
        }

        logger?.LogInformation("Started Garnet journal partition at {EndPoint} with data directory {DataDirectory}.", endPoint, dataDirectory);
        return new(server, endPoint);
    }

    /// <summary>Stops the partition. Its durable state remains in its data directory.</summary>
    public void Dispose() => _server.Dispose(deleteDir: false);

    private static int GetFreePort(IPAddress address)
    {
        using var listener = new TcpListener(address, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// Settings for an embedded journal partition.
/// </summary>
internal sealed class GarnetJournalPartitionSettings
{
    /// <summary>Gets or sets the directory holding the partition's AOF and checkpoints.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Gets or sets the endpoint to listen on. Port 0 chooses a free port. Defaults to a free loopback port.</summary>
    public IPEndPoint? ListenEndPoint { get; init; }

    /// <summary>Gets or sets the in-memory size of the hybrid log. Older records spill to the data directory.</summary>
    public string LogMemorySize { get; init; } = GarnetJournalStorageOptions.DEFAULT_LOG_MEMORY_SIZE;

    /// <summary>Gets or sets the size of the hash index.</summary>
    public string IndexMemorySize { get; init; } = GarnetJournalStorageOptions.DEFAULT_INDEX_MEMORY_SIZE;

    /// <summary>Gets or sets the AOF size at which the partition takes a checkpoint.</summary>
    public string CheckpointAofSize { get; init; } = GarnetJournalStorageOptions.DEFAULT_CHECKPOINT_AOF_SIZE;
}
