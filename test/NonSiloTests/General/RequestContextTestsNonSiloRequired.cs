using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using TestExtensions;
using Xunit;

namespace UnitTests.General
{
    public class RequestContextTests_Local : IDisposable
    {
        private readonly Dictionary<string, object> headers = new Dictionary<string, object>();

        private static bool oldPropagateActivityId;

        private static readonly SafeRandom random = new SafeRandom();

        public RequestContextTests_Local()
        {
            SerializationTestEnvironment.Initialize();
            oldPropagateActivityId = RequestContext.PropagateActivityId;
            RequestContext.PropagateActivityId = true;
            Trace.CorrelationManager.ActivityId = Guid.Empty;
            RequestContext.Clear();
            headers.Clear();
            GrainClient.ClientInvokeCallback = null;
        }

        public void Dispose()
        {
            TestCleanup();
        }

        private void TestCleanup()
        {
            Trace.CorrelationManager.ActivityId = Guid.Empty;
            RequestContext.Clear();
            headers.Clear();
            GrainClient.ClientInvokeCallback = null;
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_MultiThreads_ExportToMessage()
        {
            const int NumLoops = 50;
            string id = "key" + random.Next();

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
                    msg.RequestContextData = RequestContext.Export();
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
            msg.RequestContextData = RequestContext.Export();
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
                {
                    headers.Add(kvp.Key, kvp.Value);
                };
            Assert.False(headers.ContainsKey(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "ActivityId should not be be present " + headers.ToStrings(separator: ","));
            TestCleanup();

#if !NETSTANDARD
            Trace.CorrelationManager.ActivityId = activityId;
#else
            RequestContext.ActivityId.Value = activityId;
#endif
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
                {
                    headers.Add(kvp.Key, kvp.Value);
                };
            Assert.True(headers.ContainsKey(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "ActivityId #1 should be present " + headers.ToStrings(separator: ","));
            object result = headers[RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER];
            Assert.NotNull(result);// ActivityId #1 should not be null
            Assert.Equal(activityId, result);  // "E2E ActivityId #1 not propagated correctly"
#if !NETSTANDARD
            Assert.Equal(activityId, Trace.CorrelationManager.ActivityId);  // "Original E2E ActivityId #1 should not have changed"
#else
            Assert.Equal(activityId, RequestContext.ActivityId.Value);  // "Original E2E ActivityId #1 should not have changed"
#endif
            TestCleanup();

#if !NETSTANDARD
            Trace.CorrelationManager.ActivityId = nullActivityId;
#else
            RequestContext.ActivityId.Value = nullActivityId;
#endif
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
                {
                    headers.Add(kvp.Key, kvp.Value);
                };
            Assert.False(headers.ContainsKey(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "Null ActivityId should not be present " + headers.ToStrings(separator: ","));
            TestCleanup();

#if !NETSTANDARD
            Trace.CorrelationManager.ActivityId = activityId2;
#else
            RequestContext.ActivityId.Value = activityId2;
#endif
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            foreach (var kvp in msg.RequestContextData)
            {
                headers.Add(kvp.Key, kvp.Value);
            };
            Assert.True(headers.ContainsKey(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "ActivityId #2 should be present " + headers.ToStrings(separator: ","));
            result = headers[RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER];
            Assert.NotNull(result); // ActivityId #2 should not be null
            Assert.Equal(activityId2, result);  // "E2E ActivityId #2 not propagated correctly"
#if !NETSTANDARD
            Assert.Equal(activityId2, Trace.CorrelationManager.ActivityId);  // "Original E2E ActivityId #2 should not have changed"
#else
            Assert.Equal(activityId2, RequestContext.ActivityId.Value);  // "Original E2E ActivityId #2 should not have changed"
#endif
            TestCleanup();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public void RequestContext_ActivityId_ExportImport()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();
            Guid nullActivityId = Guid.Empty;

            Message msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            RequestContext.Clear();
            RequestContext.Import(msg.RequestContextData);
            var actId = RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER);
            Assert.Null(actId);
            TestCleanup();
#if !NETSTANDARD
            Trace.CorrelationManager.ActivityId = activityId;
#else
            RequestContext.ActivityId.Value = activityId;
#endif
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            RequestContext.Clear();
            RequestContext.Import(msg.RequestContextData);
            actId = RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER);
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
                {
                    headers.Add(kvp.Key, kvp.Value);
                };
            Assert.NotNull(actId); // "ActivityId #1 should be present " + headers.ToStrings(separator: ",")
            object result = headers[RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER];
            Assert.NotNull(result);// "ActivityId #1 should not be null"
            Assert.Equal(activityId, result);  // "E2E ActivityId #1 not propagated correctly"
#if !NETSTANDARD
            Assert.Equal(activityId, Trace.CorrelationManager.ActivityId);  // "Original E2E ActivityId #1 should not have changed"
#else
            Assert.Equal(activityId, RequestContext.ActivityId.Value);  // "Original E2E ActivityId #1 should not have changed"
#endif
            TestCleanup();

#if !NETSTANDARD
            Trace.CorrelationManager.ActivityId = nullActivityId;
#else
            RequestContext.ActivityId.Value = nullActivityId;
#endif
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            RequestContext.Clear();
            RequestContext.Import(msg.RequestContextData);
            actId = RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER);
            Assert.Null(actId);
            TestCleanup();

#if !NETSTANDARD
            Trace.CorrelationManager.ActivityId = activityId2;
#else
            RequestContext.ActivityId.Value = activityId2;
#endif
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            RequestContext.Clear();
            RequestContext.Import(msg.RequestContextData);
            actId = RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER);
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
                {
                    headers.Add(kvp.Key, kvp.Value);
                };
            Assert.NotNull(actId); // "ActivityId #2 should be present " + headers.ToStrings(separator: ",")
            result = headers[RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER];
            Assert.NotNull(result); // "ActivityId #2 should not be null"
            Assert.Equal(activityId2, result);// "E2E ActivityId #2 not propagated correctly
#if !NETSTANDARD
            Assert.Equal(activityId2, Trace.CorrelationManager.ActivityId); // "Original E2E ActivityId #2 should not have changed"
#else
            Assert.Equal(activityId2, RequestContext.ActivityId.Value); // "Original E2E ActivityId #2 should not have changed"
#endif
            TestCleanup();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task LCC_Basic()
        {
            string name1 = "Name" + random.Next();
            string data1 = "Main";
            const int NumLoops = 1000;

            CallContext.LogicalSetData(name1, data1);

            Assert.Equal(data1, CallContext.LogicalGetData(name1));

            Task t = Task.Run(() =>
            {
                Assert.Equal(data1, CallContext.LogicalGetData(name1));
            });
            await t;

            Task[] promises = new Task[NumLoops];
            for (int i = 0; i < NumLoops; i++)
            {
                string str = i.ToString(CultureInfo.InvariantCulture);
                promises[i] = Task.Run(async () =>
                {
                    CallContext.LogicalSetData(name1, str);

                    await Task.Delay(10);

                    Assert.Equal(str, CallContext.LogicalGetData(name1));  // "LCC.GetData-Task.Run-"+str
                });
            }
            await Task.WhenAll(promises);
        }
        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task LCC_Dictionary()
        {
            string name1 = "Name" + random.Next();
            string data1 = "Main";
            const int NumLoops = 1000;

            var dict = new Dictionary<string, string>();
            dict[name1] = data1;
            CallContext.LogicalSetData(name1, dict);

            var result1 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
            Assert.Equal(data1, result1[name1]);  // "LCC.GetData-Main"

            Task t = Task.Run(() =>
            {
                var result2 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                Assert.Equal(data1, result2[name1]);  // "LCC.GetData-Task.Run"
                Assert.Same(dict, result2);  // "Same object LCC.GetData-Task.Run"
            });
            await t;

            Task[] promises = new Task[NumLoops];
            for (int i = 0; i < NumLoops; i++)
            {
                string str = i.ToString(CultureInfo.InvariantCulture);
                promises[i] = Task.Run(async () =>
                {
                    var dict2 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.Equal(data1, dict2[name1]);  // "LCC.GetData-Task.Run-Get-" + str
                    Assert.Same(dict, dict2);  // "Same object LCC.GetData-Task.Run-Get" + str

                    var dict3 = new Dictionary<string, string>();
                    dict3[name1] = str;
                    CallContext.LogicalSetData(name1, dict3);

                    await Task.Delay(10);

                    var result3 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.Equal(str, result3[name1]);  // "LCC.GetData-Task.Run-Set-" + str
                    Assert.Same(dict3, result3);  // "Same object LCC.GetData-Task.Run-Set-" + str
                    Assert.NotSame(dict2, result3);  // "Different object LCC.GetData-Task.Run-Set-" + str
                });
            }
            await Task.WhenAll(promises);
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task LCC_CrossThread()
        {
            const int NumLoops = 1000;

            string name1 = "Name" + random.Next();
            string data1 = "Main";

            CallContext.LogicalSetData(name1, data1);
            Assert.Equal(data1, CallContext.LogicalGetData(name1));  // "LCC.GetData-Main"

            Task[] promises = new Task[NumLoops];
            for (int i = 0; i < NumLoops; i++)
            {
                string str = i.ToString(CultureInfo.InvariantCulture);
                promises[i] = Task.Run(async () =>
                {
                    await Task.Delay(5);
                    Assert.Equal(data1, CallContext.LogicalGetData(name1));  // "LCC.GetData-Main"
                    await Task.Delay(5);
                    CallContext.LogicalSetData(name1, str);
                    Assert.Equal(str, CallContext.LogicalGetData(name1));  // "LCC.GetData-Task.Run-1-" + str
                    await Task.Delay(5);
                    Assert.Equal(str, CallContext.LogicalGetData(name1));  // "LCC.GetData-Task.Run-1-" + str
                    await Task.Delay(5);
                    Assert.Equal(str, CallContext.LogicalGetData(name1));  // "LCC.GetData-Task.Run-2-" + str
                });
            }
            await Task.WhenAll(promises);
            Assert.Equal(data1, CallContext.LogicalGetData(name1));  // "LCC.GetData-Main-Final"
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task LCC_CrossThread_Dictionary()
        {
            const int NumLoops = 1000;

            string name1 = "Name" + random.Next();
            string data1 = "Main";

            var dict = new Dictionary<string, string>();
            dict[name1] = data1;
            CallContext.LogicalSetData(name1, dict);

            var result0 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
            Assert.Equal(data1, result0[name1]);  // "LCC.GetData-Main"

            Task[] promises = new Task[NumLoops];
            for (int i = 0; i < NumLoops; i++)
            {
                string str = i.ToString(CultureInfo.InvariantCulture);
                promises[i] = Task.Run(async () =>
                {
                    var result1 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.Same(dict, result1);  // "Same object LCC.GetData-Task.Run-Get" + str
                    Assert.Equal(data1, result1[name1]);  // "LCC.GetData-Task.Run-Get-" + str

                    await Task.Delay(5);

                    var dict2 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.Same(dict, dict2);  // "Same object LCC.GetData-Task.Run-Get" + str
                    Assert.Equal(data1, dict2[name1]);  // "LCC.GetData-Task.Run-Get-" + str

                    // Set New Dictionary
                    var dict3 = new Dictionary<string, string>();
                    dict3[name1] = str;
                    CallContext.LogicalSetData(name1, dict3);

                    var result3 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.Same(dict3, result3);  // "Same object LCC.GetData-Task.Run-Set-1-" + str
                    Assert.Equal(str, result3[name1]);  // "LCC.GetData-Task.Run-Set-" + str

                    await Task.Delay(5);

                    result3 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.Same(dict3, result3);  // "Same object LCC.GetData-Task.Run-Set-1-" + str
                    Assert.Equal(str, result3[name1]);  // "LCC.GetData-Task.Run-Set-" + str

                    await Task.Delay(5);
                    result3 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.Same(dict3, result3);  // "Same object LCC.GetData-Task.Run-Set-2-" + str
                    Assert.Equal(str, result3[name1]);  // "LCC.GetData-Task.Run-Set-" + str
                });
            }
            await Task.WhenAll(promises);
            result0 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
            Assert.Same(dict, result0);  // "Same object LCC.GetData-Task.Run-Get"
            Assert.Equal(data1, result0[name1]);  // "LCC.GetData-Main-Final"
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_CrossThread()
        {
            const int NumLoops = 1000;

            string name1 = "Name" + random.Next();
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
