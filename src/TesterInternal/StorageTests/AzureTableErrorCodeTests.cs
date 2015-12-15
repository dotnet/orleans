using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.AzureUtils;
using Orleans.TestingHost;
using System;
using System.Net;
using UnitTests.Tester;

namespace UnitTests.StorageTests
{
    [TestClass]
    public class AzureTableErrorCodeTests
    {
        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            UnitTestSiloHost.CheckForAzureStorage();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        public void AzureTableErrorCode_IsRetriableHttpError()
        {
            Assert.IsTrue(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 503, null));
            Assert.IsTrue(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 504, null));
            Assert.IsTrue(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 408, null));

            Assert.IsTrue(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 500, "OperationTimedOut"));
            Assert.IsFalse(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 500, null));
            Assert.IsFalse(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 500, "SomeOtherStatusValue"));

            // Current behaviour is to ignore successes as not retriable:
            Assert.IsFalse(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 200, null));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        public void AzureTableErrorCode_IsContentionError()
        {
            Assert.IsTrue(AzureStorageUtils.IsContentionError(HttpStatusCode.PreconditionFailed));
            Assert.IsTrue(AzureStorageUtils.IsContentionError(HttpStatusCode.Conflict));
            Assert.IsTrue(AzureStorageUtils.IsContentionError(HttpStatusCode.NotFound));
            Assert.IsTrue(AzureStorageUtils.IsContentionError(HttpStatusCode.NotImplemented));

            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode) 503));
            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode) 504));
            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode) 408));
            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode) 500));
            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode) 500));
            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode) 500));
            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode) 200));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        [ExpectedException(typeof (ArgumentException))]
        public void AzureTableErrorCode_BadTableName()
        {
            string tableName = "abc-123";
            AzureStorageUtils.ValidateTableName(tableName);
        }
    }
}
