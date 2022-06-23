using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.BroadcastChannel.SubscriberTable
{
    internal class ImplicitChannelSubscriberTable
    {
        private readonly object _lockObj = new object();
        private readonly GrainBindingsResolver _bindings;
        private readonly IChannelNamespacePredicateProvider[] _providers;
        private readonly IServiceProvider _serviceProvider;
        private Cache _cache;

        public ImplicitChannelSubscriberTable(
            GrainBindingsResolver bindings,
            IEnumerable<IChannelNamespacePredicateProvider> providers,
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
            var newPredicates = new List<BroadcastChannelSubscriberPredicate>();

            foreach (var binding in bindings.Values)
            {
                foreach (var grainBinding in binding.Bindings)
                {
                    if (!grainBinding.TryGetValue(WellKnownGrainTypeProperties.BindingTypeKey, out var type)
                        || !string.Equals(type, WellKnownGrainTypeProperties.BroadcastChannelBindingTypeValue, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!grainBinding.TryGetValue(WellKnownGrainTypeProperties.BroadcastChannelBindingPatternKey, out var pattern))
                    {
                        throw new KeyNotFoundException(
                           $"Channel binding for grain type {binding.GrainType} is missing a \"{WellKnownGrainTypeProperties.BroadcastChannelBindingPatternKey}\" value");
                    }

                    IChannelNamespacePredicate predicate = null;
                    foreach (var provider in _providers)
                    {
                        if (provider.TryGetPredicate(pattern, out predicate)) break;
                    }

                    if (predicate is null)
                    {
                        throw new KeyNotFoundException(
                            $"Could not find an {nameof(IChannelNamespacePredicate)} for the pattern \"{pattern}\"."
                            + $" Ensure that a corresponding {nameof(IChannelNamespacePredicateProvider)} is registered");
                    }

                    if (!grainBinding.TryGetValue(WellKnownGrainTypeProperties.ChannelIdMapperKey, out var mapperName))
                    {
                        throw new KeyNotFoundException(
                           $"Channel binding for grain type {binding.GrainType} is missing a \"{WellKnownGrainTypeProperties.ChannelIdMapperKey}\" value");
                    }

                    var channelIdMapper = _serviceProvider.GetServiceByName<IChannelIdMapper>(string.IsNullOrWhiteSpace(mapperName) ? DefaultChannelIdMapper.Name : mapperName);
                    var subscriber = new BroadcastChannelSubscriber(binding, channelIdMapper);
                    newPredicates.Add(new BroadcastChannelSubscriberPredicate(subscriber, predicate));
                }
            }

            return new Cache(version, newPredicates);
        }

        /// <summary>
        /// Retrieve a map of implicit subscriptionsIds to implicit subscribers, given a channel ID. This method throws an exception if there's no namespace associated with the channel ID.
        /// </summary>
        /// <param name="channelId">A channel ID.</param>
        /// <param name="grainFactory">The grain factory used to get consumer references.</param>
        /// <returns>A set of references to implicitly subscribed grains. They are expected to support the broadcast channel consumer extension.</returns>
        /// <exception cref="ArgumentException">The channel ID doesn't have an associated namespace.</exception>
        /// <exception cref="InvalidOperationException">Internal invariant violation.</exception>
        internal IDictionary<Guid, IBroadcastChannelConsumerExtension> GetImplicitSubscribers(InternalChannelId channelId, IGrainFactory grainFactory)
        {
            if (!IsImplicitSubscribeEligibleNameSpace(channelId.GetNamespace()))
            {
                throw new ArgumentException("The channel ID doesn't have an associated namespace.", nameof(channelId));
            }

            var entries = GetOrAddImplicitSubscribers(channelId.GetNamespace());

            var result = new Dictionary<Guid, IBroadcastChannelConsumerExtension>();
            foreach (var entry in entries)
            {
                var consumer = MakeConsumerReference(grainFactory, channelId, entry);
                var subscriptionGuid = MakeSubscriptionGuid(entry.GrainType, channelId);
                if (result.ContainsKey(subscriptionGuid))
                {
                    throw new InvalidOperationException(
                        $"Internal invariant violation: generated duplicate subscriber reference: {consumer}, subscriptionId: {subscriptionGuid}");
                }
                result.Add(subscriptionGuid, consumer);
            }
            return result;
        }

        private HashSet<BroadcastChannelSubscriber> GetOrAddImplicitSubscribers(string channelNamespace)
        {
            var cache = GetCache();
            if (cache.Namespaces.TryGetValue(channelNamespace, out var result))
            {
                return result;
            }

            return cache.Namespaces[channelNamespace] = FindImplicitSubscribers(channelNamespace, cache.Predicates);
        }

        /// <summary>
        /// Determines whether the specified grain is an implicit subscriber of a given channel.
        /// </summary>
        /// <param name="grainId">The grain identifier.</param>
        /// <param name="channelId">The channel identifier.</param>
        /// <returns>true if the grain id describes an implicit subscriber of the channel described by the channel id.</returns>
        internal bool IsImplicitSubscriber(GrainId grainId, InternalChannelId channelId)
        {
            return HasImplicitSubscription(channelId.GetNamespace(), grainId.Type);
        }

        /// <summary>
        /// Try to get the implicit subscriptionId.
        /// If an implicit subscription exists, return a subscription Id that is unique per grain type, grainId, namespace combination.
        /// </summary>
        /// <param name="grainId"></param>
        /// <param name="channelId"></param>
        /// <param name="subscriptionId"></param>
        /// <returns></returns>
        internal bool TryGetImplicitSubscriptionGuid(GrainId grainId, InternalChannelId channelId, out Guid subscriptionId)
        {
            subscriptionId = Guid.Empty;

            if (!IsImplicitSubscriber(grainId, channelId))
            {
                return false;
            }

            // make subscriptionId
            subscriptionId = MakeSubscriptionGuid(grainId.Type, channelId);

            return true;
        }

        /// <summary>
        /// Create a subscriptionId that is unique per grainId, grainType, namespace combination.
        /// </summary>
        private Guid MakeSubscriptionGuid(GrainType grainType, InternalChannelId channelId)
        {
            // next 2 shorts inc guid are from namespace hash
            var namespaceHash = JenkinsHash.ComputeHash(channelId.GetNamespace());
            var namespaceHashByes = BitConverter.GetBytes(namespaceHash);
            var s1 = BitConverter.ToInt16(namespaceHashByes, 0);
            var s2 = BitConverter.ToInt16(namespaceHashByes, 2);

            // Tailing 8 bytes of the guid are from the hash of the channelId Guid and a hash of the provider name.
            // get channelId guid hash code
            var channelIdGuidHash = JenkinsHash.ComputeHash(channelId.ChannelId.Key.Span);
            // get provider name hash code
            var providerHash = JenkinsHash.ComputeHash(channelId.ProviderName);

            // build guid tailing 8 bytes from grainIdHash and the hash of the provider name.
            var tail = new List<byte>();
            tail.AddRange(BitConverter.GetBytes(channelIdGuidHash));
            tail.AddRange(BitConverter.GetBytes(providerHash));

            // make guid.
            // - First int is grain type
            // - Two shorts from namespace hash
            // - 8 byte tail from channelId Guid and provider name hash.
            var id = new Guid((int)JenkinsHash.ComputeHash(grainType.ToString()), s1, s2, tail.ToArray());
            var result = MarkSubscriptionGuid(id, isImplicitSubscription: true);
            return result;
        }

        internal static bool IsImplicitSubscribeEligibleNameSpace(string channelNameSpace)
        {
            return !string.IsNullOrWhiteSpace(channelNameSpace);
        }

        private bool HasImplicitSubscription(string channelNamespace, GrainType grainType)
        {
            if (!IsImplicitSubscribeEligibleNameSpace(channelNamespace))
            {
                return false;
            }

            var entry = GetOrAddImplicitSubscribers(channelNamespace);
            return entry.Any(e => e.GrainType == grainType);
        }

        /// <summary>
        /// Finds all implicit subscribers for the given channel namespace.
        /// </summary>
        private static HashSet<BroadcastChannelSubscriber> FindImplicitSubscribers(string channelNamespace, List<BroadcastChannelSubscriberPredicate> predicates)
        {
            var result = new HashSet<BroadcastChannelSubscriber>();
            foreach (var predicate in predicates)
            {
                if (predicate.Predicate.IsMatch(channelNamespace))
                {
                    result.Add(predicate.Subscriber);
                }
            }

            return result;
        }

        private static Guid MarkSubscriptionGuid(Guid subscriptionGuid, bool isImplicitSubscription)
        {
            byte[] guidBytes = subscriptionGuid.ToByteArray();
            if (isImplicitSubscription)
            {
                // set high bit of last byte
                guidBytes[guidBytes.Length - 1] = (byte)(guidBytes[guidBytes.Length - 1] | 0x80);
            }
            else
            {
                // clear high bit of last byte
                guidBytes[guidBytes.Length - 1] = (byte)(guidBytes[guidBytes.Length - 1] & 0x7f);
            }

            return new Guid(guidBytes);
        }

        /// <summary>
        /// Create a reference to a grain that we expect to support the broadcast channel consumer extension.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to get consumer references.</param>
        /// <param name="channelId">The channel ID to use for the grain ID construction.</param>
        /// <param name="channelSubscriber">The GrainBindings for the grain to create</param>
        /// <returns></returns>
        private IBroadcastChannelConsumerExtension MakeConsumerReference(
            IGrainFactory grainFactory,
            InternalChannelId channelId,
            BroadcastChannelSubscriber channelSubscriber)
        {
            var grainId = channelSubscriber.GetGrainId(channelId);
            return grainFactory.GetGrain<IBroadcastChannelConsumerExtension>(grainId);
        }

        private class BroadcastChannelSubscriberPredicate
        {
            public BroadcastChannelSubscriberPredicate(BroadcastChannelSubscriber subscriber, IChannelNamespacePredicate predicate)
            {
                Subscriber = subscriber;
                Predicate = predicate;
            }

            public BroadcastChannelSubscriber Subscriber { get; }
            public IChannelNamespacePredicate Predicate { get; }
        }

        private class BroadcastChannelSubscriber
        {
            public BroadcastChannelSubscriber(GrainBindings grainBindings, IChannelIdMapper channelIdMapper)
            {
                GrainBindings = grainBindings;
                this.channelIdMapper = channelIdMapper;
            }

            public GrainType GrainType => GrainBindings.GrainType;

            private GrainBindings GrainBindings { get; }

            private IChannelIdMapper channelIdMapper { get; }

            public override bool Equals(object obj)
            {
                return obj is BroadcastChannelSubscriber subscriber &&
                       GrainType.Equals(subscriber.GrainType);
            }

            public override int GetHashCode() => GrainType.GetHashCode();

            internal GrainId GetGrainId(InternalChannelId channelId)
            {
                var grainKeyId = channelIdMapper.GetGrainKeyId(GrainBindings, channelId);
                return GrainId.Create(GrainType, grainKeyId);
            }
        }

        private class Cache
        {
            public Cache(MajorMinorVersion version, List<BroadcastChannelSubscriberPredicate> predicates)
            {
                Version = version;
                Predicates = predicates;
                Namespaces = new ConcurrentDictionary<string, HashSet<BroadcastChannelSubscriber>>();
            }

            public MajorMinorVersion Version { get; }
            public ConcurrentDictionary<string, HashSet<BroadcastChannelSubscriber>> Namespaces { get; }
            public List<BroadcastChannelSubscriberPredicate> Predicates { get; }
        }
    }
}