// <file_grain_storage>
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Storage;

namespace GrainStorage;

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

    // <clearstateasync>
    public async Task ClearStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var path = GetRecordPath(stateName, grainId);
        var record = await TryReadRecordAsync(path).ConfigureAwait(false);
        if (record is not null)
        {
            ValidateETag<T>("ClearState", grainId, grainState.ETag, record.Value.ETag);
            File.Delete(path);
        }

        ResetState(grainState);
    }
    // </clearstateasync>

    // <readstateasync>
    public async Task ReadStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var record = await TryReadRecordAsync(GetRecordPath(stateName, grainId)).ConfigureAwait(false);
        if (record is null)
        {
            ResetState(grainState);
            return;
        }

        grainState.State = options.GrainStorageSerializer.Deserialize<T>(new BinaryData(record.Value.Payload));
        grainState.ETag = record.Value.ETag;
        grainState.RecordExists = true;
    }
    // </readstateasync>

    // <writestateasync>
    public async Task WriteStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        Directory.CreateDirectory(_rootDirectory);
        var path = GetRecordPath(stateName, grainId);
        var existingRecord = await TryReadRecordAsync(path).ConfigureAwait(false);
        if (existingRecord is not null)
        {
            ValidateETag<T>("WriteState", grainId, grainState.ETag, existingRecord.Value.ETag);
        }
        else if (grainState.ETag is not null)
        {
            throw CreateInconsistentStateException<T>("WriteState", grainId);
        }

        var etag = Guid.NewGuid().ToString("N");
        var payload = options.GrainStorageSerializer.Serialize(grainState.State).ToArray();
        await File.WriteAllBytesAsync(path, CreateRecord(etag, payload)).ConfigureAwait(false);

        grainState.ETag = etag;
        grainState.RecordExists = true;
    }
    // </writestateasync>

    // <participate>
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(
            observerName: OptionFormattingUtilities.Name<FileGrainStorage>(storageName),
            stage: ServiceLifecycleStage.ApplicationServices,
            onStart: _ =>
            {
                Directory.CreateDirectory(_rootDirectory);
                return Task.CompletedTask;
            });
    // </participate>

    private static byte[] CreateRecord(string etag, byte[] payload)
    {
        var result = new byte[RecordHeaderLength + payload.Length];
        RecordMagic.CopyTo(result, 0);
        Guid.ParseExact(etag, "N").TryWriteBytes(result.AsSpan(RecordMagic.Length, 16));
        payload.CopyTo(result, RecordHeaderLength);
        return result;
    }

    private static async Task<StoredRecord?> TryReadRecordAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
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

    // <getkeystring>
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
    // </getkeystring>

    private readonly record struct StoredRecord(string ETag, ReadOnlyMemory<byte> Payload);
}
// </file_grain_storage>
