
using System;
using System.Net;
using Orleans.AzureUtils;
using Xunit;

namespace UnitTests.StorageTests
{
    public class AzureTableErrorCodeTests : IClassFixture<AzureStorageBasicTestFixture>
    {

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        public void AzureTableErrorCode_IsRetriableHttpError()
        {
            Assert.True(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 503, null));
            Assert.True(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 504, null));
            Assert.True(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 408, null));

            Assert.True(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 500, "OperationTimedOut"));
            Assert.False(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 500, null));
            Assert.False(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 500, "SomeOtherStatusValue"));

            // Current behaviour is to ignore successes as not retriable:
            Assert.False(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode) 200, null));
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        public void AzureTableErrorCode_IsContentionError()
        {
            Assert.True(AzureStorageUtils.IsContentionError(HttpStatusCode.PreconditionFailed));
            Assert.True(AzureStorageUtils.IsContentionError(HttpStatusCode.Conflict));
            Assert.True(AzureStorageUtils.IsContentionError(HttpStatusCode.NotFound));
            Assert.True(AzureStorageUtils.IsContentionError(HttpStatusCode.NotImplemented));

            Assert.False(AzureStorageUtils.IsContentionError((HttpStatusCode) 503));
            Assert.False(AzureStorageUtils.IsContentionError((HttpStatusCode) 504));
            Assert.False(AzureStorageUtils.IsContentionError((HttpStatusCode) 408));
            Assert.False(AzureStorageUtils.IsContentionError((HttpStatusCode) 500));
            Assert.False(AzureStorageUtils.IsContentionError((HttpStatusCode) 500));
            Assert.False(AzureStorageUtils.IsContentionError((HttpStatusCode) 500));
            Assert.False(AzureStorageUtils.IsContentionError((HttpStatusCode) 200));
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        public void AzureTableErrorCode_BadTableName()
        {
            
            string tableName = "abc-123";
            Assert.Throws<ArgumentException>(() => 
            AzureStorageUtils.ValidateTableName(tableName));
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        public void AzureStorageUtils_TablePropertyShouldBeSanitized()
        {
            var tableProperty = "/A\\C#?";
            Assert.Equal("_A_C__", AzureStorageUtils.SanitizeTableProperty(tableProperty));
        }
    }
}
