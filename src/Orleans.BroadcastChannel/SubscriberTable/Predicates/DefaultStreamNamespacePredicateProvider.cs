using System;
using Orleans.Serialization.TypeSystem;

namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// Default implementation of <see cref="IChannelNamespacePredicateProvider"/> for internally supported stream predicates.
    /// </summary>
    public class DefaultChannelNamespacePredicateProvider : IChannelNamespacePredicateProvider
    {  
        /// <inheritdoc/>
        public bool TryGetPredicate(string predicatePattern, out IChannelNamespacePredicate predicate)
        {
            switch (predicatePattern)
            {
                case "*":
                    predicate = new AllStreamNamespacesPredicate();
                    return true;
                case var regex when regex.StartsWith(RegexChannelNamespacePredicate.Prefix, StringComparison.Ordinal):
                    predicate = new RegexChannelNamespacePredicate(regex[RegexChannelNamespacePredicate.Prefix.Length..]);
                    return true;
                case var ns when ns.StartsWith(ExactMatchChannelNamespacePredicate.Prefix, StringComparison.Ordinal):
                    predicate = new ExactMatchChannelNamespacePredicate(ns[ExactMatchChannelNamespacePredicate.Prefix.Length..]);
                    return true;
            }

            predicate = null;
            return false;
        }
    }

    /// <summary>
    /// Stream namespace predicate provider which supports objects which can be constructed and optionally accept a string as a constructor argument.
    /// </summary>
    public class ConstructorChannelNamespacePredicateProvider : IChannelNamespacePredicateProvider
    {
        /// <summary>
        /// The prefix used to identify this predicate provider.
        /// </summary>
        public const string Prefix = "ctor";

        /// <summary>
        /// Formats a stream namespace predicate which indicates a concrete <see cref="IChannelNamespacePredicate"/> type to be constructed, along with an optional argument.
        /// </summary>
        public static string FormatPattern(Type predicateType, string constructorArgument)
        {
            if (constructorArgument is null)
            {
                return $"{Prefix}:{RuntimeTypeNameFormatter.Format(predicateType)}";
            }

            return $"{Prefix}:{RuntimeTypeNameFormatter.Format(predicateType)}:{constructorArgument}";
        }

        /// <inheritdoc/>
        public bool TryGetPredicate(string predicatePattern, out IChannelNamespacePredicate predicate)
        {
            if (!predicatePattern.StartsWith(Prefix, StringComparison.Ordinal))
            {
                predicate = null;
                return false;
            }

            var start = Prefix.Length + 1;
            string typeName;
            string arg;
            var index = predicatePattern.IndexOf(':', start);
            if (index < 0)
            {
                typeName = predicatePattern[start..];
                arg = null;
            }
            else
            {
                typeName = predicatePattern[start..index];
                arg = predicatePattern[(index + 1)..];
            }

            var type = Type.GetType(typeName, throwOnError: true);
            if (string.IsNullOrEmpty(arg))
            {
                predicate = (IChannelNamespacePredicate)Activator.CreateInstance(type);
            }
            else
            {
                predicate = (IChannelNamespacePredicate)Activator.CreateInstance(type, arg);
            }

            return true;
        }
    }
}