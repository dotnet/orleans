using System;
using System.Net;
using System.Text.RegularExpressions;
using Azure;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
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

        public const string PKProperty = nameof(ITableEntity.PartitionKey);
        public const string RKProperty = nameof(ITableEntity.RowKey);

        public const int MaxBatchSize = 100;
    }

    /// <summary>
    /// General utility functions related to Azure Table storage (also applies to Table endpoints in Cosmos DB).
    /// </summary>
    internal static partial class AzureTableUtils
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
            if (EvaluateException(exc, out var httpStatusCode, out var restStatus, true))
            {
                if (IsNotFoundError(httpStatusCode))
                {
                    return true;
                }

                return TableErrorCode.EntityNotFound.ToString().Equals(restStatus, StringComparison.OrdinalIgnoreCase)
                    || TableErrorCode.ResourceNotFound.ToString().Equals(restStatus, StringComparison.OrdinalIgnoreCase);
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
            while (exc != null && exc is not RequestFailedException)
            {
                exc = exc?.InnerException?.GetBaseException();
            }

            return (exc as RequestFailedException)?.ErrorCode;
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
                    if (e is RequestFailedException ste)
                    {
                        httpStatusCode = (HttpStatusCode)ste.Status;
                        if (getRESTErrors)
                        {
                            restStatus = ExtractRestErrorCode(ste);
                        }
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
                    && TableErrorCode.OperationTimedOut.ToString().Equals(restStatusCode, StringComparison.OrdinalIgnoreCase))
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

        [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{2,62}$")]
        private static partial Regex TableNameRegex();

        internal static void ValidateTableName(string tableName)
        {
            // Regular expression from documentation: https://docs.microsoft.com/rest/api/storageservices/understanding-the-table-service-data-model#table-names
            if (!TableNameRegex().IsMatch(tableName))
            {
                throw new ArgumentException($"Table name \"{tableName}\" is invalid according to the following rules:"
                    + " 1. Table names may contain only alphanumeric characters."
                    + " 2. Table names cannot begin with a numeric character."
                    + " 3. Table names must be from 3 to 63 characters long.");
            }
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
                logger.LogWarning((int)Utilities.ErrorCode.AzureTable_10,
                    exc,
                    "Intermediate issue reading Azure storage table {TableName}: HTTP status code={StatusCode}",
                    tableName,
                    statusCode);
            }
            else
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                if (EvaluateException(exc, out httpStatusCode, out restStatus, true))
                {
                    if (TableErrorCode.ResourceNotFound.ToString().Equals(restStatus))
                    {
                        if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)Utilities.ErrorCode.AzureTable_DataNotFound,
                            exc,
                            "DataNotFound reading Azure storage table {TableName}:{Retry} HTTP status code={StatusCode} REST status code={RESTStatusCode}",
                            tableName,
                            iteration == 0 ? String.Empty : (" Repeat=" + iteration),
                            httpStatusCode,
                            restStatus);

                        isLastErrorRetriable = false;
                    }
                    else
                    {
                        isLastErrorRetriable = IsRetriableHttpError(httpStatusCode, restStatus);

                        logger.LogWarning((int)Utilities.ErrorCode.AzureTable_11,
                            exc,
                            "Intermediate issue reading Azure storage table {TableName}:{Retry} IsRetriable={IsLastErrorRetriable} HTTP status code={StatusCode} REST status code={RestStatusCode}",
                            tableName,
                            iteration == 0 ? "" : (" Repeat=" + iteration),
                            isLastErrorRetriable,
                            httpStatusCode,
                            restStatus);
                    }
                }
                else
                {
                    logger.LogError((int)Utilities.ErrorCode.AzureTable_12,
                        exc,
                        "Unexpected issue reading Azure storage table {TableName}",
                        tableName);
                    isLastErrorRetriable = false;
                }
            }
            return isLastErrorRetriable;
        }

        internal static string PrintStorageException(Exception exception)
        {
            if (exception is not RequestFailedException storeExc)
            {
                throw new ArgumentException($"Unexpected exception type {exception.GetType().FullName}");
            }

            return $"Message = {storeExc.Message}, HTTP Status = {storeExc.Status}, HTTP Error Code = {storeExc.ErrorCode}.";
        }

        internal static string PointQuery(string partitionKey, string rowKey)
        {
            return TableClient.CreateQueryFilter($"(PartitionKey eq {partitionKey}) and (RowKey eq {rowKey})");
        }

        internal static string RangeQuery(string partitionKey, string minRowKey, string maxRowKey)
        {
            return TableClient.CreateQueryFilter($"((PartitionKey eq {partitionKey}) and (RowKey ge {minRowKey})) and (RowKey le {maxRowKey})");
        }
    }
}
