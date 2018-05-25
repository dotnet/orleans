using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.MessageCenterTests
{
    public class AsynchAgentRestartTest
    {
        private readonly ITestOutputHelper output;
        private readonly ServiceProvider serviceProvider;

        private class TestAgent : DedicatedAsynchAgent
        {
            private readonly ITestOutputHelper output;

            public TestAgent(ITestOutputHelper output, IServiceProvider serviceProvider)
                : base(
                    ActivatorUtilities.CreateInstance<ExecutorService>(serviceProvider),
                    NullLoggerFactory.Instance)
            {
                this.output = output;
            }

            protected override void Run()
            {
                output.WriteLine("Agent running in thread " + Thread.CurrentThread.Name);
                Cts.Token.WaitHandle.WaitOne();
                output.WriteLine("Agent stopping in thread " + Thread.CurrentThread.Name);
            }
        }

        public AsynchAgentRestartTest(ITestOutputHelper output)
        {
            this.output = output;

            var services = new ServiceCollection();
            services.AddSingleton<SchedulerStatisticsGroup>();
            services.AddSingleton<StageAnalysisStatisticsGroup>();
            services.AddLogging();
            services.Configure<StatisticsOptions>(options => options.CollectionLevel = StatisticsLevel.Info);
            this.serviceProvider = services.BuildServiceProvider();
        }

        [Fact, TestCategory("Functional"), TestCategory("Messaging")]
        public void AgentRestart()
        {
            TestAgent t = new TestAgent(output, this.serviceProvider);

            t.Start();
            Assert.Equal(ThreadState.Running, t.State); // "Agent state is wrong after initial start"
            Thread.Sleep(100);

            t.Stop();
            Assert.Equal(ThreadState.Stopped, t.State); // "Agent state is wrong after initial stop"
            Thread.Sleep(100);

            try
            {
                t.Start();
            }
            catch (Exception ex)
            {
                Assert.True(false, "Exception while restarting agent: " + ex);
                throw;
            }
            Assert.Equal(ThreadState.Running, t.State); // "Agent state is wrong after restart"
            Thread.Sleep(100);

            t.Stop();
            Assert.Equal(ThreadState.Stopped, t.State); // "Agent state is wrong after final stop"
            Thread.Sleep(100);
        }

        [Fact, TestCategory("Functional"), TestCategory("Messaging")]
        public void AgentStartWhileStarted()
        {
            TestAgent t = new TestAgent(output, this.serviceProvider);

            t.Start();
            Thread.Sleep(100);

            try
            {
                t.Start();
            }
            catch (Exception ex)
            {
                Assert.True(false, "Exception while starting agent that is already started: " + ex.ToString());
                throw;
            }

            t.Stop();
        }

        [Fact, TestCategory("Functional"), TestCategory("Messaging")]
        public void AgentStopWhileStopped()
        {
            TestAgent t = new TestAgent(output, this.serviceProvider);

            t.Start();
            Thread.Sleep(100);

            t.Stop();
            Thread.Sleep(100);

            try
            {
                t.Stop();
            }
            catch (Exception ex)
            {
                Assert.True(false, "Exception while stopping agent that is already stopped: " + ex.ToString());
                throw;
            }
        }
    }
}
