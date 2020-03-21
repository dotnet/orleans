#define USE_STORAGE
//#define USE_CAST
#define COUNT_ACTIVATE_DEACTIVATE

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Providers;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class StreamLifecycleProducerInternalGrain : StreamLifecycleProducerGrain, IStreamLifecycleProducerInternalGrain
    {
        public StreamLifecycleProducerInternalGrain(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }

        public async Task TestInternalRemoveProducer(Guid streamId, string providerName)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("RemoveProducer StreamId={0} StreamProvider={1}", streamId, providerName);
            }

            if (!State.IsProducer) throw new InvalidOperationException("Not a Producer");

            // Whitebox testing
            var cleanup = State.Stream as IStreamControl;
            await cleanup.Cleanup(true, false);

            State.IsProducer = false;
#if USE_STORAGE
            await WriteStateAsync();
#endif
        }

        public async Task DoBadDeactivateNoClose()
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("DoBadDeactivateNoClose");

            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Suppressing Cleanup when Deactivate for stream {0}", State.Stream);
            StreamResourceTestControl.TestOnlySuppressStreamCleanupOnDeactivate = true;

            State.IsProducer = false;
            State.Stream = null;
#if USE_STORAGE
            await WriteStateAsync();
#endif

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Calling DeactivateOnIdle");
            base.DeactivateOnIdle();
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    internal class StreamLifecycleConsumerInternalGrain : StreamLifecycleConsumerGrain, IStreamLifecycleConsumerInternalGrain
    {
        public StreamLifecycleConsumerInternalGrain(ILoggerFactory loggerFactory, ISiloRuntimeClient runtimeClient, IStreamProviderRuntime streamProviderRuntime)
            : base(runtimeClient, streamProviderRuntime, loggerFactory)
        {
        }

        public virtual async Task TestBecomeConsumerSlim(Guid streamIdGuid, string providerName)
        {
            InitStream(streamIdGuid, null, providerName);
            var observer = new MyStreamObserver<int>(logger);

            //var subsHandle = await State.Stream.SubscribeAsync(observer);

            IStreamConsumerExtension myExtensionReference;
#if USE_CAST
            myExtensionReference = StreamConsumerExtensionFactory.Cast(this.AsReference());
#else
            var tup = await this.runtimeClient.BindExtension<StreamConsumerExtension, IStreamConsumerExtension>(
                        () => new StreamConsumerExtension(this.streamProviderRuntime));
            StreamConsumerExtension myExtension = tup.Item1;
            myExtensionReference = tup.Item2;
#endif
            string extKey = providerName + "_" + State.Stream.Namespace;
            IPubSubRendezvousGrain pubsub = GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamIdGuid, extKey, null);
            GuidId subscriptionId = GuidId.GetNewGuidId();
            await pubsub.RegisterConsumer(subscriptionId, ((StreamImpl<int>)State.Stream).StreamId, myExtensionReference, null);

            myExtension.SetObserver(subscriptionId, ((StreamImpl<int>)State.Stream), observer, null, null, null);
        }
    }
}