using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Providers.Azure;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.Storage
{
    /// <summary>
    /// Simple storage provider for writing grain state data to Azure blob storage in JSON format.
    /// </summary>
    public class AzureBlobGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private JsonSerializerSettings jsonSettings;

        private CloudBlobContainer container;
        private ILogger logger;
        private readonly string name;
        private AzureBlobStorageOptions options;
        private SerializationManager serializationManager;
        private IGrainFactory grainFactory;
        private ITypeResolver typeResolver;
        private ILoggerFactory loggerFactory;

        /// <summary> Default constructor </summary>
        public AzureBlobGrainStorage(
            string name,
            AzureBlobStorageOptions options, 
            SerializationManager serializationManager, 
            IGrainFactory grainFactory, 
            ITypeResolver typeResolver, 
            ILoggerFactory loggerFactory)
        {
            this.name = name;
            this.options = options;
            this.serializationManager = serializationManager;
            this.grainFactory = grainFactory;
            this.typeResolver = typeResolver;
            this.loggerFactory = loggerFactory;
            this.logger = this.loggerFactory.CreateLogger($"{typeof(AzureTableGrainStorageFactory).FullName}.{name}");
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync"/>
        public async Task ReadStateAsync(string grainType, GrainReference grainId, IGrainState grainState)
        {
            var blobName = GetBlobName(grainType, grainId);
            if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_Storage_Reading, "Reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);

            try
            {
                var blob = container.GetBlockBlobReference(blobName);

                string json;

                try
                {
                    json = await blob.DownloadTextAsync().ConfigureAwait(false);
                }
                catch (StorageException exception) when (exception.IsBlobNotFound())
                {
                    if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_BlobNotFound, "BlobNotFound reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
                    return;
                }
                catch (StorageException exception) when (exception.IsContainerNotFound())
                {
                    if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_ContainerNotFound, "ContainerNotFound reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
                    return;
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_BlobEmpty, "BlobEmpty reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
                    return;
                }

                grainState.State = JsonConvert.DeserializeObject(json, grainState.State.GetType(), jsonSettings);
                grainState.ETag = blob.Properties.ETag;

                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_Storage_DataRead, "Read: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
            }
            catch (Exception ex)
            {
                logger.Error((int)AzureProviderErrorCode.AzureBlobProvider_ReadError,
                    string.Format("Error reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4} Exception={5}", grainType, grainId, grainState.ETag, blobName, container.Name, ex.Message),
                    ex);

                throw;
            }
        }

        private static string GetBlobName(string grainType, GrainReference grainId)
        {
            return string.Format("{0}-{1}.json", grainType, grainId.ToKeyString());
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainId, IGrainState grainState)
        {
            var blobName = GetBlobName(grainType, grainId);
            try
            {
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_Storage_Writing, "Writing: GrainType={0} Grainid={1} ETag={2} to BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);

                var json = JsonConvert.SerializeObject(grainState.State, jsonSettings);

                var blob = container.GetBlockBlobReference(blobName);
                blob.Properties.ContentType = "application/json";

                await WriteStateAndCreateContainerIfNotExists(grainType, grainId, grainState, json, blob);

                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_Storage_DataRead, "Written: GrainType={0} Grainid={1} ETag={2} to BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
            }
            catch (Exception ex)
            {
                logger.Error((int)AzureProviderErrorCode.AzureBlobProvider_WriteError,
                    string.Format("Error writing: GrainType={0} Grainid={1} ETag={2} to BlobName={3} in Container={4} Exception={5}", grainType, grainId, grainState.ETag, blobName, container.Name, ex.Message),
                    ex);

                throw;
            }
        }

        /// <summary> Clear / Delete state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ClearStateAsync"/>
        public async Task ClearStateAsync(string grainType, GrainReference grainId, IGrainState grainState)
        {
            var blobName = GetBlobName(grainType, grainId);
            try
            {
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_ClearingData, "Clearing: GrainType={0} Grainid={1} ETag={2} BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);

                var blob = container.GetBlockBlobReference(blobName);

                await DoOptimisticUpdate(() => blob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, AccessCondition.GenerateIfMatchCondition(grainState.ETag), null, null),
                    blob, grainState.ETag).ConfigureAwait(false);

                grainState.ETag = null;

                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_Cleared, "Cleared: GrainType={0} Grainid={1} ETag={2} BlobName={3} in Container={4}", grainType, grainId, blob.Properties.ETag, blobName, container.Name);
            }
            catch (Exception ex)
            {
                logger.Error((int)AzureProviderErrorCode.AzureBlobProvider_ClearError,
                  string.Format("Error clearing: GrainType={0} Grainid={1} ETag={2} BlobName={3} in Container={4} Exception={5}", grainType, grainId, grainState.ETag, blobName, container.Name, ex.Message),
                  ex);

                throw;
            }
        }

        private async Task WriteStateAndCreateContainerIfNotExists(string grainType, GrainReference grainId, IGrainState grainState, string json, CloudBlockBlob blob)
        {
            try
            {
                await DoOptimisticUpdate(() => blob.UploadTextAsync(json, Encoding.UTF8, AccessCondition.GenerateIfMatchCondition(grainState.ETag), null, null),
                    blob, grainState.ETag).ConfigureAwait(false);

                grainState.ETag = blob.Properties.ETag;
            }
            catch (StorageException exception) when (exception.IsContainerNotFound())
            {
                // if the container does not exist, create it, and make another attempt
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_ContainerNotFound, "Creating container: GrainType={0} Grainid={1} ETag={2} to BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blob.Name, container.Name);
                await container.CreateIfNotExistsAsync().ConfigureAwait(false);

                await WriteStateAndCreateContainerIfNotExists(grainType, grainId, grainState, json, blob).ConfigureAwait(false);
            }
        }

        private static async Task DoOptimisticUpdate(Func<Task> updateOperation, CloudBlob blob, string currentETag)
        {
            try
            {
                await updateOperation.Invoke().ConfigureAwait(false);
            }
            catch (StorageException ex) when (ex.IsPreconditionFailed() || ex.IsConflict())
            {
                throw new InconsistentStateException($"Blob storage condition not Satisfied.  BlobName: {blob.Name}, Container: {blob.Container?.Name}, CurrentETag: {currentETag}", "Unknown", currentETag, ex);
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
                this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(OrleansJsonSerializer.GetDefaultSerializerSettings(this.typeResolver, this.grainFactory), this.options.UseFullAssemblyNames, this.options.IndentJson, this.options.TypeNameHandling);
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureTableProvider_InitProvider, $"AzureTableGrainStorage initializing: {this.options.ToString()}");
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureTableProvider_ParamConnectionString, "AzureTableGrainStorage is using DataConnectionString: {0}", ConfigUtilities.RedactConnectionStringInfo(this.options.ConnectionString));
                this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(OrleansJsonSerializer.GetDefaultSerializerSettings(this.typeResolver, this.grainFactory), this.options.UseFullAssemblyNames, this.options.IndentJson, this.options.TypeNameHandling);

                var account = CloudStorageAccount.Parse(this.options.ConnectionString);
                var blobClient = account.CreateCloudBlobClient();
                container = blobClient.GetContainerReference(this.options.ContainerName);
                await container.CreateIfNotExistsAsync().ConfigureAwait(false);
                stopWatch.Stop();
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureBlobProvider_InitProvider, $"Initializing provider {this.name} of type {this.GetType().Name} in stage {this.options.InitStage} took {stopWatch.ElapsedMilliseconds} Milliseconds.");
            }
            catch (Exception ex)
            {
                stopWatch.Stop();
                this.logger.LogError((int)ErrorCode.Provider_ErrorFromInit, $"Initialization failed for provider {this.name} of type {this.GetType().Name} in stage {this.options.InitStage} in {stopWatch.ElapsedMilliseconds} Milliseconds.", ex);
                throw;
            }
        }
    }

    public static class AzureBlobGrainStorageFactory
    {
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            IOptionsSnapshot<AzureBlobStorageOptions> optionsSnapshot = services.GetRequiredService<IOptionsSnapshot<AzureBlobStorageOptions>>();
            return ActivatorUtilities.CreateInstance<AzureBlobGrainStorage>(services, name, optionsSnapshot.Get(name));
        }
    }
}
