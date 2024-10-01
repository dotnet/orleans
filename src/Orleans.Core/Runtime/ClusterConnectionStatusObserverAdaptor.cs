using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime;

internal sealed class ClusterConnectionStatusObserverAdaptor(
    IEnumerable<GatewayCountChangedHandler> gatewayCountChangedHandlers,
    IEnumerable<ConnectionToClusterLostHandler> connectionLostHandlers,
    ILogger<ClusterClient> logger) : IClusterConnectionStatusObserver
{
    private readonly ImmutableArray<GatewayCountChangedHandler> _gatewayCountChangedHandlers = gatewayCountChangedHandlers.ToImmutableArray();
    private readonly ImmutableArray<ConnectionToClusterLostHandler> _connectionLostHandler = connectionLostHandlers.ToImmutableArray();

    public void NotifyClusterConnectionLost()
    {
        foreach (var handler in _connectionLostHandler)
        {
            try
            {
                handler(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                logger.LogError((int)ErrorCode.ClientError, ex, "Error sending cluster connection lost notification.");
            }
        }
    }

    public void NotifyGatewayCountChanged(int currentNumberOfGateways, int previousNumberOfGateways, bool connectionRecovered)
    {
        var args = new GatewayCountChangedEventArgs(currentNumberOfGateways, previousNumberOfGateways);
        foreach (var handler in _gatewayCountChangedHandlers)
        {
            try
            {
                handler(null, args);
            }
            catch (Exception ex)
            {
                logger.LogError((int)ErrorCode.ClientError, ex, "Error sending gateway count changed notification.");
            }
        }
    }
}