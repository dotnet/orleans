using Microsoft.Azure.Storage;

#if ORLEANS_PERSISTENCE
namespace Orleans.Persistence.AzureStorage
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.AzureStorage
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// General utility functions related to Azure Blob storage.
    /// </summary>
    internal static class AzureBlobUtils
    {
        internal static void ValidateContainerName(string containerName)
        {
            NameValidator.ValidateContainerName(containerName);
        }

        internal static void ValidateBlobName(string blobName)
        {
            NameValidator.ValidateBlobName(blobName);
        }
    }
}
