using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;
using UnitTests.Tester;
using Tester;
using Xunit.Abstractions;

namespace UnitTests.General
{
    public class RequestContextTests_Local : IDisposable
    {
        private readonly Dictionary<string, object> headers = new Dictionary<string, object>();

        private static bool oldPropagateActivityId;

        private static readonly SafeRandom random = new SafeRandom();

        public RequestContextTests_Local()
        {
            SerializationManager.InitializeForTesting();
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
            if(msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
            {
                headers.Add(kvp.Key, kvp.Value);
            };
            Assert.IsFalse(headers.ContainsKey(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "ActivityId should not be be present " + headers.ToStrings(separator: ","));
            TestCleanup();

            Trace.CorrelationManager.ActivityId = activityId;
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
            {
                headers.Add(kvp.Key, kvp.Value);
            };
            Assert.IsTrue(headers.ContainsKey(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "ActivityId #1 should be present " + headers.ToStrings(separator: ","));
            object result = headers[RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER];
            Assert.IsNotNull(result, "ActivityId #1 should not be null");
            Assert.AreEqual(activityId, result, "E2E ActivityId #1 not propagated correctly");
            Assert.AreEqual(activityId, Trace.CorrelationManager.ActivityId, "Original E2E ActivityId #1 should not have changed");
            TestCleanup();

            Trace.CorrelationManager.ActivityId = nullActivityId;
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
            {
                headers.Add(kvp.Key, kvp.Value);
            };
            Assert.IsFalse(headers.ContainsKey(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "Null ActivityId should not be present " + headers.ToStrings(separator: ","));
            TestCleanup();

            Trace.CorrelationManager.ActivityId = activityId2;
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            foreach (var kvp in msg.RequestContextData)
            {
                headers.Add(kvp.Key, kvp.Value);
            };
            Assert.IsTrue(headers.ContainsKey(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "ActivityId #2 should be present " + headers.ToStrings(separator: ","));
            result = headers[RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER];
            Assert.IsNotNull(result, "ActivityId #2 should not be null");
            Assert.AreEqual(activityId2, result, "E2E ActivityId #2 not propagated correctly");
            Assert.AreEqual(activityId2, Trace.CorrelationManager.ActivityId, "Original E2E ActivityId #2 should not have changed");
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
            Assert.IsNull(actId, "ActivityId should not be be present " + headers.ToStrings(separator: ","));
            TestCleanup();

            Trace.CorrelationManager.ActivityId = activityId;
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            RequestContext.Clear();
            RequestContext.Import(msg.RequestContextData);
            actId = RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER);
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
            {
                headers.Add(kvp.Key, kvp.Value);
            };
            Assert.IsNotNull(actId, "ActivityId #1 should be present " + headers.ToStrings(separator: ","));
            object result = headers[RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER];
            Assert.IsNotNull(result, "ActivityId #1 should not be null");
            Assert.AreEqual(activityId, result, "E2E ActivityId #1 not propagated correctly");
            Assert.AreEqual(activityId, Trace.CorrelationManager.ActivityId, "Original E2E ActivityId #1 should not have changed");
            TestCleanup();

            Trace.CorrelationManager.ActivityId = nullActivityId;
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            RequestContext.Clear();
            RequestContext.Import(msg.RequestContextData);
            actId = RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER);
            Assert.IsNull(actId, "Null ActivityId should not be present " + headers.ToStrings(separator: ","));
            TestCleanup();

            Trace.CorrelationManager.ActivityId = activityId2;
            msg = new Message();
            msg.RequestContextData = RequestContext.Export();
            RequestContext.Clear();
            RequestContext.Import(msg.RequestContextData);
            actId = RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER);
            if (msg.RequestContextData != null) foreach (var kvp in msg.RequestContextData)
            {
                headers.Add(kvp.Key, kvp.Value);
            };
            Assert.IsNotNull(actId, "ActivityId #2 should be present " + headers.ToStrings(separator: ","));
            result = headers[RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER];
            Assert.IsNotNull(result, "ActivityId #2 should not be null");
            Assert.AreEqual(activityId2, result, "E2E ActivityId #2 not propagated correctly");
            Assert.AreEqual(activityId2, Trace.CorrelationManager.ActivityId, "Original E2E ActivityId #2 should not have changed");
            TestCleanup();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task LCC_Basic()
        {
            string name1 = "Name" + random.Next();
            string data1 = "Main";
            const int NumLoops = 1000;

            CallContext.LogicalSetData(name1, data1);

            Assert.AreEqual(data1, CallContext.LogicalGetData(name1), "LCC.GetData-Main");

            Task t = Task.Run(() =>
            {
                Assert.AreEqual(data1, CallContext.LogicalGetData(name1), "LCC.GetData-Task.Run");
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
                    
                    Assert.AreEqual(str, CallContext.LogicalGetData(name1), "LCC.GetData-Task.Run-"+str);
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

            var dict = new Dictionary<string,string>();
            dict[name1] = data1;
            CallContext.LogicalSetData(name1, dict);

            var result1 = (Dictionary<string,string>) CallContext.LogicalGetData(name1);
            Assert.AreEqual(data1, result1[name1], "LCC.GetData-Main");

            Task t = Task.Run(() =>
            {
                var result2 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                Assert.AreEqual(data1, result2[name1], "LCC.GetData-Task.Run");
                Assert.AreSame(dict, result2, "Same object LCC.GetData-Task.Run");
            });
            await t;

            Task[] promises = new Task[NumLoops];
            for (int i = 0; i < NumLoops; i++)
            {
                string str = i.ToString(CultureInfo.InvariantCulture);
                promises[i] = Task.Run(async () =>
                {
                    var dict2 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.AreEqual(data1, dict2[name1], "LCC.GetData-Task.Run-Get-" + str);
                    Assert.AreSame(dict, dict2, "Same object LCC.GetData-Task.Run-Get" + str);
                    
                    var dict3 = new Dictionary<string, string>();
                    dict3[name1] = str;
                    CallContext.LogicalSetData(name1, dict3);

                    await Task.Delay(10);
                    
                    var result3 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.AreEqual(str, result3[name1], "LCC.GetData-Task.Run-Set-" + str);
                    Assert.AreSame(dict3, result3, "Same object LCC.GetData-Task.Run-Set-" + str);
                    Assert.AreNotSame(dict2, result3, "Differebntobject LCC.GetData-Task.Run-Set-" + str);
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
            Assert.AreEqual(data1, CallContext.LogicalGetData(name1), "LCC.GetData-Main");

            Task[] promises = new Task[NumLoops];
            for (int i = 0; i < NumLoops; i++)
            {
                string str = i.ToString(CultureInfo.InvariantCulture);
                promises[i] = Task.Run(async () =>
                {
                    await Task.Delay(5);
                    Assert.AreEqual(data1, CallContext.LogicalGetData(name1), "LCC.GetData-Main");
                    await Task.Delay(5);
                    CallContext.LogicalSetData(name1, str);
                    Assert.AreEqual(str, CallContext.LogicalGetData(name1), "LCC.GetData-Task.Run-1-" + str);
                    await Task.Delay(5);
                    Assert.AreEqual(str, CallContext.LogicalGetData(name1), "LCC.GetData-Task.Run-1-" + str);
                    await Task.Delay(5);
                    Assert.AreEqual(str, CallContext.LogicalGetData(name1), "LCC.GetData-Task.Run-2-" + str);
                });
            }
            await Task.WhenAll(promises);
            Assert.AreEqual(data1, CallContext.LogicalGetData(name1), "LCC.GetData-Main-Final");
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
            Assert.AreEqual(data1, result0[name1], "LCC.GetData-Main");
            
            Task[] promises = new Task[NumLoops];
            for (int i = 0; i < NumLoops; i++)
            {
                string str = i.ToString(CultureInfo.InvariantCulture);
                promises[i] = Task.Run(async () =>
                {
                    var result1 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.AreSame(dict, result1, "Same object LCC.GetData-Task.Run-Get" + str);
                    Assert.AreEqual(data1, result1[name1], "LCC.GetData-Task.Run-Get-" + str);

                    await Task.Delay(5);

                    var dict2 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.AreSame(dict, dict2, "Same object LCC.GetData-Task.Run-Get" + str);
                    Assert.AreEqual(data1, dict2[name1], "LCC.GetData-Task.Run-Get-" + str);

                    // Set New Dictionary
                    var dict3 = new Dictionary<string, string>();
                    dict3[name1] = str;
                    CallContext.LogicalSetData(name1, dict3);

                    var result3 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.AreSame(dict3, result3, "Same object LCC.GetData-Task.Run-Set-1-" + str);
                    Assert.AreEqual(str, result3[name1], "LCC.GetData-Task.Run-Set-" + str);

                    await Task.Delay(5);

                    result3 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.AreSame(dict3, result3, "Same object LCC.GetData-Task.Run-Set-1-" + str);
                    Assert.AreEqual(str, result3[name1], "LCC.GetData-Task.Run-Set-" + str);
                    
                    await Task.Delay(5);
                    result3 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
                    Assert.AreSame(dict3, result3, "Same object LCC.GetData-Task.Run-Set-2-" + str);
                    Assert.AreEqual(str, result3[name1], "LCC.GetData-Task.Run-Set-" + str);
                });
            }
            await Task.WhenAll(promises);
            result0 = (Dictionary<string, string>)CallContext.LogicalGetData(name1);
            Assert.AreSame(dict, result0, "Same object LCC.GetData-Task.Run-Get");
            Assert.AreEqual(data1, result0[name1], "LCC.GetData-Main-Final");
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_CrossThread()
        {
            const int NumLoops = 1000;

            string name1 = "Name" + random.Next();
            string data1 = "Main";

            RequestContext.Set(name1, data1);
            Assert.AreEqual(data1, RequestContext.Get(name1), "RC.GetData-Main");

            Task[] promises = new Task[NumLoops];
            for (int i = 0; i < NumLoops; i++)
            {
                string str = i.ToString(CultureInfo.InvariantCulture);
                promises[i] = Task.Run(async () =>
                {
                    await Task.Delay(5);
                    Assert.AreEqual(data1, RequestContext.Get(name1), "RC.GetData-Task.Run-0");
                    await Task.Delay(5);
                    // Set New value
                    RequestContext.Set(name1, str);
                    Assert.AreEqual(str, RequestContext.Get(name1), "RC.GetData-Task.Run-1-" + str);
                    await Task.Delay(5);
                    Assert.AreEqual(str, RequestContext.Get(name1), "RC.GetData-Task.Run-2-" + str);
                    await Task.Delay(5);
                    Assert.AreEqual(str, RequestContext.Get(name1), "RC.GetData-Task.Run-3-" + str);
                });
            }
            await Task.WhenAll(promises);
            Assert.AreEqual(data1, RequestContext.Get(name1), "RC.GetData-Main-Final");
        }

    }

    public class RequestContextTests_Silo : OrleansTestingBase, IClassFixture<RequestContextTests_Silo.Fixture>, IDisposable
    {
        private readonly ITestOutputHelper output;

        public class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    StartPrimary = true,
                    StartSecondary = false,
                    PropagateActivityId = true,
                    SiloConfigFile = new FileInfo("OrleansConfigurationForTesting.xml")
                },
                new TestingClientOptions { PropagateActivityId = true });
            }
        }

        public RequestContextTests_Silo(ITestOutputHelper output)
        {
            this.output = output;
            RequestContext.PropagateActivityId = true; // Client-side setting
            Trace.CorrelationManager.ActivityId = Guid.Empty;
            RequestContext.Clear();
        }
        
        public void Dispose()
        {
            Trace.CorrelationManager.ActivityId = Guid.Empty;
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_Simple()
        {
            Guid activityId = Guid.NewGuid();
            IRequestContextTestGrain grain = GrainClient.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());
            Trace.CorrelationManager.ActivityId = activityId;
            Guid result = await grain.E2EActivityId();
            Assert.AreEqual(activityId, result, "E2E ActivityId not propagated correctly");
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_AC_Test1()
        {
            long id = GetRandomGrainId();
            const string key = "TraceId";
            string val = "TraceValue-" + id;
            string val2 = val + "-2";

            var grain = GrainClient.GrainFactory.GetGrain<IRequestContextTestGrain>(id);

            RequestContext.Set(key, val);
            var result = await grain.TraceIdEcho();
            Assert.AreEqual(val, result, "Immediate RequestContext echo was not correct");

            RequestContext.Set(key, val2);
            result = await grain.TraceIdDoubleEcho();
            Assert.AreEqual(val2, result, "Transitive RequestContext echo was not correct");

            RequestContext.Set(key, val);
            result = await grain.TraceIdDelayedEcho1();
            Assert.AreEqual(val, result, "Delayed (StartNew) RequestContext echo was not correct");

            RequestContext.Set(key, val2);
            result = await grain.TraceIdDelayedEcho2();
            Assert.AreEqual(val2, result, "Delayed (ContinueWith) RequestContext echo was not correct");
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_Task_Test1()
        {
            long id = GetRandomGrainId();
            const string key = "TraceId";
            string val = "TraceValue-" + id;
            string val2 = val + "-2";

            var grain = GrainClient.GrainFactory.GetGrain<IRequestContextTaskGrain>(id);

            RequestContext.Set(key, val);
            var result = await grain.TraceIdEcho();
            Assert.AreEqual(val, result, "Immediate RequestContext echo was not correct");

            RequestContext.Set(key, val2);
            result = await grain.TraceIdDoubleEcho();
            Assert.AreEqual(val2, result, "Transitive RequestContext echo was not correct");

            RequestContext.Set(key, val);
            result = await grain.TraceIdDelayedEcho1();
            Assert.AreEqual(val, result, "Delayed (StartNew) RequestContext echo was not correct");

            RequestContext.Set(key, val2);
            result = await grain.TraceIdDelayedEcho2();
            Assert.AreEqual(val2, result, "Delayed (ContinueWith) RequestContext echo was not correct");

            RequestContext.Set(key, val);
            result = await grain.TraceIdDelayedEchoAwait();
            Assert.AreEqual(val, result, "Delayed (Await) RequestContext echo was not correct");

            // Expected behaviour is this won't work, because Task.Run by design does not use Orleans task scheduler
            //RequestContext.Set(key, val2);
            //result = await grain.TraceIdDelayedEchoTaskRun();
            //Assert.AreEqual(val2, result, "Delayed (Task.Run) RequestContext echo was not correct");
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_Task_TestRequestContext()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IRequestContextTaskGrain>(1);
            Tuple<string, string> requestContext = await grain.TestRequestContext();
            logger.Info("Request Context is: " + requestContext);
            Assert.AreEqual("binks", requestContext.Item1, "Item1=" + requestContext.Item1);
            Assert.AreEqual("binks", requestContext.Item2, "Item2=" + requestContext.Item2);
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_RC_Set_E2E()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();
            Guid nullActivityId = Guid.Empty;

            IRequestContextTestGrain grain = GrainClient.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            RequestContext.Set(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER, activityId);
            Guid result = await grain.E2EActivityId();
            Assert.AreEqual(activityId, result, "E2E ActivityId not propagated correctly");
            RequestContext.Clear();

            RequestContext.Set(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER, nullActivityId);
            result = await grain.E2EActivityId();
            Assert.AreEqual(nullActivityId, result, "Null ActivityId propagated E2E incorrectly");
            RequestContext.Clear();

            RequestContext.Set(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER, activityId2);
            result = await grain.E2EActivityId();
            Assert.AreEqual(activityId2, result, "E2E ActivityId 2 not propagated correctly");
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_E2E()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();
            Guid nullActivityId = Guid.Empty;

            IRequestContextTestGrain grain = GrainClient.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = activityId;
            Assert.IsNull(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");
            Guid result = await grain.E2EActivityId();
            Assert.AreEqual(activityId, result, "E2E ActivityId not propagated correctly");
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = nullActivityId;
            Assert.IsNull(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                result = await grain.E2EActivityId();
                Assert.AreEqual(nullActivityId, result, "Null ActivityId propagated E2E incorrectly");
            }
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = activityId2;
            Assert.IsNull(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");
            result = await grain.E2EActivityId();
            Assert.AreEqual(activityId2, result, "E2E ActivityId 2 not propagated correctly");
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_E2E_ViaProxy()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();
            Guid nullActivityId = Guid.Empty;

            IRequestContextProxyGrain grain = GrainClient.GrainFactory.GetGrain<IRequestContextProxyGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = activityId;
            Assert.IsNull(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");
            Guid result = await grain.E2EActivityId();
            Assert.AreEqual(activityId, result, "E2E ActivityId not propagated correctly");
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = nullActivityId;
            Assert.IsNull(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                result = await grain.E2EActivityId();
                Assert.AreEqual(nullActivityId, result, "Null ActivityId propagated E2E incorrectly");
            }
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = activityId2;
            Assert.IsNull(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");
            result = await grain.E2EActivityId();
            Assert.AreEqual(activityId2, result, "E2E ActivityId 2 not propagated correctly");
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_RC_None_E2E()
        {
            Guid nullActivityId = Guid.Empty;

            RequestContext.Clear();

            IRequestContextTestGrain grain = GrainClient.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Guid result = await grain.E2EActivityId();
            Assert.AreEqual(nullActivityId, result, "E2E ActivityId should not exist");

            result = await grain.E2EActivityId();
            Assert.AreEqual(nullActivityId, result, "Null ActivityId propagated E2E incorrectly");
            RequestContext.Clear();

            result = await grain.E2EActivityId();
            Assert.AreEqual(nullActivityId, result, "E2E ActivityId 2 should not exist");
            Assert.AreEqual(null, RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");

            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                Assert.AreEqual(null, RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");

                result = await grain.E2EActivityId();

                Assert.AreEqual(nullActivityId, result, "Null ActivityId propagated E2E incorrectly");

                RequestContext.Clear();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_None_E2E()
        {
            Guid nullActivityId = Guid.Empty;

            IRequestContextTestGrain grain = GrainClient.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Guid result = grain.E2EActivityId().Result;
            Assert.AreEqual(nullActivityId, result, "E2E ActivityId should not exist");

            Trace.CorrelationManager.ActivityId = nullActivityId;
            Assert.IsNull(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");
            result = await grain.E2EActivityId();
            Assert.AreEqual(nullActivityId, result, "Null ActivityId propagated E2E incorrectly");
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = nullActivityId;
            Assert.IsNull(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                result = await grain.E2EActivityId();
                Assert.AreEqual(nullActivityId, result, "Null ActivityId propagated E2E incorrectly");
            }
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = nullActivityId;
            Assert.IsNull(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER), "No ActivityId context should be set");
            result = await grain.E2EActivityId();
            Assert.AreEqual(nullActivityId, result, "Null ActivityId propagated E2E incorrectly");
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_DynamicChange_Client()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();

            IRequestContextTestGrain grain = GrainClient.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = activityId;
            Guid result = await grain.E2EActivityId();
            Assert.AreEqual(activityId, result, "E2E ActivityId #1 not propagated correctly");
            RequestContext.Clear();

            RequestContext.PropagateActivityId = false;
            output.WriteLine("Set RequestContext.PropagateActivityId={0}", RequestContext.PropagateActivityId);

            Trace.CorrelationManager.ActivityId = activityId2;
            result = await grain.E2EActivityId();
            Assert.AreEqual(Guid.Empty, result, "E2E ActivityId #2 not not have been propagated");
            RequestContext.Clear();

            RequestContext.PropagateActivityId = true;
            output.WriteLine("Set RequestContext.PropagateActivityId={0}", RequestContext.PropagateActivityId);

            Trace.CorrelationManager.ActivityId = activityId2;
            result = await grain.E2EActivityId();
            Assert.AreEqual(activityId2, result, "E2E ActivityId #2 should have been propagated");
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = activityId;
            result = await grain.E2EActivityId();
            Assert.AreEqual(activityId, result, "E2E ActivityId #1 not propagated correctly after #2");
            RequestContext.Clear();
        }

        [Fact(Skip = "Silo setting update of PropagateActivityId is not correctly implemented")]
        [TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_DynamicChange_Server()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();

            const string PropagateActivityIdConfigKey = @"/OrleansConfiguration/Defaults/Tracing/@PropagateActivityId";
            var changeConfig = new Dictionary<string, string>();

            IManagementGrain mgmtGrain = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);

            IRequestContextTestGrain grain = GrainClient.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = activityId;
            Guid result = await grain.E2EActivityId();
            Assert.AreEqual(activityId, result, "E2E ActivityId #1 not propagated correctly");
            RequestContext.Clear();

            changeConfig[PropagateActivityIdConfigKey] = Boolean.FalseString;
            output.WriteLine("Set {0}={1}", PropagateActivityIdConfigKey, changeConfig[PropagateActivityIdConfigKey]);
            await mgmtGrain.UpdateConfiguration(null, changeConfig, null);

            Trace.CorrelationManager.ActivityId = activityId2;
            result = await grain.E2EActivityId();
            Assert.AreNotEqual(activityId2, result, "E2E ActivityId #2 should not have been propagated");
            Assert.AreEqual(Guid.Empty, result, "E2E ActivityId #2 should not have been propagated");
            RequestContext.Clear();

            changeConfig[PropagateActivityIdConfigKey] = Boolean.TrueString;
            output.WriteLine("Set {0}={1}", PropagateActivityIdConfigKey, changeConfig[PropagateActivityIdConfigKey]);
            await mgmtGrain.UpdateConfiguration(null, changeConfig, null);

            Trace.CorrelationManager.ActivityId = activityId2;
            result = await grain.E2EActivityId();
            Assert.AreEqual(activityId2, result, "E2E ActivityId #2 should have been propagated");
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = activityId;
            result = await grain.E2EActivityId();
            Assert.AreEqual(activityId, result, "E2E ActivityId #1 not propagated correctly after #2");
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task ClientInvokeCallback_CountCallbacks()
        {
            TestClientInvokeCallback callback = new TestClientInvokeCallback(output, Guid.Empty);
            GrainClient.ClientInvokeCallback = callback.OnInvoke;
            IRequestContextProxyGrain grain = GrainClient.GrainFactory.GetGrain<IRequestContextProxyGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = Guid.Empty;
            Guid activityId = await grain.E2EActivityId();
            Assert.AreEqual(Guid.Empty, activityId, "E2EActivityId Call#1");
            Assert.AreEqual(1, callback.TotalCalls, "Number of callbacks");

            GrainClient.ClientInvokeCallback = null;
            activityId = await grain.E2EActivityId();
            Assert.AreEqual(Guid.Empty, activityId, "E2EActivityId Call#2");
            Assert.AreEqual(1, callback.TotalCalls, "Number of callbacks - should be unchanged");
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task ClientInvokeCallback_SetActivityId()
        {
            Guid setActivityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();

            Trace.CorrelationManager.ActivityId = activityId2; // Set up initial value that will be overridden by the callback function

            TestClientInvokeCallback callback = new TestClientInvokeCallback(output, setActivityId);
            GrainClient.ClientInvokeCallback = callback.OnInvoke;
            IRequestContextProxyGrain grain = GrainClient.GrainFactory.GetGrain<IRequestContextProxyGrain>(GetRandomGrainId());

            Guid activityId = await grain.E2EActivityId();
            Assert.AreEqual(setActivityId, activityId, "E2EActivityId Call#1");
            Assert.AreEqual(1, callback.TotalCalls, "Number of callbacks");

            Trace.CorrelationManager.ActivityId = Guid.Empty;
            RequestContext.Clear(); // Need this to clear out any old ActivityId value cached in RequestContext. Code optimization in RequestContext does not unset entry if Trace.CorrelationManager.ActivityId == Guid.Empty [which is the "normal" case]
            GrainClient.ClientInvokeCallback = null;

            activityId = await grain.E2EActivityId();
            Assert.AreEqual(Guid.Empty, activityId, "E2EActivityId Call#2 == Zero");
            Assert.AreEqual(1, callback.TotalCalls, "Number of callbacks - should be unchanged");
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task ClientInvokeCallback_GrainObserver()
        {
            TestClientInvokeCallback callback = new TestClientInvokeCallback(output, Guid.Empty);
            GrainClient.ClientInvokeCallback = callback.OnInvoke;
            RequestContextGrainObserver observer = new RequestContextGrainObserver(output, null, null);
            // CreateObjectReference will result in system target call to IClientObserverRegistrar.
            // We want to make sure this does not invoke ClientInvokeCallback.
            ISimpleGrainObserver reference = await GrainClient.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer);

            GC.KeepAlive(observer);
            Assert.AreEqual(0, callback.TotalCalls, "Number of callbacks");
        }
    }

    internal class RequestContextGrainObserver : ISimpleGrainObserver
    {
        private readonly ITestOutputHelper output;
        readonly Action<int, int, object> action;
        readonly object result;

        public RequestContextGrainObserver(ITestOutputHelper output, Action<int, int, object> action, object result)
        {
            this.output = output;
            this.action = action;
            this.result = result;
        }

        public void StateChanged(int a, int b)
        {
            output.WriteLine("RequestContextGrainObserver.StateChanged a={0} b={1}", a, b);
            if (action != null)
            {
                action(a, b, result);
            }
        }
    }
    
    public class Halo_RequestContextTests : OrleansTestingBase
    {
        private readonly ITestOutputHelper output;

        public Halo_RequestContextTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task Halo_RequestContextShouldBeMaintainedWhenThreadHoppingOccurs()
        {
            int numTasks = 20;
            Task[] tasks = new Task[numTasks];

            for (int i = 0; i < numTasks; i++)
            {
                var closureInt = i;
                Func<Task> func = async () => { await ContextTester(closureInt); };
                tasks[i] = Task.Run(func);
            }

            await Task.WhenAll(tasks);
        }

        private async Task ContextTester(int i)
        {
            RequestContext.Set("threadId", i);
            int contextId = (int)(RequestContext.Get("threadId") ?? -1);
            output.WriteLine("ExplicitId={0}, ContextId={2}, ManagedThreadId={1}", i, Thread.CurrentThread.ManagedThreadId, contextId);
            await FrameworkContextVerification(i).ConfigureAwait(false);
        }

        private async Task FrameworkContextVerification(int id)
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(10);
                int contextId = (int)(RequestContext.Get("threadId") ?? -1);
                output.WriteLine("Inner, in loop {0}, Explicit Id={2}, ContextId={3}, ManagedThreadId={1}", i, Thread.CurrentThread.ManagedThreadId, id, contextId);
                Assert.AreEqual(id, contextId);
            }
        }
    }
    
    public class Halo_CallContextTests : OrleansTestingBase
    {
        private readonly ITestOutputHelper output;

        public Halo_CallContextTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task Halo_LogicalCallContextShouldBeMaintainedWhenThreadHoppingOccurs()
        {
            int numTasks = 20;
            Task[] tasks = new Task[numTasks];

            for (int i = 0; i < numTasks; i++)
            {
                var closureInt = i;
                Func<Task> func = async () => { await ContextTester(closureInt); };
                tasks[i] = Task.Run(func);
            }

            await Task.WhenAll(tasks);
        }

        private async Task ContextTester(int i)
        {
            CallContext.LogicalSetData("threadId", i);
            int contextId = (int)(CallContext.LogicalGetData("threadId") ?? -1);
            output.WriteLine("ExplicitId={0}, ContextId={2}, ManagedThreadId={1}", i, Thread.CurrentThread.ManagedThreadId, contextId);
            await FrameworkContextVerification(i).ConfigureAwait(false);
        }

        private async Task FrameworkContextVerification(int id)
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(10);
                int contextId = (int)(CallContext.LogicalGetData("threadId") ?? -1);
                output.WriteLine("Inner, in loop {0}, Explicit Id={2}, ContextId={3}, ManagedThreadId={1}", i, Thread.CurrentThread.ManagedThreadId, id, contextId);
                Assert.AreEqual(id, contextId);
            }
        }
    }

    public  class TestClientInvokeCallback
    {
        public int TotalCalls;

        private readonly ITestOutputHelper output;
        private readonly Guid setActivityId;

        public TestClientInvokeCallback(ITestOutputHelper output, Guid setActivityId)
        {
            this.output = output;
            this.setActivityId = setActivityId;
        }

        public void OnInvoke(InvokeMethodRequest request, IGrain grain)
        {
            // (NOT YET AVAILABLE) Interface name is available from: <c>grainReference.InterfaceName</c>
            // (NOT YET AVAILABLE) Method name is available from: <c>grainReference.GetMethodName(request.InterfaceId, request.MethodId)</c>
            // GrainId is available from: <c>grainReference.GrainId</c>
            // PrimaryKey is availabe from: <c>grainReference.GrainId.GetPrimaryKeyLong()</c> or <c>grainReference.GrainId.GetPrimaryKey()</c> depending on key type.
            // Call arguments are available from: <c>request.Arguments</c> array

            TotalCalls++;

            output.WriteLine("OnInvoke TotalCalls={0}", TotalCalls);

            try
            {
                output.WriteLine("OnInvoke called for Grain={0} PrimaryKey={1} GrainId={2} with {3} arguments",
                    grain.GetType().FullName,
                    ((GrainReference) grain).GrainId.GetPrimaryKeyLong(),
                    ((GrainReference) grain).GrainId,
                    request.Arguments != null ? request.Arguments.Length : 0);
            }
            catch (Exception exc)
            {
                output.WriteLine("**** Error OnInvoke for Grain={0} GrainId={1} with {2} arguments. Exception = {3}",
                    grain.GetType().FullName,
                    ((GrainReference)grain).GrainId,
                    request.Arguments != null ? request.Arguments.Length : 0,
                    exc);
            }

            if (setActivityId != Guid.Empty)
            {
                Trace.CorrelationManager.ActivityId = setActivityId;
                output.WriteLine("OnInvoke Set ActivityId={0}", setActivityId);
            }
            output.WriteLine("OnInvoke Current ActivityId={0}", Trace.CorrelationManager.ActivityId);
        }
    }
}
