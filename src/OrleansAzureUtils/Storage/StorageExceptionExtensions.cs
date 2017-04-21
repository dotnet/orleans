using System.Net;
using Microsoft.WindowsAzure.Storage;

namespace Orleans.Storage
{
    internal static class StorageExceptionExtensions
    {
        internal static bool IsPreconditionFailed(this StorageException storageException)
        {
            return storageException?.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed;
        }
    }
}
