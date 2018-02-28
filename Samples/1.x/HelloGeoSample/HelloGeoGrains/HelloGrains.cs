using System;
using System.Threading.Tasks;
using HelloGeoInterfaces;
using Microsoft.Azure;
using Orleans.MultiCluster;

namespace HelloGeoGrains
{
    /// <summary>
    /// Implementation of the Hello grain (same for both versions)
    ///  </summary>
    public class HelloGrain : Orleans.Grain, IHelloGrain
    {
        private int count = 0; // counts the number of pings

        Task<string> IHelloGrain.Ping()
        {
            var answer = string.Format("Hello #{0}\n(on machine \"{2}\" in cluster \"{1}\")",
                ++this.count, CloudConfigurationManager.GetSetting("ClusterId"), Environment.MachineName);

            return Task.FromResult(answer);
        }
    }

    /// <summary>
    /// One-per-cluster version
    /// </summary>
    [OneInstancePerCluster]
    public class OneInstancePerClusterGrain : HelloGrain
    {
    }


    /// <summary>
    /// Global-single-instance version
    /// </summary>
    [GlobalSingleInstance]
    public class GlobalSingleInstanceGrain : HelloGrain
    {
    }
}