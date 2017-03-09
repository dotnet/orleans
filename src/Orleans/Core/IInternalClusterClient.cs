using System;
using Orleans.CodeGeneration;
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// The internal-facing client interface.
    /// </summary>
    internal interface IInternalClusterClient : IClusterClient, IInternalGrainFactory
    {
        /// <summary>
        /// Gets the client's <see cref="IStreamProviderRuntime"/>.
        /// </summary>
        IStreamProviderRuntime StreamProviderRuntime { get; }

        /// <summary>
        /// Global pre-call interceptor function
        /// Synchronous callback made just before a message is about to be constructed and sent by a client to a grain.
        /// This call will be made from the same thread that constructs the message to be sent, so any thread-local settings
        /// such as <c>Orleans.RequestContext</c> will be picked up.
        /// The action receives an <see cref="InvokeMethodRequest"/> with details of the method to be invoked, including InterfaceId and MethodId,
        /// and a <see cref="IGrain"/> which is the GrainReference this request is being sent through
        /// </summary>
        /// <remarks>This callback method should return promptly and do a minimum of work, to avoid blocking calling thread or impacting throughput.</remarks>
        ClientInvokeCallback ClientInvokeCallback { get; set; }

        /// <summary>
        /// Event fired when connection to the cluster is lost.
        /// </summary>
        event ConnectionToClusterLostHandler ClusterConnectionLost;

        /// <summary>
        /// Gets or sets the response timeout used by this client.
        /// </summary>
        TimeSpan ResponseTimeout { get; set; }
    }
}