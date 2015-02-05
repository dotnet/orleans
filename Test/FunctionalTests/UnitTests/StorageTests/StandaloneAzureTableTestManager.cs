using System;
using System.Collections.Generic;
using System.Data.Services.Common;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.StorageClient;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Storage;

// for checking the insert/upsert/read rate of AzureTable
namespace UnitTests.StorageTests
{
    [DataServiceKey("PartitionKey", "RowKey")]
    public class StandaloneAzureTableTestEntry : TableServiceEntity
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

    internal class StandaloneAzureTableTestManager : AzureTableDataManager<StandaloneAzureTableTestEntry>
    {
        private const string INSTANCE_TABLE_NAME_PREFIX = "TestRate";

        public string DeploymentId { get; private set; }

        private static StandaloneAzureTableTestManager singleton;

        private readonly static object classLock = new object();

        internal static StandaloneAzureTableTestManager GetManager(string deploymentId, string storageConnectionString)
        {
            lock (classLock)
            {
                if (singleton == null)
                {
                    singleton = new StandaloneAzureTableTestManager(deploymentId, storageConnectionString);
                    try
                    {
                        singleton.InitTableAsync().WaitWithThrow(AzureTableDefaultPolicies.TableCreationTimeout);
                    }
                    catch (TimeoutException te)
                    {
                        singleton.Logger.Error(ErrorCode.AzureTable_38, String.Format("Unable to create or connect to the Azure table in {0} {1}", AzureTableDefaultPolicies.TableCreationTimeout, te));
                        throw;
                    }
                    catch (Exception ex)
                    {
                        singleton.Logger.Error(ErrorCode.AzureTable_39, String.Format("Exception trying to create or connect to the Azure table: {0}", ex));
                        throw;
                    }
                }
            }
            return singleton;
        }

        private StandaloneAzureTableTestManager(string deploymentId, string storageConnectionString)
            : base(INSTANCE_TABLE_NAME_PREFIX + deploymentId, storageConnectionString)
        {
            this.DeploymentId = deploymentId;
        }

        internal async Task<List<Tuple<StandaloneAzureTableTestEntry, string>>> FindAllTestEntries()
        {
            var queryResults = await ReadAllTableEntriesAsync();
            return queryResults.ToList();
        }

        public async Task<Tuple<StandaloneAzureTableTestEntry, string>> FindTestEntry(string partitionKey, string rowKey)
        {
            return await ReadSingleTableEntryAsync(partitionKey, rowKey);
        }

        public async Task<bool> InsertTestEntry(StandaloneAzureTableTestEntry testEntry)
        {
            try
            {
                await InsertTableEntryConditionallyAsync(testEntry, null, null, false);
                return true;
            }
            catch(Exception exc)
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                string errMsg = "InsertTestEntry failed";
                if (AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus))
                {
                    errMsg += string.Format(" with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                    Logger.Warn(1, errMsg);
                    if (AzureStorageUtils.IsContentionError(httpStatusCode)) return false;
                }
                throw new AggregateException(errMsg, exc);
            }
        }

        public async Task<string> UpsertRow(StandaloneAzureTableTestEntry testEntry)
        {
            try
            {
                return await UpsertTableEntryAsync(testEntry);
            }
            catch(Exception exc)
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                string errMsg = "UpsertRow failed";
                if (AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus))
                {
                    errMsg += string.Format(" with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                    Logger.Warn(1, errMsg);
                    if (AzureStorageUtils.IsContentionError(httpStatusCode)) return null; // false;
                }
                throw new AggregateException(errMsg, exc);
            }
        }
    }
}

