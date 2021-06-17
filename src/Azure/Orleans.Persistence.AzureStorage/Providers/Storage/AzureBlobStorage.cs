using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Providers.Azure;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.Serialization.TypeSystem;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.Storage
{
    /// <summary>
    /// Simple storage provider for writing grain state data to Azure blob storage in JSON format.
    /// </summary>
    public class AzureBlobGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private JsonSerializerSettings jsonSettings;

        private BlobContainerClient container;
        private ILogger logger;
        private readonly string name;
        private AzureBlobStorageOptions options;
        private Serializer serializer;
        private readonly IServiceProvider services;

        /// <summary> Default constructor </summary>
        public AzureBlobGrainStorage(
            string name,
            AzureBlobStorageOptions options,
            Serializer serializer,
            IServiceProvider services,
            ILogger<AzureBlobGrainStorage> logger)
        {
            this.name = name;
            this.options = options;
            this.serializer = serializer;
            this.services = services;
            this.logger = logger;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync"/>
        public async Task ReadStateAsync(string grainType, GrainReference grainId, IGrainState grainState)
        {
            var blobName = GetBlobName(grainType, grainId);
            if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_Storage_Reading, "Reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);

            try
            {
                var blob = container.GetBlobClient(blobName);

                byte[] contents;
                try
                {
                    using var stream = new MemoryStream();
                    var response = await blob.DownloadToAsync(stream).ConfigureAwait(false);
                    grainState.ETag = response.Headers.ETag.ToString();
                    contents = stream.ToArray();
                }
                catch (RequestFailedException exception) when (exception.IsBlobNotFound())
                {
                    if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_BlobNotFound, "BlobNotFound reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
                    return;
                }
                catch (RequestFailedException exception) when (exception.IsContainerNotFound())
                {
                    if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_ContainerNotFound, "ContainerNotFound reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
                    return;
                }

                if (contents == null || contents.Length == 0)
                {
                    if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_BlobEmpty, "BlobEmpty reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
                    grainState.RecordExists = false;
                    return;
                }
                else
                {
                    grainState.RecordExists = true;
                }

                grainState.State = this.ConvertFromStorageFormat(contents);

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

                var (contents, mimeType) = ConvertToStorageFormat(grainState.State);

                var blob = container.GetBlobClient(blobName);

                await WriteStateAndCreateContainerIfNotExists(grainType, grainId, grainState, contents, mimeType, blob);

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

                var blob = container.GetBlobClient(blobName);

                await DoOptimisticUpdate(() => blob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, conditions: new BlobRequestConditions { IfMatch = new ETag(grainState.ETag) }),
                    blob, grainState.ETag).ConfigureAwait(false);

                grainState.ETag = null;
                grainState.RecordExists = false;

                if (this.logger.IsEnabled(LogLevel.Trace))
                {
                    var properties = await blob.GetPropertiesAsync();
                    this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_Cleared, "Cleared: GrainType={0} Grainid={1} ETag={2} BlobName={3} in Container={4}", grainType, grainId, properties.Value.ETag, blobName, container.Name);
                }
            }
            catch (Exception ex)
            {
                logger.Error((int)AzureProviderErrorCode.AzureBlobProvider_ClearError,
                  string.Format("Error clearing: GrainType={0} Grainid={1} ETag={2} BlobName={3} in Container={4} Exception={5}", grainType, grainId, grainState.ETag, blobName, container.Name, ex.Message),
                  ex);

                throw;
            }
        }

        private async Task WriteStateAndCreateContainerIfNotExists(string grainType, GrainReference grainId, IGrainState grainState, byte[] contents, string mimeType, BlobClient blob)
        {
            try
            {
                using var stream = new MemoryStream(contents);
                var result = await DoOptimisticUpdate(() => blob.UploadAsync(stream,
                        conditions: new BlobRequestConditions { IfMatch = grainState.ETag != null ? new ETag(grainState.ETag) : (ETag?)null },
                        httpHeaders: new BlobHttpHeaders { ContentType = mimeType }),
                    blob, grainState.ETag).ConfigureAwait(false);

                grainState.ETag = result.Value.ETag.ToString();
                grainState.RecordExists = true;
            }
            catch (RequestFailedException exception) when (exception.IsContainerNotFound())
            {
                // if the container does not exist, create it, and make another attempt
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace((int)AzureProviderErrorCode.AzureBlobProvider_ContainerNotFound, "Creating container: GrainType={0} Grainid={1} ETag={2} to BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blob.Name, container.Name);
                await container.CreateIfNotExistsAsync().ConfigureAwait(false);

                await WriteStateAndCreateContainerIfNotExists(grainType, grainId, grainState, contents, mimeType, blob).ConfigureAwait(false);
            }
        }

        private static async Task<TResult> DoOptimisticUpdate<TResult>(Func<Task<TResult>> updateOperation, BlobClient blob, string currentETag)
        {
            try
            {
                return await updateOperation.Invoke().ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.IsPreconditionFailed() || ex.IsConflict())
            {
                throw new InconsistentStateException($"Blob storage condition not Satisfied.  BlobName: {blob.Name}, Container: {blob.BlobContainerName}, CurrentETag: {currentETag}", "Unknown", currentETag, ex);
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
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureTableProvider_InitProvider, $"AzureTableGrainStorage initializing: {this.options.ToString()}");
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureTableProvider_ParamConnectionString, "AzureTableGrainStorage is using DataConnectionString: {0}", ConfigUtilities.RedactConnectionStringInfo(this.options.ConnectionString));
                this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(OrleansJsonSerializer.GetDefaultSerializerSettings(this.services), this.options.UseFullAssemblyNames, this.options.IndentJson, this.options.TypeNameHandling);

                this.options.ConfigureJsonSerializerSettings?.Invoke(this.jsonSettings);

                var client = this.options.ServiceUri != null ? new BlobServiceClient(this.options.ServiceUri, this.options.TokenCredential) : new BlobServiceClient(this.options.ConnectionString);
                container = client.GetBlobContainerClient(this.options.ContainerName);
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

        /// <summary>
        /// Serialize to the configured storage format, either binary or JSON.
        /// </summary>
        /// <param name="grainState">The grain state data to be serialized</param>
        /// <remarks>
        /// See:
        /// http://msdn.microsoft.com/en-us/library/system.web.script.serialization.javascriptserializer.aspx
        /// for more on the JSON serializer.
        /// </remarks>
        private (byte[], string) ConvertToStorageFormat(object grainState)
        {
            byte[] data;
            string mimeType;
            if (this.options.UseJson)
            {
                data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(grainState, this.jsonSettings));
                mimeType = "application/json";
            }
            else
            {
                data = this.serializer.SerializeToArray(grainState);
                mimeType = "application/octet-stream";
            }

            return (data, mimeType);
        }

        /// <summary>
        /// Deserialize from the configured storage format, either binary or JSON.
        /// </summary>
        /// <param name="contents">The serialized contents.</param>
        /// <remarks>
        /// See:
        /// http://msdn.microsoft.com/en-us/library/system.web.script.serialization.javascriptserializer.aspx
        /// for more on the JSON serializer.
        /// </remarks>
        private object ConvertFromStorageFormat(byte[] contents)
        {
            object result;
            if (this.options.UseJson)
            {
                var str = Encoding.UTF8.GetString(contents);
                result = JsonConvert.DeserializeObject<object>(str, this.jsonSettings);
            }
            else
            {
                result = this.serializer.Deserialize<object>(contents);
            }

            return result;
        }
    }

    public static class AzureBlobGrainStorageFactory
    {
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>();
            return ActivatorUtilities.CreateInstance<AzureBlobGrainStorage>(services, name, optionsMonitor.Get(name));
        }
    }
}
