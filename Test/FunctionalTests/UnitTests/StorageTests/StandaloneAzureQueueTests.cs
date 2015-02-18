﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data.Services.Client;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Microsoft.WindowsAzure.Storage.Shared.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.AzureUtils;


namespace UnitTests.StorageTests
{
    [TestClass]
    public class StandaloneAzureQueueTests
    {
        private readonly TraceLogger logger;
        public static string DeploymentId = "standaloneaqtests".ToLower();
        private string queueName;

        public StandaloneAzureQueueTests()
        {
            ClientConfiguration config = new ClientConfiguration();
            config.TraceFilePattern = null;
            TraceLogger.Initialize(config);
            logger = TraceLogger.GetLogger("StandaloneAzureQueueTests", TraceLogger.LoggerType.Application);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            CleanupAfterTest();
        }

        private void CleanupAfterTest()
        {
            AzureQueueDataManager manager = GetTableManager(queueName).Result;
            manager.DeleteQueue().Wait();
        }

        private async Task<AzureQueueDataManager> GetTableManager(string qName)
        {
            AzureQueueDataManager manager = new AzureQueueDataManager(qName, DeploymentId, TestConstants.DataConnectionString);
            await manager.InitQueueAsync();
            return manager;
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure"), TestCategory("Queue")]
        public async Task AQ_Standalone_1()
        {
            queueName = "Test-1-".ToLower() + Guid.NewGuid();
            AzureQueueDataManager manager = await GetTableManager(queueName);
            Assert.AreEqual(0, await manager.GetApproximateMessageCount());

            CloudQueueMessage inMessage = new CloudQueueMessage("Hello, World");
            await manager.AddQueueMessage(inMessage);
            //Nullable<int> count = manager.ApproximateMessageCount;
            Assert.AreEqual(1, await manager.GetApproximateMessageCount());

            CloudQueueMessage outMessage1 = await manager.PeekQueueMessage();
            logger.Info("PeekQueueMessage 1: {0}", AzureStorageUtils.PrintCloudQueueMessage(outMessage1));
            Assert.AreEqual(inMessage.AsString, outMessage1.AsString);

            CloudQueueMessage outMessage2 = await manager.PeekQueueMessage();
            logger.Info("PeekQueueMessage 2: {0}", AzureStorageUtils.PrintCloudQueueMessage(outMessage2));
            Assert.AreEqual(inMessage.AsString, outMessage2.AsString);

            CloudQueueMessage outMessage3 = await manager.GetQueueMessage();
            logger.Info("GetQueueMessage 3: {0}", AzureStorageUtils.PrintCloudQueueMessage(outMessage3));
            Assert.AreEqual(inMessage.AsString, outMessage3.AsString);
            Assert.AreEqual(1, await manager.GetApproximateMessageCount());

            CloudQueueMessage outMessage4 = await manager.GetQueueMessage();
            Assert.IsNull(outMessage4);

            Assert.AreEqual(1, await manager.GetApproximateMessageCount());

            await manager.DeleteQueueMessage(outMessage3);
            Assert.AreEqual(0, await manager.GetApproximateMessageCount());
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure"), TestCategory("Queue")]
        public async Task AQ_Standalone_2()
        {
            queueName = "Test-2-".ToLower() + Guid.NewGuid();
            AzureQueueDataManager manager = await GetTableManager(queueName);

            IEnumerable<CloudQueueMessage> msgs = await manager.GetQueueMessages();
            Assert.IsTrue(msgs == null || msgs.Count() == 0);

            int numMsgs = 10;
            List<Task> promises = new List<Task>();
            for (int i = 0; i < numMsgs; i++)
            {
                promises.Add(manager.AddQueueMessage(new CloudQueueMessage(i.ToString())));
            }
            Task.WaitAll(promises.ToArray());
            Assert.AreEqual(numMsgs, await manager.GetApproximateMessageCount());

            msgs = new List<CloudQueueMessage>(await manager.GetQueueMessages(numMsgs));
            Assert.AreEqual(numMsgs, msgs.Count());
            Assert.AreEqual(numMsgs, await manager.GetApproximateMessageCount());

            promises = new List<Task>();
            foreach (var msg in msgs)
            {
                promises.Add(manager.DeleteQueueMessage(msg));
            }
            Task.WaitAll(promises.ToArray());
            Assert.AreEqual(0, await manager.GetApproximateMessageCount());
        }

#if DEBUG || REVISIT
        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure"), TestCategory("Queue")]
        public async Task AQ_Standalone_3_CreateDelete()
        {
            queueName = "Test-3-".ToLower() + Guid.NewGuid();
            AzureQueueDataManager manager = new AzureQueueDataManager(queueName, DeploymentId, TestConstants.DataConnectionString);
            await manager.InitQueueAsync();
            await manager.DeleteQueue();

            AzureQueueDataManager manager2 = new AzureQueueDataManager(queueName, DeploymentId, TestConstants.DataConnectionString);
            await manager2.InitQueueAsync();
            await manager2.DeleteQueue();

            AzureQueueDataManager manager3 = await GetTableManager(queueName);
            await manager3.DeleteQueue();
            await manager3.DeleteQueue();
            await manager3.DeleteQueue();
        }
#endif

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure"), TestCategory("Queue"), TestCategory("Stress"),]
        public async Task AQ_Standalone_4_Init_MultipleThreads()
        {
            queueName = "Test-4-".ToLower() + Guid.NewGuid();

            const int NumThreads = 100;
            Task<bool>[] promises = new Task<bool>[NumThreads];

            for (int i = 0; i < NumThreads; i++)
            {
                promises[i] = Task.Run<bool>(async () =>
                {
                    AzureQueueDataManager manager = new AzureQueueDataManager(queueName, DeploymentId, TestConstants.DataConnectionString);
                    await manager.InitQueueAsync();
                    return true;
                });
            }
            await Task.WhenAll(promises);
        }
    }
}
