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
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;

using Orleans.Streams;

using Orleans.Runtime;

namespace Orleans.Providers
{
    internal class ClientProviderRuntime : IProviderRuntime, IStreamProviderRuntime
    { 
        private IStreamPubSub pubSub;
        private StreamDirectory streamDirectory;
        private readonly Dictionary<Type, Tuple<IGrainExtension, IAddressable>> caoTable;
        private readonly AsyncLock lockable;

        private ClientProviderRuntime() 
        {
            caoTable = new Dictionary<Type, Tuple<IGrainExtension, IAddressable>>();
            lockable = new AsyncLock();
        }

        public static ClientProviderRuntime Instance { get; private set; }

        public static void InitializeSingleton() 
        {
            if (Instance != null)
            {
                UninitializeSingleton();
            }
            Instance = new ClientProviderRuntime();
        }

        public static void StreamingInitialize(ImplicitStreamSubscriberTable implicitStreamSubscriberTable) 
        {
            if (null == implicitStreamSubscriberTable)
            {
                throw new ArgumentNullException("implicitStreamSubscriberTable");
            }
            Instance.pubSub = new StreamPubSubImpl(new GrainBasedPubSubRuntime(), implicitStreamSubscriberTable);
            Instance.streamDirectory = new StreamDirectory();
        }

        public StreamDirectory GetStreamDirectory()
        {
            return streamDirectory;
        }

        public static void UninitializeSingleton()
        {
            Instance = null;
        }

        public Logger GetLogger(string loggerName)
        {
            return TraceLogger.GetLogger(loggerName, TraceLogger.LoggerType.Provider);
        }

        public Guid ServiceId
        {
            get
            {
                // Note: In theory nobody should be requesting ServcieId from client so might want to throw exception in this case, 
                // but several PersistenceProvider_Azure_* test cases in PersistenceProviderTests.cs 
                // are testing Azure provider in standalone mode which currently looks like access from "client", 
                // so we return default value here instead of throw exception.
                //
                return Guid.Empty;
            }
        }

        public string ExecutingEntityIdentity()
        {
            return RuntimeClient.Current.Identity;
        }

        public SiloAddress ExecutingSiloAddress
        {
            get { throw new NotImplementedException(); }
        }

        public void RegisterSystemTarget(ISystemTarget target)
        {
            throw new NotImplementedException();
        }

        public void UnRegisterSystemTarget(ISystemTarget target)
        {
            throw new NotImplementedException();
        }

        public IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            return new AsyncTaskSafeTimer(asyncCallback, state, dueTime, period);
        }

        public async Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension
        {
            IAddressable addressable;
            TExtension extension;

            // until we have a means to get the factory related to a grain interface, we have to search linearly for the factory. 
            var factoryName = String.Format("{0}.{1}Factory", typeof(TExtensionInterface).Namespace, typeof(TExtensionInterface).Name.Substring(1)); // skip the I
            var factoryType = TypeUtils.ResolveType(factoryName);


            using (await lockable.LockAsync())
            {
                Tuple<IGrainExtension, IAddressable> entry;
                if (caoTable.TryGetValue(typeof(TExtensionInterface), out entry))
                {
                    extension = (TExtension)entry.Item1;
                    addressable = entry.Item2;
                }
                else
                {
                    extension = newExtensionFunc();
                    var obj = factoryType.InvokeMember("CreateObjectReference", 
                        BindingFlags.Default | BindingFlags.InvokeMethod, 
                        null, null, new object[]{ extension }, CultureInfo.InvariantCulture);
                    addressable = (IAddressable) await (Task<TExtensionInterface>) obj;

                    if (null == addressable)
                    {
                        throw new NullReferenceException("addressable");
                    }
                    entry = Tuple.Create((IGrainExtension)extension, addressable);
                    caoTable.Add(typeof(TExtensionInterface), entry);
                }
            }

            var typedAddressable = (TExtensionInterface) GrainClient.InvokeStaticMethodThroughReflection(
                 typeof(TExtensionInterface).Assembly.FullName,
                 factoryName,
                 "Cast",
                 new Type[] { typeof(IAddressable) },
                 new object[] { addressable });
            // we have to return the extension as well as the IAddressable because the caller needs to root the extension
            // to prevent it from being collected (the IAddressable uses a weak reference).
            return Tuple.Create(extension, typedAddressable);
        }

        public IStreamPubSub PubSub(StreamPubSubType pubSubType)
        {
            return pubSubType == StreamPubSubType.GrainBased ? pubSub : null;
        }

        public IConsistentRingProviderForGrains GetConsistentRingProvider(int mySubRangeIndex, int numSubRanges)
        {
            throw new NotImplementedException("GetConsistentRingProvider");
        }

        public bool InSilo { get { return false; } }

        public Task InvokeWithinSchedulingContextAsync(Func<Task> asyncFunc, object context)
        {
            if (context != null)
                throw new ArgumentException("The grain client only supports a null scheduling context.");
            return Task.Run(asyncFunc);
        }

        public object GetCurrentSchedulingContext()
        {
            return null;
        }

        public Task StartPullingAgents(
            string streamProviderName,
            StreamQueueBalancerType balancerType,
            IQueueAdapter queueAdapter,
            TimeSpan getQueueMsgsTimerPeriod,
            TimeSpan initQueueTimeout)
        {
            return TaskDone.Done;
        }
    }
}