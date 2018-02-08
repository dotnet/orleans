using System;
using System.Collections.Generic;
using System.Text;
using Orleans.CodeGeneration;

namespace Orleans
{
    /// <summary>
    /// Handler for client disconnection from a cluster.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event arguments.</param>
    public delegate void ConnectionToClusterLostHandler(object sender, EventArgs e);

    /// <summary>
    /// The delegate called before every request to a grain.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="grain">The grain.</param>
    public delegate void ClientInvokeCallback(InvokeMethodRequest request, IGrain grain);
}
