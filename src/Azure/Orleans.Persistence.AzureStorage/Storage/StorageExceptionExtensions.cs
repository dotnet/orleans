using System.Net;
using Azure;
using Azure.Storage.Blobs.Models;

namespace Orleans.Storage
{
    internal static class StorageExceptionExtensions
    {
        public static bool IsNotFound(this RequestFailedException requestFailedException)
        {
            return requestFailedException?.Status == (int)HttpStatusCode.NotFound;
        }

        public static bool IsPreconditionFailed(this RequestFailedException requestFailedException)
        {
            return requestFailedException?.Status == (int)HttpStatusCode.PreconditionFailed;
        }

        public static bool IsConflict(this RequestFailedException requestFailedException)
        {
            return requestFailedException?.Status == (int)HttpStatusCode.Conflict;
        }

        public static bool IsContainerNotFound(this RequestFailedException requestFailedException)
        {
            return requestFailedException?.Status == (int)HttpStatusCode.NotFound
                && requestFailedException.ErrorCode == BlobErrorCode.ContainerNotFound;
        }

        public static bool IsBlobNotFound(this RequestFailedException requestFailedException)
        {
            return requestFailedException?.Status == (int)HttpStatusCode.NotFound
                && requestFailedException.ErrorCode == BlobErrorCode.BlobNotFound;
        }
    }
}
