using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Hosting;
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
        private OutsideRuntimeClient runtimeClient;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
                builder.AddClientBuilderConfigurator<ClientConfigurator>();
            }
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Configure<SiloMessagingOptions>(options => options.PropagateActivityId = true);
            }
        }

        public class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<SiloMessagingOptions>(options => options.PropagateActivityId = true);
            }
        }

        public RequestContextTests_Silo(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.runtimeClient = this.fixture.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
            RequestContext.PropagateActivityId = true; // Client-side setting

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
            Guid activityId = Guid.NewGuid();
            IRequestContextTestGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            RequestContextTestUtils.SetActivityId(activityId);
            Guid result = await grain.E2EActivityId();
            Assert.Equal(activityId,  result);  // "E2E ActivityId not propagated correctly"
        }

        [Fact, TestCategory("BVT"), TestCategory("RequestContext")]
        public async Task RequestContext_LegacyActivityId_Simple()
        {
            Guid activityId = Guid.NewGuid();
            IRequestContextTestGrain grain = this.fixture.GrainFactory.GetGrain<IRequestContextTestGrain>(GetRandomGrainId());

            Trace.CorrelationManager.ActivityId = activityId;
            Assert.True(RequestContext.PropagateActivityId); // "Verify activityId propagation is enabled."
            Guid result = await grain.E2ELegacyActivityId();
            Assert.Equal(activityId, result);  // "E2E ActivityId not propagated correctly"
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
            this.fixture.Logger.LogInformation("Request Context is: {RequestContext}", requestContext);
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
            Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));  // "No ActivityId context should be set"

            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));  // "No ActivityId context should be set"

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

            RequestContextTestUtils.SetActivityId(nullActivityId);
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            RequestContext.Clear();

            RequestContextTestUtils.SetActivityId(nullActivityId);
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                result = await grain.E2EActivityId();
                Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            }
            RequestContext.Clear();
            RequestContextTestUtils.SetActivityId(nullActivityId);
           Assert.Null(RequestContext.Get(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER));
            result = await grain.E2EActivityId();
            Assert.Equal(nullActivityId,  result);  // "Null ActivityId propagated E2E incorrectly"
            RequestContext.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("RequestContext")]
        public async Task RequestContext_ActivityId_CM_DynamicChange_Client()
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
            this.action?.Invoke(a, b, this.result);
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

        private AsyncLocal<int> threadId = new AsyncLocal<int>();

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
            threadId.Value = i;
            int contextId = threadId.Value;
            output.WriteLine("ExplicitId={0}, ContextId={2}, ManagedThreadId={1}", i, Thread.CurrentThread.ManagedThreadId, contextId);
            await FrameworkContextVerification(i).ConfigureAwait(false);
        }

        private async Task FrameworkContextVerification(int id)
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(10);
                int contextId = threadId.Value;
                output.WriteLine("Inner, in loop {0}, Explicit Id={2}, ContextId={3}, ManagedThreadId={1}", i, Thread.CurrentThread.ManagedThreadId, id, contextId);
                Assert.Equal(id, contextId);
            }
        }
    }
}
