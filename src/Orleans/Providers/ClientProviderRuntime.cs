using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers
{
    internal class ClientProviderRuntime : IStreamProviderRuntime
    {
        private IStreamPubSub grainBasedPubSub;
        private IStreamPubSub implictPubSub;
        private IStreamPubSub combinedGrainBasedAndImplicitPubSub;
        private StreamDirectory streamDirectory;
        private readonly Dictionary<Type, Tuple<IGrainExtension, IAddressable>> caoTable;
        private readonly AsyncLock lockable;
        private InvokeInterceptor invokeInterceptor;
        private readonly IRuntimeClient runtimeClient;

        public ClientProviderRuntime(IGrainFactory grainFactory, IServiceProvider serviceProvider) 
        {
            this.runtimeClient = RuntimeClient.Current;
            caoTable = new Dictionary<Type, Tuple<IGrainExtension, IAddressable>>();
            lockable = new AsyncLock();
            GrainFactory = grainFactory;
            ServiceProvider = serviceProvider;
        }

        public IGrainFactory GrainFactory { get; private set; }
        public IServiceProvider ServiceProvider { get; private set; }
        public void SetInvokeInterceptor(InvokeInterceptor interceptor)
        {
            this.invokeInterceptor = interceptor;
        }

        public InvokeInterceptor GetInvokeInterceptor()
        {
            return this.invokeInterceptor;
        }

        public void StreamingInitialize(ImplicitStreamSubscriberTable implicitStreamSubscriberTable) 
        {
            if (null == implicitStreamSubscriberTable)
            {
                throw new ArgumentNullException("implicitStreamSubscriberTable");
            }
            grainBasedPubSub = new GrainBasedPubSubRuntime(GrainFactory);
            var tmp = new ImplicitStreamPubSub(implicitStreamSubscriberTable);
            implictPubSub = tmp;
            combinedGrainBasedAndImplicitPubSub = new StreamPubSubImpl(grainBasedPubSub, tmp);
            streamDirectory = new StreamDirectory();
        }

        public StreamDirectory GetStreamDirectory()
        {
            return streamDirectory;
        }

        public async Task Reset(bool cleanup = true)
        {
            if (streamDirectory != null)
            {
                var tmp = streamDirectory;
                streamDirectory = null; // null streamDirectory now, just to make sure we call cleanup only once, in all cases.
                if (cleanup)
                {
                    await tmp.Cleanup(true, true);
                }
            }
        }

        public Logger GetLogger(string loggerName)
        {
            return LogManager.GetLogger(loggerName, LoggerType.Provider);
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
            return this.runtimeClient.CurrentActivationIdentity;
        }

        public SiloAddress ExecutingSiloAddress
        {
            get { throw new NotImplementedException(); }
        }

        public void RegisterSystemTarget(ISystemTarget target)
        {
            throw new NotImplementedException();
        }

        public void UnregisterSystemTarget(ISystemTarget target)
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
                    var obj = ((GrainFactory)this.GrainFactory).CreateObjectReference<TExtensionInterface>(extension);

                    addressable = obj;
                     
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
                case StreamPubSubType.ExplicitGrainBasedAndImplicit:
                    return combinedGrainBasedAndImplicitPubSub;
                case StreamPubSubType.ExplicitGrainBasedOnly:
                    return grainBasedPubSub;
                case StreamPubSubType.ImplicitOnly:
                    return implictPubSub;
                default:
                    return null;
            }
        }

        public IConsistentRingProviderForGrains GetConsistentRingProvider(int mySubRangeIndex, int numSubRanges)
        {
            throw new NotImplementedException("GetConsistentRingProvider");
        }
    }
}
