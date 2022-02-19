using Orleans.Streams.Core;
using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams.PubSub
{
    /// <summary>
    /// Extension methods for <see cref="IStreamSubscriptionManager"/>.
    /// </summary>
    public static class StreamSubscriptionManagerExtensions
    {
        /// <summary>
        /// Subscribes the specified grain to the specified stream.
        /// </summary>
        /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
        /// <param name="manager">The manager.</param>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="grainId">The grain to subscribe.</param>
        /// <returns>The newly added subscription.</returns>
        public static Task<StreamSubscription> AddSubscription<TGrainInterface>(
            this IStreamSubscriptionManager manager,
            IGrainFactory grainFactory,
            StreamId streamId,
            string streamProviderName,
            GrainId grainId)
            where TGrainInterface : IGrainWithGuidKey
        {
            var grainRef = grainFactory.GetGrain(grainId) as GrainReference;
            return manager.AddSubscription(streamProviderName, streamId, grainRef);
        }

        /// <summary>
        /// Subscribes the specified grain to the specified stream.
        /// </summary>
        /// <typeparam name="TGrainInterface">An interface which the grain is the primary implementation of.</typeparam>
        /// <param name="manager">The manager.</param>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="primaryKey">The grain's primary key.</param>
        /// <param name="grainClassNamePrefix">The grain class name prefix.</param>
        /// <returns>The newly added subscription.</returns>
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

        /// <summary>
        /// Subscribes the specified grain to the specified stream.
        /// </summary>
        /// <typeparam name="TGrainInterface">An interface which the grain is the primary implementation of.</typeparam>
        /// <param name="manager">The manager.</param>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="primaryKey">The grain's primary key.</param>
        /// <param name="grainClassNamePrefix">The grain class name prefix.</param>
        /// <returns>The newly added subscription.</returns>
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

        /// <summary>
        /// Subscribes the specified grain to the specified stream.
        /// </summary>
        /// <typeparam name="TGrainInterface">An interface which the grain is the primary implementation of.</typeparam>
        /// <param name="manager">The manager.</param>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="primaryKey">The grain's primary key.</param>
        /// <param name="grainClassNamePrefix">The grain class name prefix.</param>
        /// <returns>The newly added subscription.</returns>
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

        /// <summary>
        /// Subscribes the specified grain to the specified stream.
        /// </summary>
        /// <typeparam name="TGrainInterface">An interface which the grain is the primary implementation of.</typeparam>
        /// <param name="manager">The manager.</param>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="primaryKey">The grain's primary key.</param>
        /// <param name="keyExtension">The grain's key extension.</param>
        /// <param name="grainClassNamePrefix">The grain class name prefix.</param>
        /// <returns>The newly added subscription.</returns>
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

        /// <summary>
        /// Subscribes the specified grain to the specified stream.
        /// </summary>
        /// <typeparam name="TGrainInterface">An interface which the grain is the primary implementation of.</typeparam>
        /// <param name="manager">The manager.</param>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="primaryKey">The grain's primary key.</param>
        /// <param name="keyExtension">The grain's key extension.</param>
        /// <param name="grainClassNamePrefix">The grain class name prefix.</param>
        /// <returns>The newly added subscription.</returns>
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

        /// <summary>
        /// Returns the <see cref="IStreamSubscriptionManager"/> for the provided stream provider.
        /// </summary>
        /// <param name="streamProvider">The stream provider.</param>
        /// <param name="manager">The manager.</param>
        /// <returns><see langword="true" /> if the stream subscription manager could be retrieved, <see langword="false" /> otherwise.</returns>
        public static bool TryGetStreamSubscriptionManager(this IStreamProvider streamProvider, out IStreamSubscriptionManager manager)
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
