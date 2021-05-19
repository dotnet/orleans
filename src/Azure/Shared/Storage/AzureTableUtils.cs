using System;
using System.Net;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Azure.Cosmos.Tables.SharedFiles;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

//
// Number of #ifs can be reduced (or removed), once we separate test projects by feature/area, otherwise we are ending up with ambiguous types and build errors.
//

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.AzureStorage
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.AzureStorage
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.AzureStorage
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.AzureStorage
#elif ORLEANS_EVENTHUBS
namespace Orleans.Streaming.EventHubs
#elif TESTER_AZUREUTILS
namespace Orleans.Tests.AzureUtils
#elif ORLEANS_TRANSACTIONS
namespace Orleans.Transactions.AzureStorage
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.AzureStorage
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// Constants related to Azure Table storage (also applies to Table endpoints in Cosmos DB).
    /// </summary>
    internal static class AzureTableConstants
    {
        public const string ANY_ETAG = "*";

        public const string PKProperty = nameof(TableEntity.PartitionKey);
        public const string RKProperty = nameof(TableEntity.RowKey);

        public const int MaxBatchSize = 100;
    }

    /// <summary>
    /// General utility functions related to Azure Table storage (also applies to Table endpoints in Cosmos DB).
    /// </summary>
    internal static class AzureTableUtils
    {
        /// <summary>
        /// ETag of value "*" to match any etag for conditional table operations (update, merge, delete).
        /// </summary>
        public const string ANY_ETAG = AzureTableConstants.ANY_ETAG;

        /// <summary>
        /// Inspect an exception returned from Azure storage libraries to check whether it means that attempt was made to read some data that does not exist in storage table.
        /// </summary>
        /// <param name="exc">Exception that was returned by Azure storage library operation</param>
        /// <returns><c>True</c> if this exception means the data being read was not present in Azure table storage</returns>
        public static bool TableStorageDataNotFound(Exception exc)
        {
            HttpStatusCode httpStatusCode;
            string restStatus;
            if (AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true))
            {
                if (AzureTableUtils.IsNotFoundError(httpStatusCode)
                    /* New table: Azure table schema not yet initialized, so need to do first create */)
                {
                    return true;
                }
                return StorageErrorCodeStrings.ResourceNotFound.Equals(restStatus);
            }
            return false;
        }

        /// <summary>
        /// Extract REST error code from DataServiceClientException or DataServiceQueryException
        /// </summary>
        /// <param name="exc">Exception to be inspected.</param>
        /// <returns>Returns REST error code if found, otherwise <c>null</c></returns>
        private static string ExtractRestErrorCode(Exception exc)
        {
            while (exc != null && !(exc is StorageException))
            {
                exc = (exc.InnerException != null) ? exc.InnerException.GetBaseException() : exc.InnerException;
            }
            if (exc is StorageException)
            {
                StorageException ste = exc as StorageException;
                if(ste.RequestInformation.ExtendedErrorInformation != null)
                    return ste.RequestInformation.ExtendedErrorInformation.ErrorCode;
            }
            return null;
        }

        /// <summary>
        /// Examine a storage exception, and if applicable extracts the HTTP status code, and REST error code if <c>getRESTErrors=true</c>.
        /// </summary>
        /// <param name="e">Exception to be examined.</param>
        /// <param name="httpStatusCode">Output HTTP status code if applicable, otherwise HttpStatusCode.Unused (306)</param>
        /// <param name="restStatus">When <c>getRESTErrors=true</c>, will output REST error code if applicable, otherwise <c>null</c></param>
        /// <param name="getRESTErrors">Whether REST error code should also be examined / extracted.</param>
        /// <returns>Returns <c>true</c> if HTTP status code and REST error were extracted.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public static bool EvaluateException(
            Exception e,
            out HttpStatusCode httpStatusCode,
            out string restStatus,
            bool getRESTErrors = false)
        {
            httpStatusCode = HttpStatusCode.Unused;
            restStatus = null;

            try
            {
                while (e != null)
                {
                    if (e is StorageException)
                    {
                        var ste = e as StorageException;
                        httpStatusCode = (HttpStatusCode)ste.RequestInformation.HttpStatusCode;
                        if (getRESTErrors)
                            restStatus = ExtractRestErrorCode(ste);
                        return true;
                    }
                    e = e.InnerException;
                }
            }
            catch
            {
                // if we failed to parse the exception, treat it as if we could not EvaluateException.
                return false;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the specified HTTP status / error code is returned in a transient / retriable error condition
        /// </summary>
        /// <param name="httpStatusCode">HTTP error code value</param>
        /// <param name="restStatusCode">REST error code value</param>
        /// <returns><c>true</c> if this is a transient / retriable error condition</returns>
        public static bool IsRetriableHttpError(HttpStatusCode httpStatusCode, string restStatusCode)
        {
            // Note: We ignore the 20X values as they are successful outcomes, not errors

            return (
                httpStatusCode == HttpStatusCode.RequestTimeout /* 408 */
                || httpStatusCode == HttpStatusCode.BadGateway          /* 502 */
                || httpStatusCode == HttpStatusCode.ServiceUnavailable  /* 503 */
                || httpStatusCode == HttpStatusCode.GatewayTimeout      /* 504 */
                || (httpStatusCode == HttpStatusCode.InternalServerError /* 500 */
                    && !String.IsNullOrEmpty(restStatusCode)
                    && StorageErrorCodeStrings.OperationTimedOut.Equals(restStatusCode, StringComparison.OrdinalIgnoreCase))
            );
        }

        /// <summary>
        /// Check whether a HTTP status code returned from a REST call might be due to a (temporary) storage contention error.
        /// </summary>
        /// <param name="httpStatusCode">HTTP status code to be examined.</param>
        /// <returns>Returns <c>true</c> if the HTTP status code is due to storage contention.</returns>
        public static bool IsContentionError(HttpStatusCode httpStatusCode)
        {
            // Status and Error Codes
            // http://msdn.microsoft.com/en-us/library/dd179382.aspx

            if (httpStatusCode == HttpStatusCode.PreconditionFailed) return true;
            if (httpStatusCode == HttpStatusCode.Conflict) return true;     //Primary key violation. The app is trying to insert an entity, but there’s an entity on the table with the same values for PartitionKey and RowKey properties on the entity being inserted.
            if (httpStatusCode == HttpStatusCode.NotFound) return true;
            if (httpStatusCode == HttpStatusCode.NotImplemented) return true; // New table: Azure table schema not yet initialized, so need to do first create
            return false;
        }

        /// <summary>
        /// Check whether a HTTP status code returned from a REST call might be due to a (temporary) storage contention error.
        /// </summary>
        /// <param name="httpStatusCode">HTTP status code to be examined.</param>
        /// <returns>Returns <c>true</c> if the HTTP status code is due to storage contention.</returns>
        public static bool IsNotFoundError(HttpStatusCode httpStatusCode)
        {
            // Status and Error Codes
            // http://msdn.microsoft.com/en-us/library/dd179382.aspx

            if (httpStatusCode == HttpStatusCode.NotFound) return true;
            if (httpStatusCode == HttpStatusCode.NotImplemented) return true; // New table: Azure table schema not yet initialized, so need to do first create
            return false;
        }

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

        internal static void ValidateTableName(string tableName)
        {
            NameValidator.ValidateTableName(tableName);
        }

        /// <summary>
        /// Remove any characters that can't be used in Azure PartitionKey or RowKey values.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public static string SanitizeTableProperty(string key)
        {
            // Remove any characters that can't be used in Azure PartitionKey or RowKey values
            // http://www.jamestharpe.com/web-development/azure-table-service-character-combinations-disallowed-in-partitionkey-rowkey/
            key = key
                .Replace('/', '_')        // Forward slash
                .Replace('\\', '_')       // Backslash
                .Replace('#', '_')        // Pound sign
                .Replace('?', '_');       // Question mark

            if (key.Length >= 1024)
                throw new ArgumentException(string.Format("Key length {0} is too long to be an Azure table key. Key={1}", key.Length, key));

            return key;
        }

        internal static bool AnalyzeReadException(Exception exc, int iteration, string tableName, ILogger logger)
        {
            bool isLastErrorRetriable;
            var we = exc as WebException;
            if (we != null)
            {
                isLastErrorRetriable = true;
                var statusCode = we.Status;
                logger.Warn((int)Utilities.ErrorCode.AzureTable_10,
                    $"Intermediate issue reading Azure storage table {tableName}: HTTP status code={statusCode} Exception Type={exc.GetType().FullName} Message='{exc.Message}'",
                    exc);
            }
            else
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                if (EvaluateException(exc, out httpStatusCode, out restStatus, true))
                {
                    if (StorageErrorCodeStrings.ResourceNotFound.Equals(restStatus))
                    {
                        if (logger.IsEnabled(LogLevel.Debug)) logger.Debug((int)Utilities.ErrorCode.AzureTable_DataNotFound,
                            "DataNotFound reading Azure storage table {0}:{1} HTTP status code={2} REST status code={3} Exception={4}",
                            tableName,
                            iteration == 0 ? "" : (" Repeat=" + iteration),
                            httpStatusCode,
                            restStatus,
                            exc);

                        isLastErrorRetriable = false;
                    }
                    else
                    {
                        isLastErrorRetriable = IsRetriableHttpError(httpStatusCode, restStatus);

                        logger.Warn((int)Utilities.ErrorCode.AzureTable_11,
                            $"Intermediate issue reading Azure storage table {tableName}:{(iteration == 0 ? "" : (" Repeat=" + iteration))} IsRetriable={isLastErrorRetriable} HTTP status code={httpStatusCode} REST status code={restStatus} Exception Type={exc.GetType().FullName} Message='{exc.Message}'",
                                exc);
                    }
                }
                else
                {
                    logger.Error((int)Utilities.ErrorCode.AzureTable_12,
                        $"Unexpected issue reading Azure storage table {tableName}: Exception Type={exc.GetType().FullName} Message='{exc.Message}'",
                                 exc);
                    isLastErrorRetriable = false;
                }
            }
            return isLastErrorRetriable;
        }

        internal static string PrintStorageException(Exception exception)
        {
            var storeExc = exception as StorageException;
            if(storeExc == null)
                throw new ArgumentException(String.Format("Unexpected exception type {0}", exception.GetType().FullName));

            var result = storeExc.RequestInformation;
            if (result == null) return storeExc.Message;
            var extendedError = storeExc.RequestInformation.ExtendedErrorInformation;
            if (extendedError == null)
            {
                return String.Format("Message = {0}, HttpStatusCode = {1}, HttpStatusMessage = {2}.",
                        storeExc.Message,
                        result.HttpStatusCode,
                        result.HttpStatusMessage);

            }
            return String.Format("Message = {0}, HttpStatusCode = {1}, HttpStatusMessage = {2}, " +
                                   "ExtendedErrorInformation.ErrorCode = {3}, ExtendedErrorInformation.ErrorMessage = {4}{5}.",
                        storeExc.Message,
                        result.HttpStatusCode,
                        result.HttpStatusMessage,
                        extendedError.ErrorCode,
                        extendedError.ErrorMessage,
                        (extendedError.AdditionalDetails != null && extendedError.AdditionalDetails.Count > 0) ?
                            String.Format(", ExtendedErrorInformation.AdditionalDetails = {0}", Utils.DictionaryToString(extendedError.AdditionalDetails)) : String.Empty);
        }

        internal static string PointQuery(string partitionKey, string rowKey)
        {
            return TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(AzureTableConstants.PKProperty, QueryComparisons.Equal, partitionKey),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(AzureTableConstants.RKProperty, QueryComparisons.Equal, rowKey));
        }

        internal static string RangeQuery(string partitionKey, string minRowKey, string maxRowKey)
        {
            return TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(AzureTableConstants.PKProperty, QueryComparisons.Equal, partitionKey),
                TableOperators.And,
                TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition(AzureTableConstants.RKProperty, QueryComparisons.GreaterThanOrEqual, minRowKey),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition(AzureTableConstants.RKProperty, QueryComparisons.LessThanOrEqual, maxRowKey)));
        }
    }
}
