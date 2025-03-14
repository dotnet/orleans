using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime;

internal sealed partial class ClusterConnectionStatusObserverAdaptor(
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
                LogErrorSendingClusterConnectionLostNotification(logger, ex);
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
                LogErrorSendingGatewayCountChangedNotification(logger, ex);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error sending cluster connection lost notification."
    )]
    private static partial void LogErrorSendingClusterConnectionLostNotification(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error sending gateway count changed notification."
    )]
    private static partial void LogErrorSendingGatewayCountChangedNotification(ILogger logger, Exception ex);
}