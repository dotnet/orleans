using System;
using System.Fabric;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Orleans.ServiceFabric;

namespace Orleans.Hosting.ServiceFabric
{
    /// <summary>
    /// Creates Service Fabric listeners for Orleans silos.
    /// </summary>
    public static class OrleansServiceListener
    {
        /// <summary>
        /// Creates a <see cref="ServiceInstanceListener"/> which manages an Orleans silo for a stateless service.
        /// </summary>
        /// <param name="configure">The <see cref="ISiloHostBuilder"/> configuration delegate.</param>
        /// <returns>A <see cref="ServiceInstanceListener"/> which manages an Orleans silo.</returns>
        public static ServiceInstanceListener CreateStateless(Action<StatelessServiceContext, ISiloHostBuilder> configure)
        {
            return new ServiceInstanceListener(
                context => new OrleansCommunicationListener(builder => configure(context, builder)),
                ServiceFabricConstants.ListenerName);
        }

        /// <summary>
        /// Creates a <see cref="ServiceInstanceListener"/> which manages an Orleans silo for a stateless service.
        /// </summary>
        /// <param name="configure">The <see cref="ISiloHostBuilder"/> configuration delegate.</param>
        /// <returns>A <see cref="ServiceInstanceListener"/> which manages an Orleans silo.</returns>
        public static ServiceReplicaListener CreateStateful(Action<StatefulServiceContext, ISiloHostBuilder> configure)
        {
            return new ServiceReplicaListener(
                context => new OrleansCommunicationListener(builder => configure(context, builder)),
                ServiceFabricConstants.ListenerName);
        }
    }
}