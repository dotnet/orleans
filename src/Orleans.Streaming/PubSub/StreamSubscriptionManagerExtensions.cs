using Orleans.Streams.Core;
using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams.PubSub
{
    public static class StreamSubscriptionManagerExtensions
    {
        public static Task<StreamSubscription> AddSubscription<TGrainInterface>(
            this IStreamSubscriptionManager manager,
            IGrainFactory grainFactory,
            StreamId streamId,
            string streamProviderName,
            Guid primaryKey,
            string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey
        {
            var grainRef = grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix) as GrainReference;
            return manager.AddSubscription(streamProviderName, streamId, grainRef);
        }

        public static Task<StreamSubscription> AddSubscription<TGrainInterface>(
            this IStreamSubscriptionManager manager,
            IGrainFactory grainFactory,
            StreamId streamId,
            string streamProviderName,
            long primaryKey,
            string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey
        {
            var grainRef = grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix) as GrainReference;
            return manager.AddSubscription(streamProviderName, streamId, grainRef);
        }

        public static Task<StreamSubscription> AddSubscription<TGrainInterface>(
            this IStreamSubscriptionManager manager,
            IGrainFactory grainFactory,
            StreamId streamId,
            string streamProviderName,
            string primaryKey,
            string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            var grainRef = grainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix) as GrainReference;
            return manager.AddSubscription(streamProviderName, streamId, grainRef);
        }

        public static Task<StreamSubscription> AddSubscription<TGrainInterface>(
            this IStreamSubscriptionManager manager,
            IGrainFactory grainFactory,
            StreamId streamId,
            string streamProviderName,
            Guid primaryKey,
            string keyExtension,
            string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            var grainRef = grainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix) as GrainReference;
            return manager.AddSubscription(streamProviderName, streamId, grainRef);
        }

        public static Task<StreamSubscription> AddSubscription<TGrainInterface>(
            this IStreamSubscriptionManager manager,
            IGrainFactory grainFactory,
            StreamId streamId,
            string streamProviderName,
            long primaryKey,
            string keyExtension,
            string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
        {
            var grainRef = grainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix) as GrainReference;
            return manager.AddSubscription(streamProviderName, streamId, grainRef);
        }

        public static bool TryGetStreamSubscrptionManager(this IStreamProvider streamProvider, out IStreamSubscriptionManager manager)
        {
            manager = null;
            if (streamProvider is IStreamSubscriptionManagerRetriever)
            {
                var streamSubManagerRetriever = streamProvider as IStreamSubscriptionManagerRetriever;
                manager = streamSubManagerRetriever.GetStreamSubscriptionManager();
                //implicit only stream provider don't have a subscription manager configured 
                //so manager can be null;
                return manager != null;
            }
            return false;
        }
    }

}
