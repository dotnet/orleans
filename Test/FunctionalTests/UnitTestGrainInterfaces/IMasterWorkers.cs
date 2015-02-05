using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTestGrains
{
    public interface IMasterGrain : IGrain
    {
        Task Initialize(IWorkerGrain[] workers);
        Task DoWork(int numItemsPerWorker, int itemLenght);
    }

    public interface IWorkerGrain : IGrain
    {
        Task Initialize(IAggregatorGrain aggregator);
        Task DoWork(int numItems, int itemLenght);
    }

    public interface IAggregatorGrain : IGrain
    {
        Task Initialize(int numWorkers);
        Task TakeResult(double result);
        Task<List<double>> GetResults();
    }
}
