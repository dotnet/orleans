using System.Threading.Tasks;
using Orleans;
using System;
using System.Data.Services.Common;
using System.Linq;
using System.Text;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.StorageClient;

namespace UnitTestGrainInterfaces
{
    [Serializable]
    [DataServiceKey("PartitionKey", "RowKey")]
    public class UnitTestAzureData : TableServiceEntity
    {
        public byte[] Data { get; set; }
        public string StringData { get; set; }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("UnitTestAzureData[");
            sb.Append(" PartitionKey=").Append(PartitionKey);
            sb.Append(" RowKey=").Append(RowKey);
            sb.Append(" ]");
            return sb.ToString();
        }
    }

    public interface IAzureStorageAccessSMGrain : IGrain
    {
        Task<string> GetLabel();

        Task SetAzureStorageConnectionString(string connectionString);

        Task Echo(byte[] data);
    }

    public interface IAzureTableStorageAccessSMGrain : IAzureStorageAccessSMGrain, IGrain
    {
        Task WriteToAzureTable(string partitionKey, string rowKey, byte[] data);

        Task<byte[]> ReadFromAzureTable(string partitionKey, string rowKey);
    }

    public interface IAzureBlobStorageAccessSMGrain : IAzureStorageAccessSMGrain, IGrain
    {
        Task WriteToAzureBlob(string containerName, string blobName, byte[] data);

        Task<byte[]> ReadFromAzureBlob(string containerName, string blobName);
    }
}
