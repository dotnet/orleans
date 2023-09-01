using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using Xunit;

namespace Tester.AzureUtils
{
    [TestCategory("AzureStorage"), TestCategory("Storage"), TestCategory("AzureQueue")]
    public class AzureQueueDataManagerTests : IClassFixture<AzureStorageBasicTests>, IAsyncLifetime
    {
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        public static string DeploymentId = "aqdatamanagertests".ToLower();
        private string queueName;

        public AzureQueueDataManagerTests()
        {
            var loggerFactory = TestingUtils.CreateDefaultLoggerFactory(TestingUtils.CreateTraceFileName("Client", DateTime.Now.ToString("yyyyMMdd_hhmmss")));
            logger = loggerFactory.CreateLogger<AzureQueueDataManagerTests>();
            this.loggerFactory = loggerFactory;
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            AzureQueueDataManager manager = await GetTableManager(queueName);
            await manager.DeleteQueue();
        }

        private async Task<AzureQueueDataManager> GetTableManager(string qName, TimeSpan? visibilityTimeout = null)
        {
            AzureQueueDataManager manager = new AzureQueueDataManager(this.loggerFactory, $"{qName}-{DeploymentId}", new AzureQueueOptions { MessageVisibilityTimeout = visibilityTimeout }.ConfigureTestDefaults());
            await manager.InitQueueAsync();
            return manager;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Standalone_1()
        {
            queueName = "Test-1-".ToLower() + Guid.NewGuid();
            AzureQueueDataManager manager = await GetTableManager(queueName);
            Assert.Equal(0, await manager.GetApproximateMessageCount());

            var inMessage = "Hello, World";
            await manager.AddQueueMessage(inMessage);
            //Nullable<int> count = manager.ApproximateMessageCount;
            Assert.Equal(1, await manager.GetApproximateMessageCount());

            var outMessage1 = await manager.PeekQueueMessage();
            logger.LogInformation("PeekQueueMessage 1: {Message}", PrintQueueMessage(outMessage1));
            Assert.Equal(inMessage, outMessage1.MessageText);

            var outMessage2 = await manager.PeekQueueMessage();
            logger.LogInformation("PeekQueueMessage 2: {Message}", PrintQueueMessage(outMessage2));
            Assert.Equal(inMessage, outMessage2.MessageText);

            QueueMessage outMessage3 = await manager.GetQueueMessage();
            logger.LogInformation("GetQueueMessage 3: {Message}", PrintQueueMessage(outMessage3));
            Assert.Equal(inMessage, outMessage3.MessageText);
            Assert.Equal(1, await manager.GetApproximateMessageCount());

            QueueMessage outMessage4 = await manager.GetQueueMessage();
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

            IEnumerable<QueueMessage> msgs = await manager.GetQueueMessages();
            Assert.True(msgs == null || !msgs.Any());

            int numMsgs = 10;
            List<Task> promises = new List<Task>();
            for (int i = 0; i < numMsgs; i++)
            {
                promises.Add(manager.AddQueueMessage(i.ToString()));
            }
            Task.WaitAll(promises.ToArray());
            Assert.Equal(numMsgs, await manager.GetApproximateMessageCount());

            msgs = new List<QueueMessage>(await manager.GetQueueMessages(numMsgs));
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
                promises[i] = Task.Run(async () =>
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

            var inMessage = "Hello, World";
            await manager.AddQueueMessage(inMessage);
            Assert.Equal(1, await manager.GetApproximateMessageCount());

            QueueMessage outMessage = await manager.GetQueueMessage();
            logger.LogInformation("GetQueueMessage: {Message}", PrintQueueMessage(outMessage));
            Assert.Equal(inMessage, outMessage.MessageText);

            await Task.Delay(visibilityTimeout);

            Assert.Equal(1, await manager.GetApproximateMessageCount());

            QueueMessage outMessage2 = await manager.GetQueueMessage();
            Assert.Equal(inMessage, outMessage2.MessageText);

            await manager.DeleteQueueMessage(outMessage2);
            Assert.Equal(0, await manager.GetApproximateMessageCount());
        }

        private static string PrintQueueMessage(QueueMessage message)
        {
            return string.Format("QueueMessage: Id = {0}, NextVisibleTime = {1}, DequeueCount = {2}, PopReceipt = {3}, Content = {4}",
                    message.MessageId,
                    message.NextVisibleOn.HasValue ? LogFormatter.PrintDate(message.NextVisibleOn.Value.DateTime) : "",
                    message.DequeueCount,
                    message.PopReceipt,
                    message.MessageText);
        }

        private static string PrintQueueMessage(PeekedMessage message)
        {
            return string.Format("QueueMessage: Id = {0}, DequeueCount = {1}, Content = {2}",
                    message.MessageId,
                    message.DequeueCount,
                    message.MessageText);
        }
    }
}
