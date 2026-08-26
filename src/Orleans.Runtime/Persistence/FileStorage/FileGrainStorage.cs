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
    private const int RecordHeaderLength = 24;
    private static readonly byte[] RecordMagic = "ORLFS001"u8.ToArray();
    private readonly string _serviceId = clusterOptions.Value.ServiceId;
    private readonly string _rootDirectory = Path.GetFullPath(options.RootDirectory);

    /// <inheritdoc />
    public Task ClearStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState) =>
        ClearStateAsync(stateName, grainId, grainState, CancellationToken.None);

    Task IGrainStorage.ClearStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState,
        CancellationToken cancellationToken) =>
        ClearStateAsync(stateName, grainId, grainState, cancellationToken);

    private async Task ClearStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRecordPath(stateName, grainId);
        var record = await TryReadRecordAsync(path, cancellationToken).ConfigureAwait(false);
        if (record is not null)
        {
            ValidateETag<T>("ClearState", grainId, grainState.ETag, record.Value.ETag);
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }

        ResetState(grainState);
    }

    /// <inheritdoc />
    public Task ReadStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState) =>
        ReadStateAsync(stateName, grainId, grainState, CancellationToken.None);

    Task IGrainStorage.ReadStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState,
        CancellationToken cancellationToken) =>
        ReadStateAsync(stateName, grainId, grainState, cancellationToken);

    private async Task ReadStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = await TryReadRecordAsync(
            GetRecordPath(stateName, grainId),
            cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            ResetState(grainState);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        grainState.State = options.GrainStorageSerializer.Deserialize<T>(new BinaryData(record.Value.Payload));
        grainState.ETag = record.Value.ETag;
        grainState.RecordExists = true;
    }

    /// <inheritdoc />
    public Task WriteStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState) =>
        WriteStateAsync(stateName, grainId, grainState, CancellationToken.None);

    Task IGrainStorage.WriteStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState,
        CancellationToken cancellationToken) =>
        WriteStateAsync(stateName, grainId, grainState, cancellationToken);

    private async Task WriteStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRecordPath(stateName, grainId);
        var existingRecord = await TryReadRecordAsync(path, cancellationToken).ConfigureAwait(false);
        if (existingRecord is not null)
        {
            ValidateETag<T>("WriteState", grainId, grainState.ETag, existingRecord.Value.ETag);
        }
        else if (grainState.ETag is not null)
        {
            throw CreateInconsistentStateException<T>("WriteState", grainId);
        }

        var etag = Guid.NewGuid().ToString("N");
        cancellationToken.ThrowIfCancellationRequested();
        var payload = options.GrainStorageSerializer.Serialize(grainState.State).ToArray();
        await File.WriteAllBytesAsync(
            path,
            CreateRecord(etag, payload),
            cancellationToken).ConfigureAwait(false);

        grainState.ETag = etag;
        grainState.RecordExists = true;
    }

    /// <inheritdoc />
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(
            observerName: OptionFormattingUtilities.Name<FileGrainStorage>(storageName),
            stage: ServiceLifecycleStage.ApplicationServices,
            onStart: _ =>
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

    private static async Task<StoredRecord?> TryReadRecordAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < RecordHeaderLength ||
                !bytes.AsSpan(0, RecordMagic.Length).SequenceEqual(RecordMagic))
            {
                throw new InvalidDataException($"The file storage record '{path}' has an invalid format.");
            }

            var etag = new Guid(bytes.AsSpan(RecordMagic.Length, 16)).ToString("N");
            return new StoredRecord(etag, bytes.AsMemory(RecordHeaderLength));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private void ResetState<T>(IGrainState<T> grainState)
    {
        grainState.State = activatorProvider.GetActivator<T>().Create();
        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private void ValidateETag<T>(
        string operation,
        GrainId grainId,
        string? currentETag,
        string storedETag)
    {
        if (!string.Equals(currentETag, storedETag, StringComparison.Ordinal))
        {
            throw CreateInconsistentStateException<T>(operation, grainId);
        }
    }

    private InconsistentStateException CreateInconsistentStateException<T>(
        string operation,
        GrainId grainId) =>
        new($"Version conflict ({operation}): ServiceId={_serviceId} ProviderName={storageName} GrainType={typeof(T)} GrainReference={grainId}.");

    private string GetRecordPath(string stateName, GrainId grainId)
    {
        using var identity = new MemoryStream();
        WriteIdentityComponent(identity, Encoding.UTF8.GetBytes(_serviceId));
        WriteIdentityComponent(identity, grainId.Type.AsSpan());
        WriteIdentityComponent(identity, grainId.Key.AsSpan());
        WriteIdentityComponent(identity, Encoding.UTF8.GetBytes(stateName));

        var hash = SHA256.HashData(identity.GetBuffer().AsSpan(0, checked((int)identity.Length)));
        return Path.Combine(_rootDirectory, $"{Convert.ToHexString(hash)}.grain");
    }

    private static void WriteIdentityComponent(Stream destination, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        destination.Write(length);
        destination.Write(value);
    }

    private readonly record struct StoredRecord(string ETag, ReadOnlyMemory<byte> Payload);
}
