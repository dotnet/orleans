using System;
using System.Collections.Generic;
using System.Data.Services.Common;
using System.Linq;
using System.Net;
using System.Text;
using Microsoft.WindowsAzure.StorageClient;
using Orleans;
using Orleans.AzureUtils;
using Orleans.RuntimeCore;

// for checking the insert/upsert/read rate of AzureTable
namespace UnitTests.General
{
    [DataServiceKey("PartitionKey", "RowKey")]
    public class AzureTableTestEntry : TableServiceEntity
    {
        public int Property1 { get; set; }   // PartitionKey
        public int Property2 { get; set; }   // RowKey

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.Append("TestEntry [");
            sb.Append(" Property1=").Append(Property1);
            sb.Append(" Property2=").Append(Property1);
            sb.Append("]");

            return sb.ToString();
        }
    }

    public class AzureTableTestManager : Async_AzureTableDataManager<AzureTableTestEntry>
    {
        private const string INSTANCE_TABLE_NAME_PREFIX = "TestRate";

        public string DeploymentId { get; private set; }

        private static AzureTableTestManager singleton;
        private readonly static object classLock = new object();

        protected override Type ResolveEntityType(string name)
        {
            return typeof(AzureTableTestEntry);
        }

        public static AzureTableTestManager GetManager(string deploymentId, string storageConnectionString)
        {
            lock (classLock)
            {
                if (singleton == null)
                {
                    singleton = new AzureTableTestManager(deploymentId, storageConnectionString);
                    try
                    {
                        singleton.InitTable_Async().Wait(AzureTableDefaultPolicies.TableCreation_TIMEOUT);
                    }
                    catch (TimeoutException)
                    {
                        singleton.logger.Fail(ErrorCode.AzureTable_38, String.Format("Unable to create or connect to the Azure table in {0}", AzureTableDefaultPolicies.TableCreation_TIMEOUT));
                    }
                    catch (Exception ex)
                    {
                        singleton.logger.Fail(ErrorCode.AzureTable_39, String.Format("Exception trying to create or connect to the Azure table: {0}", ex));
                    }
                }
            }
            return singleton;
        }

        private AzureTableTestManager(string deploymentId, string storageConnectionString)
            : base(INSTANCE_TABLE_NAME_PREFIX + deploymentId, storageConnectionString)
        {
            this.DeploymentId = deploymentId;
        }

        internal AsyncValue<List<Tuple<AzureTableTestEntry, string>>> FindAllTestEntries()
        {
            var queryResultsPromise = ReadTableEntriesAndEtags(_ => true);

            return queryResultsPromise.ContinueWith(queryResults => queryResults.ToList());
        }

        public AsyncValue<Tuple<AzureTableTestEntry, string>> FindTestEntry(string partitionKey, string rowKey)
        {
            var queryResultsPromise = ReadTableEntriesAndEtags((AzureTableTestEntry instance) =>
                instance.PartitionKey == partitionKey
                    && instance.RowKey == rowKey);

            return queryResultsPromise.ContinueWith(queryResults => queryResults.First());
        }

        public AsyncValue<bool> InsertTestEntry(AzureTableTestEntry testEntry)
        {
            return InsertTableEntryConditionally(testEntry, null, null, false)
                .ContinueWith(() =>
                {
                    return true;
                },
                    (Exception exc) =>
                    {
                        HttpStatusCode httpStatusCode;
                        string restStatus;
                        if (EvaluateException(exc, out httpStatusCode, out restStatus))
                        {
                            if (logger.IsVerbose2) logger.Verbose2("InsertTestEntry failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                            if (IsContentionError(httpStatusCode)) return false;
                        }
                        throw exc;
                    });
        }

        public AsyncValue<string> UpsertRow(AzureTableTestEntry testEntry)
        {
            return UpsertTableEntry(testEntry)
                .ContinueWith((eTag) => eTag,
                    (Exception exc) =>
                    {
                        HttpStatusCode httpStatusCode;
                        string restStatus;
                        if (EvaluateException(exc, out httpStatusCode, out restStatus))
                        {
                            if (logger.IsVerbose2) logger.Verbose2("UpsertRow failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                            if (IsContentionError(httpStatusCode)) return null; // false;
                        }
                        throw exc;
                    });
        }

        #region Table operations

        internal static AsyncCompletion DeleteTableEntries(string deploymentId, string connectionString)
        {
            var manager = GetManager(deploymentId, connectionString);

            if (deploymentId == null)
                return manager.DeleteTable();
            else
            {
                AsyncValue<List<Tuple<AzureTableTestEntry, string>>> entriesPromise = manager.FindAllTestEntries();
                return entriesPromise.ContinueWith(entries =>
                {
                    return manager.DeleteTableEntries(entries);
                });
            }
        }

        #endregion
    }

}

