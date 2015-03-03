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
using Orleans.Runtime;
using Orleans.Providers;

namespace Orleans.Streams
{
    /// <summary>
    /// Provider-facing interface for manager of streaming providers
    /// </summary>
    internal interface IStreamProviderRuntime : IProviderRuntime
    {
        /// <summary>
        /// Retrieves the opaque identity of currently executing grain or client object. 
        /// Just for logging purposes.
        /// </summary>
        /// <param name="handler"></param>
        string ExecutingEntityIdentity();

        SiloAddress ExecutingSiloAddress { get; }

        StreamDirectory GetStreamDirectory();

        void RegisterSystemTarget(ISystemTarget target);

        void UnRegisterSystemTarget(ISystemTarget target);

        /// <summary>
        /// Register a timer to send regular callbacks to this grain.
        /// This timer will keep the current grain from being deactivated.
        /// </summary>
        /// <param name="callback"></param>
        /// <param name="state"></param>
        /// <param name="dueTime"></param>
        /// <param name="period"></param>
        /// <returns></returns>
        IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period);

        /// <summary>
        /// Binds an extension to an addressable object, if not already done.
        /// </summary>
        /// <typeparam name="TExtension">The type of the extension (e.g. StreamConsumerExtension).</typeparam>
        /// <param name="newExtensionFunc">A factory function that constructs a new extension object.</param>
        /// <returns>A tuple, containing first the extension and second an addressable reference to the extension's interface.</returns>
        Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension;

        /// <summary>
        /// A Pub Sub runtime interface.
        /// </summary>
        /// <returns></returns>
        IStreamPubSub PubSub(StreamPubSubType pubSubType);

        /// <summary>
        /// A consistent ring interface.
        /// </summary>
        /// <param name="numSubRanges">Total number of sub ranges within this silo range.</param>
        /// <returns></returns>
        IConsistentRingProviderForGrains GetConsistentRingProvider(int mySubRangeIndex, int numSubRanges);

        /// <summary>
        /// Return true if this runtime executes inside silo, false otherwise (on the client).
        /// </summary>
        /// <param name="pubSubType"></param>
        /// <returns></returns>
        bool InSilo { get; }

        /// <summary>
        /// Invoke the given async function from within a valid Orleans scheduler context.
        /// </summary>
        /// <param name="asyncFunc"></param>
        Task InvokeWithinSchedulingContextAsync(Func<Task> asyncFunc, object context);

        object GetCurrentSchedulingContext();

        /// <summary>
        /// Start the pulling agents for a given persistent stream provider.
        /// </summary>
        /// <param name="streamProviderName"></param>
        /// <param name="balancerType"></param>
        /// <param name="queueAdapter"></param>
        /// <param name="getQueueMsgsTimerPeriod"></param>
        /// <param name="initQueueTimeout"></param>
        /// <returns></returns>
        Task StartPullingAgents(
            string streamProviderName,
            StreamQueueBalancerType balancerType,
            IQueueAdapter queueAdapter,
            TimeSpan getQueueMsgsTimerPeriod,
            TimeSpan initQueueTimeout);
    }

    internal enum StreamPubSubType
    {
        GrainBased
    }

    internal interface IStreamPubSub // Compare with: IPubSubRendezvousGrain
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer);

        Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer);

        Task RegisterConsumer(StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, StreamSequenceToken token, IStreamFilterPredicateWrapper filter);

        Task UnregisterConsumer(StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer);

        Task<int> ProducerCount(Guid streamId, string streamProvider, string streamNamespace);

        Task<int> ConsumerCount(Guid streamId, string streamProvider, string streamNamespace);
    }
}