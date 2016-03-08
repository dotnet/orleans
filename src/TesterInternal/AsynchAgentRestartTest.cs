using System;
using System.Threading;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.MessageCenterTests
{
    public class AsynchAgentRestartTest
    {
        private readonly ITestOutputHelper output;

        private class TestAgent : AsynchAgent
        {
            private readonly ITestOutputHelper output;

            public TestAgent(ITestOutputHelper output)
            {
                this.output = output;
            }

            protected override void Run()
            {
                output.WriteLine("Agent running in thread " + this.ManagedThreadId);
                Cts.Token.WaitHandle.WaitOne();
                output.WriteLine("Agent stopping in thread " + this.ManagedThreadId);
            }
        }

        public AsynchAgentRestartTest(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("Functional"), TestCategory("Messaging")]
        public void AgentRestart()
        {
            TestAgent t = new TestAgent(output);

            t.Start();
            Assert.AreEqual<ThreadState>(ThreadState.Running, t.State, "Agent state is wrong after initial start");
            Thread.Sleep(100);

            t.Stop();
            Assert.AreEqual<ThreadState>(ThreadState.Stopped, t.State, "Agent state is wrong after initial stop");
            Thread.Sleep(100);

            try
            {
                t.Start();
            }
            catch (Exception ex)
            {
                Assert.Fail("Exception while restarting agent: " + ex.ToString());
                throw;
            }
            Assert.AreEqual<ThreadState>(ThreadState.Running, t.State, "Agent state is wrong after restart");
            Thread.Sleep(100);

            t.Stop();
            Assert.AreEqual<ThreadState>(ThreadState.Stopped, t.State, "Agent state is wrong after final stop");
            Thread.Sleep(100);
        }

        [Fact, TestCategory("Functional"), TestCategory("Messaging")]
        public void AgentStartWhileStarted()
        {
            TestAgent t = new TestAgent(output);

            t.Start();
            Thread.Sleep(100);

            try
            {
                t.Start();
            }
            catch (Exception ex)
            {
                Assert.Fail("Exception while starting agent that is already started: " + ex.ToString());
                throw;
            }

            t.Stop();
        }

        [Fact, TestCategory("Functional"), TestCategory("Messaging")]
        public void AgentStopWhileStopped()
        {
            TestAgent t = new TestAgent(output);

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
                Assert.Fail("Exception while stopping agent that is already stopped: " + ex.ToString());
                throw;
            }
        }
    }
}
