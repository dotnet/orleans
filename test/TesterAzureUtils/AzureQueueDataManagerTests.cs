using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    [TestCategory("Azure"), TestCategory("Storage"), TestCategory("AzureQueue")]
    public class AzureQueueDataManagerTests : IClassFixture<AzureStorageBasicTests>, IDisposable
    {
        private readonly Logger logger;
        public static string DeploymentId = "aqdatamanagertests".ToLower();
        private string queueName;

        public AzureQueueDataManagerTests()
        {
            ClientConfiguration config = new ClientConfiguration();
            config.TraceFilePattern = null;
            LogManager.Initialize(config);
            logger = LogManager.GetLogger("AzureQueueDataManagerTests", LoggerType.Application);
        }

        public void Dispose()
        {
            AzureQueueDataManager manager = GetTableManager(queueName).Result;
            manager.DeleteQueue().Wait();
        }


        private async Task<AzureQueueDataManager> GetTableManager(string qName, TimeSpan? visibilityTimeout = null)
        {
            AzureQueueDataManager manager = new AzureQueueDataManager(qName, DeploymentId, TestDefaultConfiguration.DataConnectionString, visibilityTimeout);
            await manager.InitQueueAsync();
            return manager;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Standalone_1()
        {
            queueName = "Test-1-".ToLower() + Guid.NewGuid();
            AzureQueueDataManager manager = await GetTableManager(queueName);
            Assert.Equal(0, await manager.GetApproximateMessageCount());

            CloudQueueMessage inMessage = new CloudQueueMessage("Hello, World");
            await manager.AddQueueMessage(inMessage);
            //Nullable<int> count = manager.ApproximateMessageCount;
            Assert.Equal(1, await manager.GetApproximateMessageCount());

            CloudQueueMessage outMessage1 = await manager.PeekQueueMessage();
            logger.Info("PeekQueueMessage 1: {0}", AzureStorageUtils.PrintCloudQueueMessage(outMessage1));
            Assert.Equal(inMessage.AsString, outMessage1.AsString);

            CloudQueueMessage outMessage2 = await manager.PeekQueueMessage();
            logger.Info("PeekQueueMessage 2: {0}", AzureStorageUtils.PrintCloudQueueMessage(outMessage2));
            Assert.Equal(inMessage.AsString, outMessage2.AsString);

            CloudQueueMessage outMessage3 = await manager.GetQueueMessage();
            logger.Info("GetQueueMessage 3: {0}", AzureStorageUtils.PrintCloudQueueMessage(outMessage3));
            Assert.Equal(inMessage.AsString, outMessage3.AsString);
            Assert.Equal(1, await manager.GetApproximateMessageCount());

            CloudQueueMessage outMessage4 = await manager.GetQueueMessage();
           Assert.Null(outMessage4);

            Assert.Equal(1, await manager.GetApproximateMessageCount());

            await manager.DeleteQueueMessage(outMessage3);
            Assert.Equal(0, await manager.GetApproximateMessageCount());
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Standalone_2()
        {
            queueName = "Test-2-".ToLower() + Guid.NewGuid();
            AzureQueueDataManager manager = await GetTableManager(queueName);

            IEnumerable<CloudQueueMessage> msgs = await manager.GetQueueMessages();
            Assert.True(msgs == null || msgs.Count() == 0);

            int numMsgs = 10;
            List<Task> promises = new List<Task>();
            for (int i = 0; i < numMsgs; i++)
            {
                promises.Add(manager.AddQueueMessage(new CloudQueueMessage(i.ToString())));
            }
            Task.WaitAll(promises.ToArray());
            Assert.Equal(numMsgs, await manager.GetApproximateMessageCount());

            msgs = new List<CloudQueueMessage>(await manager.GetQueueMessages(numMsgs));
            Assert.Equal(numMsgs, msgs.Count());
            Assert.Equal(numMsgs, await manager.GetApproximateMessageCount());

            promises = new List<Task>();
            foreach (var msg in msgs)
            {
                promises.Add(manager.DeleteQueueMessage(msg));
            }
            Task.WaitAll(promises.ToArray());
            Assert.Equal(0, await manager.GetApproximateMessageCount());
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Standalone_3_Init_MultipleThreads()
        {
            queueName = "Test-4-".ToLower() + Guid.NewGuid();

            const int NumThreads = 100;
            Task<bool>[] promises = new Task<bool>[NumThreads];

            for (int i = 0; i < NumThreads; i++) 
            {
                promises[i] = Task.Run<bool>(async () =>
                {
                    AzureQueueDataManager manager = await GetTableManager(queueName);
                    return true;
                });
            }
            await Task.WhenAll(promises);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Standalone_4()
        {
            TimeSpan visibilityTimeout = TimeSpan.FromSeconds(2);

            queueName = "Test-5-".ToLower() + Guid.NewGuid();
            AzureQueueDataManager manager = await GetTableManager(queueName, visibilityTimeout);
            Assert.Equal(0, await manager.GetApproximateMessageCount());

            CloudQueueMessage inMessage = new CloudQueueMessage("Hello, World");
            await manager.AddQueueMessage(inMessage);
            Assert.Equal(1, await manager.GetApproximateMessageCount());
            
            CloudQueueMessage outMessage = await manager.GetQueueMessage();
            logger.Info("GetQueueMessage: {0}", AzureStorageUtils.PrintCloudQueueMessage(outMessage));
            Assert.Equal(inMessage.AsString, outMessage.AsString);

            await Task.Delay(visibilityTimeout);

            Assert.Equal(1, await manager.GetApproximateMessageCount());

            CloudQueueMessage outMessage2 = await manager.GetQueueMessage();
            Assert.Equal(inMessage.AsString, outMessage2.AsString);

            await manager.DeleteQueueMessage(outMessage2);
            Assert.Equal(0, await manager.GetApproximateMessageCount());
        }
    }
}
