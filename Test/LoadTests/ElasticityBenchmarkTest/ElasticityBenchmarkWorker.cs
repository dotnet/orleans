using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.IO;

using Orleans;
using Orleans.Runtime;
using Orleans.Concurrency;

using LoadTestBase;
using LoadTestGrainInterfaces;

using ExcelGenerator;

namespace GrainBenchmarkLoadTest
{
    public enum BenchmarkFunctionType
    {
        ReuseGrains,
        NewGrainPerRequest,
        KeepAlive
    }

    public class ElasticityBenchmarkWorker : OrleansClientWorkerBase
    {
        private int nGrains;
        private int startPoint;
        private BenchmarkFunctionType functionType;
        private List<IBenchmarkLoadGrain> grains;
        private List<int> grainMessageCount;
        private DateTime nextSampleTime;
        private IList<PlotStatsData> loggedStats = new List<PlotStatsData>();

        private static readonly TimeSpan sampleInterval = TimeSpan.FromSeconds(5);
        private static readonly bool statsCollectionEnabled = true;
        private int sampleCounter = 0;
        private Random rng;

        private int KEEP_ALIVE_MAX_NUM_REQUESTS_PER_GRAIN = 1000;

        public void ApplicationInitialize(int numGrains, BenchmarkFunctionType functionType, bool warmUpEnabled)
        {
            this.nGrains = numGrains;
            this.functionType = functionType;
            this.startPoint = new Random().Next(numGrains);
            rng = new Random();

            this.nextSampleTime = DateTime.Now;

            if (functionType == BenchmarkFunctionType.ReuseGrains)
            {
                grains = new List<IBenchmarkLoadGrain>(numGrains);
                for (long i = 1; i <= nGrains; i++)
                {
                    // we want all clients to have same grains
                    IBenchmarkLoadGrain grain = RandomNonReentrantBenchmarkLoadGrainFactory.GetGrain(-i);
                    grains.Add(grain);
                }
            }
            if (functionType == BenchmarkFunctionType.KeepAlive)
            {
                grains = new List<IBenchmarkLoadGrain>(numGrains);
                grainMessageCount = new List<int>(numGrains);
                for (long i = 1; i <= nGrains; i++)
                {
                    // we want all clients to have same grains
                    IBenchmarkLoadGrain grain = RandomNonReentrantBenchmarkLoadGrainFactory.GetGrain(Guid.NewGuid());
                    grains.Add(grain);
                    grainMessageCount.Add(rng.Next(KEEP_ALIVE_MAX_NUM_REQUESTS_PER_GRAIN));
                }
            }
            else if (functionType == BenchmarkFunctionType.NewGrainPerRequest && warmUpEnabled)
            {
                WriteProgress("Ignoring warmUpEnabled = true in NewGrainPerRequestMode!");
            }

            if (warmUpEnabled)
            {
                WarmupGrains();
            }

            Thread.Sleep(TimeSpan.FromSeconds(10));
            SaveSiloStats();
            WriteProgress("Done ApplicationInitialize by worker " + Name);
        }

        private void WarmupGrains()
        {
            WriteProgress("Warming up {0} grains", grains.Count);
            AsyncPipeline initPipeline = new AsyncPipeline(50);
            Random rng = new Random();
            int _startPoint = rng.Next(nGrains);
            List<Task> initPromises = new List<Task>(grains.Count);

            for (int i = 0; i < grains.Count; i++)
            {
                Task task = grains[(i + _startPoint) % grains.Count].Initialize();
                initPromises.Add(task);
                initPipeline.Add(task);
            }
            initPipeline.Wait();
            Task.WhenAll(initPromises).Wait();
        }

        private void WriteToExcel(string name)
        {
            WriteProgress("Write Excel File");
            var template = new FileInfo("GraphTemplate.xlsx");
            var outputFile = new FileInfo(ElasticBenchmarkDriver.EXCEL_NAME);
            ExcelSheetGenerator.GenerateSiloStats(loggedStats, template, outputFile);
            WriteProgress("Excel Sheet created: {0}", outputFile.ToString());

            IFormatter formatter = new BinaryFormatter();
            FileStream outputFile2 = new FileStream(ElasticBenchmarkDriver.EXCEL_NAME + ".dat", FileMode.Create);
            formatter.Serialize(outputFile2, loggedStats);
            outputFile2.Close();
            WriteProgress("Binary Blob created: {0}", outputFile2.ToString());
        }

        private void sampleStatistics(string desc, SiloAddress[] silos, SiloRuntimeStatistics[] stats)
        {
            var psd = new PlotStatsData();
            psd.Timestamp = DateTime.Now;
            psd.Description = desc;

            for (int i = 0; i < silos.Count(); i++)
            {
                var handle = stats[i];
                
                var pm = new PlotMetrics();
                pm.DateTime = handle.DateTime;
                pm.RequestQueueLength = handle.RequestQueueLength;
                pm.ActivationCount = handle.ActivationCount;
                pm.RecentlyUsedActivationCount = handle.RecentlyUsedActivationCount;
                pm.ClientCount = handle.ClientCount;
                pm.IsOverloaded = handle.IsOverloaded;
                pm.CpuUsage = handle.CpuUsage;
                pm.AvailableMemory = handle.AvailableMemory;
                pm.MemoryUsage = handle.MemoryUsage;
                pm.TotalPhysicalMemory = handle.TotalPhysicalMemory;
                pm.SendQueueLength = handle.SendQueueLength;
                pm.ReceiveQueueLength = handle.ReceiveQueueLength;
                //pm.ReducePlacementRate = handle.ReducePlacementRate;

                psd.siloStats.Add(silos[i].ToString(), pm);
            }

            loggedStats.Add(psd);
        }

        public override void Uninitialize()
        {
            try 
            {
                WriteProgress("Save statistics.");
                WriteToExcel(functionType.ToString());
            }
            catch (Exception e) {
                Console.WriteLine(e.ToString());
            }
            base.Uninitialize();
        }


        private Task SaveSiloStats()
        {
            IManagementGrain mgmtGrain = ManagementGrainFactory.GetGrain(1);

            var siloHosts = mgmtGrain.GetHosts(onlyActive: true);
            SiloAddress[] silos = siloHosts.Result.Keys.ToArray();

            var stats = mgmtGrain.GetRuntimeStatistics(silos).Result;
            sampleStatistics((sampleCounter++).ToString(), silos, stats);

            return TaskDone.Done;
        }

        private bool CanSampleSiloStats()
        {
            return statsCollectionEnabled && (DateTime.Now > nextSampleTime);
        }

        protected override Task IssueRequest(int requestNumber, int threadNumber)
        {
            //Don't log anything here or quickparser will not go into stable state since it looks for consecutive TPS lines :(
            //WriteProgress("IssueRequest {0} on thread {1}", requestNumber, threadNumber);
            if (CanSampleSiloStats())
            {
                nextSampleTime = DateTime.Now + sampleInterval;
                return SaveSiloStats();
            }

            if (functionType == BenchmarkFunctionType.ReuseGrains) 
            {
                int index = (requestNumber + startPoint) % nGrains;
                IBenchmarkLoadGrain grain = grains[index];
                return grain.Initialize();
            }
            else if (functionType == BenchmarkFunctionType.NewGrainPerRequest)
            {
                IBenchmarkLoadGrain grain = RandomNonReentrantBenchmarkLoadGrainFactory.GetGrain(Guid.NewGuid());
                return grain.Initialize();
            }
            else if (functionType == BenchmarkFunctionType.KeepAlive)
            {
                int index = (requestNumber + startPoint) % nGrains;
                IBenchmarkLoadGrain grain = grains[index];
                grainMessageCount[index] -= 1;

                if (grainMessageCount[index] <= 0)
                {
                    grains[index] = RandomNonReentrantBenchmarkLoadGrainFactory.GetGrain(Guid.NewGuid());
                    grainMessageCount[index] = rng.Next(KEEP_ALIVE_MAX_NUM_REQUESTS_PER_GRAIN);
                }
                return grain.Initialize();
            }
            else
            {
                throw new Exception("Unsupported functionType in ElasticityBenchmark!");
            }
        }
    }
}