/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
using System.Data.Services.Client;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.StorageClient;

using Orleans.Runtime;


namespace Orleans.AzureUtils
{
    /// <summary>
    /// General utility functions related to Azure storage.
    /// </summary>
    /// <remarks>
    /// These functions are mostly intended for internal usage by Orleans runtime, but due to certain assembly packaging constrants this class needs to have public visibility.
    /// </remarks>
    public static class AzureStorageUtils
    {
        /// <summary>
        /// Inspect an exception returned from Azure storage libraries to check whether it means that attempt was made to read some data that does not exist in storage table.
        /// </summary>
        /// <param name="exc">Exception that was returned by Azure storage library operation</param>
        /// <returns><c>True</c> if this exception means the data being read was not present in Azure table storage</returns>
        public static bool TableStorageDataNotFound(Exception exc)
        {
            if (exc is AggregateException)
            {
                exc = exc.GetBaseException();
            }
            var dsce = exc as DataServiceClientException;
            if (dsce == null)
            {
                var dsre = exc as DataServiceRequestException;
                if (dsre != null)
                {
                    dsce = dsre.GetBaseException() as DataServiceClientException;
                }
            }
            if (dsce != null)
            {
                // Check for appropriate HTTP status codes
                if (dsce.StatusCode == (int)HttpStatusCode.NotFound
                    || dsce.StatusCode == (int)HttpStatusCode.NotImplemented /* New table: Azure table schema not yet initialized, so need to do first create */ )
                {
                    return true;
                }
                exc = dsce;
            }
            string restErrorCode = ExtractRestErrorCode(exc);
            // Check for appropriate Azure REST error codes
            return StorageErrorCodeStrings.ResourceNotFound.Equals(restErrorCode);
        }

        /// <summary>
        /// Extract REST error code from DataServiceClientException or DataServiceQueryException
        /// </summary>
        /// <param name="exc">Exception to be inspected.</param>
        /// <returns>Returns REST error code if found, otherwise <c>null</c></returns>
        public static string ExtractRestErrorCode(Exception exc)
        {
            // Sample of REST error message returned from Azure storage service
            //<?xml version="1.0" encoding="utf-8" standalone="yes"?>
            //<error xmlns="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
            //  <code>OperationTimedOut</code>
            //  <message xml:lang="en-US">Operation could not be completed within the specified time. RequestId:6b75e963-c56c-4734-a656-066cfd03f327 Time:2011-10-09T19:33:26.7631923Z</message>
            //</error>

            while (exc != null && !(exc is DataServiceClientException || exc is DataServiceQueryException))
            {
                exc = (exc.InnerException != null) ? exc.InnerException.GetBaseException() : exc.InnerException;
            }

            while (exc is DataServiceQueryException)
            {
                exc = (exc.InnerException != null) ? exc.InnerException.GetBaseException() : exc.InnerException;
            }
            if (exc is DataServiceClientException)
            {
                try
                {
                    var xml = new XmlDocument();
                    var xmlReader = XmlReader.Create(new StringReader(exc.Message));
                    xml.Load(xmlReader);
                    var namespaceManager = new XmlNamespaceManager(xml.NameTable);
                    namespaceManager.AddNamespace("n", "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata");
                   return xml.SelectSingleNode("/n:error/n:code", namespaceManager).InnerText;
                }
                catch (Exception e)
                {
                    var log = TraceLogger.GetLogger("AzureStorageUtils", TraceLogger.LoggerType.Runtime);
                    log.Warn(ErrorCode.AzureTable_16, String.Format("Problem extracting REST error code from Data='{0}'", exc.Message), e);
                    if (Debugger.IsAttached) throw; // For debug purposes only; Problem will just get logged in all other cases
                }
            }
            return null;
        }

        /// <summary>
        /// Examine a storage exception, and if applicable extracts the HTTP status code, and REST error code if <c>getExtendedErrors=true</c>.
        /// </summary>
        /// <param name="e">Exeption to be examined.</param>
        /// <param name="httpStatusCode">Output HTTP status code if applicable, otherwise HttpStatusCode.Unused (306)</param>
        /// <param name="restStatus">When <c>getExtendedErrors=true</c>, will output REST error code if applicable, otherwise <c>null</c></param>
        /// <param name="getExtendedErrors">Whether REST error code should also be examined / extracted.</param>
        /// <returns>Returns <c>true</c> if HTTP status code and REST error were extracted.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public static bool EvaluateException(
            Exception e,
            out HttpStatusCode httpStatusCode,
            out string restStatus,
            bool getExtendedErrors = false)
        {
            httpStatusCode = HttpStatusCode.Unused;
            restStatus = null;

            try
            {
                while (e != null)
                {
                    if (e is DataServiceQueryException)
                    {
                        var dsqe = e as DataServiceQueryException;
                        httpStatusCode = (HttpStatusCode)dsqe.Response.StatusCode;
                        if (getExtendedErrors)
                            restStatus = ExtractRestErrorCode(dsqe);
                        return true;
                    }
                    if (e is DataServiceClientException)
                    {
                        var dsce = e as DataServiceClientException;
                        httpStatusCode = (HttpStatusCode)dsce.StatusCode;
                        if (getExtendedErrors)
                            restStatus = ExtractRestErrorCode(dsce);
                        return true;
                    }
                    if (e is DataServiceRequestException)
                    {
                        var dsre = e as DataServiceRequestException;
                        List<OperationResponse> innerResponses = dsre.Response.ToList();
                        if (innerResponses.Any())
                        {
                            httpStatusCode = (HttpStatusCode)innerResponses.First().StatusCode;
                            if (getExtendedErrors)
                                restStatus = ExtractRestErrorCode(dsre);
                            return true;
                        }
                    }
                    e = e.InnerException;
                }
            }
            catch (Exception)
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

        internal static CloudQueueClient GetCloudQueueClient(
            string storageConnectionString,
            RetryPolicy retryPolicy,
            TimeSpan timeout,
            TraceLogger logger)
        {
            try
            {
                var storageAccount = GetCloudStorageAccount(storageConnectionString);
                CloudQueueClient operationsClient = storageAccount.CreateCloudQueueClient();
                operationsClient.RetryPolicy = retryPolicy;
                operationsClient.Timeout = timeout;
                return operationsClient;
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.AzureQueue_14, String.Format("Error creating GetCloudQueueOperationsClient."), exc);
                throw;
            }
        }

        internal static void ValidateQueueName(string queueName)
        {
            // Naming Queues and Metadata: http://msdn.microsoft.com/en-us/library/windowsazure/dd179349.aspx
            if (!(queueName.Length >= 3 && queueName.Length <= 63))
            {
                // A queue name must be from 3 through 63 characters long.
                throw new ArgumentException(String.Format("A queue name must be from 3 through 63 characters long, while your queueName length is {0}, queueName is {1}.", queueName.Length, queueName), queueName);
            }

            if (!Char.IsLetterOrDigit(queueName.First()))
            {
                // A queue name must start with a letter or number
                throw new ArgumentException(String.Format("A queue name must start with a letter or number, while your queueName is {0}.", queueName), queueName);
            }

            if (!Char.IsLetterOrDigit(queueName.Last()))
            {
                // The first and last letters in the queue name must be alphanumeric. The dash (-) character cannot be the first or last character. 
                throw new ArgumentException(String.Format("The last letter in the queue name must be alphanumeric, while your queueName is {0}.", queueName), queueName);
            }

            if (!queueName.All(c => Char.IsLetterOrDigit(c) || c.Equals('-')))
            {
                // A queue name can only contain letters, numbers, and the dash (-) character. 
                throw new ArgumentException(String.Format("A queue name can only contain letters, numbers, and the dash (-) character, while your queueName is {0}.", queueName), queueName);
            }

            if (queueName.Contains("--"))
            {
                // Consecutive dash characters are not permitted in the queue name.
                throw new ArgumentException(String.Format("Consecutive dash characters are not permitted in the queue name, while your queueName is {0}.", queueName), queueName);
            }

            if (queueName.Where(Char.IsLetter).Any(c => !Char.IsLower(c)))
            {
                // All letters in a queue name must be lowercase.
                throw new ArgumentException(String.Format("All letters in a queue name must be lowercase, while your queueName is {0}.", queueName), queueName);
            }
        }

        internal static void ValidateTableName(string tableName)
        {
            // Table Name Rules: http://msdn.microsoft.com/en-us/library/dd179338.aspx

            if (!(tableName.Length >= 3 && tableName.Length <= 63))
            {
                // Table names must be from 3 to 63 characters long.
                throw new ArgumentException(String.Format("A table name must be from 3 through 63 characters long, while your tableName length is {0}, tableName is {1}.", tableName.Length, tableName), tableName);
            }

            if (Char.IsDigit(tableName.First()))
            {
                // Table names cannot begin with a numeric character. 
                throw new ArgumentException(String.Format("A table name cannot begin with a numeric character, while your tableName is {0}.", tableName), tableName);
            }

            if (!tableName.All(Char.IsLetterOrDigit))
            {
                // Table names may contain only alphanumeric characters. 
                throw new ArgumentException(String.Format("A table name can only contain alphanumeric characters, while your tableName is {0}.", tableName), tableName);
            }
        }

        internal static string PrintCloudQueueMessage(CloudQueueMessage message)
        {
            return String.Format("CloudQueueMessage: Id = {0}, NextVisibleTime = {1}, DequeueCount = {2}, PopReceipt = {3}, Content = {4}",
                    message.Id,
                    message.NextVisibleTime.HasValue ? TraceLogger.PrintDate(message.NextVisibleTime.Value) : "",
                    message.DequeueCount,
                    message.PopReceipt,
                    message.AsString);
        }

        internal static bool AnalyzeReadException(Exception exc, int iteration, string tableName, TraceLogger logger)
        {
            bool isLastErrorRetriable;
            var we = exc as WebException;
            if (we != null)
            {
                isLastErrorRetriable = true;
                var statusCode = we.Status;
                logger.Warn(ErrorCode.AzureTable_10, String.Format("Intermediate issue reading Azure storage table {0}: HTTP status code={1} Exception Type={2} Message='{3}'",
                    tableName,
                    statusCode,
                    exc.GetType().FullName,
                    exc.Message),
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
                        if (logger.IsVerbose) logger.Verbose(ErrorCode.AzureTable_DataNotFound,
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

                        logger.Warn(ErrorCode.AzureTable_11, String.Format("Intermediate issue reading Azure storage table {0}:{1} IsRetriable={2} HTTP status code={3} REST status code={4} Exception Type={5} Message='{6}'",
                                tableName,
                                iteration == 0 ? "" : (" Repeat=" + iteration),
                                isLastErrorRetriable,
                                httpStatusCode,
                                restStatus,
                                exc.GetType().FullName,
                                exc.Message),
                                exc);
                    }
                }
                else
                {
                    logger.Error(ErrorCode.AzureTable_12, string.Format("Unexpected issue reading Azure storage table {0}: Exception Type={1} Message='{2}'",
                                     tableName,
                                     exc.GetType().FullName,
                                     exc.Message),
                                 exc);
                    isLastErrorRetriable = false;
                }
            }
            return isLastErrorRetriable;
        }
    }
}
