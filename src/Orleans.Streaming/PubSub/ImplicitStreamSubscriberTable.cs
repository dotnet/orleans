using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class ImplicitStreamSubscriberTable
    {
        private readonly object _lockObj = new object();
        private readonly GrainBindingsResolver _bindings;
        private readonly IStreamNamespacePredicateProvider[] _providers;
        private readonly IServiceProvider _serviceProvider;
        private Cache _cache;

        public ImplicitStreamSubscriberTable(
            GrainBindingsResolver bindings,
            IEnumerable<IStreamNamespacePredicateProvider> providers,
            IServiceProvider serviceProvider)
        {
            _bindings = bindings;
            var initialBindings = bindings.GetAllBindings();
            _providers = providers.ToArray();
            _serviceProvider = serviceProvider;
            _cache = BuildCache(initialBindings.Version, initialBindings.Bindings);
        }

        private Cache GetCache()
        {
            var cache = _cache;
            var bindings = _bindings.GetAllBindings();
            if (bindings.Version == cache.Version)
            {
                return cache;
            }

            lock (_lockObj)
            {
                bindings = _bindings.GetAllBindings();
                if (bindings.Version == cache.Version)
                {
                    return cache;
                }

                return _cache = BuildCache(bindings.Version, bindings.Bindings);
            }
        }

        private Cache BuildCache(MajorMinorVersion version, ImmutableDictionary<GrainType, GrainBindings> bindings)
        {
            var newPredicates = new List<StreamSubscriberPredicate>();

            foreach (var binding in bindings.Values)
            {
                foreach (var grainBinding in binding.Bindings)
                {
                    if (!grainBinding.TryGetValue(WellKnownGrainTypeProperties.BindingTypeKey, out var type)
                        || !string.Equals(type, WellKnownGrainTypeProperties.StreamBindingTypeValue, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!grainBinding.TryGetValue(WellKnownGrainTypeProperties.StreamBindingPatternKey, out var pattern))
                    {
                        throw new KeyNotFoundException(
                           $"Stream binding for grain type {binding.GrainType} is missing a \"{WellKnownGrainTypeProperties.StreamBindingPatternKey}\" value");
                    }

                    IStreamNamespacePredicate predicate = null;
                    foreach (var provider in _providers)
                    {
                        if (provider.TryGetPredicate(pattern, out predicate)) break;
                    }

                    if (predicate is null)
                    {
                        throw new KeyNotFoundException(
                            $"Could not find an {nameof(IStreamNamespacePredicate)} for the pattern \"{pattern}\"."
                            + $" Ensure that a corresponding {nameof(IStreamNamespacePredicateProvider)} is registered");
                    }

                    if (!grainBinding.TryGetValue(WellKnownGrainTypeProperties.StreamIdMapperKey, out var mapperName))
                    {
                        throw new KeyNotFoundException(
                           $"Stream binding for grain type {binding.GrainType} is missing a \"{WellKnownGrainTypeProperties.StreamIdMapperKey}\" value");
                    }
                    var streamIdMapper = _serviceProvider.GetServiceByName<IStreamIdMapper>(string.IsNullOrWhiteSpace(mapperName) ? DefaultStreamIdMapper.Name : mapperName);

                    var subscriber = new StreamSubscriber(binding, streamIdMapper);
                    newPredicates.Add(new StreamSubscriberPredicate(subscriber, predicate));
                }
            }

            return new Cache(version, newPredicates);
        }

        /// <summary>
        /// Retrieve a map of implicit subscriptionsIds to implicit subscribers, given a stream ID. This method throws an exception if there's no namespace associated with the stream ID.
        /// </summary>
        /// <param name="streamId">A stream ID.</param>
        /// <param name="grainFactory">The grain factory used to get consumer references.</param>
        /// <returns>A set of references to implicitly subscribed grains. They are expected to support the streaming consumer extension.</returns>
        /// <exception cref="System.ArgumentException">The stream ID doesn't have an associated namespace.</exception>
        /// <exception cref="System.InvalidOperationException">Internal invariant violation.</exception>
        internal IDictionary<Guid, IStreamConsumerExtension> GetImplicitSubscribers(InternalStreamId streamId, IInternalGrainFactory grainFactory)
        {
            if (!IsImplicitSubscribeEligibleNameSpace(streamId.GetNamespace()))
            {
                throw new ArgumentException("The stream ID doesn't have an associated namespace.", nameof(streamId));
            }

            var entries = GetOrAddImplicitSubscribers(streamId.GetNamespace());

            var result = new Dictionary<Guid, IStreamConsumerExtension>();
            foreach (var entry in entries)
            {
                var consumer = MakeConsumerReference(grainFactory, streamId, entry);
                Guid subscriptionGuid = MakeSubscriptionGuid(entry.GrainType, streamId);
                if (result.ContainsKey(subscriptionGuid))
                {
                    throw new InvalidOperationException(
                        $"Internal invariant violation: generated duplicate subscriber reference: {consumer}, subscriptionId: {subscriptionGuid}");
                }
                result.Add(subscriptionGuid, consumer);
            }
            return result;
        }

        private HashSet<StreamSubscriber> GetOrAddImplicitSubscribers(string streamNamespace)
        {
            var cache = GetCache();
            if (cache.Namespaces.TryGetValue(streamNamespace, out var result))
            {
                return result;
            }

            return cache.Namespaces[streamNamespace] = FindImplicitSubscribers(streamNamespace, cache.Predicates);
        }

        /// <summary>
        /// Determines whether the specified grain is an implicit subscriber of a given stream.
        /// </summary>
        /// <param name="grainId">The grain identifier.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns>true if the grain id describes an implicit subscriber of the stream described by the stream id.</returns>
        internal bool IsImplicitSubscriber(GrainId grainId, InternalStreamId streamId)
        {
            return HasImplicitSubscription(streamId.GetNamespace(), grainId.Type);
        }

        /// <summary>
        /// Try to get the implicit subscriptionId.
        /// If an implicit subscription exists, return a subscription Id that is unique per grain type, grainId, namespace combination.
        /// </summary>
        /// <param name="grainId"></param>
        /// <param name="streamId"></param>
        /// <param name="subscriptionId"></param>
        /// <returns></returns>
        internal bool TryGetImplicitSubscriptionGuid(GrainId grainId, InternalStreamId streamId, out Guid subscriptionId)
        {
            subscriptionId = Guid.Empty;

            if (!IsImplicitSubscriber(grainId, streamId))
            {
                return false;
            }

            // make subscriptionId
            subscriptionId = MakeSubscriptionGuid(grainId.Type, streamId);

            return true;
        }

        /// <summary>
        /// Create a subscriptionId that is unique per grainId, grainType, namespace combination.
        /// </summary>
        private Guid MakeSubscriptionGuid(GrainType grainType, InternalStreamId streamId)
        {
            // next 2 shorts inc guid are from namespace hash
            uint namespaceHash = JenkinsHash.ComputeHash(streamId.GetNamespace());
            byte[] namespaceHashByes = BitConverter.GetBytes(namespaceHash);
            short s1 = BitConverter.ToInt16(namespaceHashByes, 0);
            short s2 = BitConverter.ToInt16(namespaceHashByes, 2);

            // Tailing 8 bytes of the guid are from the hash of the streamId Guid and a hash of the provider name.
            // get streamId guid hash code
            uint streamIdGuidHash = JenkinsHash.ComputeHash(streamId.StreamId.Key.Span);
            // get provider name hash code
            uint providerHash = JenkinsHash.ComputeHash(streamId.ProviderName);

            // build guid tailing 8 bytes from grainIdHash and the hash of the provider name.
            var tail = new List<byte>();
            tail.AddRange(BitConverter.GetBytes(streamIdGuidHash));
            tail.AddRange(BitConverter.GetBytes(providerHash));

            // make guid.
            // - First int is grain type
            // - Two shorts from namespace hash
            // - 8 byte tail from streamId Guid and provider name hash.
            var id = new Guid((int)JenkinsHash.ComputeHash(grainType.ToString()), s1, s2, tail.ToArray());
            var result = SubscriptionMarker.MarkAsImplictSubscriptionId(id);
            return result;
        }

        internal static bool IsImplicitSubscribeEligibleNameSpace(string streamNameSpace)
        {
            return !string.IsNullOrWhiteSpace(streamNameSpace);
        }

        private bool HasImplicitSubscription(string streamNamespace, GrainType grainType)
        {
            if (!IsImplicitSubscribeEligibleNameSpace(streamNamespace))
            {
                return false;
            }

            var entry = GetOrAddImplicitSubscribers(streamNamespace);
            return entry.Any(e => e.GrainType == grainType);
        }

        /// <summary>
        /// Finds all implicit subscribers for the given stream namespace.
        /// </summary>
        private static HashSet<StreamSubscriber> FindImplicitSubscribers(string streamNamespace, List<StreamSubscriberPredicate> predicates)
        {
            var result = new HashSet<StreamSubscriber>();
            foreach (var predicate in predicates)
            {
                if (predicate.Predicate.IsMatch(streamNamespace))
                {
                    result.Add(predicate.Subscriber);
                }
            }

            return result;
        }

        /// <summary>
        /// Create a reference to a grain that we expect to support the stream consumer extension.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to get consumer references.</param>
        /// <param name="streamId">The stream ID to use for the grain ID construction.</param>
        /// <param name="streamSubscriber">The GrainBindings for the grain to create</param>
        /// <returns></returns>
        private IStreamConsumerExtension MakeConsumerReference(
            IInternalGrainFactory grainFactory,
            InternalStreamId streamId,
            StreamSubscriber streamSubscriber)
        {
            var grainId = streamSubscriber.GetGrainId(streamId);
            return grainFactory.GetGrain<IStreamConsumerExtension>(grainId);
        }

        private class StreamSubscriberPredicate
        {
            public StreamSubscriberPredicate(StreamSubscriber subscriber, IStreamNamespacePredicate predicate)
            {
                this.Subscriber = subscriber;
                this.Predicate = predicate;
            }

            public StreamSubscriber Subscriber { get; }
            public IStreamNamespacePredicate Predicate { get; }
        }

        private class StreamSubscriber
        {
            public StreamSubscriber(GrainBindings grainBindings, IStreamIdMapper streamIdMapper)
            {
                this.grainBindings = grainBindings;
                this.streamIdMapper = streamIdMapper;
            }

            public GrainType GrainType => this.grainBindings.GrainType;

            private GrainBindings grainBindings { get; }

            private IStreamIdMapper streamIdMapper { get; }

            public override bool Equals(object obj)
            {
                return obj is StreamSubscriber subscriber &&
                       this.GrainType.Equals(subscriber.GrainType);
            }

            public override int GetHashCode() => GrainType.GetHashCode();

            internal GrainId GetGrainId(InternalStreamId streamId)
            {
                var grainKeyId = this.streamIdMapper.GetGrainKeyId(this.grainBindings, streamId);
                return GrainId.Create(this.GrainType, grainKeyId);
            }
        }

        private class Cache
        {
            public Cache(MajorMinorVersion version, List<StreamSubscriberPredicate> predicates)
            {
                this.Version = version;
                this.Predicates = predicates;
                this.Namespaces = new ConcurrentDictionary<string, HashSet<StreamSubscriber>>(); 
            }

            public MajorMinorVersion Version { get; }
            public ConcurrentDictionary<string, HashSet<StreamSubscriber>> Namespaces { get; }
            public List<StreamSubscriberPredicate> Predicates { get; }
        }
    }
}