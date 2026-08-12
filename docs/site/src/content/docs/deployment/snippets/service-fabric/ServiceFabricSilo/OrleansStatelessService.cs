// <service_fabric_orleans_stateless_service>
using System.Fabric;
using Microsoft.Extensions.Hosting;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;

namespace ServiceFabricSilo;

internal sealed class OrleansStatelessService(
    StatelessServiceContext context,
    Func<StatelessServiceContext, IHost> createHost)
    : StatelessService(context)
{
    protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
    {
        yield return new ServiceInstanceListener(
            serviceContext => new OrleansCommunicationListener(
                serviceContext,
                () => createHost(serviceContext)),
            "Orleans");
    }
}
// </service_fabric_orleans_stateless_service>
