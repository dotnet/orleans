using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTestGrains;

namespace UnitTests
{
    [TestClass]
    public class MasterWorkersTests : UnitTestBase
    {
        private IMasterGrain masterGrain;
        private List<IWorkerGrain> workerGrains;
        private IAggregatorGrain aggregatorGrain;

        private readonly int NumSilos = 1;
        private readonly int NumWorkers = 2;
        private readonly int NumItemsPerWorker = 100;
        private readonly int ItemLength = 5000;

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        public static void RunTest(string[] args)
        {
            int numSilos = int.Parse(args[0]);
            int numWorkers = int.Parse(args[1]);
            int numItemsPerWorker = int.Parse(args[2]);
            int itemLenght = int.Parse(args[3]);

            MasterWorkersTests tests = new MasterWorkersTests(numSilos, numWorkers, numItemsPerWorker, itemLenght);
            tests.MasterWorkersTests_1().WaitWithThrow(TimeSpan.FromMinutes(1));
        }

        public MasterWorkersTests(int numSilos, int numWorkers, int numItemsPerWorker, int itemLength)
            : base(new Options { StartPrimary = numSilos > 0, StartSecondary = numSilos > 1, StartClient = numSilos > 0 })
        {
            NumSilos = numSilos;
            NumWorkers = numWorkers;
            NumItemsPerWorker = numItemsPerWorker;
            ItemLength = itemLength;
            if (numSilos==0)
            {
                GrainClient.Initialize();
            }
        }

        private async Task StartGrains()
        {
            Console.WriteLine("Starting creating grains");

            // create all grains and wait to make sure all created.
            masterGrain = MasterGrainFactory.GetGrain(GetRandomGrainId());
            aggregatorGrain = AggregatorGrainFactory.GetGrain(GetRandomGrainId());
            workerGrains = new List<IWorkerGrain>();
            for (int i = 0; i < NumWorkers; i++)
            {
                IWorkerGrain worker = WorkerGrainFactory.GetGrain(GetRandomGrainId());
                workerGrains.Add(worker);
            }
            List<IAddressable> grains = new List<IAddressable>();
            grains.AddRange(workerGrains);
            grains.Add(masterGrain);
            grains.Add(aggregatorGrain);

            // now initialize grains.
            List<Task> promises = new List<Task>();
            promises.Add(masterGrain.Initialize(workerGrains.ToArray()));
            for (int i = 0; i < workerGrains.Count; i++)
            {
                promises.Add(workerGrains[i].Initialize(aggregatorGrain));
            }
            promises.Add(aggregatorGrain.Initialize(NumWorkers));
            await Task.WhenAll(promises);
            Console.WriteLine("Done creating grains");
        }

        [TestMethod, TestCategory("FanOut")]
        public async Task MasterWorkersTests_1()
        {
            Console.WriteLine("MasterWorkersTests_1, numWorkers={0}, numItems={1}", NumWorkers, NumItemsPerWorker);

            await StartGrains();

            Stopwatch s = new Stopwatch();
            s.Start();
            await masterGrain.DoWork(NumItemsPerWorker, ItemLength);
            Console.WriteLine("Fan-out - Work distributed.");

            List<double> result = await aggregatorGrain.GetResults();
            s.Stop();
            Console.WriteLine("Fan-in - Work completed.");

            double timeActual = s.Elapsed.TotalSeconds;
            Console.WriteLine("MasterWorkersTests_1 took {0:F2} seconds to do all {1} work.", timeActual, result.Count);
        }
    }
}