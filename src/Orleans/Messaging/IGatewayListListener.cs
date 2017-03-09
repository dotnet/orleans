using System;
using System.Collections.Generic;

namespace Orleans.Messaging
{
    /// <summary>
    /// A listener interface for optional GatewayList notifications provided by the IGatewayListObservable interface.
    /// </summary>
    public interface IGatewayListListener
    {
        void GatewayListNotification(IEnumerable<Uri> gateways);
    }
}