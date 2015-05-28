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

using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Shared.Protocol;
using Microsoft.WindowsAzure.Storage.Table.Protocol;
using Orleans.AzureUtils;
using Orleans.TestingHost;

namespace UnitTests.StorageTests
{
    [TestClass]
    public class AzureTableDataManagerTests
    {
        private string PartitionKey;
        private UnitTestAzureTableDataManager manager;


        private UnitTestAzureTableData GenerateNewData()
        {
            return new UnitTestAzureTableData("JustData", PartitionKey, "RK-" + Guid.NewGuid());
        }

        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            //Starts the storage emulator if not started already and it exists (i.e. is installed).
            if(!StorageEmulator.TryStart())
            {
                Console.WriteLine("Azure Storage Emulator could not be started.");
            }
        }

        [TestInitialize]
        public void TestInitialize()
        {
            TestingUtils.ConfigureThreadPoolSettingsForStorageTests();
            // Pre-create table, if required
            manager = new UnitTestAzureTableDataManager(StorageTestConstants.DataConnectionString);
            PartitionKey = "PK-AzureTableDataManagerTests-" + Guid.NewGuid();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureTableDataManager_CreateTableEntryAsync()
        {
            var data = GenerateNewData();
            await manager.CreateTableEntryAsync(data);
            try
            {
                var data2 = data.Clone();
                data2.StringData = "NewData";
                await manager.CreateTableEntryAsync(data2);
                Assert.Fail("Should have thrown StorageException.");
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.Conflict, exc.RequestInformation.HttpStatusCode, "Creating an already existing entry.");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.Conflict, httpStatusCode);
                Assert.AreEqual("EntityAlreadyExists", restStatus);
            }
            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.AreEqual(data.StringData, tuple.Item1.StringData);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureTableDataManager_UpsertTableEntryAsync()
        {
            var data = GenerateNewData();
            await manager.UpsertTableEntryAsync(data);
            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.AreEqual(data.StringData, tuple.Item1.StringData);

            var data2 = data.Clone();
            data2.StringData = "NewData";
            await manager.UpsertTableEntryAsync(data2);
            tuple = await manager.ReadSingleTableEntryAsync(data2.PartitionKey, data2.RowKey);
            Assert.AreEqual(data2.StringData, tuple.Item1.StringData);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureTableDataManager_UpdateTableEntryAsync()
        {
            var data = GenerateNewData();
            try
            {
                await manager.UpdateTableEntryAsync(data, AzureStorageUtils.ANY_ETAG);
                Assert.Fail("Should have thrown StorageException.");
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.NotFound, exc.RequestInformation.HttpStatusCode, "Update before insert.");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.NotFound, httpStatusCode);
                Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, restStatus);
            }

            await manager.UpsertTableEntryAsync(data);
            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.AreEqual(data.StringData, tuple.Item1.StringData);

            var data2 = data.Clone();
            data2.StringData = "NewData";
            string eTag1 = await manager.UpdateTableEntryAsync(data2, AzureStorageUtils.ANY_ETAG);
            tuple = await manager.ReadSingleTableEntryAsync(data2.PartitionKey, data2.RowKey);
            Assert.AreEqual(data2.StringData, tuple.Item1.StringData);

            var data3 = data.Clone();
            data3.StringData = "EvenNewerData";
            string ignoredETag = await manager.UpdateTableEntryAsync(data3, eTag1);
            tuple = await manager.ReadSingleTableEntryAsync(data3.PartitionKey, data3.RowKey);
            Assert.AreEqual(data3.StringData, tuple.Item1.StringData);

            try
            {
                string eTag3 = await manager.UpdateTableEntryAsync(data3.Clone(), eTag1);
                Assert.Fail("Should have thrown StorageException.");
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.PreconditionFailed, exc.RequestInformation.HttpStatusCode, "Wrong eTag");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.PreconditionFailed, httpStatusCode);
                Assert.IsTrue(restStatus == TableErrorCodeStrings.UpdateConditionNotSatisfied
                            || restStatus == StorageErrorCodeStrings.ConditionNotMet, restStatus);
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureTableDataManager_DeleteTableAsync()
        {
            var data = GenerateNewData();
            try
            {
                await manager.DeleteTableEntryAsync(data, AzureStorageUtils.ANY_ETAG);
                Assert.Fail("Should have thrown StorageException.");
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.NotFound, exc.RequestInformation.HttpStatusCode, "Delete before create.");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.NotFound, httpStatusCode);
                Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, restStatus);
            }

            string eTag1 = await manager.UpsertTableEntryAsync(data);
            await manager.DeleteTableEntryAsync(data, eTag1);

            try
            {
                await manager.DeleteTableEntryAsync(data, eTag1);
                Assert.Fail("Should have thrown StorageException.");
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.NotFound, exc.RequestInformation.HttpStatusCode, "Deleting an already deleted item.");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.NotFound, httpStatusCode);
                Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, restStatus);
            }

            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.IsNull(tuple);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureTableDataManager_MergeTableAsync()
        {
            var data = GenerateNewData();
            try
            {
                await manager.MergeTableEntryAsync(data, AzureStorageUtils.ANY_ETAG);
                Assert.Fail("Should have thrown StorageException.");
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.NotFound, exc.RequestInformation.HttpStatusCode, "Merge before create.");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.NotFound, httpStatusCode);
                Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, restStatus);
            }

            string eTag1 = await manager.UpsertTableEntryAsync(data);
            var data2 = data.Clone();
            data2.StringData = "NewData";
            await manager.MergeTableEntryAsync(data2, eTag1);

            try
            {
                await manager.MergeTableEntryAsync(data, eTag1);
                Assert.Fail("Should have thrown StorageException.");
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.PreconditionFailed, exc.RequestInformation.HttpStatusCode, "Wrong eTag.");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.PreconditionFailed, httpStatusCode);
                Assert.IsTrue(restStatus == TableErrorCodeStrings.UpdateConditionNotSatisfied
                            || restStatus == StorageErrorCodeStrings.ConditionNotMet, restStatus);
            }

            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.AreEqual("NewData", tuple.Item1.StringData);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureTableDataManager_ReadSingleTableEntryAsync()
        {
            var data = GenerateNewData();
            var tuple = await manager.ReadSingleTableEntryAsync(data.PartitionKey, data.RowKey);
            Assert.IsNull(tuple);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureTableDataManager_InsertTwoTableEntriesConditionallyAsync()
        {
            var data1 = GenerateNewData();
            var data2 = GenerateNewData();
            try
            {
                await manager.InsertTwoTableEntriesConditionallyAsync(data1, data2, AzureStorageUtils.ANY_ETAG);
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.NotFound, exc.RequestInformation.HttpStatusCode, "Upadte item 2 before created it.");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.NotFound, httpStatusCode);
                Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, restStatus);
            }

            string etag = await manager.CreateTableEntryAsync(data2.Clone());
            var tuple = await manager.InsertTwoTableEntriesConditionallyAsync(data1, data2, etag);
            try
            {
                await manager.InsertTwoTableEntriesConditionallyAsync(data1.Clone(), data2.Clone(), tuple.Item2);
                Assert.Fail("Should have thrown StorageException.");
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.Conflict, exc.RequestInformation.HttpStatusCode, "Inserting an already existing item 1.");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.Conflict, httpStatusCode);
                Assert.AreEqual("EntityAlreadyExists", restStatus);
            }

            try
            {
                await manager.InsertTwoTableEntriesConditionallyAsync(data1.Clone(), data2.Clone(), AzureStorageUtils.ANY_ETAG);
                Assert.Fail("Should have thrown StorageException.");
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.Conflict, exc.RequestInformation.HttpStatusCode, "Inserting an already existing item 1 AND wring eTag");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.Conflict, httpStatusCode);
                Assert.AreEqual("EntityAlreadyExists", restStatus);
            };
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureTableDataManager_UpdateTwoTableEntriesConditionallyAsync()
        {
            var data1 = GenerateNewData();
            var data2 = GenerateNewData();
            try
            {
                await manager.UpdateTwoTableEntriesConditionallyAsync(data1, AzureStorageUtils.ANY_ETAG, data2, AzureStorageUtils.ANY_ETAG);
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.NotFound, exc.RequestInformation.HttpStatusCode, "Update before insert.");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.NotFound, httpStatusCode);
                Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, restStatus);
            }

            string etag = await manager.CreateTableEntryAsync(data2.Clone());
            var tuple1 = await manager.InsertTwoTableEntriesConditionallyAsync(data1, data2, etag);
            var tuple2 = await manager.UpdateTwoTableEntriesConditionallyAsync(data1, tuple1.Item1, data2, tuple1.Item2);

            try
            {
                await manager.UpdateTwoTableEntriesConditionallyAsync(data1, tuple1.Item1, data2, tuple1.Item2);
                Assert.Fail("Should have thrown StorageException.");
            }
            catch(StorageException exc)
            {
                Assert.AreEqual((int)HttpStatusCode.PreconditionFailed, exc.RequestInformation.HttpStatusCode, "Wrong eTag");
                HttpStatusCode httpStatusCode;
                string restStatus;
                AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus, true);
                Assert.AreEqual(HttpStatusCode.PreconditionFailed, httpStatusCode);
                Assert.IsTrue(restStatus == TableErrorCodeStrings.UpdateConditionNotSatisfied
                        || restStatus == StorageErrorCodeStrings.ConditionNotMet, restStatus);
            }
        }
    }
}
