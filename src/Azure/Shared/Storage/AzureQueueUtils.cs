using System;
using Microsoft.Azure.Storage;

#if ORLEANS_STREAMING
namespace Orleans.Streaming.AzureStorage
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// General utility functions related to Azure Queue storage.
    /// </summary>
    internal static class AzureQueueUtils
    {
        internal static CloudStorageAccount GetCloudStorageAccount(string storageConnectionString)
        {
            // Connection string must be specified always, even for development storage.
            if (string.IsNullOrEmpty(storageConnectionString))
            {
                throw new ArgumentException("Azure storage connection string cannot be blank");
            }
            else
            {
                return CloudStorageAccount.Parse(storageConnectionString);
            }
        }
    }
}
