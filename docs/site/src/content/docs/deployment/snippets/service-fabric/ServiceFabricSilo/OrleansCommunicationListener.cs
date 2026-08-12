// <service_fabric_orleans_communication_listener>
using System.Fabric;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.ServiceFabric.Services.Communication.Runtime;

namespace ServiceFabricSilo;

internal sealed class OrleansCommunicationListener(
    StatelessServiceContext context,
    Func<IHost> createHost)
    : ICommunicationListener
{
    private readonly object _lock = new();
    private ListenerState? _state;

    public async Task<string> OpenAsync(CancellationToken cancellationToken)
    {
        var state = new ListenerState(createHost(), new CancellationTokenSource());
        lock (_lock)
        {
            if (_state is not null)
            {
                state.Host.Dispose();
                state.Abort.Dispose();
                throw new InvalidOperationException("The listener is already open.");
            }

            _state = state;
            state.ActiveOperations++;
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                state.Abort.Token);
            await state.Host.StartAsync(linkedCancellation.Token);
            var activationContext = context.CodePackageActivationContext;
            var address = context.NodeContext.IPAddressOrFQDN;
            var siloPort = activationContext.GetEndpoint("OrleansSiloEndpoint").Port;
            var gatewayPort = activationContext.GetEndpoint("OrleansGatewayEndpoint").Port;

            return JsonSerializer.Serialize(new
            {
                Endpoints = new
                {
                    Silo = $"tcp://{address}:{siloPort}",
                    Gateway = $"tcp://{address}:{gatewayPort}",
                },
            });
        }
        catch
        {
            RemoveState(state);
            throw;
        }
        finally
        {
            CompleteOperation(state);
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        ListenerState? state;
        lock (_lock)
        {
            state = _state;
            if (state is not null)
            {
                state.ActiveOperations++;
            }
        }

        if (state is null)
        {
            return;
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                state.Abort.Token);
            await state.Host.StopAsync(linkedCancellation.Token);
        }
        finally
        {
            RemoveState(state);
            CompleteOperation(state);
        }
    }

    public void Abort()
    {
        ListenerState? state;
        lock (_lock)
        {
            state = _state;
            if (state is not null)
            {
                _state = null;
                state.Removed = true;
                state.AbortRequested = true;
            }
        }

        if (state is not null)
        {
            state.Abort.Cancel();
            lock (_lock)
            {
                state.AbortSignaled = true;
            }

            DisposeIfComplete(state);
        }
    }

    private void RemoveState(ListenerState state)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_state, state))
            {
                _state = null;
            }

            state.Removed = true;
        }
    }

    private void CompleteOperation(ListenerState state)
    {
        lock (_lock)
        {
            state.ActiveOperations--;
        }

        DisposeIfComplete(state);
    }

    private void DisposeIfComplete(ListenerState state)
    {
        var dispose = false;
        lock (_lock)
        {
            if (state.Removed
                && state.ActiveOperations == 0
                && (!state.AbortRequested || state.AbortSignaled)
                && !state.Disposed)
            {
                state.Disposed = true;
                dispose = true;
            }
        }

        if (dispose)
        {
            state.Host.Dispose();
            state.Abort.Dispose();
        }
    }

    private sealed record ListenerState(IHost Host, CancellationTokenSource Abort)
    {
        public int ActiveOperations { get; set; }

        public bool Removed { get; set; }

        public bool AbortRequested { get; set; }

        public bool AbortSignaled { get; set; }

        public bool Disposed { get; set; }
    }
}
// </service_fabric_orleans_communication_listener>
