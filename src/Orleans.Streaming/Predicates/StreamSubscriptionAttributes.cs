using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// The [Orleans.ImplicitStreamSubscription] attribute is used to mark grains as implicit stream subscriptions.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ImplicitStreamSubscriptionAttribute : Attribute, IGrainBindingsProviderAttribute
    {
        /// <summary>
        /// Gets the stream namespace filter predicate.
        /// </summary>
        public IStreamNamespacePredicate Predicate { get; }

        /// <summary>
        /// Gets the name of the stream identifier mapper.
        /// </summary>
        /// <value>The name of the stream identifier mapper.</value>
        /// <remarks>
        /// This value is the name used to resolve the <see cref="IStreamIdMapper"/> registered in the dependency injection container.
        /// </remarks>
        public string StreamIdMapper { get; init; }

        /// <summary>
        /// Used to subscribe to all stream namespaces.
        /// </summary>
        public ImplicitStreamSubscriptionAttribute()
        {
            Predicate = new AllStreamNamespacesPredicate();
        }

        /// <summary>
        /// Used to subscribe to the specified stream namespace.
        /// </summary>
        /// <param name="streamNamespace">The stream namespace to subscribe.</param>
        /// <param name="streamIdMapper">The name of the stream identity mapper.</param>
        public ImplicitStreamSubscriptionAttribute(string streamNamespace, string streamIdMapper = null)
        {
            Predicate = new ExactMatchStreamNamespacePredicate(streamNamespace.Trim());
            StreamIdMapper = streamIdMapper;
        }

        /// <summary>
        /// Allows to pass an arbitrary predicate type to filter stream namespaces to subscribe. The predicate type 
        /// must have a constructor without parameters.
        /// </summary>
        /// <param name="predicateType">The stream namespace predicate type.</param>
        /// <param name="streamIdMapper">The name of the stream identity mapper.</param>
        public ImplicitStreamSubscriptionAttribute(Type predicateType, string streamIdMapper = null)
        {
            Predicate = (IStreamNamespacePredicate) Activator.CreateInstance(predicateType);
            StreamIdMapper = streamIdMapper;
        }

        /// <summary>
        /// Allows to pass an instance of the stream namespace predicate. To be used mainly as an extensibility point
        /// via inheriting attributes.
        /// </summary>
        /// <param name="predicate">The stream namespace predicate.</param>
        /// <param name="streamIdMapper">The name of the stream identity mapper.</param>
        public ImplicitStreamSubscriptionAttribute(IStreamNamespacePredicate predicate, string streamIdMapper = null)
        {
            Predicate = predicate;
            StreamIdMapper = streamIdMapper;
        }

        /// <inheritdoc />
        public IEnumerable<Dictionary<string, string>> GetBindings(IServiceProvider services, Type grainClass, GrainType grainType)
        {
            var binding = new Dictionary<string, string>
            {
                [WellKnownGrainTypeProperties.BindingTypeKey] = WellKnownGrainTypeProperties.StreamBindingTypeValue,
                [WellKnownGrainTypeProperties.StreamBindingPatternKey] = this.Predicate.PredicatePattern,
                [WellKnownGrainTypeProperties.StreamIdMapperKey] = this.StreamIdMapper,
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
    public sealed class RegexImplicitStreamSubscriptionAttribute : ImplicitStreamSubscriptionAttribute
    {
        /// <summary>
        /// Allows to pass a regular expression to filter stream namespaces to subscribe to.
        /// </summary>
        /// <param name="pattern">The stream namespace regular expression filter.</param>
        public RegexImplicitStreamSubscriptionAttribute([StringSyntax(StringSyntaxAttribute.Regex)] string pattern)
            : base(new RegexStreamNamespacePredicate(pattern))
        {
        }
    }
}