using Microsoft.Extensions.Hosting;
using Microsoft.ServiceFabric.Services.Communication.Runtime;

namespace ServiceFabric.HostingExample;

internal sealed class HostedServiceCommunicationListener : ICommunicationListener
{
    private IHost? _host;
    private readonly Func<Task<IHost>> _createHost;

    public HostedServiceCommunicationListener(Func<Task<IHost>> createHost) =>
        _createHost = createHost ?? throw new ArgumentNullException(nameof(createHost));

    /// <inheritdoc />
    public async Task<string?> OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            _host = await _createHost.Invoke();
            await _host.StartAsync(cancellationToken);
        }
        catch
        {
            Abort();
            throw;
        }

        // This service does not expose any endpoints to Service Fabric for discovery by others.
        return null;
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_host is { } host)
        {
            await host.StopAsync(cancellationToken);
        }

        _host = null;
    }

    /// <inheritdoc />
    public void Abort()
    {
        IHost? host = _host;
        if (host is null)
        {
            return;
        }

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel(false);

        try
        {
            host.StopAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore.
        }
        finally
        {
            _host = null;
        }
    }
}
