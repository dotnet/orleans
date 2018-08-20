using System;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
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
        /// <param name="onOpen"></param>
        /// <param name="onOpened"></param>
        /// <returns>A <see cref="ServiceInstanceListener"/> which manages an Orleans silo.</returns>
        public static ServiceInstanceListener CreateStateless(Action<StatelessServiceContext, ISiloHostBuilder> configure,
            Func<CancellationToken, Task> onOpen = null,
            Func<ISiloHost, CancellationToken, Task> onOpened = null)
        {
            return new ServiceInstanceListener(
                context => new OrleansCommunicationListener(builder => configure(context, builder), onOpen, onOpened),
                ServiceFabricConstants.ListenerName);
        }

        /// <summary>
        /// Creates a <see cref="ServiceInstanceListener"/> which manages an Orleans silo for a stateless service.
        /// </summary>
        /// <param name="configure">The <see cref="ISiloHostBuilder"/> configuration delegate.</param>
        /// <param name="onOpen"></param>
        /// <param name="onOpened"></param>
        /// <returns>A <see cref="ServiceInstanceListener"/> which manages an Orleans silo.</returns>
        public static ServiceReplicaListener CreateStateful(Action<StatefulServiceContext, ISiloHostBuilder> configure,
            Func<CancellationToken, Task> onOpen = null,
            Func<ISiloHost, CancellationToken, Task> onOpened = null)
        {
            return new ServiceReplicaListener(
                context => new OrleansCommunicationListener(builder => configure(context, builder), onOpen, onOpened),
                ServiceFabricConstants.ListenerName,
                listenOnSecondary: false);
        }
    }
}