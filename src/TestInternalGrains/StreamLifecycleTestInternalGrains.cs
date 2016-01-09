#define USE_STORAGE
//#define USE_CAST
#define COUNT_ACTIVATE_DEACTIVATE

using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Providers;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class StreamLifecycleProducerInternalGrain : StreamLifecycleProducerGrain, IStreamLifecycleProducerInternalGrain
    {
        public async Task TestInternalRemoveProducer(Guid streamId, string providerName)
        {
            if (logger.IsVerbose)
                logger.Verbose("RemoveProducer StreamId={0} StreamProvider={1}", streamId, providerName);
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
            if (logger.IsVerbose)
                logger.Verbose("DoBadDeactivateNoClose");

            if (logger.IsVerbose)
                logger.Verbose("Suppressing Cleanup when Deactivate for stream {0}", State.Stream);
            StreamResourceTestControl.TestOnlySuppressStreamCleanupOnDeactivate = true;

            State.IsProducer = false;
            State.Stream = null;
#if USE_STORAGE
            await WriteStateAsync();
#endif

            if (logger.IsVerbose) logger.Verbose("Calling DeactivateOnIdle");
            base.DeactivateOnIdle();
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class StreamLifecycleConsumerInternalGrain : StreamLifecycleConsumerGrain, IStreamLifecycleConsumerInternalGrain
    {
       public virtual async Task TestBecomeConsumerSlim(Guid streamIdGuid, string providerName)
        {
            InitStream(streamIdGuid, null, providerName);
            var observer = new MyStreamObserver<int>(logger);

            //var subsHandle = await State.Stream.SubscribeAsync(observer);

            IStreamConsumerExtension myExtensionReference;
#if USE_CAST
            myExtensionReference = StreamConsumerExtensionFactory.Cast(this.AsReference());
#else
            var tup = await SiloProviderRuntime.Instance.BindExtension<StreamConsumerExtension, IStreamConsumerExtension>(
                        () => new StreamConsumerExtension(SiloProviderRuntime.Instance));
            StreamConsumerExtension myExtension = tup.Item1;
            myExtensionReference = tup.Item2;
#endif
            string extKey = providerName + "_" + State.Stream.Namespace;
            IPubSubRendezvousGrain pubsub = GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamIdGuid, extKey, null);
            GuidId subscriptionId = GuidId.GetNewGuidId();
            await pubsub.RegisterConsumer(subscriptionId, ((StreamImpl<int>)State.Stream).StreamId, myExtensionReference, null);

            myExtension.SetObserver(subscriptionId, ((StreamImpl<int>)State.Stream), observer, null, null);
        }
    }
}