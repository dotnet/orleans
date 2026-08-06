using System.Fabric;
using Microsoft.Extensions.Hosting;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;

namespace ServiceFabric.HostingExample;

public sealed class OrleansHostedStatelessService : StatelessService
{
    private readonly Func<StatelessServiceContext, Task<IHost>> _createHost;

    public OrleansHostedStatelessService(
        Func<StatelessServiceContext, Task<IHost>> createHost, StatelessServiceContext serviceContext)
        : base(serviceContext) =>
        _createHost = createHost ?? throw new ArgumentNullException(nameof(createHost));  

    /// <inheritdoc/>
    protected sealed override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
    {
        // Create a listener which creates and runs an IHost
        yield return new ServiceInstanceListener(
            context => new HostedServiceCommunicationListener(() => _createHost(context)),
            nameof(HostedServiceCommunicationListener));
    }
}
