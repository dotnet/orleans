﻿//using System;
//using System.Data.Services.Client;
//using System.Net;
//using Microsoft.VisualStudio.TestTools.UnitTesting;
//using Microsoft.WindowsAzure;
//using Microsoft.WindowsAzure.Storage;
//using Orleans;
//using Orleans.Runtime;
//using Orleans.Storage;
//using Orleans.AzureUtils;


//namespace UnitTests.StorageTests
//{
//    // ReSharper disable ConvertToConstant.Local

//    [TestClass]
//    public class AzureTableDataManagerTests
//    {
//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        public void ExtractRestErrorCode()
//        {
//            string xml = 
//"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" +
//"<error xmlns=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\">" +
//"<code>OperationTimedOut</code>" +
//"<message xml:lang=\"en-US\">Operation could not be completed within the specified time. RequestId:6b75e963-c56c-4734-a656-066cfd03f327 Time:2011-10-09T19:33:26.7631923Z</message>" +
//"</error>";
//            var exc = new DataServiceClientException(xml);
//            var strCode = AzureStorageUtils.ExtractRestErrorCode(exc);
//            Assert.AreEqual(StorageErrorCodeStrings.OperationTimedOut, strCode);

//            var wrapped = new AggregateException(exc);
//            strCode = AzureStorageUtils.ExtractRestErrorCode(wrapped);
//            Assert.AreEqual(StorageErrorCodeStrings.OperationTimedOut, strCode);
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        public void ExtractRestErrorCode_ServerBusy()
//        {
//            string xml = 
//"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" +
//"<error xmlns=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\">" +
//"<code>ServerBusy</code>" +
//"<message xml:lang=\"en-US\">The server is busy." +
//"RequestId:14ab4de6-fe80-4a45-a364-b0c2a27a4a86" +
//"Time:2013-09-24T23:29:13.4913945Z</message>" +
//"</error>";

//            var exc = new DataServiceClientException(xml);
//            var strCode = AzureStorageUtils.ExtractRestErrorCode(exc);
//            Assert.AreEqual(StorageErrorCodeStrings.ServerBusy, strCode);

//            var wrapped = new AggregateException(exc);
//            strCode = AzureStorageUtils.ExtractRestErrorCode(wrapped);
//            Assert.AreEqual(StorageErrorCodeStrings.ServerBusy, strCode);

//            //Assert.IsTrue(Async_AzureTableDataManager<SiloMetricsData>.IsRetriableHttpError((HttpStatusCode)500, "ServerBusy"));
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        public void ExtractRestErrorCode_InsufficientAccountPermissions()
//        {
//            string xml = 
//"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" +
//"<error xmlns=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\">" +
//"<code>InsufficientAccountPermissions</code>" +
//"<message xml:lang=\"en-US\">The account being accessed does not have sufficient permissions to execute this operation." +
//"RequestId:f6f4d0d3-879f-414c-bdf4-582faa7581e8" +
//"Time:2012-05-14T18:13:13.1526007Z</message>" +
//"</error>";
//            var exc = new DataServiceClientException(xml);
//            var strCode = AzureStorageUtils.ExtractRestErrorCode(exc);
//            Assert.AreEqual("InsufficientAccountPermissions", strCode);

//            var wrapped = new AggregateException(exc);
//            strCode = AzureStorageUtils.ExtractRestErrorCode(wrapped);
//            Assert.AreEqual("InsufficientAccountPermissions", strCode);
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        public void ExtractRestErrorCode_ResourceNotFound()
//        {
//            string xml =
//"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" +
//"<error xmlns=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\">" +
//"<code>ResourceNotFound</code>" +
//"<message xml:lang=\"en-US\">The specified resource does not exist." +
//"RequestId:23e7a3b9-3d08-4461-ba49-737c9c211142" +
//"Time:2013-10-10T17:35:59.7597108Z</message>" +
//"</error>";

//            var clientExc = new DataServiceClientException(xml);
//            var strCode = AzureStorageUtils.ExtractRestErrorCode(clientExc);
//            Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, strCode);

//            var clientWrapped1 = new AggregateException(clientExc);
//            strCode = AzureStorageUtils.ExtractRestErrorCode(clientWrapped1);
//            Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, strCode);

//            var clientWrapped2 = new OrleansException("client", clientExc);
//            strCode = AzureStorageUtils.ExtractRestErrorCode(clientWrapped2);
//            Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, strCode);

//            var clientWrapped3 = new DataServiceQueryException("QueryException", clientExc);
//            strCode = AzureStorageUtils.ExtractRestErrorCode(clientWrapped3);
//            Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, strCode);

//            var clientWrapped4 = new DataServiceQueryException("Wrapped-clientWrapper1", clientWrapped1);
//            strCode = AzureStorageUtils.ExtractRestErrorCode(clientWrapped4);
//            Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, strCode);

//            var superWrapped1 = new DataServiceQueryException("SuperWrapped-Client4", new AggregateException("Wrapper5", clientWrapped4));
//            strCode = AzureStorageUtils.ExtractRestErrorCode(superWrapped1);
//            Assert.AreEqual(StorageErrorCodeStrings.ResourceNotFound, strCode);
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        public void ResourceNotFound()
//        {
//            string xml =
//"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" +
//"<error xmlns=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\">" +
//"<code>ResourceNotFound</code>" +
//"<message xml:lang=\"en-US\">The specified resource does not exist." +
//"RequestId:23e7a3b9-3d08-4461-ba49-737c9c211142" +
//"Time:2013-10-10T17:35:59.7597108Z</message>" +
//"</error>";

//            var clientExc = new DataServiceClientException(xml);
//            bool isTableStorageDataNotFound = AzureStorageUtils.TableStorageDataNotFound(clientExc);
//            Assert.IsTrue(isTableStorageDataNotFound, "Is TableStorageDataNotFound " + clientExc);

//            var clientWrapped1 = new AggregateException(clientExc);
//            isTableStorageDataNotFound = AzureStorageUtils.TableStorageDataNotFound(clientWrapped1);
//            Assert.IsTrue(isTableStorageDataNotFound, "Is TableStorageDataNotFound " + clientWrapped1);

//            var clientWrapped2 = new OrleansException("client", clientExc);
//            isTableStorageDataNotFound = AzureStorageUtils.TableStorageDataNotFound(clientWrapped2);
//            Assert.IsTrue(isTableStorageDataNotFound, "Is TableStorageDataNotFound " + clientWrapped2);

//            var clientWrapped3 = new DataServiceQueryException("QueryException", clientExc);
//            isTableStorageDataNotFound = AzureStorageUtils.TableStorageDataNotFound(clientWrapped3);
//            Assert.IsTrue(isTableStorageDataNotFound, "Is TableStorageDataNotFound " + clientWrapped3);

//            var clientWrapped4 = new DataServiceQueryException("Wrapped-clientWrapper1", clientWrapped1);
//            isTableStorageDataNotFound = AzureStorageUtils.TableStorageDataNotFound(clientWrapped4);
//            Assert.IsTrue(isTableStorageDataNotFound, "Is TableStorageDataNotFound " + clientWrapped4);

//            var superWrapped1 = new DataServiceQueryException("SuperWrapped-Client4", new AggregateException("Wrapper5", clientWrapped4));
//            isTableStorageDataNotFound = AzureStorageUtils.TableStorageDataNotFound(superWrapped1);
//            Assert.IsTrue(isTableStorageDataNotFound, "Is TableStorageDataNotFound " + superWrapped1);
//        }



//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        public void UpdateConditionNotSatisfied()
//        {
//            string xml =
//"<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
//"<error xmlns=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\">" + 
//"<code>UpdateConditionNotSatisfied</code>" + 
//"<message xml:lang=\"en-US\">The update condition specified in the request was not satisfied." +
//"RequestId:0781ff48-0002-0023-3167-3673bb000000" +
//"Time:2014-10-29T14:43:17.4073101Z</message>" + 
//"</error>";

//            var clientExc = new DataServiceClientException(xml);
//            var strCode = AzureStorageUtils.ExtractRestErrorCode(clientExc);
//            Assert.AreEqual(TableErrorCodeStrings.UpdateConditionNotSatisfied, strCode);
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        public void ExtractRestErrorCode_BadData_Garbage()
//        {
//            string xml = "GARBAGE";

//            var exc = new DataServiceClientException(xml);
//            var strCode = AzureStorageUtils.ExtractRestErrorCode(exc);
//            Assert.IsNull(strCode);

//            var wrapped = new AggregateException(exc);
//            strCode = AzureStorageUtils.ExtractRestErrorCode(wrapped);
//            Assert.IsNull(strCode);
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        public void ExtractRestErrorCode_BadData_Null()
//        {
//            string xml = null;

//            var exc = new DataServiceClientException(xml);
//            var strCode = AzureStorageUtils.ExtractRestErrorCode(exc);
//            Assert.IsNull(strCode);

//            var wrapped = new AggregateException(exc);
//            strCode = AzureStorageUtils.ExtractRestErrorCode(wrapped);
//            Assert.IsNull(strCode);
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        public void IsRetriableHttpError()
//        {
//            Assert.IsTrue(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode)503, null));
//            Assert.IsTrue(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode)504, null));
//            Assert.IsTrue(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode)408, null));

//            Assert.IsTrue(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode)500, "OperationTimedOut"));
//            Assert.IsFalse(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode)500, null));
//            Assert.IsFalse(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode)500, "SomeOtherStatusValue"));
            
//            // Current behaviour is to ignore successes as not retriable:
//            Assert.IsFalse(AzureStorageUtils.IsRetriableHttpError((HttpStatusCode)200, null));
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        public void IsContentionError()
//        {
//            Assert.IsTrue(AzureStorageUtils.IsContentionError(HttpStatusCode.PreconditionFailed));
//            Assert.IsTrue(AzureStorageUtils.IsContentionError(HttpStatusCode.Conflict));
//            Assert.IsTrue(AzureStorageUtils.IsContentionError(HttpStatusCode.NotFound));
//            Assert.IsTrue(AzureStorageUtils.IsContentionError(HttpStatusCode.NotImplemented));

//            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode)503));
//            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode)504));
//            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode)408));
//            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode)500));
//            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode)500));
//            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode)500));
//            Assert.IsFalse(AzureStorageUtils.IsContentionError((HttpStatusCode)200));
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Persistence")]
//        [ExpectedException(typeof(ArgumentException))]
//        public void BadTableName()
//        {
//            string tableName = "abc-123";
//            AzureStorageUtils.ValidateTableName(tableName);
//        }
//    }
//    // ReSharper restore ConvertToConstant.Local
//}
