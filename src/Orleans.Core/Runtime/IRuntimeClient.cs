using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// The IRuntimeClient interface defines a subset of the runtime API that is exposed to both silo and client.
    /// </summary>
    internal interface IRuntimeClient
    {
        /// <summary>
        /// Grain Factory to get and cast grain references.
        /// </summary>
        IInternalGrainFactory InternalGrainFactory { get; }

        /// <summary>
        /// A unique identifier for the current client.
        /// There is no semantic content to this string, but it may be useful for logging.
        /// </summary>
        string CurrentActivationIdentity { get; }

        /// <summary>
        /// Gets the service provider.
        /// </summary>
        IServiceProvider ServiceProvider { get; }

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
        /// Get the current response timeout setting for this client.
        /// </summary>
        /// <returns>Response timeout value</returns>
        TimeSpan GetResponseTimeout();

        /// <summary>
        /// Sets the current response timeout setting for this client.
        /// </summary>
        /// <param name="timeout">New response timeout value</param>
        void SetResponseTimeout(TimeSpan timeout);

        void SendRequest(GrainReference target, InvokeMethodRequest request, TaskCompletionSource<object> context, Action<Message, TaskCompletionSource<object>> callback, string debugContext = null, InvokeMethodOptions options = InvokeMethodOptions.None, string genericArguments = null);

        void ReceiveResponse(Message message);

        void Reset(bool cleanup);

        GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker);

        void DeleteObjectReference(IAddressable obj);
        
        Streams.IStreamProviderRuntime CurrentStreamProviderRuntime { get; }

        IGrainTypeResolver GrainTypeResolver { get; }

        SerializationManager SerializationManager { get; }

        IGrainReferenceRuntime GrainReferenceRuntime { get; }

        void BreakOutstandingMessagesToDeadSilo(SiloAddress deadSilo);
    }
}
