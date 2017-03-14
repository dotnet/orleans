using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{ 
    public class RequestContextTests_Silo : OrleansTestingBase, IClassFixture<RequestContextTests_Silo.Fixture>, IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(initialSilosCount: 1);

                options.ClusterConfiguration.ApplyToAllNodes(n => n.PropagateActivityId = true);
                options.ClientConfiguration.PropagateActivityId = true;

                return new TestCluster(options);
            }
        }

        public RequestContextTests_Silo(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            RequestContext.PropagateActivityId = true; // Client-side setting
            Trace.CorrelationManager.ActivityId = Guid.Empty;
            RequestContext.Clear();
        }
        
        public void Dispose()
        {
            Trace.CorrelationManager.ActivityId = Guid.Empty;
            GrainClient.ClientInvokeCallback = null;
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_Simple()
        {
            Guid activityId = Guid.NewGuid();
            IRequestContextTestGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());
            Trace.CorrelationManager.ActivityId = activityId;
            Guid result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId not propagated correctly"
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_AC_Test1()
        {
            long id = GetRandomGrainId();
            const string key = "TraceId";
            string val = "TraceValue-" + id;
            string val2 = val + "-2";

            var grain = this.fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(id);

            RequestContext.Set(key, val);
            var result = await grain.TraceIdEcho();
            Assert.Equal(val,  result);  // "Immediate RequestContext echo was not correct"

            RequestContext.Set(key, val2);
            result = await grain.TraceIdDoubleEcho();
            Assert.Equal(val2,  result);  // "Transitive RequestContext echo was not correct"

            RequestContext.Set(key, val);
            result = await grain.TraceIdDelayedEcho1();
            Assert.Equal(val,  result); // "Delayed (StartNew) RequestContext echo was not correct");

            RequestContext.Set(key, val2);
            result = await grain.TraceIdDelayedEcho2();
            Assert.Equal(val2,  result); // "Delayed (ContinueWith) RequestContext echo was not correct");
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_Task_Test1()
        {
            long id = GetRandomGrainId();
            const string key = "TraceId";
            string val = "TraceValue-" + id;
            string val2 = val + "-2";

            var grain = this.fixture.GrainFactory.GetGrain<IRequestContextTaskGrain>(id);

            RequestContext.Set(key, val);
            var result = await grain.TraceIdEcho();
            Assert.Equal(val,  result);  // "Immediate RequestContext echo was not correct"

            RequestContext.Set(key, val2);
            result = await grain.TraceIdDoubleEcho();
            Assert.Equal(val2,  result);  // "Transitive RequestContext echo was not correct"

            RequestContext.Set(key, val);
            result = await grain.TraceIdDelayedEcho1();
            Assert.Equal(val,  result); // "Delayed (StartNew) RequestContext echo was not correct");

            RequestContext.Set(key, val2);
            result = await grain.TraceIdDelayedEcho2();
            Assert.Equal(val2,  result); // "Delayed (ContinueWith) RequestContext echo was not correct");

            RequestContext.Set(key, val);
            result = await grain.TraceIdDelayedEchoAwait();
            Assert.Equal(val,  result); // "Delayed (Await) RequestContext echo was not correct");

            // Expected behaviour is this won't work, because Task.Run by design does not use Orleans task scheduler
            //RequestContext.Set(key, val2);
            //result = await grain.TraceIdDelayedEchoTaskRun();
            //Assert.Equal(val2,  result); // "Delayed (Task.Run) RequestContext echo was not correct");
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_Task_TestRequestContext()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IRequestContextTaskGrain>(1);
            Tuple<string, string> requestContext = await grain.TestRequestContext();
            logger.Info("Request Context is: " + requestContext);
            Assert.Equal("binks",  requestContext.Item1);  // "Item1=" + requestContext.Item1
            Assert.Equal("binks",  requestContext.Item2);  // "Item2=" + requestContext.Item2
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_RC_Set_E2E()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();
            Guid nullActivityId = Guid.Empty;

            IRequestContextTestGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            RequestContext.Set(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER, activityId);
            Guid result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId not propagated correctly"
            RequestContext.Clear();

            RequestContext.Set(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER, nullActivityId);
            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            RequestContext.Clear();

            RequestContext.Set(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER, activityId2);
            result = await grain.E2EActivityId();
            Assert.Equal(activityId2,  result);  // "E2E ActivityId 2 not propagated correctly"
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_E2E()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();
            Guid nullActivityId = Guid.Empty;

            IRequestContextTestGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = activityId;
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            Guid result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId not propagated correctly"
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = nullActivityId;
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                result = await grain.E2EActivityId();
                Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            }
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = activityId2;
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            result = await grain.E2EActivityId();
            Assert.Equal(activityId2,  result);  // "E2E ActivityId 2 not propagated correctly"
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_E2E_ViaProxy()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();
            Guid nullActivityId = Guid.Empty;

            IRequestContextProxyGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextProxyGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = activityId;
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            Guid result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId not propagated correctly"
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = nullActivityId;
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                result = await grain.E2EActivityId();
                Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            }
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = activityId2;
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            result = await grain.E2EActivityId();
            Assert.Equal(activityId2,  result);  // "E2E ActivityId 2 not propagated correctly"
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_RC_None_E2E()
        {
            Guid nullActivityId = Guid.Empty;

            RequestContext.Clear();

            IRequestContextTestGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Guid result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "E2E ActivityId should not exist"

            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            RequestContext.Clear();

            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "E2E ActivityId 2 should not exist"
            Assert.Equal(null,  RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));  // "No ActivityId context should be set"

            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                Assert.Equal(null,  RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));  // "No ActivityId context should be set"

                result = await grain.E2EActivityId();

                Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"

                RequestContext.Clear();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_None_E2E()
        {
            Guid nullActivityId = Guid.Empty;

            IRequestContextTestGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Guid result = grain.E2EActivityId().Result;
            Assert.Equal(nullActivityId,  result);  // "E2E ActivityId should not exist"

            Trace.CorrelationManager.ActivityId = nullActivityId;
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = nullActivityId;
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                result = await grain.E2EActivityId();
                Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            }
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = nullActivityId;
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_DynamicChange_Client()
        {
            Guid activityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();

            IRequestContextTestGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = activityId;
            Guid result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId #1 not propagated correctly"
            RequestContext.Clear();

            RequestContext.PropagateActivityId = false;
            output.WriteLine("Set RequestContext.PropagateActivityId={0}", RequestContext.PropagateActivityId);

            Trace.CorrelationManager.ActivityId = activityId2;
            result = await grain.E2EActivityId();
            Assert.Equal(Guid.Empty,  result);  // "E2E ActivityId #2 not not have been propagated"
            RequestContext.Clear();

            RequestContext.PropagateActivityId = true;
            output.WriteLine("Set RequestContext.PropagateActivityId={0}", RequestContext.PropagateActivityId);

            Trace.CorrelationManager.ActivityId = activityId2;
            result = await grain.E2EActivityId();
            Assert.Equal(activityId2,  result);  // "E2E ActivityId #2 should have been propagated"
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = activityId;
            result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId #1 not propagated correctly after #2"
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

            IManagementGrain mgmtGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);

            IRequestContextTestGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = activityId;
            Guid result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId #1 not propagated correctly"
            RequestContext.Clear();

            changeConfig[PropagateActivityIdConfigKey] = Boolean.FalseString;
            output.WriteLine("Set {0}={1}", PropagateActivityIdConfigKey, changeConfig[PropagateActivityIdConfigKey]);
            await mgmtGrain.UpdateConfiguration(null, changeConfig, null);

            Trace.CorrelationManager.ActivityId = activityId2;
            result = await grain.E2EActivityId();
            Assert.NotEqual(activityId2, result);  // "E2E ActivityId #2 should not have been propagated"
            Assert.Equal(Guid.Empty,  result);  // "E2E ActivityId #2 should not have been propagated"
            RequestContext.Clear();

            changeConfig[PropagateActivityIdConfigKey] = Boolean.TrueString;
            output.WriteLine("Set {0}={1}", PropagateActivityIdConfigKey, changeConfig[PropagateActivityIdConfigKey]);
            await mgmtGrain.UpdateConfiguration(null, changeConfig, null);

            Trace.CorrelationManager.ActivityId = activityId2;
            result = await grain.E2EActivityId();
            Assert.Equal(activityId2,  result);  // "E2E ActivityId #2 should have been propagated"
            RequestContext.Clear();

            Trace.CorrelationManager.ActivityId = activityId;
            result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId #1 not propagated correctly after #2"
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task ClientInvokeCallback_CountCallbacks()
        {
            TestClientInvokeCallback callback = new TestClientInvokeCallback(output, Guid.Empty);
            GrainClient.ClientInvokeCallback = callback.OnInvoke;
            IRequestContextProxyGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextProxyGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = Guid.Empty;
            Guid activityId = await grain.E2EActivityId();
            Assert.Equal(Guid.Empty,  activityId);  // "E2EActivityId Call#1"
            Assert.Equal(1,  callback.TotalCalls);  // "Number of callbacks"

            GrainClient.ClientInvokeCallback = null;
            activityId = await grain.E2EActivityId();
            Assert.Equal(Guid.Empty,  activityId);  // "E2EActivityId Call#2"
            Assert.Equal(1,  callback.TotalCalls);  // "Number of callbacks - should be unchanged"
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task ClientInvokeCallback_SetActivityId()
        {
            Guid setActivityId = Guid.NewGuid();
            Guid activityId2 = Guid.NewGuid();

            Trace.CorrelationManager.ActivityId = activityId2; // Set up initial value that will be overridden by the callback function

            TestClientInvokeCallback callback = new TestClientInvokeCallback(output, setActivityId);
            GrainClient.ClientInvokeCallback = callback.OnInvoke;
            IRequestContextProxyGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextProxyGrain>(GetRandomGrainId());

            Guid activityId = await grain.E2EActivityId();
            Assert.Equal(setActivityId,  activityId);  // "E2EActivityId Call#1"
            Assert.Equal(1,  callback.TotalCalls);  // "Number of callbacks"

            Trace.CorrelationManager.ActivityId = Guid.Empty;
            RequestContext.Clear(); // Need this to clear out any old ActivityId value cached in RequestContext. Code optimization in RequestContext does not unset entry if Trace.CorrelationManager.ActivityId == Guid.Empty [which is the "normal" case]
            GrainClient.ClientInvokeCallback = null;

            activityId = await grain.E2EActivityId();
            Assert.Equal(Guid.Empty,  activityId);  // "E2EActivityId Call#2 == Zero"
            Assert.Equal(1,  callback.TotalCalls);  // "Number of callbacks - should be unchanged"
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task ClientInvokeCallback_GrainObserver()
        {
            TestClientInvokeCallback callback = new TestClientInvokeCallback(output, Guid.Empty);
            GrainClient.ClientInvokeCallback = callback.OnInvoke;
            RequestContextGrainObserver observer = new RequestContextGrainObserver(output, null, null);
            // CreateObjectReference will result in system target call to IClientObserverRegistrar.
            // We want to make sure this does not invoke ClientInvokeCallback.
            ISimpleGrainObserver reference = await this.fixture.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer);

            GC.KeepAlive(observer);
            Assert.Equal(0,  callback.TotalCalls);  // "Number of callbacks"
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
                Assert.Equal(id, contextId);
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
                Assert.Equal(id, contextId);
            }
        }
    }

    public class TestClientInvokeCallback
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
                    ((GrainReference)grain).GrainId.GetPrimaryKeyLong(),
                    ((GrainReference)grain).GrainId,
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
