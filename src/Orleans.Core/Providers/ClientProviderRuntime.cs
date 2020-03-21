using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Streams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Providers
{
    internal class ClientProviderRuntime : IStreamProviderRuntime
    {
        private IStreamPubSub grainBasedPubSub;
        private IStreamPubSub implicitPubSub;
        private IStreamPubSub combinedGrainBasedAndImplicitPubSub;
        private StreamDirectory streamDirectory;
        private readonly Dictionary<Type, Tuple<IGrainExtension, IAddressable>> caoTable;
        private readonly AsyncLock lockable;
        private readonly IInternalGrainFactory grainFactory;
        private readonly IRuntimeClient runtimeClient;
        private readonly ILogger timerLogger;
        private readonly ClusterOptions clusterOptions;

        public ClientProviderRuntime(
            IInternalGrainFactory grainFactory,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            IOptions<ClusterOptions> clusterOptions)
        {
            this.clusterOptions = clusterOptions.Value;
            this.grainFactory = grainFactory;
            this.ServiceProvider = serviceProvider;
            this.runtimeClient = serviceProvider.GetService<IRuntimeClient>();
            caoTable = new Dictionary<Type, Tuple<IGrainExtension, IAddressable>>();
            lockable = new AsyncLock();
            //all async timer created through current class all share this logger for perf reasons
            this.timerLogger = loggerFactory.CreateLogger<AsyncTaskSafeTimer>();
        }

        public IGrainFactory GrainFactory => this.grainFactory;
        public IServiceProvider ServiceProvider { get; }

        public void StreamingInitialize(ImplicitStreamSubscriberTable implicitStreamSubscriberTable) 
        {
            if (null == implicitStreamSubscriberTable)
            {
                throw new ArgumentNullException(nameof(implicitStreamSubscriberTable));
            }
            grainBasedPubSub = new GrainBasedPubSubRuntime(GrainFactory);
            var tmp = new ImplicitStreamPubSub(this.grainFactory, implicitStreamSubscriberTable);
            implicitPubSub = tmp;
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

        public string ExecutingEntityIdentity()
        {
            return this.runtimeClient.CurrentActivationIdentity;
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
            return new AsyncTaskSafeTimer(this.timerLogger, asyncCallback, state, dueTime, period);
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
                    return implicitPubSub;
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
