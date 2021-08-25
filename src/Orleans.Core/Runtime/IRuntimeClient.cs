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

    public class OnDeserializedCallbacks : DeserializationContext
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IRuntimeClient _runtimeClient;

        public OnDeserializedCallbacks(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _runtimeClient = serviceProvider.GetRequiredService<IRuntimeClient>();
        }

        public override IServiceProvider ServiceProvider => _serviceProvider;

        public override object RuntimeClient => _runtimeClient;

        public void OnDeserialized(IOnDeserialized value) => value.OnDeserialized(this);
    }
}
