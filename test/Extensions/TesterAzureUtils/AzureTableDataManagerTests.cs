using System;
using System.Net;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables.Models;
using Orleans.Clustering.AzureStorage;
using Orleans.TestingHost.Utils;
using Xunit;

namespace Tester.AzureUtils
{
    [TestCategory("AzureStorage"), TestCategory("Storage")]
    public class AzureTableDataManagerTests : AzureStorageBasicTests
    {
        private string PartitionKey;
        private UnitTestAzureTableDataManager manager;

        private UnitTestAzureTableData GenerateNewData()
        {
            return new UnitTestAzureTableData("JustData", PartitionKey, "RK-" + Guid.NewGuid());
        }

        public AzureTableDataManagerTests()
        {
            TestingUtils.ConfigureThreadPoolSettingsForStorageTests();
            // Pre-create table, if required
            manager = new UnitTestAzureTableDataManager();
            PartitionKey = "PK-AzureTableDataManagerTests-" + Guid.NewGuid();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AzureTableDataManager_CreateTableEntryAsync()
        {
            var data = GenerateNewData();
            await manager.CreateTableEntryAsync(data);
            try
            {
                var data2 = data.Clone();
                data2.StringData = "NewData";
                await manager.CreateTableEntryAsync(data2);
                Assert.True(false, "Should have thrown RequestFailedException.");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.Conflict, exc.Status);  // "Creating an already existing entry."
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.Equal(HttpStatusCode.Conflict, httpStatusCode);
                Assert.Equal("EntityAlreadyExists", restStatus);
            }
            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.Equal(data.StringData, tuple.Entity.StringData);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AzureTableDataManager_UpsertTableEntryAsync()
        {
            var data = GenerateNewData();
            await manager.UpsertTableEntryAsync(data);
            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.Equal(data.StringData, tuple.Entity.StringData);

            var data2 = data.Clone();
            data2.StringData = "NewData";
            await manager.UpsertTableEntryAsync(data2);
            tuple = await manager.ReadSingleTableEntryAsync(data2.PartitionKey, data2.RowKey);
            Assert.Equal(data2.StringData, tuple.Entity.StringData);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AzureTableDataManager_UpdateTableEntryAsync()
        {
            StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();
            var data = GenerateNewData();
            try
            {
                await manager.UpdateTableEntryAsync(data, AzureTableUtils.ANY_ETAG);
                Assert.True(false, "Should have thrown RequestFailedException.");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.NotFound, exc.Status);  // "Update before insert."
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.Equal(HttpStatusCode.NotFound, httpStatusCode);
                Assert.Equal(TableErrorCode.ResourceNotFound.ToString(), restStatus);
            }

            await manager.UpsertTableEntryAsync(data);
            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.Equal(data.StringData, tuple.Entity.StringData);

            var data2 = data.Clone();
            data2.StringData = "NewData";
            string eTag1 = await manager.UpdateTableEntryAsync(data2, AzureTableUtils.ANY_ETAG);
            tuple = await manager.ReadSingleTableEntryAsync(data2.PartitionKey, data2.RowKey);
            Assert.Equal(data2.StringData, tuple.Entity.StringData);

            var data3 = data.Clone();
            data3.StringData = "EvenNewerData";
            _ = await manager.UpdateTableEntryAsync(data3, eTag1);
            tuple = await manager.ReadSingleTableEntryAsync(data3.PartitionKey, data3.RowKey);
            Assert.Equal(data3.StringData, tuple.Entity.StringData);

            try
            {
                string eTag3 = await manager.UpdateTableEntryAsync(data3.Clone(), eTag1);
                Assert.True(false, "Should have thrown RequestFailedException.");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.PreconditionFailed, exc.Status);  // "Wrong eTag"
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.Equal(HttpStatusCode.PreconditionFailed, httpStatusCode);
                Assert.True(restStatus == TableErrorCode.UpdateConditionNotSatisfied.ToString());
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AzureTableDataManager_DeleteTableAsync()
        {
            var data = GenerateNewData();
            try
            {
                await manager.DeleteTableEntryAsync(data, AzureTableUtils.ANY_ETAG);
                Assert.True(false, "Should have thrown RequestFailedException.");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.NotFound, exc.Status);  // "Delete before create."
                HttpStatusCode httpStatusCode;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out _, true);
                Assert.Equal(HttpStatusCode.NotFound, httpStatusCode);
            }

            string eTag1 = await manager.UpsertTableEntryAsync(data);
            await manager.DeleteTableEntryAsync(data, eTag1);

            try
            {
                await manager.DeleteTableEntryAsync(data, eTag1);
                Assert.True(false, "Should have thrown RequestFailedException.");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.NotFound, exc.Status);  // "Deleting an already deleted item."
                HttpStatusCode httpStatusCode;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out _, true);
                Assert.Equal(HttpStatusCode.NotFound, httpStatusCode);
            }

            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.Null(tuple.Entity);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AzureTableDataManager_MergeTableAsync()
        {
            StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();
            var data = GenerateNewData();
            try
            {
                await manager.MergeTableEntryAsync(data, AzureTableUtils.ANY_ETAG);
                Assert.True(false, "Should have thrown RequestFailedException.");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.NotFound, exc.Status);  // "Merge before create."
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.Equal(HttpStatusCode.NotFound, httpStatusCode);
                Assert.Equal(TableErrorCode.ResourceNotFound.ToString(), restStatus);
            }

            string eTag1 = await manager.UpsertTableEntryAsync(data);
            var data2 = data.Clone();
            data2.StringData = "NewData";
            await manager.MergeTableEntryAsync(data2, eTag1);

            try
            {
                await manager.MergeTableEntryAsync(data, eTag1);
                Assert.True(false, "Should have thrown RequestFailedException.");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.PreconditionFailed, exc.Status);  // "Wrong eTag."
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.Equal(HttpStatusCode.PreconditionFailed, httpStatusCode);
                Assert.True(restStatus == TableErrorCode.UpdateConditionNotSatisfied.ToString());
            }

            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.Equal("NewData", tuple.Entity.StringData);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AzureTableDataManager_ReadSingleTableEntryAsync()
        {
            var data = GenerateNewData();
            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.Null(tuple.Entity);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AzureTableDataManager_InsertTwoTableEntriesConditionallyAsync()
        {
            StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();

            var data1 = GenerateNewData();
            var data2 = GenerateNewData();
            try
            {
                await manager.InsertTwoTableEntriesConditionallyAsync(data1, data2, AzureTableUtils.ANY_ETAG);
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.NotFound, exc.Status);  // "Upadte item 2 before created it."
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.Equal(HttpStatusCode.NotFound, httpStatusCode);
                Assert.Equal(TableErrorCode.ResourceNotFound.ToString(), restStatus);
            }

            string etag = await manager.CreateTableEntryAsync(data2.Clone());
            var tuple = await manager.InsertTwoTableEntriesConditionallyAsync(data1, data2, etag);
            try
            {
                await manager.InsertTwoTableEntriesConditionallyAsync(data1.Clone(), data2.Clone(), tuple.Item2);
                Assert.True(false, "Should have thrown RequestFailedException.");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.Conflict, exc.Status);  // "Inserting an already existing item 1."
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.Equal(HttpStatusCode.Conflict, httpStatusCode);
                Assert.Equal("EntityAlreadyExists", restStatus);
            }

            try
            {
                await manager.InsertTwoTableEntriesConditionallyAsync(data1.Clone(), data2.Clone(), AzureTableUtils.ANY_ETAG);
                Assert.True(false, "Should have thrown RequestFailedException.");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.Conflict, exc.Status);  // "Inserting an already existing item 1 AND wring eTag"
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.Equal(HttpStatusCode.Conflict, httpStatusCode);
                Assert.Equal("EntityAlreadyExists", restStatus);
            };
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AzureTableDataManager_UpdateTwoTableEntriesConditionallyAsync()
        {
            StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();

            var data1 = GenerateNewData();
            var data2 = GenerateNewData();
            try
            {
                await manager.UpdateTwoTableEntriesConditionallyAsync(data1, AzureTableUtils.ANY_ETAG, data2, AzureTableUtils.ANY_ETAG);
                Assert.True(false, "Update should have failed since the data has not been created yet");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.NotFound, exc.Status);  // "Update before insert."
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.Equal(HttpStatusCode.NotFound, httpStatusCode);
                Assert.Equal(TableErrorCode.ResourceNotFound.ToString(), restStatus);
            }

            string etag = await manager.CreateTableEntryAsync(data2.Clone());
            var tuple1 = await manager.InsertTwoTableEntriesConditionallyAsync(data1, data2, etag);
            _ = await manager.UpdateTwoTableEntriesConditionallyAsync(data1, tuple1.Item1, data2, tuple1.Item2);

            try
            {
                await manager.UpdateTwoTableEntriesConditionallyAsync(data1, tuple1.Item1, data2, tuple1.Item2);
                Assert.True(false, "Should have thrown RequestFailedException.");
            }
            catch (RequestFailedException exc)
            {
                Assert.Equal((int)HttpStatusCode.PreconditionFailed, exc.Status);  // "Wrong eTag"
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.Equal(HttpStatusCode.PreconditionFailed, httpStatusCode);
                Assert.True(restStatus == TableErrorCode.UpdateConditionNotSatisfied.ToString());
            }
        }
    }
}
