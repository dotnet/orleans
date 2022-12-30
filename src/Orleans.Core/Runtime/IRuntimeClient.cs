using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

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
        /// Get the current response timeout setting for this client.
        /// </summary>
        /// <returns>Response timeout value</returns>
        TimeSpan GetResponseTimeout();

        /// <summary>
        /// Sets the current response timeout setting for this client.
        /// </summary>
        /// <param name="timeout">New response timeout value</param>
        void SetResponseTimeout(TimeSpan timeout);

        void SendRequest(GrainReference target, IInvokable request, IResponseCompletionSource context, InvokeMethodOptions options);

        void SendResponse(Message request, Response response);

        void ReceiveResponse(Message message);

        IAddressable CreateObjectReference(IAddressable obj);

        void DeleteObjectReference(IAddressable obj);

        IGrainReferenceRuntime GrainReferenceRuntime { get; }

        void BreakOutstandingMessagesToDeadSilo(SiloAddress deadSilo);
    }

    /// <summary>
    /// Helper class used to invoke <see cref="IOnDeserialized.OnDeserialized"/> on objects which implement <see cref="IOnDeserialized"/>, immediately after deserialization.
    /// </summary>
    public class OnDeserializedCallbacks : DeserializationContext
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OnDeserializedCallbacks"/> class.
        /// </summary>
        /// <param name="serviceProvider">
        /// The service provider.
        /// </param>
        public OnDeserializedCallbacks(IServiceProvider serviceProvider)
        {
            ServiceProvider = serviceProvider;
            RuntimeClient = serviceProvider.GetRequiredService<IRuntimeClient>();
        }

        /// <summary>
        /// Gets the service provider.
        /// </summary>
        public override IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Gets the runtime client.
        /// </summary>
        public override object RuntimeClient { get; }

        /// <summary>
        /// The hook method invoked by the serialization infrastructure.
        /// </summary>
        /// <param name="value">The value which was deserialized.</param>
        public void OnDeserialized(IOnDeserialized value) => value.OnDeserialized(this);
    }
}
