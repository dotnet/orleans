using Orleans.Runtime;
using Orleans.Runtime.TestHooks;

namespace Orleans.TestingHost
{
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
            var internalClient = (IInternalClusterClient) client;
            return internalClient.GetSystemTarget<ITestHooksSystemTarget>(Constants.TestHooksSystemTargetId, silo.GatewayAddress);
        }
    }
}
