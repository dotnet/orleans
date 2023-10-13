using System;
using System.Linq;
using System.Text.RegularExpressions;

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
    internal static partial class AzureBlobUtils
    {
        [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.ExplicitCapture | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
        private static partial Regex ContainerNameRegex();

        internal static void ValidateContainerName(string containerName)
        {
            if (string.IsNullOrWhiteSpace(containerName) || containerName.Length < 3 || containerName.Length > 63 || !ContainerNameRegex().IsMatch(containerName))
            {
                throw new ArgumentException("Invalid container name", nameof(containerName));
            }
        }

        internal static void ValidateBlobName(string blobName)
        {
            if (string.IsNullOrWhiteSpace(blobName) || blobName.Length > 1024 || blobName.Count(c => c == '/') >= 254)
            {
                throw new ArgumentException("Invalid blob name", nameof(blobName));
            }
        }
    }
}
