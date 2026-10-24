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
/// with write-through and unbuffered I/O, so an acknowledged operation survives the loss of the process; blob devices
/// (<see cref="GarnetPartitionBlobStorage"/>) write page blobs under the hosting tenure's lease, so it survives the loss
/// of the machine, and a host fenced by a later tenure fails every later commit. Reads also wait for an AOF commit,
/// which covers every write they may have observed, so no read observes a write that a crash could lose.
/// </para>
/// <para>
/// The journal procedures are registered before recovery, because recovery replays logged procedure invocations by
/// their registration ids.
/// </para>
/// </remarks>
internal sealed class GarnetJournalPartitionHost : IDisposable
{
    private readonly GarnetServer _server;

    private GarnetJournalPartitionHost(GarnetServer server, IPEndPoint endPoint, GarnetJournalLoad load)
    {
        _server = server;
        EndPoint = endPoint;
        Load = load;
    }

    /// <summary>Gets the partition's per-bucket load counters.</summary>
    public GarnetJournalLoad Load { get; }

    /// <summary>Gets the endpoint on which the partition accepts RESP connections.</summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>
    /// Starts the partition, recovering its state from its storage: <see cref="GarnetJournalPartitionSettings.BlobStorage"/>
    /// if set, otherwise <see cref="GarnetJournalPartitionSettings.DataDirectory"/>.
    /// </summary>
    public static GarnetJournalPartitionHost Start(GarnetJournalPartitionSettings settings, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var logger = loggerFactory?.CreateLogger<GarnetJournalPartitionHost>();
        string logDirectory, checkpointDirectory, location;
        INamedDeviceFactoryCreator devices;
        if (settings.BlobStorage is { } blobStorage)
        {
            // Directory names become prefixes within the partition's storage location.
            logDirectory = "log";
            checkpointDirectory = "checkpoint";
            devices = new GarnetPartitionBlobDeviceFactoryCreator(blobStorage);
            location = $"blob storage at epoch {blobStorage.Epoch}";
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(settings.DataDirectory);
            var dataDirectory = Path.GetFullPath(settings.DataDirectory);
            Directory.CreateDirectory(dataDirectory);
            logDirectory = Path.Combine(dataDirectory, "log");
            checkpointDirectory = Path.Combine(dataDirectory, "checkpoint");

            // The RandomAccess device opens files with write-through and unbuffered I/O on every platform, so a
            // completed AOF flush is on stable storage rather than in the device's volatile cache.
            devices = new LocalStorageNamedDeviceFactoryCreator(deviceType: DeviceType.RandomAccess, logger: logger);
            location = $"data directory {dataDirectory}";
        }

        var requested = settings.ListenEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
        var endPoint = requested.Port == 0 ? new IPEndPoint(requested.Address, GetFreePort(requested.Address)) : requested;
        var options = new GarnetServerOptions(logger)
        {
            EndPoints = [endPoint],
            EnableStorageTier = true,
            LogDir = logDirectory,
            CheckpointDir = checkpointDirectory,
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
            DeviceFactoryCreator = devices,
        };

        var server = new GarnetServer(options, loggerFactory);
        var load = new GarnetJournalLoad();
        try
        {
            GarnetJournalProcedures.Register(server, load);
            server.Start();

            // Recovery replayed logged operations, which the balancer must not count as load.
            load.Reset();
        }
        catch
        {
            server.Dispose(deleteDir: false);
            throw;
        }

        logger?.LogInformation("Started Garnet journal partition at {EndPoint} with {Location}.", endPoint, location);
        return new(server, endPoint, load);
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
    /// <summary>Gets or sets the directory holding the partition's AOF and checkpoints, if <see cref="BlobStorage"/> is not set.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>
    /// Gets or sets the hosting tenure's fenced storage, which holds the partition's AOF, hybrid log, and checkpoints in
    /// shared blob storage (D12).
    /// </summary>
    public GarnetPartitionBlobStorage? BlobStorage { get; init; }

    /// <summary>Gets or sets the endpoint to listen on. Port 0 chooses a free port. Defaults to a free loopback port.</summary>
    public IPEndPoint? ListenEndPoint { get; init; }

    /// <summary>Gets or sets the in-memory size of the hybrid log. Older records spill to the data directory.</summary>
    public string LogMemorySize { get; init; } = GarnetJournalStorageOptions.DEFAULT_LOG_MEMORY_SIZE;

    /// <summary>Gets or sets the size of the hash index.</summary>
    public string IndexMemorySize { get; init; } = GarnetJournalStorageOptions.DEFAULT_INDEX_MEMORY_SIZE;

    /// <summary>Gets or sets the AOF size at which the partition takes a checkpoint.</summary>
    public string CheckpointAofSize { get; init; } = GarnetJournalStorageOptions.DEFAULT_CHECKPOINT_AOF_SIZE;
}
