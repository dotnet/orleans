using System.Net;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table.Protocol;

namespace Orleans.Storage
{
    internal static class StorageExceptionExtensions
    {
        /// <summary>
        /// See https://msdn.microsoft.com/en-us/library/azure/dd179438.aspx
        /// </summary>
        internal static bool IsUpdateConditionNotSatisfiedError(this StorageException storageException)
        {
            return storageException?.RequestInformation != null &&
                   storageException.RequestInformation.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed &&
                   (storageException.RequestInformation.ExtendedErrorInformation == null ||
                    storageException.RequestInformation.ExtendedErrorInformation.ErrorCode.Equals(TableErrorCodeStrings.UpdateConditionNotSatisfied));
        }
    }
}
