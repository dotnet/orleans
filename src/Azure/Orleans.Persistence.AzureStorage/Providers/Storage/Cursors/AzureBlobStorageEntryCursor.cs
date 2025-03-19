using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Persistence.AzureStorage.Providers.Storage.Cursors
{
    internal class AzureBlobStorageEntryCursor
    {
        public string BlobName { get; }

        public AzureBlobStorageEntryCursor(string blobName)
        {
            BlobName = blobName;
        }
    }
}
