using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace UnitTestGrains
{
    public class MasterGrain : Grain, IMasterGrain
    {
        private List<IWorkerGrain> workers = null;
        private Logger logger;

        public Task Initialize(IWorkerGrain[] workers)
        {
            logger = base.GetLogger("MasterGrain-" + base.Identity.GetHashCode());
            logger.Info("Initialize");
            this.workers = new List<IWorkerGrain>(workers);
            return TaskDone.Done;
        }

        public Task DoWork(int numItemsPerWorker, int itemLenght)
        {
            logger.Info("DoWork(" + numItemsPerWorker + ")");
            List<Task> promises = new List<Task>();

            for (int i = 0; i < workers.Count; i++)
            {
                Task promise = workers[i].DoWork(numItemsPerWorker, itemLenght);
                promises.Add(promise);
            }
            return Task.WhenAll(promises);
        }
    }

    public class WorkerGrain : Grain, IWorkerGrain
    {
        private IAggregatorGrain aggregator = null;
        private Logger logger;
        //private static int dummyWorkLenght = 5000;

        public Task Initialize(IAggregatorGrain aggregator)
        {
            logger = base.GetLogger("WorkerGrain-" + base.Identity.GetHashCode());
            logger.Info("Initialize");
            this.aggregator = aggregator;
            return TaskDone.Done;
        }

        public Task DoWork(int numItems, int itemLenght)
        {
            logger.Info("DoWork(" + numItems + " itemLenght = " + itemLenght + ")");
            Stopwatch s = new Stopwatch();
            s.Start();
            double result = 0;
            for (int i = 0; i < numItems; i++)
            {
                result += DummyWork(itemLenght);
            }
            s.Stop();
            
            double timeActual = s.Elapsed.TotalSeconds;
            logger.Info("DoWork() DONE took {0:F2} seconds to do all work.", timeActual);
            return aggregator.TakeResult(result);
        }

        private double DummyWork(int itemLenght)
        {
            double counter = 0;
            for (int j = 0; j < itemLenght; j++)
            {
                for (int i = 0; i < itemLenght; i++)
                {
                    //counter += Math.Cos(Math.Sqrt((i * i) + (i * i * i)));
                    counter += (i * j * i);
                }
            }
            return counter;
        }
    }

    public class AggregatorGrain : Grain, IAggregatorGrain
    {
        private int numExpectedResults;
        private List<double> results = null;
        private TaskCompletionSource<List<double>> resolver;
        private Logger logger;

        public Task Initialize(int numWorkers)
        {
            logger = base.GetLogger("AggregatorGrain-" + base.Identity.GetHashCode());
            logger.Info("Initialize");
            this.numExpectedResults = numWorkers;
            this.results = new List<double>();
            resolver = new TaskCompletionSource<List<double>>();
            return TaskDone.Done;
        }

        public Task TakeResult(double result)
        {
            logger.Info("TakeResult");
            results.Add(result);
            if (results.Count == numExpectedResults)
            {
                resolver.SetResult(results);
            }
            return TaskDone.Done;
        }

        public Task<List<double>> GetResults()
        {
            logger.Info("GetResults");
            return resolver.Task;
        }
    }
}
