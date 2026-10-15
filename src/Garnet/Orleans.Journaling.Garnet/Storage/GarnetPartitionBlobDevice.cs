using Azure;
using Azure.Storage.Blobs.Models;
using Tsavorite.core;

namespace Orleans.Journaling;

/// <summary>
/// Supplies every device of an embedded Garnet server (hybrid log, append-only file, and checkpoints) from one hosting
/// tenure's fenced partition storage (D12).
/// </summary>
/// <remarks>
/// Pass an instance as <c>GarnetServerOptions.DeviceFactoryCreator</c>. Each base name, such as the server's log or
/// checkpoint directory, becomes a path prefix within the partition's storage location.
/// </remarks>
internal sealed class GarnetPartitionBlobDeviceFactoryCreator(GarnetPartitionBlobStorage storage) : INamedDeviceFactoryCreator
{
    public INamedDeviceFactory Create(string baseName) => new GarnetPartitionBlobDeviceFactory(storage, GarnetPartitionBlobStorage.NormalizePath(baseName ?? string.Empty));
}

/// <summary>
/// Names the devices of one base directory within a partition's fenced storage. Listing and deletion use the
/// partition's manifest, so a blob outside it is invisible.
/// </summary>
internal sealed class GarnetPartitionBlobDeviceFactory(GarnetPartitionBlobStorage storage, string baseName) : INamedDeviceFactory
{
    public IDevice Get(FileDescriptor fileInfo) => new GarnetPartitionBlobDevice(storage, Combine(baseName, fileInfo.directoryName, fileInfo.fileName));

    public void Delete(FileDescriptor fileInfo)
    {
        // A file's segments are named file.0, file.1, and so on; a descriptor without a file names a whole directory, as
        // do the descriptors ListContents returns for directories, whose file name is empty.
        var prefix = string.IsNullOrEmpty(fileInfo.fileName)
            ? Combine(baseName, fileInfo.directoryName) is { Length: > 0 } directory ? directory + "/" : string.Empty
            : Combine(baseName, fileInfo.directoryName, fileInfo.fileName) + ".";
        var names = storage.List(prefix).Select(entry => entry.Name).ToList();
        storage.DeleteAsync(names, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <remarks>Lists directories, then files, each most recently created first, as the local device factory does.</remarks>
    public IEnumerable<FileDescriptor> ListContents(string path)
    {
        var root = Combine(baseName, path);
        var prefix = root.Length == 0 ? string.Empty : root + "/";
        var directories = new Dictionary<string, long>(StringComparer.Ordinal);
        var files = new List<(string Name, long Sequence)>();
        foreach (var (name, sequence) in storage.List(prefix))
        {
            var relative = name[prefix.Length..];
            var slash = relative.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0)
            {
                var directory = relative[..slash];
                directories[directory] = Math.Max(sequence, directories.GetValueOrDefault(directory, long.MinValue));
            }
            else
            {
                files.Add((relative, sequence));
            }
        }

        foreach (var (directory, _) in directories.OrderByDescending(entry => entry.Value))
        {
            yield return new FileDescriptor(directory, string.Empty);
        }

        foreach (var (file, _) in files.OrderByDescending(entry => entry.Sequence))
        {
            yield return new FileDescriptor(string.Empty, file);
        }
    }

    private static string Combine(params string?[] parts)
        => string.Join('/', parts.Select(part => GarnetPartitionBlobStorage.NormalizePath(part ?? string.Empty)).Where(part => part.Length > 0));
}

/// <summary>
/// A Tsavorite device whose segments are page blobs in a partition's fenced storage. Every write is conditioned on the
/// tenure's lease, so it fails once a later tenure has taken the storage over.
/// </summary>
internal sealed class GarnetPartitionBlobDevice : StorageDeviceBase
{
    private const uint PageBlobSectorSize = 512;
    private const int MaxUploadSize = 4 * 1024 * 1024;

    // The size of blobs for unsegmented devices, such as checkpoint files. Page blobs are billed for written pages only.
    private const long UnsegmentedBlobSize = 64L * 1024 * 1024 * 1024;

    private readonly GarnetPartitionBlobStorage _storage;
    private readonly string _name;

    public GarnetPartitionBlobDevice(GarnetPartitionBlobStorage storage, string name)
        : base(name, PageBlobSectorSize, Devices.CAPACITY_UNSPECIFIED)
    {
        _storage = storage;
        _name = name;

        // The existing segments are the longest contiguous run ending at the last one.
        var segments = storage.List(name + ".")
            .Select(entry => int.TryParse(entry.Name.AsSpan(name.Length + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var segment) ? segment : -1)
            .Where(segment => segment >= 0)
            .Order()
            .ToList();
        if (segments.Count > 0)
        {
            endSegment = startSegment = segments[^1];
            for (var i = segments.Count - 2; i >= 0 && segments[i] == startSegment - 1; i--)
            {
                startSegment = segments[i];
            }
        }
    }

    public override void WriteAsync(IntPtr sourceAddress, int segmentId, ulong destinationAddress, uint numBytesToWrite, DeviceIOCompletionCallback callback, object context)
        => _ = WriteCoreAsync(sourceAddress, segmentId, (long)destinationAddress, numBytesToWrite, callback, context);

    public override void ReadAsync(int segmentId, ulong sourceAddress, IntPtr destinationAddress, uint readLength, DeviceIOCompletionCallback callback, object context)
        => _ = ReadCoreAsync(segmentId, (long)sourceAddress, destinationAddress, readLength, callback, context);

    public override void RemoveSegmentAsync(int segment, AsyncCallback callback, IAsyncResult result)
        => _ = RemoveSegmentCoreAsync(segment, callback, result);

    public override long GetFileSize(int segment) => _storage.GetSize(GetSegmentFilename(_name, segment)) ?? 0;

    public override void Dispose()
    {
    }

    private async Task WriteCoreAsync(IntPtr source, int segment, long destination, uint length, DeviceIOCompletionCallback callback, object context)
    {
        Exception? failure = null;
        try
        {
            if (_storage.Failure is { } failed)
            {
                throw failed;
            }

            var blobName = GetSegmentFilename(_name, segment);
            await _storage.EnsureCreatedAsync(blobName, segmentSize > 0 ? segmentSize : UnsegmentedBlobSize).ConfigureAwait(false);
            var blob = _storage.GetPageBlob(blobName);
            var options = new PageBlobUploadPagesOptions { Conditions = _storage.WriteConditions };
            for (var offset = 0L; offset < length; offset += MaxUploadSize)
            {
                var count = Math.Min(MaxUploadSize, length - offset);
                using var stream = CreateStream(source + (nint)offset, count, FileAccess.Read);
                await blob.UploadPagesAsync(stream, destination + offset, options).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = _storage.Fail(exception);
        }

        callback(failure is null ? 0 : uint.MaxValue, length, context, failure);
    }

    private async Task ReadCoreAsync(int segment, long source, IntPtr destination, uint length, DeviceIOCompletionCallback callback, object context)
    {
        Exception? failure = null;
        try
        {
            var blobName = GetSegmentFilename(_name, segment);
            var size = _storage.GetSize(blobName) ?? 0;
            var available = Math.Clamp(size - source, 0, length);
            using var target = CreateStream(destination, length, FileAccess.Write);
            if (available > 0)
            {
                // The manifest lists the blob only once it exists, so a missing blob is lost data, which the read reports.
                var response = await _storage.GetPageBlob(blobName)
                    .DownloadStreamingAsync(new BlobDownloadOptions { Range = new HttpRange(source, available) })
                    .ConfigureAwait(false);
                await using var content = response.Value.Content;
                await content.CopyToAsync(target).ConfigureAwait(false);
            }

            // Pages beyond the blob, or never written, read as zeros.
            ZeroRemainder(destination, target.Position, length);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        callback(failure is null ? 0 : uint.MaxValue, length, context, failure);
    }

    private async Task RemoveSegmentCoreAsync(int segment, AsyncCallback callback, IAsyncResult result)
    {
        try
        {
            await _storage.DeleteAsync([GetSegmentFilename(_name, segment)], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _storage.Fail(exception);
        }

        callback(result);
    }

    private static unsafe UnmanagedMemoryStream CreateStream(IntPtr address, long length, FileAccess access)
        => new((byte*)address, length, length, access);

    private static unsafe void ZeroRemainder(IntPtr address, long start, long length)
    {
        if (start < length)
        {
            new Span<byte>((byte*)address + start, checked((int)(length - start))).Clear();
        }
    }
}
