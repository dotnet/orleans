using System;
using System.Threading.Tasks;
using HelloEnvironmentInterfaces;

namespace HelloEnvironmentGrains
{
    /// <summary>
    /// Orleans grain implementation class HelloGrain.
    /// </summary>
    public class HelloEnvironmentHelloGrain : Orleans.Grain, IHelloEnvironment
    {
        Task<string> IHelloEnvironment.RequestDetails()
        {
            return Task.FromResult($"{Environment.MachineName} - {Environment.OSVersion} - {Environment.ProcessorCount} Processors");
        }
    }
}
