using Microsoft.Orleans.Docker.Models;

namespace Microsoft.Orleans.Docker
{
    /// <summary>
    /// Listen for Docker container changes
    /// </summary>
    internal interface IDockerStatusListener
    {
        /// <summary>
        /// Notifies this instance of an update to cluster members.
        /// </summary>
        /// <param name="silos">The updated set of silos.</param>
        void OnUpdate(DockerSiloInfo[] silos);
    }
}
