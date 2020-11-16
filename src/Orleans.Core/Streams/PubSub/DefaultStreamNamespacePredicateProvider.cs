using System;
using Orleans.Utilities;

namespace Orleans.Streams
{
    /// <summary>
    /// Default implementation of <see cref="IStreamNamespacePredicateProvider"/> for internally supported stream predicates.
    /// </summary>
    public class DefaultStreamNamespacePredicateProvider : IStreamNamespacePredicateProvider
    {  
        /// <inheritdoc/>
        public bool TryGetPredicate(string predicatePattern, out IStreamNamespacePredicate predicate)
        {
            switch (predicatePattern)
            {
                case "*":
                    predicate = new AllStreamNamespacesPredicate();
                    return true;
                case var regex when regex.StartsWith(RegexStreamNamespacePredicate.Prefix, StringComparison.Ordinal):
                    predicate = new RegexStreamNamespacePredicate(regex.Substring(RegexStreamNamespacePredicate.Prefix.Length));
                    return true;
                case var ns when ns.StartsWith(ExactMatchStreamNamespacePredicate.Prefix, StringComparison.Ordinal):
                    predicate = new ExactMatchStreamNamespacePredicate(ns.Substring(ExactMatchStreamNamespacePredicate.Prefix.Length));
                    return true;
            }

            predicate = null;
            return false;
        }
    }

    /// <summary>
    /// Stream namespace predicate provider which supports objects which can be constructed and optionally accept a string as a constructor argument.
    /// </summary>
    public class ConstructorStreamNamespacePredicateProvider : IStreamNamespacePredicateProvider
    {
        public const string Prefix = "ctor";

        /// <summary>
        /// Formats a stream namespace predicate which indicates a concrete <see cref="IStreamNamespacePredicate"/> type to be constructed, along with an optional argument.
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
        public bool TryGetPredicate(string predicatePattern, out IStreamNamespacePredicate predicate)
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
                typeName = predicatePattern.Substring(start);
                arg = null;
            }
            else
            {
                typeName = predicatePattern.Substring(start, index - start);
                arg = predicatePattern.Substring(index + 1);
            }

            var type = Type.GetType(typeName, throwOnError: true);
            if (string.IsNullOrEmpty(arg))
            {
                predicate = (IStreamNamespacePredicate)Activator.CreateInstance(type);
            }
            else
            {
                predicate = (IStreamNamespacePredicate)Activator.CreateInstance(type, arg);
            }

            return true;
        }
    }
}