using System.Net;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob.Protocol;

namespace Orleans.Storage
{
    internal static class StorageExceptionExtensions
    {
        public static bool IsPreconditionFailed(this Microsoft.Azure.Cosmos.Table.StorageException storageException)
        {
            return storageException?.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed;
        }

        public static bool IsConflict(this Microsoft.Azure.Cosmos.Table.StorageException storageException)
        {
            return storageException?.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.Conflict;
        }

        public static bool IsNotFound(this Microsoft.Azure.Cosmos.Table.StorageException storageException)
        {
            return storageException?.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.NotFound;
        }

        public static bool IsPreconditionFailed(this StorageException storageException)
        {
            return storageException?.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed;
        }

        public static bool IsConflict(this StorageException storageException)
        {
            return storageException?.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.Conflict;
        }

        public static bool IsContainerNotFound(this StorageException storageException)
        {
            return storageException?.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.NotFound
                && storageException.RequestInformation.ExtendedErrorInformation.ErrorCode == BlobErrorCodeStrings.ContainerNotFound;
        }

        public static bool IsBlobNotFound(this StorageException storageException)
        {
            return storageException?.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.NotFound
                && storageException.RequestInformation.ExtendedErrorInformation.ErrorCode == BlobErrorCodeStrings.BlobNotFound;
        }
    }
}
