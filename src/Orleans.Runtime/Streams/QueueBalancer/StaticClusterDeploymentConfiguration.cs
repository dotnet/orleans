using System.Collections.Generic;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Deployment configuration that reads from orleans cluster configuration
    /// </summary>
    public class StaticClusterDeploymentOptions : IDeploymentConfiguration
    {
        public IList<string> SiloNames { get; set; } = new List<string>();

        IList<string> IDeploymentConfiguration.GetAllSiloNames()
        {
            return this.SiloNames;
        }
    }
}
