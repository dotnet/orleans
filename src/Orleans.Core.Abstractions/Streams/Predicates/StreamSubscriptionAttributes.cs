using System;
using System.Text.RegularExpressions;
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// The [Orleans.ImplicitStreamSubscription] attribute is used to mark grains as implicit stream subscriptions.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ImplicitStreamSubscriptionAttribute : Attribute
    {
        /// <summary>
        /// Gets the stream namespace filter predicate.
        /// </summary>
        public IStreamNamespacePredicate Predicate { get; }

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
        public ImplicitStreamSubscriptionAttribute(string streamNamespace)
        {
            Predicate = new ExactMatchStreamNamespacePredicate(streamNamespace.Trim());
        }

        /// <summary>
        /// Allows to pass an arbitrary predicate type to filter stream namespaces to subscribe. The predicate type 
        /// must have a constructor without parameters.
        /// </summary>
        /// <param name="predicateType">The stream namespace predicate type.</param>
        public ImplicitStreamSubscriptionAttribute(Type predicateType)
        {
            Predicate = (IStreamNamespacePredicate) Activator.CreateInstance(predicateType);
        }


        /// <summary>
        /// Allows to pass an instance of the stream namespace predicate. To be used mainly as an extensibility point
        /// via inheriting attributes.
        /// </summary>
        /// <param name="predicate">The stream namespace predicate.</param>
        public ImplicitStreamSubscriptionAttribute(IStreamNamespacePredicate predicate)
        {
            Predicate = predicate;
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
        public RegexImplicitStreamSubscriptionAttribute(string pattern)
            : base(new RegexStreamNamespacePredicate(new Regex(pattern)))
        {
        }
    }
}