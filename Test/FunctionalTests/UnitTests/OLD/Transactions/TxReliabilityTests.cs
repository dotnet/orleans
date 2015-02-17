using System;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Coordination;
using Orleans.Runtime;
using Orleans.RuntimeCore;
using UnitTestGrainInterfaces;

namespace UnitTests.Transactions
{
    [TestClass]
    public class TxReliabilityTests : UnitTestBase
    {
        private const string StoragePath = ".\\RelTestStorage";

        private static Options TestOptions = new Options
        {
            StartFreshOrleans = true,
            UseStore = true,
            StorageDirectory = StoragePath,
            Validation = true,
            DisableTasks = false,
            SingleActivationMode = false,
            MaxResendCount = 5,
            StartOutOfProcess = false,
        };

        public TxReliabilityTests()
            : base(true)
        {
            Console.WriteLine("#### TxReliabilityTests.");
        }

        private static void Clear()
        {
            try
            {
                Directory.Delete(StoragePath, true);
            }
            catch (DirectoryNotFoundException)
            {
                // ignore
            }
        }

        [TestCleanup]
        public void Cleanup()
        {
            ResetAllAdditionalRuntimes();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod]
        public void TxReliabilityShutdownSilo()
        {
            Clear();

            Initialize(TestOptions);
            Thread.Sleep(3000);

            var silo0 = new[] {GrainStrategy.PartitionPlacement(0)};
            var silo1 = new[] {GrainStrategy.PartitionPlacement(1)};

            IReliabilityTestGrain a = ReliabilityTestGrainFactory.CreateGrain(Label: "a", Strategies: silo1);
            IReliabilityTestGrain b = ReliabilityTestGrainFactory.CreateGrain(Label: "b", Other: a, Strategies: silo0);
            IReliabilityTestGrain c = ReliabilityTestGrainFactory.CreateGrain(Label: "c", Other: b, Strategies: silo1);

            AsyncCompletion done = c.SetLabels("done", 1000);

            Thread.Sleep(1000);
            Secondary.Silo.ShutDown();
            Secondary.Silo.SiloTerminatedEvent.WaitOne(30000);
            Thread.Sleep(30000); // wait for oracle to decide silo is dead

            done.Wait();
            string la = a.Label.GetValue();
            string lb = b.Label.GetValue();
            string lc = c.Label.GetValue();

            Assert.IsTrue(la == "done" && lb == "done" && lc == "done",
                "Transaction completed succesfully");
        }

        [TestMethod]
        public void TxReliabilityKillSilo()
        {
            Clear();

            Initialize(TestOptions);
            // allow time for directory to decide silo really is dead
            //const int threshold = 2 * RuntimeTimeouts.LIVENESS_MISSED_I_AM_ALIVE_THRESHOLD * RuntimeTimeouts.LIVENESS_I_AM_ALIVE_TIMEOUT;
            TimeSpan threshold = Globals.ProbeTimeout.Multiply(2 * Globals.NumMissedProbesLimit);
            GrainClient.Current.SetResponseTimeout(threshold);
            Thread.Sleep(3000);

            var silo0 = new[] {GrainStrategy.PartitionPlacement(0)};
            var silo1 = new[] {GrainStrategy.PartitionPlacement(1)};

            IReliabilityTestGrain a = ReliabilityTestGrainFactory.CreateGrain(Label: "a", Strategies: silo1);
            IReliabilityTestGrain b = ReliabilityTestGrainFactory.CreateGrain(Label: "b", Other: a, Strategies: silo0);
            IReliabilityTestGrain c = ReliabilityTestGrainFactory.CreateGrain(Label: "c", Other: b, Strategies: silo1);

            c.Wait();
            AsyncCompletion done = c.SetLabels("done", 1000);

            Thread.Sleep(500);
            ResetRuntime(Secondary);

            done.Wait(threshold);
            string la = a.Label.GetValue();
            string lb = b.Label.GetValue();
            string lc = c.Label.GetValue();

            Assert.IsTrue(la == "done" && lb == "done" && lc == "done",
                "Transaction completed succesfully");
        }
    }
}

