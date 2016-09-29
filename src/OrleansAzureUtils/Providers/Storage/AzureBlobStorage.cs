using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Blob.Protocol;
using Newtonsoft.Json;
using Orleans.Providers;
using Orleans.Providers.Azure;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace Orleans.Storage
{
    /// <summary>
    /// Simple storage provider for writing grain state data to Azure blob storage in JSON format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required configuration params: <c>DataConnectionString</c>
    /// </para>
    /// <para>
    /// Optional configuration params:
    /// <c>ContainerName</c> -- defaults to <c>grainstate</c>
    /// <c>SerializeTypeNames</c> -- defaults to <c>OrleansGrainState</c>
    /// <c>PreserveReferencesHandling</c> -- defaults to <c>false</c>
    /// <c>UseFullAssemblyNames</c> -- defaults to <c>false</c>
    /// <c>IndentJSON</c> -- defaults to <c>false</c>
    /// </para>
    /// </remarks>
    /// <example>
    /// Example configuration for this storage provider in OrleansConfiguration.xml file:
    /// <code>
    /// &lt;OrleansConfiguration xmlns="urn:orleans">
    ///   &lt;Globals>
    ///     &lt;StorageProviders>
    ///       &lt;Provider Type="Orleans.Storage.AzureBlobStorage" Name="AzureStore"
    ///         DataConnectionString="UseDevelopmentStorage=true"
    ///       />
    ///   &lt;/StorageProviders>
    /// </code>
    /// </example>
    public class AzureBlobStorage : IStorageProvider
    {
        internal const string DataConnectionStringPropertyName = AzureTableStorage.DataConnectionStringPropertyName;
        internal const string ContainerNamePropertyName = "ContainerName";
        internal const string ContainerNameDefaultValue = "grainstate";

        private JsonSerializerSettings jsonSettings;

        private CloudBlobContainer container;

        /// <summary> Logger used by this storage provider instance. </summary>
        /// <see cref="IStorageProvider.Log"/>
        public Logger Log { get; private set; }

        /// <summary> Name of this storage provider instance. </summary>
        /// <see cref="IProvider.Name"/>
        public string Name { get; private set; }

        /// <summary> Initialization function for this storage provider. </summary>
        /// <see cref="IProvider.Init"/>
        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Log = providerRuntime.GetLogger("Storage.AzureBlobStorage");

            try
            {
                this.Name = name;
                this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(OrleansJsonSerializer.GetDefaultSerializerSettings(), config);

                if (!config.Properties.ContainsKey(DataConnectionStringPropertyName)) throw new BadProviderConfigException($"The {DataConnectionStringPropertyName} setting has not been configured in the cloud role. Please add a {DataConnectionStringPropertyName} setting with a valid Azure Storage connection string.");

                var account = CloudStorageAccount.Parse(config.Properties[DataConnectionStringPropertyName]);
                var blobClient = account.CreateCloudBlobClient();
                var containerName = config.Properties.ContainsKey(ContainerNamePropertyName) ? config.Properties[ContainerNamePropertyName] : ContainerNameDefaultValue;
                container = blobClient.GetContainerReference(containerName);
                await container.CreateIfNotExistsAsync().ConfigureAwait(false);

                Log.Info((int)AzureProviderErrorCode.AzureBlobProvider_InitProvider, "Init: Name={0} ServiceId={1} {2}", name, providerRuntime.ServiceId.ToString(), string.Join(" ", FormatPropertyMessage(config)));
                Log.Info((int)AzureProviderErrorCode.AzureBlobProvider_ParamConnectionString, "AzureBlobStorage Provider is using DataConnectionString: {0}", ConfigUtilities.PrintDataConnectionInfo(config.Properties["DataConnectionString"]));
            }
            catch (Exception ex)
            {
                Log.Error((int)AzureProviderErrorCode.AzureBlobProvider_InitProvider, ex.ToString(), ex);
                throw;
            }
        }

        IEnumerable<string> FormatPropertyMessage(IProviderConfiguration config)
        {
            var properties = new[]
            {
                ContainerNamePropertyName,
                "SerializeTypeNames",
                "PreserveReferencesHandling",
                OrleansJsonSerializer.UseFullAssemblyNamesProperty,
                OrleansJsonSerializer.IndentJsonProperty
            };
            foreach (var property in properties)
            {
                if (!config.Properties.ContainsKey(property)) continue;
                yield return string.Format("{0}={1}", property, config.Properties[property]);
            }
        }

        /// <summary> Shutdown this storage provider. </summary>
        /// <see cref="IProvider.Close"/>
        public Task Close()
        {
            return TaskDone.Done;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.ReadStateAsync"/>
        public async Task ReadStateAsync(string grainType, GrainReference grainId, IGrainState grainState)
        {
            var blobName = GetBlobName(grainType, grainId);
            if (this.Log.IsVerbose3) this.Log.Verbose3((int)AzureProviderErrorCode.AzureBlobProvider_Storage_Reading, "Reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);

            try
            {
                var blob = container.GetBlockBlobReference(blobName);

                string json;

                try
                {
                    json = await blob.DownloadTextAsync().ConfigureAwait(false);
                }
                catch (StorageException exception)
                {
                    var errorCode = exception.RequestInformation.ExtendedErrorInformation.ErrorCode;
                    if (errorCode == BlobErrorCodeStrings.BlobNotFound)
                    {
                        if (this.Log.IsVerbose2) this.Log.Verbose2((int)AzureProviderErrorCode.AzureBlobProvider_BlobNotFound, "BlobNotFound reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
                        return;
                    }
                    if (errorCode == BlobErrorCodeStrings.ContainerNotFound)
                    {
                        if (this.Log.IsVerbose2) this.Log.Verbose2((int)AzureProviderErrorCode.AzureBlobProvider_ContainerNotFound, "ContainerNotFound reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
                        return;
                    }

                    throw;
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    if (this.Log.IsVerbose2) this.Log.Verbose2((int)AzureProviderErrorCode.AzureBlobProvider_BlobEmpty, "BlobEmpty reading: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
                    return;
                }

                grainState.State = JsonConvert.DeserializeObject(json, grainState.State.GetType(), jsonSettings);
                grainState.ETag = blob.Properties.ETag;

                if (this.Log.IsVerbose3) this.Log.Verbose3((int)AzureProviderErrorCode.AzureBlobProvider_Storage_DataRead, "Read: GrainType={0} Grainid={1} ETag={2} from BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
            }
            catch (Exception ex)
            {
                Log.Error((int)AzureProviderErrorCode.AzureBlobProvider_ReadError,
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
        /// <see cref="IStorageProvider.WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainId, IGrainState grainState)
        {
            var blobName = GetBlobName(grainType, grainId);
            try
            {
                if (this.Log.IsVerbose3) this.Log.Verbose3((int)AzureProviderErrorCode.AzureBlobProvider_Storage_Writing, "Writing: GrainType={0} Grainid={1} ETag={2} to BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);

                var json = JsonConvert.SerializeObject(grainState.State, jsonSettings);

                var blob = container.GetBlockBlobReference(blobName);
                blob.Properties.ContentType = "application/json";

                var containerNotFound = false;
                try
                {
                    await blob.UploadTextAsync(
                            json,
                            Encoding.UTF8,
                            AccessCondition.GenerateIfMatchCondition(grainState.ETag),
                            null,
                            null).ConfigureAwait(false);
                }
                catch (StorageException exception)
                {
                    var errorCode = exception.RequestInformation.ExtendedErrorInformation.ErrorCode;
                    containerNotFound = errorCode == BlobErrorCodeStrings.ContainerNotFound;
                }
                if (containerNotFound)
                {
                    // if the container does not exist, create it, and make another attempt
                    if (this.Log.IsVerbose3) this.Log.Verbose3((int)AzureProviderErrorCode.AzureBlobProvider_ContainerNotFound, "Creating container: GrainType={0} Grainid={1} ETag={2} to BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
                    await container.CreateIfNotExistsAsync().ConfigureAwait(false);

                    await blob.UploadTextAsync(
                        json,
                        Encoding.UTF8,
                        AccessCondition.GenerateIfMatchCondition(grainState.ETag),
                        null,
                        null).ConfigureAwait(false);
                }

                grainState.ETag = blob.Properties.ETag;

                if (this.Log.IsVerbose3) this.Log.Verbose3((int)AzureProviderErrorCode.AzureBlobProvider_Storage_DataRead, "Written: GrainType={0} Grainid={1} ETag={2} to BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
            }
            catch (Exception ex)
            {
                Log.Error((int)AzureProviderErrorCode.AzureBlobProvider_WriteError,
                    string.Format("Error writing: GrainType={0} Grainid={1} ETag={2} to BlobName={3} in Container={4} Exception={5}", grainType, grainId, grainState.ETag, blobName, container.Name, ex.Message),
                    ex);

                throw;
            }
        }

        /// <summary> Clear / Delete state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.ClearStateAsync"/>
        public async Task ClearStateAsync(string grainType, GrainReference grainId, IGrainState grainState)
        {
            var blobName = GetBlobName(grainType, grainId);
            try
            {
                if (this.Log.IsVerbose3) this.Log.Verbose3((int)AzureProviderErrorCode.AzureBlobProvider_ClearingData, "Clearing: GrainType={0} Grainid={1} ETag={2} BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);

                var blob = container.GetBlockBlobReference(blobName);
                await blob.DeleteIfExistsAsync(
                        DeleteSnapshotsOption.None,
                        AccessCondition.GenerateIfMatchCondition(grainState.ETag),
                        null,
                        null).ConfigureAwait(false);
                grainState.ETag = blob.Properties.ETag;

                if (this.Log.IsVerbose3) this.Log.Verbose3((int)AzureProviderErrorCode.AzureBlobProvider_Cleared, "Cleared: GrainType={0} Grainid={1} ETag={2} BlobName={3} in Container={4}", grainType, grainId, grainState.ETag, blobName, container.Name);
            }
            catch (Exception ex)
            {
                Log.Error((int)AzureProviderErrorCode.AzureBlobProvider_ClearError,
                  string.Format("Error clearing: GrainType={0} Grainid={1} ETag={2} BlobName={3} in Container={4} Exception={5}", grainType, grainId, grainState.ETag, blobName, container.Name, ex.Message),
                  ex);

                throw;
            }
        }
    }
}
