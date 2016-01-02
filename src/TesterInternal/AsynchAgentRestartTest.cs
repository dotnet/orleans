using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;

namespace UnitTests.MessageCenterTests
{
    [TestClass]
    public class AsynchAgentRestartTest
    {
        private class TestAgent : AsynchAgent
        {
            protected override void Run()
            {
                Console.WriteLine("Agent running in thread " + this.ManagedThreadId);
                Cts.Token.WaitHandle.WaitOne();
                Console.WriteLine("Agent stopping in thread " + this.ManagedThreadId);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Messaging")]
        public void AgentRestart()
        {
            TestAgent t = new TestAgent();

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

        [TestMethod, TestCategory("Functional"), TestCategory("Messaging")]
        public void AgentStartWhileStarted()
        {
            TestAgent t = new TestAgent();

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

        [TestMethod, TestCategory("Functional"), TestCategory("Messaging")]
        public void AgentStopWhileStopped()
        {
            TestAgent t = new TestAgent();

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
