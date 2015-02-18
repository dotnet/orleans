using System;
using System.Data.Services.Common;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.AzureUtils;

using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    public abstract class AzureStorageAccessSMGrainBase : Grain, IAzureStorageAccessSMGrain
    {
        protected string storageConnectionString = "UseDevelopmentStorage=true";

        [NonSerialized] protected Logger logger;

        private string label;

        public Task Activate(string primaryKey)
        {
            logger = base.GetLogger();

            label = primaryKey;

            logger.Info("Activate - Key={0}", label);

            return TaskDone.Done;
        }

        public Task<string> GetLabel()
        {
            logger.Info("Getlabel - Key={0}", label);
            return Task.FromResult(label);
        }

        public Task SetAzureStorageConnectionString(string connectionString)
        {
            logger.Info("SetAzureStorageConnectionString - ConnectionString={0}", connectionString);
            this.storageConnectionString = connectionString;
            return TaskDone.Done;
        }

        public Task Echo(byte[] data)
        {
            logger.Info("Echo - byte[{0}]", data.Length);
            return TaskDone.Done;
        }
    }

    public class AzureTableStorageAccessSMGrain : AzureStorageAccessSMGrainBase, IAzureTableStorageAccessSMGrain
    {
        [NonSerialized] private UnitTestAzureDataReporter azureDataReporter;

        public override Task OnActivateAsync()
        {
            return base.Activate(this.GetPrimaryKeyLong().ToString());
        }

        public async Task WriteToAzureTable(string partitionKey, string rowKey, byte[] data)
        {
            logger.Info("WriteToAzureTable - PartitionKey={0} RowKey={1} byte[].Length={2}", partitionKey, rowKey, data.Length);
            if (data.Length > 32768) throw new ArgumentOutOfRangeException("data", "Cannot store more than 32768 bytes in an Azure table row field");
            UnitTestAzureDataReporter reporter = await GetAzureDataReporter();
            await reporter.WriteData(partitionKey, rowKey, data);
        }

        public async Task<byte[]> ReadFromAzureTable(string partitionKey, string rowKey)
        {
            logger.Info("ReadFromAzureTable - PartitionKey={0} RowKey={1}", partitionKey, rowKey);
            UnitTestAzureDataReporter reporter = await GetAzureDataReporter();
            return await reporter.ReadData(partitionKey, rowKey);
        }

        private async Task<UnitTestAzureDataReporter> GetAzureDataReporter()
        {
            if(azureDataReporter != null)
            {
                return azureDataReporter;
            }
            UnitTestAzureDataReporter reporter = new UnitTestAzureDataReporter(storageConnectionString);
            await reporter.InitTableAsync();
            azureDataReporter = reporter;
            return azureDataReporter;
        }
    }

    public class AzureBlobStorageAccessSMGrain : AzureStorageAccessSMGrainBase, IAzureBlobStorageAccessSMGrain
    {
        [NonSerialized] private CloudBlobClient blobClient;

        public override Task OnActivateAsync()
        {
            return base.Activate(this.GetPrimaryKeyLong().ToString());
        }

        public Task WriteToAzureBlob(string containerName, string blobName, byte[] data)
        {
            containerName = GetContainerName(containerName);
            logger.Info("WriteToAzureBlob - ContainerName={0} BlobName={1} byte[].Length={2}", containerName, blobName, data.Length);
            CloudBlobClient client = GetAzureBlobClient();
            CloudBlobContainer container = client.GetContainerReference(containerName);
            container.CreateIfNotExists();
            var blobReference = container.GetBlobReferenceFromServer(blobName);
            blobReference.UploadFromByteArray(data, 0, data.Length);
            return TaskDone.Done;
        }

        public Task<byte[]> ReadFromAzureBlob(string containerName, string blobName)
        {
            containerName = GetContainerName(containerName);
            logger.Info("ReadFromAzureBlob - ContainerName={0} BlobName={1}", containerName, blobName);
            CloudBlobClient client = GetAzureBlobClient();
            CloudBlobContainer container = client.GetContainerReference(containerName);
            container.CreateIfNotExists();
            var blobReference = container.GetBlobReferenceFromServer(blobName);
            byte[] data = new byte[1000*1000];
            blobReference.DownloadToByteArray(data, 0);
            return Task.FromResult(data);
        }

        internal static string GetContainerName(string name)
        {
            // Based on Azure container name rules:
            // http://msdn.microsoft.com/en-us/library/dd135715.aspx

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!Char.IsLetterOrDigit(c) && c != '-')
                {
                    // Can contain only letters, numbers, and the dash (-) character.
                    name = name.Substring(0, i) + '-' + name.Substring(i + 1);
                }
            }
            name = name.ToLower(); // Must be lower case only
            return name;
        }

        private CloudBlobClient GetAzureBlobClient()
        {
            if (blobClient == null)
            {
                var storageAccount = CloudStorageAccount.Parse(storageConnectionString);
                blobClient = storageAccount.CreateCloudBlobClient();
            }
            return blobClient;
        }
    }

    internal class UnitTestAzureDataReporter : AzureTableDataManager<UnitTestAzureData>
    {
        protected const string INSTANCE_TABLE_NAME = "UnitTestAzureData";

        public UnitTestAzureDataReporter(string storageConnectionString)
            : base(INSTANCE_TABLE_NAME, storageConnectionString)
        {
        }

        public async Task<byte[]> ReadData(string partitionKey, string rowKey)
        {
            var data = await ReadSingleTableEntryAsync(partitionKey, rowKey);
            return data.Item1.Data;
        }

        public Task WriteData(string partitionKey, string rowKey, byte[] data)
        {
            UnitTestAzureData dataObject = new UnitTestAzureData
            {
                PartitionKey = partitionKey,
                RowKey = rowKey,
                Data = data
            };
            return UpsertTableEntryAsync(dataObject);
        }
    }
}
