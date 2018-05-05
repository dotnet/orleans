using System;
using System.Threading.Tasks;
using Orleans;

using AzureWorker.Interfaces;

namespace AzureWorker.Grains
{
    public class GreetGrain : Grain, IGreetGrain
    {
        public Task<string> Greet(string name) 
            => Task.FromResult($"Hello from {this.GetPrimaryKeyString()} to {name}");
    }
}
