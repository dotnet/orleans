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
            return Task.FromResult(String.Format("{0} - {1} - {2} Processors", Environment.MachineName, Environment.OSVersion, Environment.ProcessorCount));
        }
    }
}
