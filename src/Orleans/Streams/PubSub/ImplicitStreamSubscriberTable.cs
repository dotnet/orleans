using System;
using System.Collections.Generic;
using System.Reflection;
using Orleans.Runtime;

namespace Orleans.Streams
{

    [Serializable]
    internal class ImplicitStreamSubscriberTable
    {
        private readonly Dictionary<string, HashSet<int>> table;

        public ImplicitStreamSubscriberTable()
        {
            table = new Dictionary<string, HashSet<int>>();
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

                // we collect all namespaces that the specified grain class should implicitly subscribe to.
                ISet<string> namespaces = GetNamespacesFromAttributes(grainClass);
                if (null == namespaces) continue;

                if (namespaces.Count > 0)
                {
                    // the grain class is subscribed to at least one namespace. in order to create a grain reference later, we need a qualifying interface but it doesn't matter which (because we'll be creating references to extensions), so we'll take the first interface in the sequence.
                    AddImplicitSubscriber(grainClass, namespaces);
                }
            }
        }

        /// <summary>
        /// Retrieve a map of implicit subscriptionsIds to implicit subscribers, given a stream ID. This method throws an exception if there's no namespace associated with the stream ID. 
        /// </summary>
        /// <param name="streamId">A stream ID.</param>
        /// <returns>A set of references to implicitly subscribed grains. They are expected to support the streaming consumer extension.</returns>
        /// <exception cref="System.ArgumentException">The stream ID doesn't have an associated namespace.</exception>
        /// <exception cref="System.InvalidOperationException">Internal invariant violation.</exception>
        internal IDictionary<Guid, IStreamConsumerExtension> GetImplicitSubscribers(StreamId streamId)
        {
            if (String.IsNullOrWhiteSpace(streamId.Namespace))
            {
                throw new ArgumentException("The stream ID doesn't have an associated namespace.", "streamId");
            }

            HashSet<int> entry;
            var result = new Dictionary<Guid, IStreamConsumerExtension>();
            if (table.TryGetValue(streamId.Namespace, out entry))
            {
                foreach (var i in entry)
                {
                    IStreamConsumerExtension consumer = MakeConsumerReference(streamId.Guid, i);
                    Guid subscriptionGuid = MakeSubscriptionGuid(i, streamId);
                    if (result.ContainsKey(subscriptionGuid))
                    {
                        throw new InvalidOperationException(string.Format("Internal invariant violation: generated duplicate subscriber reference: {0}, subscriptionId: {1}", consumer, subscriptionGuid));
                    }
                    result.Add(subscriptionGuid, consumer);
                }                
                return result;                
            }

            return result;
        }

        /// <summary>
        /// Determines whether the specified grain is an implicit subscriber of a given stream.
        /// </summary>
        /// <param name="grainId">The grain identifier.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns>true if the grain id describes an implicit subscriber of the stream described by the stream id.</returns>
        internal bool IsImplicitSubscriber(GrainId grainId, StreamId streamId)
        {
            return HasImplicitSubscription(streamId.Namespace, grainId.GetTypeCode());
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

            if (!HasImplicitSubscription(streamId.Namespace, grainId.GetTypeCode()))
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
            int grainIdTypeCode = grainId.GetTypeCode();

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

        private bool HasImplicitSubscription(string streamNamespace, int grainIdTypeCode)
        {
            if (String.IsNullOrWhiteSpace(streamNamespace))
            {
                return false;
            }

            HashSet<int> entry;
            return (table.TryGetValue(streamNamespace, out entry) && // if we don't have implictit subscriptions for this namespace, fail out
                    entry.Contains(grainIdTypeCode));                 // if we don't have an implicit subscription for this type of grain on this namespace, fail out
        }

        /// <summary>
        /// Add an implicit subscriber to the table.
        /// </summary>
        /// <param name="grainClass">Type of the grain class whose instances subscribe to the specified namespaces.</param>
        /// <param name="namespaces">Namespaces instances of the grain class should subscribe to.</param>
        /// <exception cref="System.ArgumentException">
        /// No namespaces specified.
        /// or
        /// Duplicate specification of namespace "...".
        /// </exception>
        private void AddImplicitSubscriber(Type grainClass, ISet<string> namespaces)
        {
            // convert IEnumerable<> to an array without copying, if possible.
            if (namespaces.Count == 0)
            {
                throw new ArgumentException("no namespaces specified", "namespaces");
            }

            // we'll need the class type code.
            int implTypeCode = CodeGeneration.GrainInterfaceUtils.GetGrainClassTypeCode(grainClass);

            foreach (string s in namespaces)
            {
                // first, we trim whitespace off of the namespace string. leaving these would lead to misleading log messages.
                string key = s.Trim();

                // if the table already holds the namespace we're looking at, then we don't need to create a new entry. each entry is a dictionary that holds associations between class names and interface ids. e.g.:
                //  
                // "namespace0" -> HashSet {implTypeCode.0, implTypeCode.1, ..., implTypeCode.n}
                // 
                // each class in the entry used the ImplicitStreamSubscriptionAtrribute with the associated namespace. this information will be used later to create grain references on-demand. we must use string representations to ensure that this information is serializable.
                if (table.ContainsKey(key))
                {
                    // an entry already exists. we append a class/interface association to the current set.
                    HashSet<int> entries = table[key];
                    if (!entries.Add(implTypeCode))
                    {
                        throw new InvalidOperationException(String.Format("attempt to initialize implicit subscriber more than once (key={0}, implTypeCode={1}).", key, implTypeCode));
                    }
                }
                else
                {
                    // an entry does not already exist. we create a new one with one class/interface association.
                    table[key] = new HashSet<int> { implTypeCode };
                }
            }
        }

        /// <summary>
        /// Create a reference to a grain that we expect to support the stream consumer extension.
        /// </summary>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="implTypeCode">The type code of the grain interface.</param>
        /// <returns></returns>
        private IStreamConsumerExtension MakeConsumerReference(Guid primaryKey, int implTypeCode)
        {
            GrainId grainId = GrainId.GetGrainId(implTypeCode, primaryKey);
            IAddressable addressable = GrainReference.FromGrainId(grainId);
            return addressable.Cast<IStreamConsumerExtension>();
        }

        /// <summary>
        /// Collects the namespaces associated with a grain class type through the use of ImplicitStreamSubscriptionAttribute.
        /// </summary>
        /// <param name="grainClass">A grain class type that might have ImplicitStreamSubscriptionAttributes associated with it.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentException">grainType does not describe a grain class.</exception>
        /// <exception cref="System.InvalidOperationException">duplicate specification of ImplicitConsumerActivationAttribute(...).</exception>
        private static ISet<string> GetNamespacesFromAttributes(Type grainClass)
        {
            if (!TypeUtils.IsGrainClass(grainClass))
            {
                throw new ArgumentException(string.Format("{0} is not a grain class.", grainClass.FullName), "grainClass");
            }

            var attribs = grainClass.GetTypeInfo().GetCustomAttributes<ImplicitStreamSubscriptionAttribute>(inherit: false);

            // otherwise, we'll consider all of them and aggregate the specifications. duplicates will not be permitted.
            var result = new HashSet<string>();
            foreach (var attrib in attribs)
            {
                if (string.IsNullOrWhiteSpace(attrib.Namespace))
                {
                    throw new InvalidOperationException("ImplicitConsumerActivationAttribute argument cannot be null nor whitespace");
                }

                string trimmed = attrib.Namespace;
                if (!result.Add(trimmed))
                {
                    throw new InvalidOperationException(string.Format("duplicate specification of attribute ImplicitConsumerActivationAttribute({0}).", attrib.Namespace));
                }
            }

            return result;
        }
    }
}
