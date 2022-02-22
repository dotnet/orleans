using Orleans.Runtime;
using Orleans.Runtime.TestHooks;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Extension methods for <see cref="IClusterClient"/>.
    /// </summary>
    internal static class ClientExtensions
    {
        /// <summary>
        /// Returns test hooks for the specified silo.
        /// </summary>
        /// <param name="client">The client.</param>
        /// <param name="silo">The silo.</param>
        /// <returns>Test hooks for the specified silo.</returns>
        public static ITestHooks GetTestHooks(this IClusterClient client, SiloHandle silo)
        {
            // Use the siloAddress here, not the gateway address, since we may be targeting a silo on which we are not 
            // connected to the gateway
            var internalClient = (IInternalClusterClient) client;
            return internalClient.GetSystemTarget<ITestHooksSystemTarget>(Constants.TestHooksSystemTargetType, silo.SiloAddress);
        }
    }
}
