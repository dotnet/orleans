#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Azure;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.Storage
{
    /// <summary>
    /// Simple storage provider for writing grain state data to Azure blob storage in JSON format.
    /// </summary>
    public class AzureBlobGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ILogger logger;
        private readonly string name;
        private readonly IBlobContainerFactory blobContainerFactory;
        private readonly IActivatorProvider _activatorProvider;
        private readonly AzureBlobStorageOptions options;
        private readonly IGrainStorageSerializer grainStorageSerializer;

        /// <summary> Default constructor </summary>
        public AzureBlobGrainStorage(
            string name,
            AzureBlobStorageOptions options,
            IBlobContainerFactory blobContainerFactory,
            IActivatorProvider activatorProvider,
            ILogger<AzureBlobGrainStorage> logger)
        {
            this.name = name;
            this.options = options;
            this.blobContainerFactory = blobContainerFactory;
            _activatorProvider = activatorProvider;
            this.grainStorageSerializer = options.GrainStorageSerializer; 
            this.logger = logger;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync{T}"/>
        public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var blobName = GetBlobName(grainType, grainId);
            var container = this.blobContainerFactory.GetBlobContainerClient(grainId);

            if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.LogTrace((int)AzureProviderErrorCode.AzureBlobProvider_Storage_Reading,
                "Reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}",
                grainType,
                grainId,
                grainState.ETag,
                blobName,
                container.Name);

            try
            {
                var blob = container.GetBlobClient(blobName);

                var response = await blob.DownloadContentAsync();
                grainState.ETag = response.Value.Details.ETag.ToString();
                var contents = response.Value.Content;
                T? loadedState;
                if (contents is null || contents.IsEmpty)
                {
                    loadedState = default;
                    if (this.logger.IsEnabled(LogLevel.Trace))
                    {
                        this.logger.LogTrace((int)AzureProviderErrorCode.AzureBlobProvider_BlobEmpty,
                        "BlobEmpty reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}",
                        grainType,
                        grainId,
                        grainState.ETag,
                        blobName,
                        container.Name);
                    }
                }
                else
                {
                    loadedState = this.ConvertFromStorageFormat<T>(contents);
                    if (this.logger.IsEnabled(LogLevel.Trace))
                    {
                        this.logger.LogTrace((int)AzureProviderErrorCode.AzureBlobProvider_Storage_DataRead,
                        "Read: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}",
                        grainType,
                        grainId,
                        grainState.ETag,
                        blobName,
                        container.Name);
                    }
                }

                grainState.State = loadedState ?? CreateInstance<T>();
                grainState.RecordExists = loadedState is not null;
            }
            catch (RequestFailedException ex) when (ex.IsNotFound())
            {
                ResetGrainState(grainState);
                if (ex.IsBlobNotFound())
                {
                    if (this.logger.IsEnabled(LogLevel.Trace))
                    {
                        this.logger.LogTrace((int)AzureProviderErrorCode.AzureBlobProvider_BlobNotFound,
                        "BlobNotFound reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}",
                        grainType,
                        grainId,
                        grainState.ETag,
                        blobName,
                        container.Name);
                    }
                }
                else if (ex.IsContainerNotFound())
                {
                    if (this.logger.IsEnabled(LogLevel.Trace))
                    {
                        this.logger.LogTrace((int)AzureProviderErrorCode.AzureBlobProvider_ContainerNotFound,
                        "ContainerNotFound reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}",
                        grainType,
                        grainId,
                        grainState.ETag,
                        blobName,
                        container.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError((int)AzureProviderErrorCode.AzureBlobProvider_ReadError,
                    ex,
                    "Error reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from BlobName={BlobName} in Container={ContainerName}",
                    grainType,
                    grainId,
                    grainState.ETag,
                    blobName,
                    container.Name);

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
        /// <see cref="IGrainStorage.WriteStateAsync{T}"/>
        public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var blobName = GetBlobName(grainType, grainId);
            var container = this.blobContainerFactory.GetBlobContainerClient(grainId);

            try
            {
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.LogTrace((int)AzureProviderErrorCode.AzureBlobProvider_Storage_Writing,
                    "Writing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to BlobName={BlobName} in Container={ContainerName}",
                    grainType,
                    grainId,
                    grainState.ETag,
                    blobName,
                    container.Name);

                var contents = ConvertToStorageFormat(grainState.State);

                var blob = container.GetBlobClient(blobName);

                await WriteStateAndCreateContainerIfNotExists(grainType, grainId, grainState, contents, "application/octet-stream", blob);

                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.LogTrace((int)AzureProviderErrorCode.AzureBlobProvider_Storage_DataRead,
                    "Written: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to BlobName={BlobName} in Container={ContainerName}",
                    grainType,
                    grainId,
                    grainState.ETag,
                    blobName,
                    container.Name);
            }
            catch (Exception ex)
            {
                logger.LogError((int)AzureProviderErrorCode.AzureBlobProvider_WriteError,
                    ex,
                    "Error writing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to BlobName={BlobName} in Container={ContainerName}",
                    grainType,
                    grainId,
                    grainState.ETag,
                    blobName,
                    container.Name);

                throw;
            }
        }

        /// <summary> Clear / Delete state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ClearStateAsync{T}"/>
        public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var blobName = GetBlobName(grainType, grainId);
            var container = this.blobContainerFactory.GetBlobContainerClient(grainId);

            try
            {
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.LogTrace((int)AzureProviderErrorCode.AzureBlobProvider_ClearingData,
                    "Clearing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} BlobName={BlobName} in Container={ContainerName}",
                    grainType,
                    grainId,
                    grainState.ETag,
                    blobName,
                    container.Name);

                var blob = container.GetBlobClient(blobName);

                var conditions = string.IsNullOrEmpty(grainState.ETag)
                    ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                    : new BlobRequestConditions { IfMatch = new ETag(grainState.ETag) };

                if (options.DeleteStateOnClear)
                {
                    await DoOptimisticUpdate(
                        static state => state.blob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, conditions: state.conditions),
                        (blob, conditions),
                        blob,
                        grainState.ETag).ConfigureAwait(false);
                    grainState.ETag = null;
                }
                else
                {
                    var options = new BlobUploadOptions { Conditions = conditions };
                    var response = await DoOptimisticUpdate(
                        static state => state.blob.UploadAsync(BinaryData.Empty, state.options),
                        (blob, options, conditions),
                        blob,
                        grainState.ETag).ConfigureAwait(false);
                    grainState.ETag = response.Value.ETag.ToString();
                }

                grainState.RecordExists = false;
                grainState.State = CreateInstance<T>();
                if (this.logger.IsEnabled(LogLevel.Trace))
                {
                    this.logger.LogTrace((int)AzureProviderErrorCode.AzureBlobProvider_Cleared,
                        "Cleared: GrainType={GrainType} GrainId={GrainId} ETag={ETag} BlobName={BlobName} in Container={ContainerName}",
                        grainType,
                        grainId,
                        grainState.ETag,
                        blobName,
                        container.Name);
                }
            }
            catch (Exception ex)
            {
                logger.LogError((int)AzureProviderErrorCode.AzureBlobProvider_ClearError,
                    ex,
                    "Error clearing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} BlobName={BlobName} in Container={ContainerName}",
                    grainType,
                    grainId,
                    grainState.ETag,
                    blobName,
                    container.Name);

                throw;
            }
        }

        private async Task WriteStateAndCreateContainerIfNotExists<T>(string grainType, GrainId grainId, IGrainState<T> grainState, BinaryData contents, string mimeType, BlobClient blob)
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
                };

                var result = await DoOptimisticUpdate(
                    static state => state.blob.UploadAsync(state.contents, state.options),
                    (blob, contents, options),
                    blob,
                    grainState.ETag)
                        .ConfigureAwait(false);

                grainState.ETag = result.Value.ETag.ToString();
                grainState.RecordExists = true;
            }
            catch (RequestFailedException exception) when (exception.IsContainerNotFound())
            {
                // if the container does not exist, create it, and make another attempt
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.LogTrace((int)AzureProviderErrorCode.AzureBlobProvider_ContainerNotFound,
                    "Creating container: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to BlobName={BlobName} in Container={ContainerName}",
                    grainType,
                    grainId,
                    grainState.ETag,
                    blob.Name,
                    container.Name);
                await container.CreateIfNotExistsAsync().ConfigureAwait(false);

                await WriteStateAndCreateContainerIfNotExists(grainType, grainId, grainState, contents, mimeType, blob).ConfigureAwait(false);
            }
        }

        private static async Task<TResult> DoOptimisticUpdate<TState, TResult>(Func<TState, Task<TResult>> updateOperation, TState state, BlobClient blob, string currentETag)
        {
            try
            {
                return await updateOperation(state).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.IsPreconditionFailed() || ex.IsConflict() || ex.IsNotFound())
            {
                throw new InconsistentStateException($"Blob storage condition not Satisfied. BlobName: {blob.Name}, Container: {blob.BlobContainerName}, CurrentETag: {currentETag}", "Unknown", currentETag, ex);
            }
        }

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
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureTableProvider_InitProvider, "AzureBlobGrainStorage initializing: {Options}", this.options.ToString());

                if (options.CreateClient is not { } createClient)
                {
                    throw new OrleansConfigurationException($"No credentials specified. Use the {options.GetType().Name}.{nameof(AzureBlobStorageOptions.ConfigureBlobServiceClient)} method to configure the Azure Blob Service client.");
                }

                var client = await createClient();
                await this.blobContainerFactory.InitializeAsync(client);
                stopWatch.Stop();
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureBlobProvider_InitProvider,
                    "Initializing provider {ProviderName} of type {ProviderType} in stage {Stage} took {ElapsedMilliseconds} Milliseconds.",
                    this.name,
                    this.GetType().Name,
                    this.options.InitStage,
                    stopWatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopWatch.Stop();
                this.logger.LogError((int)ErrorCode.Provider_ErrorFromInit,
                    ex,
                    "Initialization failed for provider {ProviderName} of type {ProviderType} in stage {Stage} in {ElapsedMilliseconds} Milliseconds.",
                    this.name,
                    this.GetType().Name,
                    this.options.InitStage,
                    stopWatch.ElapsedMilliseconds);
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
    }

    public static class AzureBlobGrainStorageFactory
    {
        public static AzureBlobGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>();
            var options = optionsMonitor.Get(name);

            var containerFactory = options.BuildContainerFactory(services, options);

            return ActivatorUtilities.CreateInstance<AzureBlobGrainStorage>(services, name, options, containerFactory);
        }
    }
}
