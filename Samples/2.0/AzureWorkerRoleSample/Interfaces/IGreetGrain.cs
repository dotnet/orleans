using Orleans;
using System;
using System.Threading.Tasks;

namespace AzureWorker.Interfaces
{
    public interface IGreetGrain : IGrainWithStringKey
    {
        Task<string> Greet(string name);
    }
}
