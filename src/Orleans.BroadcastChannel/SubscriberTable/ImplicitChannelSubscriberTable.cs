using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
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
                        || type != WellKnownGrainTypeProperties.BroadcastChannelBindingTypeValue)
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
        internal Dictionary<Guid, IBroadcastChannelConsumerExtension> GetImplicitSubscribers(InternalChannelId channelId, IGrainFactory grainFactory)
        {
            var channelNamespace = channelId.GetNamespace();
            if (string.IsNullOrWhiteSpace(channelNamespace))
            {
                throw new ArgumentException("The channel ID doesn't have an associated namespace.", nameof(channelId));
            }

            var entries = GetOrAddImplicitSubscribers(channelNamespace);

            var result = new Dictionary<Guid, IBroadcastChannelConsumerExtension>();
            foreach (var entry in entries)
            {
                var consumer = MakeConsumerReference(grainFactory, channelId, entry);
                var subscriptionGuid = MakeSubscriptionGuid(entry.GrainType, channelId);
                CollectionsMarshal.GetValueRefOrAddDefault(result, subscriptionGuid, out var duplicate) = consumer;
                if (duplicate)
                {
                    throw new InvalidOperationException(
                        $"Internal invariant violation: generated duplicate subscriber reference: {consumer}, subscriptionId: {subscriptionGuid}");
                }
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

            return cache.Namespaces.GetOrAdd(channelNamespace, FindImplicitSubscribers(channelNamespace, cache.Predicates));
        }

        /// <summary>
        /// Create a subscriptionId that is unique per grainId, grainType, namespace combination.
        /// </summary>
        private Guid MakeSubscriptionGuid(GrainType grainType, InternalChannelId channelId)
        {
            Span<byte> bytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, grainType.GetUniformHashCode());
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], channelId.ChannelId.GetUniformHashCode());
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], channelId.ChannelId.GetKeyIndex());
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], StableHash.ComputeHash(channelId.ProviderName));
            bytes[15] |= 0x80; // set high bit of last byte (implicit subscription)
            return new(bytes);
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

        private sealed class BroadcastChannelSubscriber : IEquatable<BroadcastChannelSubscriber>
        {
            public BroadcastChannelSubscriber(GrainBindings grainBindings, IChannelIdMapper channelIdMapper)
            {
                GrainBindings = grainBindings;
                this.channelIdMapper = channelIdMapper;
            }

            public GrainType GrainType => GrainBindings.GrainType;

            private GrainBindings GrainBindings { get; }

            private IChannelIdMapper channelIdMapper { get; }

            public override bool Equals(object obj) => Equals(obj as BroadcastChannelSubscriber);

            public bool Equals(BroadcastChannelSubscriber other) => other != null && GrainType.Equals(other.GrainType);

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