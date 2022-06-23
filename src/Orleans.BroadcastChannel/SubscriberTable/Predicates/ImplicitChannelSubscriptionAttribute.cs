using System;
using System.Collections.Generic;
using Orleans.BroadcastChannel;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// The [Orleans.ImplicitStreamSubscription] attribute is used to mark grains as implicit stream subscriptions.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ImplicitChannelSubscriptionAttribute : Attribute, IGrainBindingsProviderAttribute
    {
        /// <summary>
        /// Gets the stream namespace filter predicate.
        /// </summary>
        public IChannelNamespacePredicate Predicate { get; }

        /// <summary>
        /// Gets the name of the channel identifier mapper.
        /// </summary>
        /// <value>The name of the channel identifier mapper.</value>
        public string ChannelIdMapper { get; }

        /// <summary>
        /// Used to subscribe to all stream namespaces.
        /// </summary>
        public ImplicitChannelSubscriptionAttribute()
        {
            Predicate = new AllStreamNamespacesPredicate();
        }

        /// <summary>
        /// Used to subscribe to the specified stream namespace.
        /// </summary>
        /// <param name="streamNamespace">The stream namespace to subscribe.</param>
        /// <param name="channelIdMapper">The name of the stream identity mapper.</param>
        public ImplicitChannelSubscriptionAttribute(string streamNamespace, string channelIdMapper = null)
        {
            Predicate = new ExactMatchChannelNamespacePredicate(streamNamespace.Trim());
            ChannelIdMapper = channelIdMapper;
        }

        /// <summary>
        /// Allows to pass an arbitrary predicate type to filter stream namespaces to subscribe. The predicate type 
        /// must have a constructor without parameters.
        /// </summary>
        /// <param name="predicateType">The stream namespace predicate type.</param>
        /// <param name="channelIdMapper">The name of the stream identity mapper.</param>
        public ImplicitChannelSubscriptionAttribute(Type predicateType, string channelIdMapper = null)
        {
            Predicate = (IChannelNamespacePredicate) Activator.CreateInstance(predicateType);
            ChannelIdMapper = channelIdMapper;
        }

        /// <summary>
        /// Allows to pass an instance of the stream namespace predicate. To be used mainly as an extensibility point
        /// via inheriting attributes.
        /// </summary>
        /// <param name="predicate">The stream namespace predicate.</param>
        /// <param name="channelIdMapper">The name of the stream identity mapper.</param>
        public ImplicitChannelSubscriptionAttribute(IChannelNamespacePredicate predicate, string channelIdMapper = null)
        {
            Predicate = predicate;
            ChannelIdMapper = channelIdMapper;
        }

        /// <inheritdoc />
        public IEnumerable<Dictionary<string, string>> GetBindings(IServiceProvider services, Type grainClass, GrainType grainType)
        {
            var binding = new Dictionary<string, string>
            {
                [WellKnownGrainTypeProperties.BindingTypeKey] = WellKnownGrainTypeProperties.BroadcastChannelBindingTypeValue,
                [WellKnownGrainTypeProperties.BroadcastChannelBindingPatternKey] = this.Predicate.PredicatePattern,
                [WellKnownGrainTypeProperties.ChannelIdMapperKey] = this.ChannelIdMapper,
            };

            if (LegacyGrainId.IsLegacyGrainType(grainClass))
            {
                string keyType;

                if (typeof(IGrainWithGuidKey).IsAssignableFrom(grainClass) || typeof(IGrainWithGuidCompoundKey).IsAssignableFrom(grainClass))
                    keyType = nameof(Guid);
                else if (typeof(IGrainWithIntegerKey).IsAssignableFrom(grainClass) || typeof(IGrainWithIntegerCompoundKey).IsAssignableFrom(grainClass))
                    keyType = nameof(Int64);
                else // fallback to string
                    keyType = nameof(String);

                binding[WellKnownGrainTypeProperties.LegacyGrainKeyType] = keyType;
            }

            if (LegacyGrainId.IsLegacyKeyExtGrainType(grainClass))
            {
                binding[WellKnownGrainTypeProperties.StreamBindingIncludeNamespaceKey] = "true";
            }

            yield return binding;
        }
    }

    /// <summary>
    /// The [Orleans.RegexImplicitStreamSubscription] attribute is used to mark grains as implicit stream
    /// subscriptions by filtering stream namespaces to subscribe using a regular expression.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class RegexImplicitChannelSubscriptionAttribute : ImplicitChannelSubscriptionAttribute
    {
        /// <summary>
        /// Allows to pass a regular expression to filter stream namespaces to subscribe to.
        /// </summary>
        /// <param name="pattern">The stream namespace regular expression filter.</param>
        public RegexImplicitChannelSubscriptionAttribute(string pattern)
            : base(new RegexChannelNamespacePredicate(pattern))
        {
        }
    }
}