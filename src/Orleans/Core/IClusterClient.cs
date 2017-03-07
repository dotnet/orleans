using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// Client interface for interacting with an Orleans cluster.
    /// </summary>
    public interface IClusterClient : IDisposable, IGrainFactory
    {
        /// <summary>
        /// Gets a value indicating whether or not this client is initialized.
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Provides logging facility for applications.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if Orleans runtime is not correctly initialized before this call.</exception>
        Logger Logger { get; }

        /// <summary>
        /// Gets or sets the response timeout used by this client.
        /// </summary>
        TimeSpan ResponseTimeout { get; set; }

        /// <summary>
        /// Gets the service provider used by this client.
        /// </summary>
        IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Gets the client configuration.
        /// </summary>
        ClientConfiguration Configuration { get; }

        /// <summary>
        /// Global pre-call interceptor function
        /// Synchronous callback made just before a message is about to be constructed and sent by a client to a grain.
        /// This call will be made from the same thread that constructs the message to be sent, so any thread-local settings
        /// such as <c>Orleans.RequestContext</c> will be picked up.
        /// The action receives an <see cref="InvokeMethodRequest"/> with details of the method to be invoked, including InterfaceId and MethodId,
        /// and a <see cref="IGrain"/> which is the GrainReference this request is being sent through
        /// </summary>
        /// <remarks>This callback method should return promptly and do a minimum of work, to avoid blocking calling thread or impacting throughput.</remarks>
        Action<InvokeMethodRequest, IGrain> ClientInvokeCallback { get; set; }

        /// <summary>
        /// Event fired when connection to the cluster is lost.
        /// </summary>
        event ConnectionToClusterLostHandler ClusterConnectionLost;

        /// <summary>
        /// Returns a collection of all configured <see cref="IStreamProvider"/>s.
        /// </summary>
        /// <returns>A collection of all configured <see cref="IStreamProvider"/>s.</returns>
        IEnumerable<IStreamProvider> GetStreamProviders();

        /// <summary>
        /// Returns the <see cref="IStreamProvider"/> with the specified <paramref name="name"/>.
        /// </summary>
        /// <param name="name">The name of the stream provider.</param>
        /// <returns>The <see cref="IStreamProvider"/> with the specified <paramref name="name"/>.</returns>
        IStreamProvider GetStreamProvider(string name);

        /// <summary>
        /// Starts the client and connects to the configured cluster.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Start();

        /// <summary>
        /// Stops the client gracefully, disconnecting from the cluster.
        /// </summary>
        void Stop();

        /// <summary>
        /// Aborts the client ungracefully.
        /// </summary>
        void Abort();
    }
}