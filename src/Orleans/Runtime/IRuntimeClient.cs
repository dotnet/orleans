using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.CodeGeneration;

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
        /// Provides client application code with access to an Orleans logger.
        /// </summary>
        Logger AppLogger { get; }

        /// <summary>
        /// A unique identifier for the current client.
        /// There is no semantic content to this string, but it may be useful for logging.
        /// </summary>
        string CurrentActivationIdentity { get; }

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

        Task ExecAsync(Func<Task> asyncFunction, ISchedulingContext context, string activityName);

        void Reset(bool cleanup);

        GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker);

        void DeleteObjectReference(IAddressable obj);
        
        Streams.IStreamProviderManager CurrentStreamProviderManager { get; }

        Streams.IStreamProviderRuntime CurrentStreamProviderRuntime { get; }

        IGrainTypeResolver GrainTypeResolver { get; }
        
        void BreakOutstandingMessagesToDeadSilo(SiloAddress deadSilo);
    }
}
