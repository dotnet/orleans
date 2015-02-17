using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTestGrains;

namespace UnitTests
{
    /// <summary>
    /// Summary description for PersistenceTest
    /// </summary>
    [TestClass]
    public class ConsistencyTest : UnitTestBase
    {
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);
        const int port = 33330;

        public ConsistencyTest() : base(true)
        {
            Console.WriteLine("#### ConsistencyTest() is called.");
        }

        [TestCleanup()]
        public void Cleanup()
        {
            ResetAllAdditionalRuntimes();
            //GrainClient.Current.Reset();
        }

        [ClassCleanup()]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
            //GrainClient.GetInstance(false).Reset();
        }

        [TestMethod(), TestCategory("BVT"), TestCategory("Nightly"), TestCategory("General")]
        public void ConsistencyTestReentrancy()
        {
            ResultHandle result = new ResultHandle();
            //Console.WriteLine("\nPlease type in ENTER:");
            //Console.ReadLine();
            IErrorPersistentGrain grain = ErrorPersistentGrainFactory.CreateGrain();

            AsyncCompletion[] clients = new AsyncCompletion[5];
            for (int i = 0; i < clients.Length; i++)
            {
                clients[i] = AsyncCompletion.StartNew(() =>
                {
                    for (int j = 0; j < 3; j++)
                    {
                        AsyncCompletion p = grain.LongMethod(100);
                        p.Wait();
                    }
                });
            }
            for (int i = 0; i < clients.Length; i++)
            {
                clients[i].Wait();
            }
            result.Done = true;
            Assert.IsTrue(result.WaitForFinished(timeout));
            Console.WriteLine("\n\nENDED TEST\n\n");
        }

#if TODO

        [TestMethod()]
        public void ConsistencyTestMigrationPersistent()
        {
            Console.WriteLine("\n-----------------------------------------------------------------\n");
            ConsistencyTestMigration(true);
        }
        [TestMethod()]
        public void ConsistencyTestMigrationNONPersistent()
        {
            Console.WriteLine("\n-----------------------------------------------------------------\n");
            ConsistencyTestMigration(false);
        }

        private void ConsistencyTestMigration(bool persistent)
        {
            SimpleGrainSmartProxy grain = new SimpleGrainSmartProxy(persistent/*, "ConsistencyTestMigration-" + persistent + "-PER"*/);
            AsyncCompletion p = grain.SetA(122);
            p.Wait();

            OrleansRuntime instance2 = StartAdditionalOrleans(port);
            IManagementGrain mgmtGrain = Orleans.SystemManagement;
            mgmtGrain.SuspendHost(Orleans.RuntimeInstanceId).Wait();
            //Orleans.Suspend(false);

            int a = grain.GetA().GetValue();
            Assert.AreEqual(122, a);
            //Orleans.Resume();
            mgmtGrain.ResumeHost(Orleans.RuntimeInstanceId).Wait();
            Console.WriteLine("\n\nENDED TEST\n\n");
        }

        [TestMethod()]
        public void ConsistencyTestPersistence()
        {
            Console.WriteLine("\n-----------------------------------------------------------------\n");
            ConsistencyTestPersistence(true);
        }
        [TestMethod()]
        public void ConsistencyTestNONPersistence()
        {
            Console.WriteLine("\n-----------------------------------------------------------------\n");
            ConsistencyTestPersistence(false);
        }
        public void ConsistencyTestPersistence(bool persistent)
        {
            ResultHandle result = new ResultHandle();
            OrleansRuntime instance2 = StartAdditionalOrleans(port);
            IManagementGrain mgmtGrain = Orleans.SystemManagement;
            mgmtGrain.SuspendHost(instance2.RuntimeInstanceId).Wait();

            SimpleGrainSmartProxy grain = new SimpleGrainSmartProxy(persistent/*, "ConsistencyTest-" + persistent + "-PER"*/);
            grain.SetA(0).Wait();

            const int nIncsPerClient = 3;
            for (int j = 0; j < nIncsPerClient; j++)
            {
                AsyncCompletion p = grain.IncrementA();
                p.Wait();
            }
            mgmtGrain.SuspendHost(Orleans.RuntimeInstanceId).Wait();
            mgmtGrain.ResumeHost(instance2.RuntimeInstanceId).Wait();

            if (persistent)
            {
                int all = grain.GetA().GetValue();
                Assert.AreEqual(nIncsPerClient, all);
            }
            else
            {
                try
                {
                    grain.GetA().GetValue();
                    Assert.Fail("Expected exception hasn't been thrown.");
                }
                catch (Exception exc)
                {
                    Assert.IsTrue(exc.GetBaseException() is OrleansChannelNotFoundException);
                }
            }

            mgmtGrain.ResumeHost(Orleans.RuntimeInstanceId).Wait();
            result.Done = true;
            Assert.IsTrue(result.WaitForFinished(timeout));
            Console.WriteLine("\n\nTEST ENDED SUCCESSFULLY\n\n");
        }

        [TestMethod()]
        public void ConsistencyTest_LoggerMigrationPersistent()
        {
            Console.WriteLine("\n-----------------------------------------------------------------\n");
            ConsistencyTest_LoggerMigration(true);
        }
        [TestMethod()]
        public void ConsistencyTest_LoggerMigrationNONPersistent()
        {
            Console.WriteLine("\n-----------------------------------------------------------------\n");
            ConsistencyTest_LoggerMigration(false);
        }

        private void ConsistencyTest_LoggerMigration(bool persistent)
        {
            SimpleGrainSmartProxy grain = new SimpleGrainSmartProxy(persistent/*, "ConsistencyTest-LoggerMigration" + persistent + "-PER"*/);
            grain.LogMessage("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX-1").Wait();

            OrleansRuntime instance2 = StartAdditionalOrleans(port);
            IManagementGrain mgmtGrain = Orleans.SystemManagement;
            mgmtGrain.SuspendHost(Orleans.RuntimeInstanceId).Wait();
            //Orleans.Suspend(false);

            grain.LogMessage("YYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYY-2").Wait();
            mgmtGrain.ResumeHost(Orleans.RuntimeInstanceId).Wait();
            Console.WriteLine("\n\nENDED TEST\n\n");
        }
#endif
    }
}
