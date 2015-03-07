/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
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

        Task ExecAsync(Func<Task> asyncFunction, ISchedulingContext context);

        void Reset();

        GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker);

        void DeleteObjectReference(IAddressable obj);

        IActivationData CurrentActivationData { get; }

        ActivationAddress CurrentActivationAddress { get; }

        SiloAddress CurrentSilo { get; }

        void DeactivateOnIdle(ActivationId id);

        Streams.IStreamProviderManager CurrentStreamProviderManager { get; }

        IGrainTypeResolver GrainTypeResolver { get; }

        string CaptureRuntimeEnvironment();

        IGrainMethodInvoker GetInvoker(int interfaceId, string genericGrainType = null);

        SiloStatus GetSiloStatus(SiloAddress siloAddress);
    }
}
