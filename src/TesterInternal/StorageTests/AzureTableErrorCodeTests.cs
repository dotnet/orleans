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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.AzureUtils;
using Orleans.TestingHost;
using System;
using System.Net;

namespace UnitTests.StorageTests
{
    [TestClass]
    public class AzureTableErrorCodeTests
    {
        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            //Starts the storage emulator if not started already and it exists (i.e. is installed).
            if(!StorageEmulator.TryStart())
            {
                Console.WriteLine("Azure Storage Emulator could not be started.");
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
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

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
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

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        [ExpectedException(typeof (ArgumentException))]
        public void AzureTableErrorCode_BadTableName()
        {
            string tableName = "abc-123";
            AzureStorageUtils.ValidateTableName(tableName);
        }
    }
}
