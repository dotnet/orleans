#nullable enable
using System;
using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Azure;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Serializers;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.Storage
{
    /// <summary>
    /// Simple storage provider for writing grain state data to Azure blob storage in JSON format.
    /// </summary>
    public partial class AzureBlobGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private const string UpdateTokenMetadataKey = "OrleansUpdateToken";
        private const string ContentHashMetadataKey = "ContentSha256";

        private readonly ILogger logger;
        private readonly string name;
        private readonly IBlobContainerFactory blobContainerFactory;
        private readonly IActivatorProvider _activatorProvider;
        private readonly AzureBlobStorageOptions options;
        private readonly IGrainStorageSerializer grainStorageSerializer;
        private readonly string _storageInstanceId = Guid.NewGuid().ToString();

        /// <summary> Default constructor </summary>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public AzureBlobGrainStorage(
            string name,
            AzureBlobStorageOptions options,
            IBlobContainerFactory blobContainerFactory,
            IActivatorProvider activatorProvider,
            ILogger<AzureBlobGrainStorage> logger)
        {
            ArgumentNullException.ThrowIfNull(options);

            this.name = name;
            this.options = options;
            this.blobContainerFactory = blobContainerFactory;
            _activatorProvider = activatorProvider;
            this.grainStorageSerializer = options.GrainStorageSerializer;
            this.logger = logger;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync{T}(string, GrainId, IGrainState{T})"/>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="grainType"/> or <paramref name="grainState"/> is <see langword="null"/>.
        /// </exception>
        public Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
            => ReadStateAsync(grainType, grainId, grainState, CancellationToken.None);

        /// <inheritdoc/>
        public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(grainType);
            ArgumentNullException.ThrowIfNull(grainState);
            cancellationToken.ThrowIfCancellationRequested();

            var blobName = GetBlobName(grainType, grainId);
            var container = this.blobContainerFactory.GetBlobContainerClient(grainId);

            LogTraceReading(grainType, grainId, grainState.ETag, blobName, container.Name);

            try
            {
                var blob = container.GetBlobClient(blobName);

                T? loadedState = this.grainStorageSerializer switch
                {
                    IGrainStorageStreamingSerializer serializer => await ReadStateWithStreamAsync(serializer, blob, grainType, grainId, grainState, blobName, container.Name, cancellationToken),
                    _ => await ReadStateWithPooledBufferAsync<T>(blob, grainType, grainId, grainState, blobName, container.Name, cancellationToken),
                };

                grainState.State = loadedState ?? CreateInstance<T>();
                grainState.RecordExists = loadedState is not null;
            }
            catch (RequestFailedException ex) when (ex.IsNotFound())
            {
                ResetGrainState(grainState);
                if (ex.IsBlobNotFound())
                {
                    LogTraceBlobNotFoundReading(grainType, grainId, grainState.ETag, blobName, container.Name);
                }
                else if (ex.IsContainerNotFound())
                {
                    LogTraceContainerNotFoundReading(grainType, grainId, grainState.ETag, blobName, container.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogErrorReading(ex, grainType, grainId, grainState.ETag, blobName, container.Name);
                throw;
            }
        }

        private void ResetGrainState<T>(IGrainState<T> grainState)
        {
            grainState.ETag = null;
            grainState.RecordExists = false;
            grainState.State = CreateInstance<T>();
        }

        private static string GetBlobName(string grainType, GrainId grainId) => $"{grainType}-{grainId}.json";

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.WriteStateAsync{T}(string, GrainId, IGrainState{T})"/>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="grainType"/> or <paramref name="grainState"/> is <see langword="null"/>.
        /// </exception>
        public Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
            => WriteStateAsync(grainType, grainId, grainState, CancellationToken.None);

        /// <inheritdoc/>
        public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(grainType);
            ArgumentNullException.ThrowIfNull(grainState);
            cancellationToken.ThrowIfCancellationRequested();

            var blobName = GetBlobName(grainType, grainId);
            var container = this.blobContainerFactory.GetBlobContainerClient(grainId);
            var enableIdempotentWrites = options.EnableIdempotentWrites;
            var streamingSerializer = this.grainStorageSerializer as IGrainStorageStreamingSerializer;
            var contents = streamingSerializer is null ? ConvertToStorageFormat(grainState.State) : null;
            var localHashBase64 = enableIdempotentWrites && contents is not null ? ComputeHashBase64(contents) : null;

            try
            {
                LogTraceWriting(grainType, grainId, grainState.ETag, blobName, container.Name);

                var blob = container.GetBlobClient(blobName);

                if (streamingSerializer is not null)
                {
                    await WriteStateBufferedStreamAndCreateContainerIfNotExists(streamingSerializer, grainType, grainId, grainState, "application/octet-stream", blob, cancellationToken);
                }
                else
                {
                    await WriteStateAndCreateContainerIfNotExists(grainType, grainId, grainState, contents!, localHashBase64, "application/octet-stream", blob, cancellationToken);
                }

                LogTraceDataWritten(grainType, grainId, grainState.ETag, blobName, container.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogErrorWriting(ex, grainType, grainId, grainState.ETag, blobName, container.Name);

                throw;
            }
        }

        /// <summary> Clear / Delete state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ClearStateAsync{T}(string, GrainId, IGrainState{T})"/>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="grainType"/> or <paramref name="grainState"/> is <see langword="null"/>.
        /// </exception>
        public Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
            => ClearStateAsync(grainType, grainId, grainState, CancellationToken.None);

        /// <inheritdoc/>
        public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(grainType);
            ArgumentNullException.ThrowIfNull(grainState);
            cancellationToken.ThrowIfCancellationRequested();

            var blobName = GetBlobName(grainType, grainId);
            var container = this.blobContainerFactory.GetBlobContainerClient(grainId);

            try
            {
                LogTraceClearing(grainType, grainId, grainState.ETag, blobName, container.Name);

                var blob = container.GetBlobClient(blobName);

                var conditions = string.IsNullOrEmpty(grainState.ETag)
                    ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                    : new BlobRequestConditions { IfMatch = new ETag(grainState.ETag) };

                if (options.DeleteStateOnClear)
                {
                    await DoOptimisticUpdate(
                        static (state, cancellationToken) => state.blob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, conditions: state.conditions, cancellationToken: cancellationToken),
                        (blob, conditions),
                        blob,
                        grainState.ETag,
                        cancellationToken).ConfigureAwait(false);
                    grainState.ETag = null;
                }
                else
                {
                    var emptyHashBase64 = options.EnableIdempotentWrites ? ComputeHashBase64(BinaryData.Empty) : null;
                    var uploadOptions = new BlobUploadOptions
                    {
                        Conditions = conditions,
                        Metadata = CreateWriteMetadata(emptyHashBase64),
                    };

                    try
                    {
                        var response = await blob.UploadAsync(BinaryData.Empty, uploadOptions, cancellationToken).ConfigureAwait(false);
                        grainState.ETag = response.Value.ETag.ToString();
                        grainState.RecordExists = false;
                    }
                    catch (RequestFailedException ex) when (IsWriteConflict(ex))
                    {
                        var attemptedETag = grainState.ETag;
                        if (!ex.IsPreconditionFailed()
                            || !options.EnableIdempotentWrites
                            || !await TryHandleIdempotentRetryAsync(blob, grainState, emptyHashBase64, cancellationToken))
                        {
                            throw CreateInconsistentStateException(blob, grainState.ETag, ex);
                        }

                        LogWarningIdempotentRetryResolved(
                            "clear",
                            ex.Status,
                            ex.ErrorCode,
                            grainType,
                            grainId,
                            blob.Name,
                            container.Name,
                            attemptedETag,
                            grainState.ETag);
                    }
                }

                grainState.RecordExists = false;
                grainState.State = CreateInstance<T>();
                LogTraceCleared(grainType, grainId, grainState.ETag, blobName, container.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogErrorClearing(ex, grainType, grainId, grainState.ETag, blobName, container.Name);

                throw;
            }
        }

        private async Task WriteStateAndCreateContainerIfNotExists<T>(string grainType, GrainId grainId, IGrainState<T> grainState, BinaryData contents, string? localHashBase64, string mimeType, BlobClient blob, CancellationToken cancellationToken)
        {
            var container = this.blobContainerFactory.GetBlobContainerClient(grainId);

            try
            {
                var conditions = string.IsNullOrEmpty(grainState.ETag)
                    ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                    : new BlobRequestConditions { IfMatch = new ETag(grainState.ETag) };

                var options = new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = mimeType },
                    Conditions = conditions,
                    Metadata = CreateWriteMetadata(localHashBase64),
                };

                try
                {
                    var result = await blob.UploadAsync(contents, options, cancellationToken).ConfigureAwait(false);
                    grainState.ETag = result.Value.ETag.ToString();
                    grainState.RecordExists = true;
                }
                catch (RequestFailedException ex) when (IsWriteConflict(ex))
                {
                    var attemptedETag = grainState.ETag;
                    if (ex.IsPreconditionFailed()
                        && this.options.EnableIdempotentWrites
                        && await TryHandleIdempotentRetryAsync(blob, grainState, localHashBase64, cancellationToken))
                    {
                        LogWarningIdempotentRetryResolved(
                            "write",
                            ex.Status,
                            ex.ErrorCode,
                            grainType,
                            grainId,
                            blob.Name,
                            container.Name,
                            attemptedETag,
                            grainState.ETag);
                        return;
                    }

                    throw CreateInconsistentStateException(blob, grainState.ETag, ex);
                }
            }
            catch (RequestFailedException exception) when (exception.IsContainerNotFound())
            {
                // if the container does not exist, create it, and make another attempt
                LogTraceContainerNotFound(grainType, grainId, grainState.ETag, blob.Name, container.Name);
                await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await WriteStateAndCreateContainerIfNotExists(grainType, grainId, grainState, contents, localHashBase64, mimeType, blob, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task WriteStateBufferedStreamAndCreateContainerIfNotExists<T>(IGrainStorageStreamingSerializer serializer, string grainType, GrainId grainId, IGrainState<T> grainState, string mimeType, BlobClient blob, CancellationToken cancellationToken)
        {
            var container = this.blobContainerFactory.GetBlobContainerClient(grainId);

            try
            {
                var conditions = string.IsNullOrEmpty(grainState.ETag)
                    ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                    : new BlobRequestConditions { IfMatch = new ETag(grainState.ETag) };

                var bufferStream = PooledBufferStream.Rent();
                try
                {
                    await serializer.SerializeAsync(grainState.State, bufferStream, cancellationToken).ConfigureAwait(false);
                    string? localHashBase64 = null;
                    if (options.EnableIdempotentWrites)
                    {
                        bufferStream.Position = 0;
                        localHashBase64 = Convert.ToBase64String(SHA256.HashData(bufferStream));
                    }

                    var uploadOptions = new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = mimeType },
                        Conditions = conditions,
                        Metadata = CreateWriteMetadata(localHashBase64),
                    };

                    try
                    {
                        bufferStream.Position = 0;
                        var result = await blob.UploadAsync(bufferStream, uploadOptions, cancellationToken).ConfigureAwait(false);
                        grainState.ETag = result.Value.ETag.ToString();
                        grainState.RecordExists = true;
                    }
                    catch (RequestFailedException ex) when (IsWriteConflict(ex))
                    {
                        var attemptedETag = grainState.ETag;
                        if (ex.IsPreconditionFailed()
                            && this.options.EnableIdempotentWrites
                            && await TryHandleIdempotentRetryAsync(blob, grainState, localHashBase64, cancellationToken))
                        {
                            LogWarningIdempotentRetryResolved(
                                "write",
                                ex.Status,
                                ex.ErrorCode,
                                grainType,
                                grainId,
                                blob.Name,
                                container.Name,
                                attemptedETag,
                                grainState.ETag);
                            return;
                        }

                        throw CreateInconsistentStateException(blob, grainState.ETag, ex);
                    }
                }
                finally
                {
                    PooledBufferStream.Return(bufferStream);
                }
            }
            catch (RequestFailedException exception) when (exception.IsContainerNotFound())
            {
                // if the container does not exist, create it, and make another attempt
                LogTraceContainerNotFound(grainType, grainId, grainState.ETag, blob.Name, container.Name);
                await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await WriteStateBufferedStreamAndCreateContainerIfNotExists(serializer, grainType, grainId, grainState, mimeType, blob, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<T?> ReadStateWithStreamAsync<T>(IGrainStorageStreamingSerializer serializer, BlobClient blob, string grainType, GrainId grainId, IGrainState<T> grainState, string blobName, string containerName, CancellationToken cancellationToken)
        {
            var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var content = response.Value.Content;
            var eTag = response.Value.Details.ETag.ToString();
            var contentLength = response.Value.Details.ContentLength;

            if (contentLength <= 0)
            {
                LogTraceBlobEmptyReading(grainType, grainId, eTag, blobName, containerName);
                grainState.ETag = eTag;
                return default;
            }

            var loadedState = await serializer.DeserializeAsync<T>(content, cancellationToken).ConfigureAwait(false);
            LogTraceDataRead(grainType, grainId, eTag, blobName, containerName);
            grainState.ETag = eTag;
            return loadedState;
        }

        private async Task<T?> ReadStateWithPooledBufferAsync<T>(BlobClient blob, string grainType, GrainId grainId, IGrainState<T> grainState, string blobName, string containerName, CancellationToken cancellationToken)
        {
            var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var content = response.Value.Content;
            var eTag = response.Value.Details.ETag.ToString();
            var contentLength = response.Value.Details.ContentLength;

            if (contentLength <= 0)
            {
                LogTraceBlobEmptyReading(grainType, grainId, eTag, blobName, containerName);
                grainState.ETag = eTag;
                return default;
            }

            T? loadedState;
            if (contentLength <= int.MaxValue)
            {
                var buffer = ArrayPool<byte>.Shared.Rent((int)contentLength);
                try
                {
                    var memory = buffer.AsMemory(0, (int)contentLength);
                    await content.ReadExactlyAsync(memory, cancellationToken);
                    loadedState = this.ConvertFromStorageFormat<T>(new BinaryData(memory));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else
            {
                loadedState = this.ConvertFromStorageFormat<T>(await BinaryData.FromStreamAsync(content, cancellationToken).ConfigureAwait(false));
                LogWarningLargePayloadFallback(contentLength, grainType, grainId, eTag, blobName, containerName);
            }

            LogTraceDataRead(grainType, grainId, eTag, blobName, containerName);
            grainState.ETag = eTag;
            return loadedState;
        }

        private static async Task<TResult> DoOptimisticUpdate<TState, TResult>(Func<TState, CancellationToken, Task<TResult>> updateOperation, TState state, BlobClient blob, string? currentETag, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await updateOperation(state, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.IsPreconditionFailed() || ex.IsConflict() || ex.IsNotFound() && !ex.IsContainerNotFound())
            {
                throw new InconsistentStateException($"Blob storage condition not Satisfied. BlobName: {blob.Name}, Container: {blob.BlobContainerName}, CurrentETag: {currentETag}", "Unknown", currentETag, ex);
            }
        }

        private static bool IsWriteConflict(RequestFailedException exception)
            => exception.IsPreconditionFailed() || exception.IsConflict() || exception.IsNotFound() && !exception.IsContainerNotFound();

        private static InconsistentStateException CreateInconsistentStateException(BlobClient blob, string? currentETag, RequestFailedException exception)
            => new($"Blob storage condition not Satisfied. BlobName: {blob.Name}, Container: {blob.BlobContainerName}, CurrentETag: {currentETag}", "Unknown", currentETag, exception);

        private async Task<bool> TryHandleIdempotentRetryAsync<T>(BlobClient blob, IGrainState<T> grainState, string? localHashBase64, CancellationToken cancellationToken)
        {
            try
            {
                var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                // Check if the stored OrleansUpdateToken matches this provider's instance ID
                if (properties.Value.Metadata.TryGetValue(UpdateTokenMetadataKey, out var storedToken) &&
                    storedToken == _storageInstanceId &&
                    localHashBase64 is not null &&
                    properties.Value.Metadata.TryGetValue(ContentHashMetadataKey, out var storedHash) &&
                    storedHash == localHashBase64)
                {
                    // This is an idempotent retry - the write already succeeded with this instance's token.
                    // The 412 occurred because the response was lost during network transmission.
                    // Update the ETag to the current server state.
                    grainState.ETag = properties.Value.ETag.ToString();
                    grainState.RecordExists = true;
                    return true;
                }

                // Token mismatch - this is a real conflict, not an idempotent retry
                return false;
            }
            catch (RequestFailedException ex) when (ex.IsNotFound())
            {
                // Blob doesn't exist - this is a real precondition failure
                return false;
            }
        }

        private Dictionary<string, string>? CreateWriteMetadata(string? localHashBase64)
        {
            if (!options.EnableIdempotentWrites || localHashBase64 is null)
            {
                return null;
            }

            return new Dictionary<string, string>
            {
                [UpdateTokenMetadataKey] = _storageInstanceId,
                [ContentHashMetadataKey] = localHashBase64,
            };
        }

        private static string ComputeHashBase64(BinaryData contents) => Convert.ToBase64String(SHA256.HashData(contents.ToArray()));

        /// <inheritdoc/>
        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<AzureBlobGrainStorage>(this.name), this.options.InitStage, Init);
        }

        /// <summary> Initialization function for this storage provider. </summary>
        private async Task Init(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();

            try
            {
                LogDebugInitializing(this.name, this.options.ContainerName);
                if (options.CreateClient is not { } createClient)
                {
                    throw new OrleansConfigurationException($"No credentials specified. Use the {options.GetType().Name}.{nameof(AzureBlobStorageOptions.ConfigureBlobServiceClient)} method to configure the Azure Blob Service client.");
                }

                var client = await createClient();
                await this.blobContainerFactory.InitializeAsync(client);
                stopWatch.Stop();
                LogInformationInitProvider(this.name, this.GetType().Name, this.options.InitStage, stopWatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopWatch.Stop();
                LogErrorFromInit(ex, this.name, this.GetType().Name, this.options.InitStage, stopWatch.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>
        /// Serialize to the configured storage format
        /// </summary>
        /// <param name="grainState">The grain state data to be serialized</param>
        private BinaryData ConvertToStorageFormat<T>(T grainState) => this.grainStorageSerializer.Serialize(grainState);

        /// <summary>
        /// Deserialize from the configured storage format
        /// </summary>
        /// <param name="contents">The serialized contents.</param>
        private T? ConvertFromStorageFormat<T>(BinaryData contents) => this.grainStorageSerializer.Deserialize<T>(contents);

        private T CreateInstance<T>() => _activatorProvider.GetActivator<T>().Create();

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_Storage_Reading,
            Message = "Reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogTraceReading(string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_BlobEmpty,
            Message = "BlobEmpty reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogTraceBlobEmptyReading(string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_Storage_DataRead,
            Message = "Read: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogTraceDataRead(string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_LargePayloadFallback,
            Message = "ContentLength={ContentLength} exceeds max array size; falling back to DownloadContentAsync. GrainType={GrainType} GrainId={GrainId} ETag={ETag} BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogWarningLargePayloadFallback(long contentLength, string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_ReadError,
            Message = "Error reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogErrorReading(Exception exception, string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_BlobNotFound,
            Message = "BlobNotFound reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogTraceBlobNotFoundReading(string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_ContainerNotFound,
            Message = "ContainerNotFound reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogTraceContainerNotFoundReading(string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_Storage_Writing,
            Message = "Writing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogTraceWriting(string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_Storage_DataRead,
            Message = "Written: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogTraceDataWritten(string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_WriteError,
            Message = "Error writing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogErrorWriting(Exception exception, string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_ClearingData,
            Message = "Clearing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogTraceClearing(string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_Cleared,
            Message = "Cleared: GrainType={GrainType} GrainId={GrainId} ETag={ETag} BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogTraceCleared(string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_IdempotentRetryResolved,
            Message = "Resolved an Azure Blob {Operation} conflict as a successful idempotent retry. Status={Status} ErrorCode={ErrorCode} GrainType={GrainType} GrainId={GrainId} BlobName={BlobName} Container={ContainerName} AttemptedETag={AttemptedETag} CurrentETag={CurrentETag}"
        )]
        private partial void LogWarningIdempotentRetryResolved(
            string operation,
            int status,
            string? errorCode,
            string grainType,
            GrainId grainId,
            string blobName,
            string containerName,
            string? attemptedETag,
            string? currentETag);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_ClearError,
            Message = "Error clearing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogErrorClearing(Exception exception, string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_ContainerNotFound,
            Message = "Creating container: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to BlobName={BlobName} in Container={ContainerName}"
        )]
        private partial void LogTraceContainerNotFound(string grainType, GrainId grainId, string? eTag, string blobName, string containerName);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)AzureProviderErrorCode.AzureTableProvider_InitProvider,
            Message = "AzureBlobGrainStorage {Name} is initializing: ContainerName={ContainerName}"
        )]
        private partial void LogDebugInitializing(string name, string containerName);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)AzureProviderErrorCode.AzureBlobProvider_InitProvider,
            Message = "Initializing provider {ProviderName} of type {ProviderType} in stage {Stage} took {ElapsedMilliseconds} Milliseconds."
        )]
        private partial void LogInformationInitProvider(string providerName, string providerType, int stage, long elapsedMilliseconds);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Provider_ErrorFromInit, Message = "Initialization failed for provider {ProviderName} of type {ProviderType} in stage {Stage} in {ElapsedMilliseconds} Milliseconds."
        )]
        private partial void LogErrorFromInit(Exception exception, string providerName, string providerType, int stage, long elapsedMilliseconds);
    }

    /// <summary>
    /// Creates configured <see cref="AzureBlobGrainStorage"/> instances.
    /// </summary>
    public static class AzureBlobGrainStorageFactory
    {
        /// <summary>
        /// Creates an Azure Blob grain storage provider with the specified name.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="name">The storage provider name.</param>
        /// <returns>The configured grain storage provider.</returns>
        public static AzureBlobGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>();
            var options = optionsMonitor.Get(name);

            var containerFactory = options.BuildContainerFactory(services, options);

            return ActivatorUtilities.CreateInstance<AzureBlobGrainStorage>(services, name, options, containerFactory);
        }
    }
}
