using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using TestExtensions;
using Xunit;
using Tester;
using Orleans.Internal;

namespace UnitTests.General
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class RequestContextTests_Local : IDisposable
    {
        private readonly Dictionary<string, object> headers = new Dictionary<string, object>();


        private readonly TestEnvironmentFixture fixture;

        public RequestContextTests_Local(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
            RequestContext.Clear();
            headers.Clear();
        }

        public void Dispose()
        {
            TestCleanup();
        }

        private void TestCleanup()
        {
            RequestContextTestUtils.ClearActivityId();
            RequestContext.Clear();
            headers.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_MultiThreads_ExportToMessage()
        {
            const int NumLoops = 50;
            string id = "key" + Random.Shared.Next();

            Message msg = new Message();
            Task[] promises = new Task[NumLoops];
            ManualResetEventSlim flag = new ManualResetEventSlim(false);
            for (int i = 0; i < NumLoops; i++)
            {
                string key = id + "-" + i;
                RequestContext.Set(key, i);
                promises[i] = Task.Run(() =>
                {
                    flag.Wait();
                    msg.RequestContextData = RequestContextExtensions.Export(this.fixture.DeepCopier);
                });
                flag.Set();
                Thread.Sleep(1);
                RequestContext.Remove(key);
            }
            await Task.WhenAll(promises);
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public void RequestContext_ActivityId_ExportToMessage()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();
            Guid nullActivityId = Guid.Empty;

            Message msg = new Message();
            msg.RequestContextData = RequestContextExtensions.Export(this.fixture.DeepCopier);
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
                {
                    headers.Add(kvp.Key, kvp.Value);
                };
            Assert.False(headers.ContainsKey(RequestContext.CALL_CHAIN_REENTRANCY_HEADER), "ActivityId should not be be present " + headers.ToStrings(separator: ","));
            TestCleanup();

            RequestContext.ReentrancyId = activityId;
            msg = new Message();
            msg.RequestContextData = RequestContextExtensions.Export(this.fixture.DeepCopier);
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
                {
                    headers.Add(kvp.Key, kvp.Value);
                };
            Assert.True(headers.ContainsKey(RequestContext.CALL_CHAIN_REENTRANCY_HEADER), "ActivityId #1 should be present " + headers.ToStrings(separator: ","));
            object result = headers[RequestContext.CALL_CHAIN_REENTRANCY_HEADER];
            Assert.NotNull(result);// ActivityId #1 should not be null
            Assert.Equal(activityId, result);  // "E2E ActivityId #1 not propagated correctly"
            Assert.Equal(activityId, RequestContextTestUtils.GetActivityId());  // "Original E2E ActivityId #1 should not have changed"
            TestCleanup();

            RequestContextTestUtils.SetActivityId(nullActivityId);
            msg = new Message();
            msg.RequestContextData = RequestContextExtensions.Export(this.fixture.DeepCopier);
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
                {
                    headers.Add(kvp.Key, kvp.Value);
                };
            Assert.False(headers.ContainsKey(RequestContext.CALL_CHAIN_REENTRANCY_HEADER), "Null ActivityId should not be present " + headers.ToStrings(separator: ","));
            TestCleanup();

            RequestContextTestUtils.SetActivityId(activityId2);
            msg = new Message();
            msg.RequestContextData = RequestContextExtensions.Export(this.fixture.DeepCopier);
            foreach (var kvp in msg.RequestContextData)
            {
                headers.Add(kvp.Key, kvp.Value);
            };
            Assert.True(headers.ContainsKey(RequestContext.CALL_CHAIN_REENTRANCY_HEADER), "ActivityId #2 should be present " + headers.ToStrings(separator: ","));
            result = headers[RequestContext.CALL_CHAIN_REENTRANCY_HEADER];
            Assert.NotNull(result); // ActivityId #2 should not be null
            Assert.Equal(activityId2, result);  // "E2E ActivityId #2 not propagated correctly"

            Assert.Equal(activityId2, RequestContextTestUtils.GetActivityId());  // "Original E2E ActivityId #2 should not have changed"
            TestCleanup();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public void RequestContext_ActivityId_ExportImport()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();
            Guid nullActivityId = Guid.Empty;

            Message msg = new Message();
            msg.RequestContextData = RequestContextExtensions.Export(this.fixture.DeepCopier);
            RequestContext.Clear();
            RequestContextExtensions.Import(msg.RequestContextData);
            var actId = RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER);
            Assert.Null(actId);
            TestCleanup();

            RequestContextTestUtils.SetActivityId(activityId);
            msg = new Message();
            msg.RequestContextData = RequestContextExtensions.Export(this.fixture.DeepCopier);
            RequestContext.Clear();
            RequestContextExtensions.Import(msg.RequestContextData);
            actId = RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER);
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
                {
                    headers.Add(kvp.Key, kvp.Value);
                };
            Assert.NotNull(actId); // "ActivityId #1 should be present " + headers.ToStrings(separator: ",")
            object result = headers[RequestContext.CALL_CHAIN_REENTRANCY_HEADER];
            Assert.NotNull(result);// "ActivityId #1 should not be null"
            Assert.Equal(activityId, result);  // "E2E ActivityId #1 not propagated correctly"
            Assert.Equal(activityId, RequestContextTestUtils.GetActivityId());  // "Original E2E ActivityId #1 should not have changed"
            TestCleanup();

            RequestContextTestUtils.SetActivityId(nullActivityId);
            msg = new Message();
            msg.RequestContextData = RequestContextExtensions.Export(this.fixture.DeepCopier);
            RequestContext.Clear();
            RequestContextExtensions.Import(msg.RequestContextData);
            actId = RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER);
            Assert.Null(actId);
            TestCleanup();

            RequestContextTestUtils.SetActivityId(activityId2);
            msg = new Message();
            msg.RequestContextData = RequestContextExtensions.Export(this.fixture.DeepCopier);
            RequestContext.Clear();
            RequestContextExtensions.Import(msg.RequestContextData);
            actId = RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER);
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
                {
                    headers.Add(kvp.Key, kvp.Value);
                };
            Assert.NotNull(actId); // "ActivityId #2 should be present " + headers.ToStrings(separator: ",")
            result = headers[RequestContext.CALL_CHAIN_REENTRANCY_HEADER];
            Assert.NotNull(result); // "ActivityId #2 should not be null"
            Assert.Equal(activityId2, result);// "E2E ActivityId #2 not propagated correctly

            Assert.Equal(activityId2, RequestContextTestUtils.GetActivityId()); // "Original E2E ActivityId #2 should not have changed"
            TestCleanup();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_CrossThread()
        {
            const int NumLoops = 1000;

            string name1 = "Name" + Random.Shared.Next();
            string data1 = "Main";

            RequestContext.Set(name1, data1);
            Assert.Equal(data1, RequestContext.Get(name1));  // "RC.GetData-Main"

            Task[] promises = new Task[NumLoops];
            for (int i = 0; i < NumLoops; i++)
            {
                string str = i.ToString(CultureInfo.InvariantCulture);
                promises[i] = Task.Run(async () =>
                {
                    await Task.Delay(5);
                    Assert.Equal(data1, RequestContext.Get(name1));  // "RC.GetData-Task.Run-0"
                    await Task.Delay(5);
                    // Set New value
                    RequestContext.Set(name1, str);
                    Assert.Equal(str, RequestContext.Get(name1));  // "RC.GetData-Task.Run-1-" + str
                    await Task.Delay(5);
                    Assert.Equal(str, RequestContext.Get(name1));  // "RC.GetData-Task.Run-2-" + str
                    await Task.Delay(5);
                    Assert.Equal(str, RequestContext.Get(name1));  // "RC.GetData-Task.Run-3-" + str
                });
            }
            await Task.WhenAll(promises);
            Assert.Equal(data1, RequestContext.Get(name1));  // "RC.GetData-Main-Final"
        }

    }

}
