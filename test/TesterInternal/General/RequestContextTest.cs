using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
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
        private readonly OutsideRuntimeClient runtimeClient;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder) => builder.Options.InitialSilosCount = 1;
        }

        public RequestContextTests_Silo(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            runtimeClient = this.fixture.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();

            RequestContextTestUtils.ClearActivityId();
            RequestContext.Clear();
        }
        
        public void Dispose()
        {
            RequestContextTestUtils.ClearActivityId();
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_Simple()
        {
            var activityId = Guid.NewGuid();
            var grain = fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            RequestContextTestUtils.SetActivityId(activityId);
            var result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId not propagated correctly"
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_AC_Test1()
        {
            var id = GetRandomGrainId();
            const string key = "TraceId";
            var val = "TraceValue-" + id;
            var val2 = val + "-2";

            var grain = fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(id);

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
            var id = GetRandomGrainId();
            const string key = "TraceId";
            var val = "TraceValue-" + id;
            var val2 = val + "-2";

            var grain = fixture.GrainFactory.GetGrain<IRequestContextTaskGrain>(id);

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
            var grain = fixture.GrainFactory.GetGrain<IRequestContextTaskGrain>(1);
            var requestContext = await grain.TestRequestContext();
            fixture.Logger.LogInformation("Request Context is: {RequestContext}", requestContext);
            Assert.Equal("binks",  requestContext.Item1);  // "Item1=" + requestContext.Item1
            Assert.Equal("binks",  requestContext.Item2);  // "Item2=" + requestContext.Item2
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_RC_Set_E2E()
        {
            var activityId = Guid.NewGuid();
            var activityId2 = Guid.NewGuid();
            var nullActivityId = Guid.Empty;

            var grain = fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            RequestContext.Set(RequestContext.CALL_CHAIN_REENTRANCY_HEADER, activityId);
            var result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId not propagated correctly"
            RequestContext.Clear();

            RequestContext.Set(RequestContext.CALL_CHAIN_REENTRANCY_HEADER, nullActivityId);
            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            RequestContext.Clear();

            RequestContext.Set(RequestContext.CALL_CHAIN_REENTRANCY_HEADER, activityId2);
            result = await grain.E2EActivityId();
            Assert.Equal(activityId2,  result);  // "E2E ActivityId 2 not propagated correctly"
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_E2E()
        {
            var activityId = Guid.NewGuid();
            var activityId2 = Guid.NewGuid();
            var nullActivityId = Guid.Empty;

            var grain = fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));
            RequestContext.ReentrancyId = activityId;
            var result = await grain.E2EActivityId();
            Assert.Equal(activityId, result);  // "E2E ActivityId not propagated correctly"
            RequestContext.Clear();

            RequestContext.ReentrancyId = nullActivityId;
            Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));
            for (var i = 0; i < Environment.ProcessorCount; i++)
            {
                result = await grain.E2EActivityId();
                Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            }
            RequestContext.Clear();

            Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));
            RequestContext.ReentrancyId = activityId2;
            result = await grain.E2EActivityId();
            Assert.Equal(activityId2,  result);  // "E2E ActivityId 2 not propagated correctly"
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_E2E_ViaProxy()
        {
            var activityId = Guid.NewGuid();
            var activityId2 = Guid.NewGuid();
            var nullActivityId = Guid.Empty;

            var grain = fixture.GrainFactory.GetGrain<IRequestContextProxyGrain>(GetRandomGrainId());

            Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));
            RequestContext.ReentrancyId = activityId;
            var result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId not propagated correctly"
            RequestContext.Clear();

            RequestContext.ReentrancyId = nullActivityId;
            Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));
            for (var i = 0; i < Environment.ProcessorCount; i++)
            {
                result = await grain.E2EActivityId();
                Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            }
            RequestContext.Clear();

            Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));
            RequestContext.ReentrancyId = activityId2;
            result = await grain.E2EActivityId();
            Assert.Equal(activityId2,  result);  // "E2E ActivityId 2 not propagated correctly"
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_RC_None_E2E()
        {
            var nullActivityId = Guid.Empty;

            RequestContext.Clear();

            var grain = fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            var result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "E2E ActivityId should not exist"

            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            RequestContext.Clear();

            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "E2E ActivityId 2 should not exist"
            Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));  // "No ActivityId context should be set"

            for (var i = 0; i < Environment.ProcessorCount; i++)
            {
                Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));  // "No ActivityId context should be set"

                result = await grain.E2EActivityId();

                Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"

                RequestContext.Clear();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_None_E2E()
        {
            var nullActivityId = Guid.Empty;

            var grain = fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            var result = grain.E2EActivityId().Result;
            Assert.Equal(nullActivityId,  result);  // "E2E ActivityId should not exist"

            RequestContextTestUtils.SetActivityId(nullActivityId);
           Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));
            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            RequestContext.Clear();

            RequestContextTestUtils.SetActivityId(nullActivityId);
           Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));
            for (var i = 0; i < Environment.ProcessorCount; i++)
            {
                result = await grain.E2EActivityId();
                Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            }
            RequestContext.Clear();
            RequestContextTestUtils.SetActivityId(nullActivityId);
           Assert.Null(RequestContext.Get(RequestContext.CALL_CHAIN_REENTRANCY_HEADER));
            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_DynamicChange_Client()
        {
            var activityId = Guid.NewGuid();
            var activityId2 = Guid.NewGuid();

            var grain = fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            RequestContext.ReentrancyId = activityId;
            var result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId #1 not propagated correctly"
            RequestContext.Clear();

            using (RequestContext.SuppressCallChainReentrancy())
            {
                result = await grain.E2EActivityId();
                Assert.Equal(Guid.Empty, result);  // "E2E ActivityId #2 not not have been propagated"
                RequestContext.Clear();
            }

            RequestContext.ReentrancyId = activityId2;
            result = await grain.E2EActivityId();
            Assert.Equal(activityId2,  result);  // "E2E ActivityId #2 should have been propagated"
            RequestContext.Clear();

            RequestContext.ReentrancyId = activityId;
            result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId #1 not propagated correctly after #2"
            RequestContext.Clear();
        }
    }

    internal class RequestContextGrainObserver : ISimpleGrainObserver
    {
        private readonly ITestOutputHelper output;
        private readonly Action<int, int, object> action;
        private readonly object result;

        public RequestContextGrainObserver(ITestOutputHelper output, Action<int, int, object> action, object result)
        {
            this.output = output;
            this.action = action;
            this.result = result;
        }

        public void StateChanged(int a, int b)
        {
            output.WriteLine("RequestContextGrainObserver.StateChanged a={0} b={1}", a, b);
            action?.Invoke(a, b, result);
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
            var numTasks = 20;
            var tasks = new Task[numTasks];

            for (var i = 0; i < numTasks; i++)
            {
                var closureInt = i;
                async Task func() { await ContextTester(closureInt); }
                tasks[i] = Task.Run(func);
            }

            await Task.WhenAll(tasks);
        }

        private async Task ContextTester(int i)
        {
            RequestContext.Set("threadId", i);
            var contextId = (int)(RequestContext.Get("threadId") ?? -1);
            output.WriteLine("ExplicitId={0}, ContextId={2}, ManagedThreadId={1}", i, Thread.CurrentThread.ManagedThreadId, contextId);
            await FrameworkContextVerification(i).ConfigureAwait(false);
        }

        private async Task FrameworkContextVerification(int id)
        {
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(10);
                var contextId = (int)(RequestContext.Get("threadId") ?? -1);
                output.WriteLine("Inner, in loop {0}, Explicit Id={2}, ContextId={3}, ManagedThreadId={1}", i, Thread.CurrentThread.ManagedThreadId, id, contextId);
                Assert.Equal(id, contextId);
            }
        }
    }

    public class Halo_CallContextTests : OrleansTestingBase
    {
        private readonly ITestOutputHelper output;

        private readonly AsyncLocal<int> threadId = new AsyncLocal<int>();

        public Halo_CallContextTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task Halo_LogicalCallContextShouldBeMaintainedWhenThreadHoppingOccurs()
        {
            var numTasks = 20;
            var tasks = new Task[numTasks];

            for (var i = 0; i < numTasks; i++)
            {
                var closureInt = i;
                async Task func() { await ContextTester(closureInt); }
                tasks[i] = Task.Run(func);
            }

            await Task.WhenAll(tasks);
        }

        private async Task ContextTester(int i)
        {
            threadId.Value = i;
            var contextId = threadId.Value;
            output.WriteLine("ExplicitId={0}, ContextId={2}, ManagedThreadId={1}", i, Thread.CurrentThread.ManagedThreadId, contextId);
            await FrameworkContextVerification(i).ConfigureAwait(false);
        }

        private async Task FrameworkContextVerification(int id)
        {
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(10);
                var contextId = threadId.Value;
                output.WriteLine("Inner, in loop {0}, Explicit Id={2}, ContextId={3}, ManagedThreadId={1}", i, Thread.CurrentThread.ManagedThreadId, id, contextId);
                Assert.Equal(id, contextId);
            }
        }
    }
}
