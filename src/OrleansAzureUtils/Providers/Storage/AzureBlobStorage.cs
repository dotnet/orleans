using System;
using System.Runtime.Serialization.Formatters;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Blob.Protocol;
using Newtonsoft.Json;
using Orleans.Providers;
using Orleans.Runtime;

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
        private JsonSerializerSettings settings;

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
            Log = providerRuntime.GetLogger(this.GetType().Name);

            try
            {
                ConfigureJsonSerializerSettings(config);

                if (!config.Properties.ContainsKey("DataConnectionString")) throw new BadProviderConfigException("The DataConnectionString setting has not been configured in the cloud role. Please add a DataConnectionString setting with a valid Azure Storage connection string.");

                var account = CloudStorageAccount.Parse(config.Properties["DataConnectionString"]);
                var blobClient = account.CreateCloudBlobClient();
                var containerName = config.Properties.ContainsKey("ContainerName") ? config.Properties["ContainerName"] : "grainstate";
                container = blobClient.GetContainerReference(containerName);
                await container.CreateIfNotExistsAsync();
            }
            catch (Exception ex)
            {
                Log.Error(0, ex.ToString(), ex);
                throw;
            }
        }

        private void ConfigureJsonSerializerSettings(IProviderConfiguration config)
        {
            // By default, use automatic type name handling, simple assembly names, and no JSON formatting
            settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                TypeNameAssemblyFormat = FormatterAssemblyStyle.Simple,
                Formatting = Formatting.None
            };

            if (config.Properties.ContainsKey("SerializeTypeNames"))
            {
                bool serializeTypeNames = false;
                var serializeTypeNamesValue = config.Properties["SerializeTypeNames"];
                bool.TryParse(serializeTypeNamesValue, out serializeTypeNames);
                if (serializeTypeNames)
                {
                    settings.TypeNameHandling = TypeNameHandling.All;
                }
            }

            if (config.Properties.ContainsKey("PreserveReferencesHandling"))
            {
                bool preserveReferencesHandling;
                var preserveReferencesHandlingValue = config.Properties["PreserveReferencesHandling"];
                bool.TryParse(preserveReferencesHandlingValue, out preserveReferencesHandling);
                if (preserveReferencesHandling)
                {
                    settings.PreserveReferencesHandling = PreserveReferencesHandling.Objects;
                }
            }

            if (config.Properties.ContainsKey("UseFullAssemblyNames"))
            {
                bool useFullAssemblyNames = false;
                var UseFullAssemblyNamesValue = config.Properties["UseFullAssemblyNames"];
                bool.TryParse(UseFullAssemblyNamesValue, out useFullAssemblyNames);
                if (useFullAssemblyNames)
                {
                    settings.TypeNameAssemblyFormat = FormatterAssemblyStyle.Full;
                }
            }

            if (config.Properties.ContainsKey("IndentJSON"))
            {
                bool indentJSON = false;
                var indentJSONValue = config.Properties["IndentJSON"];
                bool.TryParse(indentJSONValue, out indentJSON);
                if (indentJSON)
                {
                    settings.Formatting = Formatting.Indented;
                }
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
            try
            {
                var blobName = GetBlobName(grainType, grainId);
                var blob = container.GetBlockBlobReference(blobName);

                string text;

                try
                {
                    text = await blob.DownloadTextAsync();
                }
                catch (StorageException exception)
                {
                    var errorCode = exception.RequestInformation.ExtendedErrorInformation.ErrorCode;

                    if (errorCode == BlobErrorCodeStrings.ContainerNotFound || errorCode == BlobErrorCodeStrings.BlobNotFound)
                    {
                        return;
                    }
                    throw;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                grainState.State = JsonConvert.DeserializeObject(text, grainState.State.GetType(), settings);
                grainState.ETag = blob.Properties.ETag;
            }
            catch (Exception ex)
            {
                Log.Error(0, ex.ToString());
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
            try
            {
                var blobName = GetBlobName(grainType, grainId);
                var storedData = JsonConvert.SerializeObject(grainState.State, settings);
                Log.Verbose("Serialized grain state is: {0}.", storedData);

                var blob = container.GetBlockBlobReference(blobName);
                blob.Properties.ContentType = "application/json";
                await blob.UploadTextAsync(
                        storedData,
                        Encoding.UTF8,
                        AccessCondition.GenerateIfMatchCondition(grainState.ETag),
                        null,
                        null);
                grainState.ETag = blob.Properties.ETag;
            }
            catch (Exception ex)
            {
                Log.Error(0, ex.ToString());
            }
        }

        /// <summary> Clear / Delete state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.ClearStateAsync"/>
        public async Task ClearStateAsync(string grainType, GrainReference grainId, IGrainState grainState)
        {
            try
            {
                var blobName = GetBlobName(grainType, grainId);
                var blob = container.GetBlockBlobReference(blobName);
                await blob.DeleteIfExistsAsync(
                        DeleteSnapshotsOption.None,
                        AccessCondition.GenerateIfMatchCondition(grainState.ETag),
                        null,
                        null);
                grainState.ETag = blob.Properties.ETag;
            }
            catch (Exception ex)
            {
                Log.Error(0, ex.ToString());
            }
        }
    }
}
