using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Storage;
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
        GrainFactory InternalGrainFactory { get; }

        /// <summary>
        /// Provides client application code with access to an Orleans logger.
        /// </summary>
        Logger AppLogger { get; }

        /// <summary>
        /// A unique identifier for the current client.
        /// There is no semantic content to this string, but it may be useful for logging.
        /// </summary>
        string Identity { get; }

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

        /// <summary>
        /// Return the currently storage provider configured for this grain, or null if no storage provider configured for this grain.
        /// </summary>
        /// <exception cref="InvalidOperationException">If called from outside grain class</exception>
        IStorageProvider CurrentStorageProvider { get; }

        Task<IGrainReminder> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period);

        Task UnregisterReminder(IGrainReminder reminder);

        Task<IGrainReminder> GetReminder(string reminderName);

        Task<List<IGrainReminder>> GetReminders();

        Task ExecAsync(Func<Task> asyncFunction, ISchedulingContext context, string activityName);

        void Reset();

        GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker);

        void DeleteObjectReference(IAddressable obj);

        IActivationData CurrentActivationData { get; }

        ActivationAddress CurrentActivationAddress { get; }

        SiloAddress CurrentSilo { get; }

        void DeactivateOnIdle(ActivationId id);

        Streams.IStreamProviderManager CurrentStreamProviderManager { get; }

        Streams.IStreamProviderRuntime CurrentStreamProviderRuntime { get; }

        IGrainTypeResolver GrainTypeResolver { get; }

        string CaptureRuntimeEnvironment();

        IGrainMethodInvoker GetInvoker(int interfaceId, string genericGrainType = null);

        SiloStatus GetSiloStatus(SiloAddress siloAddress);

        void BreakOutstandingMessagesToDeadSilo(SiloAddress deadSilo);
    }
}
