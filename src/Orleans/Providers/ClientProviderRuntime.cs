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

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Streams;

using Orleans.Runtime;

namespace Orleans.Providers
{
    internal class ClientProviderRuntime : IStreamProviderRuntime
    {
        private IStreamPubSub pubSub;
        private StreamDirectory streamDirectory;
        private readonly Dictionary<Type, Tuple<IGrainExtension, IAddressable>> caoTable;
        private readonly AsyncLock lockable;

        public ClientProviderRuntime(IGrainFactory grainFactory) 
        {
            caoTable = new Dictionary<Type, Tuple<IGrainExtension, IAddressable>>();
            lockable = new AsyncLock();
            GrainFactory = grainFactory;
        }

        public IGrainFactory GrainFactory { get; private set; }

        public void StreamingInitialize(ImplicitStreamSubscriberTable implicitStreamSubscriberTable) 
        {
            if (null == implicitStreamSubscriberTable)
            {
                throw new ArgumentNullException("implicitStreamSubscriberTable");
            }
            pubSub = new StreamPubSubImpl(new GrainBasedPubSubRuntime(GrainFactory), implicitStreamSubscriberTable);
            streamDirectory = new StreamDirectory();
        }

        public StreamDirectory GetStreamDirectory()
        {
            return streamDirectory;
        }

        public async Task Reset()
        {
            if (streamDirectory != null)
            {
                var tmp = streamDirectory;
                streamDirectory = null; // null streamDirectory now, just to make sure we call cleanup only once, in all cases.
                await tmp.Cleanup(true, true);
            }
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

        public string SiloIdentity
        {
            get
            {
                throw new InvalidOperationException("Cannot access SiloIdentity from client.");
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
                    var obj = ((Orleans.GrainFactory)this.GrainFactory).CreateObjectReference<TExtensionInterface>(extension);

                    addressable = (IAddressable) await (Task<TExtensionInterface>) obj;

                    if (null == addressable)
                    {
                        throw new NullReferenceException("addressable");
                    }
                    entry = Tuple.Create((IGrainExtension)extension, addressable);
                    caoTable.Add(typeof(TExtensionInterface), entry);
                }
            }

            var typedAddressable = addressable.Cast<TExtensionInterface>();
            // we have to return the extension as well as the IAddressable because the caller needs to root the extension
            // to prevent it from being collected (the IAddressable uses a weak reference).
            return Tuple.Create(extension, typedAddressable);
        }

        public IStreamPubSub PubSub(StreamPubSubType pubSubType)
        {
            switch (pubSubType)
            {
                case StreamPubSubType.GrainBased:
                    return pubSub;
                default:
                    return null;
            }
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
    }
}