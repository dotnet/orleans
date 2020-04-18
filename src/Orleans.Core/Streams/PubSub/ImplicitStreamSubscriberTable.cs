using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    internal class ImplicitStreamSubscriberTable
    {
        [NonSerialized]
        private readonly ConcurrentDictionary<string, HashSet<int>> table;

        private readonly HashSet<int> grainsWithKeyExtensions;
        private readonly List<Tuple<IStreamNamespacePredicate, int>> predicates;

        public ImplicitStreamSubscriberTable()
        {
            table = new ConcurrentDictionary<string, HashSet<int>>();
            grainsWithKeyExtensions = new HashSet<int>();
            predicates = new List<Tuple<IStreamNamespacePredicate, int>>();
        }

        /// <summary>Initializes any implicit stream subscriptions specified for a grain class type. If the grain class specified does not have any associated namespaces, then nothing is done.</summary>
        /// <param name="grainClasses">A grain class type.</param>
        /// <exception cref="System.ArgumentException">
        /// Duplicate specification of namespace "...".
        /// </exception>
        internal void InitImplicitStreamSubscribers(IEnumerable<Type> grainClasses)
        {
            foreach (var grainClass in grainClasses)
            {
                if (!TypeUtils.IsGrainClass(grainClass))
                {
                    continue;
                }

                // we collect all predicates that the specified grain class should implicitly subscribe to.

                IList<IStreamNamespacePredicate> grainPredicates = GetPredicatesFromAttributes(grainClass);
                if (grainPredicates.Any())
                {
                    // we'll need the class type code.
                    int implTypeCode = CodeGeneration.GrainInterfaceUtils.GetGrainClassTypeCode(grainClass);
                    if (typeof(IGrainWithGuidCompoundKey).IsAssignableFrom(grainClass))
                    {
                        grainsWithKeyExtensions.Add(implTypeCode);
                    }
                    foreach (var predicate in grainPredicates)
                    {
                        predicates.Add(Tuple.Create(predicate, implTypeCode));
                    }
                }
            }
        }

        /// <summary>
        /// Retrieve a map of implicit subscriptionsIds to implicit subscribers, given a stream ID. This method throws an exception if there's no namespace associated with the stream ID.
        /// </summary>
        /// <param name="streamId">A stream ID.</param>
        /// <param name="grainFactory">The grain factory used to get consumer references.</param>
        /// <returns>A set of references to implicitly subscribed grains. They are expected to support the streaming consumer extension.</returns>
        /// <exception cref="System.ArgumentException">The stream ID doesn't have an associated namespace.</exception>
        /// <exception cref="System.InvalidOperationException">Internal invariant violation.</exception>
        internal IDictionary<Guid, IStreamConsumerExtension> GetImplicitSubscribers(StreamId streamId, IInternalGrainFactory grainFactory)
        {
            if (!IsImplicitSubscribeEligibleNameSpace(streamId.Namespace))
            {
                throw new ArgumentException("The stream ID doesn't have an associated namespace.", nameof(streamId));
            }

            HashSet<int> entry = GetOrAddImplicitSubscriberTypeCodes(streamId.Namespace);

            var result = new Dictionary<Guid, IStreamConsumerExtension>();
            foreach (var i in entry)
            {
                IStreamConsumerExtension consumer = MakeConsumerReference(grainFactory, streamId, i);
                Guid subscriptionGuid = MakeSubscriptionGuid(i, streamId);
                if (result.ContainsKey(subscriptionGuid))
                {
                    throw new InvalidOperationException(
                        $"Internal invariant violation: generated duplicate subscriber reference: {consumer}, subscriptionId: {subscriptionGuid}");
                }
                result.Add(subscriptionGuid, consumer);
            }
            return result;
        }

        private HashSet<int> GetOrAddImplicitSubscriberTypeCodes(string streamNamespace)
        {
            return table.GetOrAdd(streamNamespace, FindImplicitSubscriberTypeCodes);
        }

        /// <summary>
        /// Determines whether the specified grain is an implicit subscriber of a given stream.
        /// </summary>
        /// <param name="grainId">The grain identifier.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns>true if the grain id describes an implicit subscriber of the stream described by the stream id.</returns>
        internal bool IsImplicitSubscriber(GrainId grainId, StreamId streamId)
        {
            return grainId.IsLegacyGrain() && HasImplicitSubscription(streamId.Namespace, ((LegacyGrainId)grainId).TypeCode);
        }

        /// <summary>
        /// Try to get the implicit subscriptionId.
        /// If an implicit subscription exists, return a subscription Id that is unique per grain type, grainId, namespace combination.
        /// </summary>
        /// <param name="grainId"></param>
        /// <param name="streamId"></param>
        /// <param name="subscriptionId"></param>
        /// <returns></returns>
        internal bool TryGetImplicitSubscriptionGuid(GrainId grainId, StreamId streamId, out Guid subscriptionId)
        {
            subscriptionId = Guid.Empty;

            if (!HasImplicitSubscription(streamId.Namespace, ((LegacyGrainId)grainId).TypeCode))
            {
                return false;
            }

            // make subscriptionId
            subscriptionId = MakeSubscriptionGuid(grainId, streamId);

            return true;
        }

        /// <summary>
        /// Create a subscriptionId that is unique per grainId, grainType, namespace combination.
        /// </summary>
        /// <param name="grainId"></param>
        /// <param name="streamId"></param>
        /// <returns></returns>
        private Guid MakeSubscriptionGuid(GrainId grainId, StreamId streamId)
        {
            // first int in guid is grain type code
            int grainIdTypeCode = ((LegacyGrainId)grainId).TypeCode;

            return MakeSubscriptionGuid(grainIdTypeCode, streamId);
        }

        /// <summary>
        /// Create a subscriptionId that is unique per grainId, grainType, namespace combination.
        /// </summary>
        /// <param name="grainIdTypeCode"></param>
        /// <param name="streamId"></param>
        /// <returns></returns>
        private Guid MakeSubscriptionGuid(int grainIdTypeCode, StreamId streamId)
        {
            // next 2 shorts ing guid are from namespace hash
            uint namespaceHash = JenkinsHash.ComputeHash(streamId.Namespace);
            byte[] namespaceHashByes = BitConverter.GetBytes(namespaceHash);
            short s1 = BitConverter.ToInt16(namespaceHashByes, 0);
            short s2 = BitConverter.ToInt16(namespaceHashByes, 2);

            // Tailing 8 bytes of the guid are from the hash of the streamId Guid and a hash of the provider name.
            // get streamId guid hash code
            uint streamIdGuidHash = JenkinsHash.ComputeHash(streamId.Guid.ToByteArray());
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
            return SubscriptionMarker.MarkAsImplictSubscriptionId(new Guid(grainIdTypeCode, s1, s2, tail.ToArray()));
        }

        internal static bool IsImplicitSubscribeEligibleNameSpace(string streamNameSpace)
        {
            return !string.IsNullOrWhiteSpace(streamNameSpace);
        }

        private bool HasImplicitSubscription(string streamNamespace, int grainIdTypeCode)
        {
            if (!IsImplicitSubscribeEligibleNameSpace(streamNamespace))
            {
                return false;
            }

            HashSet<int> entry = GetOrAddImplicitSubscriberTypeCodes(streamNamespace);
            return entry.Contains(grainIdTypeCode);
        }

        /// <summary>
        /// Finds all implicit subscribed for the given stream namespace.
        /// </summary>
        /// <param name="streamNamespace">The stream namespace to find subscribers too.</param>
        /// <returns></returns>
        private HashSet<int> FindImplicitSubscriberTypeCodes(string streamNamespace)
        {
            HashSet<int> result = new HashSet<int>();
            foreach (Tuple<IStreamNamespacePredicate, int> predicate in predicates)
            {
                if (predicate.Item1.IsMatch(streamNamespace))
                {
                    result.Add(predicate.Item2);
                }
            }

            return result;
        }

        /// <summary>
        /// Create a reference to a grain that we expect to support the stream consumer extension.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to get consumer references.</param>
        /// <param name="streamId">The stream ID to use for the grain ID construction.</param>
        /// <param name="implTypeCode">The type code of the grain interface.</param>
        /// <returns></returns>
        private IStreamConsumerExtension MakeConsumerReference(IInternalGrainFactory grainFactory, StreamId streamId,
            int implTypeCode)
        {
            var keyExtension = grainsWithKeyExtensions.Contains(implTypeCode)
                ? streamId.Namespace
                : null;
            GrainId grainId = LegacyGrainId.GetGrainId(implTypeCode, streamId.Guid, keyExtension);
            return grainFactory.GetGrain<IStreamConsumerExtension>(grainId);
        }

        /// <summary>
        /// Collects the namespace predicates associated with a grain class type through the use of
        /// <see cref="ImplicitStreamSubscriptionAttribute"/>.
        /// </summary>
        /// <param name="grainClass">A grain class type that might have
        /// attributes of type <see cref="ImplicitStreamSubscriptionAttribute"/>  associated with it.</param>
        /// <returns>The list of stream namespace predicates.</returns>
        /// <exception cref="System.ArgumentException"><paramref name="grainClass"/> does not describe a grain class.</exception>
        private static IList<IStreamNamespacePredicate> GetPredicatesFromAttributes(Type grainClass)
        {
            if (!TypeUtils.IsGrainClass(grainClass))
            {
                throw new ArgumentException($"{grainClass.FullName} is not a grain class.", nameof(grainClass));
            }

            var attribs = grainClass.GetCustomAttributes<ImplicitStreamSubscriptionAttribute>(true);

            return attribs.Select(attrib => attrib.Predicate).ToList();
        }
    }
}