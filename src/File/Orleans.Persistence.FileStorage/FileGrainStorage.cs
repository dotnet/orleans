using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Storage;

namespace Orleans.Persistence.FileStorage;

/// <summary>
/// Stores grain state as files in a configured directory.
/// </summary>
/// <param name="storageName">The storage provider name.</param>
/// <param name="options">The provider options.</param>
/// <param name="clusterOptions">The cluster options.</param>
/// <param name="activatorProvider">The grain state activator provider.</param>
public sealed class FileGrainStorage(
    string storageName,
    FileGrainStorageOptions options,
    IOptions<ClusterOptions> clusterOptions,
    IActivatorProvider activatorProvider) : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private const string LockFileNamePrefix = ".orleans-file-storage.";
    private const int LockStripeCount = 256;
    private const string RecordExtension = ".grain";
    private const int RecordHeaderLength = 24;
    private static readonly SemaphoreSlim[] RecordLocks =
        Enumerable.Range(0, LockStripeCount).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly byte[] RecordMagic = "ORLFS001"u8.ToArray();
    private readonly ClusterOptions _clusterOptions = clusterOptions.Value;
    private readonly TimeSpan _lockAcquireTimeout = options.LockAcquireTimeout;
    private readonly string _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootDirectory));

    /// <inheritdoc />
    public async Task ClearStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var location = GetRecordLocation(stateName, grainId);
        using (await AcquireRecordLockAsync(location.LockIndex).ConfigureAwait(false))
        {
            if (!File.Exists(location.Path))
            {
                ResetMissingState(grainState);
                return;
            }

            var record = await ReadRecordAsync(location.Path).ConfigureAwait(false);
            if (!string.Equals(record.ETag, grainState.ETag, StringComparison.Ordinal))
            {
                throw CreateInconsistentStateException<T>("ClearState", grainId);
            }

            File.Delete(location.Path);
            ResetMissingState(grainState);
        }
    }

    /// <inheritdoc />
    public async Task ReadStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var location = GetRecordLocation(stateName, grainId);
        using (await AcquireRecordLockAsync(location.LockIndex).ConfigureAwait(false))
        {
            if (!File.Exists(location.Path))
            {
                ResetMissingState(grainState);
                return;
            }

            var record = await ReadRecordAsync(location.Path).ConfigureAwait(false);
            grainState.State = options.GrainStorageSerializer.Deserialize<T>(new BinaryData(record.Payload));
            grainState.ETag = record.ETag;
            grainState.RecordExists = true;
        }
    }

    /// <inheritdoc />
    public async Task WriteStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var location = GetRecordLocation(stateName, grainId);
        using (await AcquireRecordLockAsync(location.LockIndex).ConfigureAwait(false))
        {
            if (File.Exists(location.Path))
            {
                var record = await ReadRecordAsync(location.Path).ConfigureAwait(false);
                if (!string.Equals(record.ETag, grainState.ETag, StringComparison.Ordinal))
                {
                    throw CreateInconsistentStateException<T>("WriteState", grainId);
                }
            }
            else if (grainState.ETag is not null)
            {
                throw CreateInconsistentStateException<T>("WriteState", grainId);
            }

            var payload = options.GrainStorageSerializer.Serialize(grainState.State).ToArray();
            var etag = Guid.NewGuid().ToString("N");
            var recordBytes = CreateRecord(etag, payload);
            var temporaryPath = $"{location.Path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, recordBytes).ConfigureAwait(false);
                File.Move(temporaryPath, location.Path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            grainState.ETag = etag;
            grainState.RecordExists = true;
        }
    }

    /// <inheritdoc />
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(
            observerName: OptionFormattingUtilities.Name<FileGrainStorage>(storageName),
            stage: ServiceLifecycleStage.ApplicationServices,
            onStart: (ct) =>
            {
                Directory.CreateDirectory(_rootDirectory);
                return Task.CompletedTask;
            });

    private static byte[] CreateRecord(string etag, byte[] payload)
    {
        var result = new byte[RecordHeaderLength + payload.Length];
        RecordMagic.CopyTo(result, 0);
        Guid.ParseExact(etag, "N").TryWriteBytes(result.AsSpan(RecordMagic.Length, 16));
        payload.CopyTo(result, RecordHeaderLength);
        return result;
    }

    private static async Task<StoredRecord> ReadRecordAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        if (bytes.Length < RecordHeaderLength || !bytes.AsSpan(0, RecordMagic.Length).SequenceEqual(RecordMagic))
        {
            throw new InvalidDataException($"The file storage record '{path}' has an invalid format.");
        }

        var etag = new Guid(bytes.AsSpan(RecordMagic.Length, 16)).ToString("N");
        return new StoredRecord(etag, bytes.AsMemory(RecordHeaderLength));
    }

    private async Task<RecordLock> AcquireRecordLockAsync(int lockIndex)
    {
        Directory.CreateDirectory(_rootDirectory);
        var stopwatch = Stopwatch.StartNew();
        var semaphore = RecordLocks[lockIndex];
        if (!await semaphore.WaitAsync(_lockAcquireTimeout).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"Timed out acquiring the file storage lock for stripe {lockIndex:X2}.");
        }

        try
        {
            var lockPath = Path.Combine(_rootDirectory, $"{LockFileNamePrefix}{lockIndex:X2}.lock");
            while (true)
            {
                try
                {
                    var lockStream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    return new RecordLock(lockStream, semaphore);
                }
                catch (IOException) when (stopwatch.Elapsed < _lockAcquireTimeout)
                {
                    var remaining = _lockAcquireTimeout - stopwatch.Elapsed;
                    await Task.Delay(
                        remaining < TimeSpan.FromMilliseconds(10)
                            ? remaining
                            : TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    throw new TimeoutException(
                        $"Timed out acquiring the file storage lock '{lockPath}'.",
                        exception);
                }
            }
        }
        catch
        {
            semaphore.Release();
            throw;
        }
    }

    private void ResetMissingState<T>(IGrainState<T> grainState)
    {
        grainState.State = activatorProvider.GetActivator<T>().Create();
        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private InconsistentStateException CreateInconsistentStateException<T>(string operation, GrainId grainId) =>
        new($"""
            Version conflict ({operation}): ServiceId={_clusterOptions.ServiceId}
            ProviderName={storageName} GrainType={typeof(T)}
            GrainReference={grainId}.
            """);

    private StorageRecordLocation GetRecordLocation(string stateName, GrainId grainId)
    {
        using var identity = new MemoryStream();
        WriteIdentityComponent(identity, Encoding.UTF8.GetBytes(_clusterOptions.ServiceId));
        WriteIdentityComponent(identity, grainId.Type.AsSpan());
        WriteIdentityComponent(identity, grainId.Key.AsSpan());
        WriteIdentityComponent(identity, Encoding.UTF8.GetBytes(stateName));

        var hash = SHA256.HashData(identity.GetBuffer().AsSpan(0, checked((int)identity.Length)));
        var fileName = $"{Convert.ToHexString(hash)}{RecordExtension}";
        var path = Path.GetFullPath(Path.Combine(_rootDirectory, fileName));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(path), _rootDirectory, comparison))
        {
            throw new InvalidOperationException($"The storage record path '{path}' is outside the configured root directory.");
        }

        return new StorageRecordLocation(path, hash[0]);
    }

    private static void WriteIdentityComponent(Stream destination, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        destination.Write(length);
        destination.Write(value);
    }

    private sealed class RecordLock(FileStream stream, SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            semaphore.Release();
        }
    }

    private readonly record struct StorageRecordLocation(string Path, int LockIndex);

    private readonly record struct StoredRecord(string ETag, ReadOnlyMemory<byte> Payload);
}
