using System.Collections.Generic;

namespace Orleans.Streams
{
    /// <summary>
    /// Interface for accessing the deployment configuration.
    /// </summary>
    public interface IDeploymentConfiguration
    {
        /// <summary>
        /// Get the silo instance names for all configured silos
        /// </summary>
        /// <returns></returns>
        IList<string> GetAllSiloNames();
    }
}
