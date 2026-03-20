using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization.TypeSystem;

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
                    predicate = new RegexStreamNamespacePredicate(regex[RegexStreamNamespacePredicate.Prefix.Length..]);
                    return true;
                case var ns when ns.StartsWith(ExactMatchStreamNamespacePredicate.Prefix, StringComparison.Ordinal):
                    predicate = new ExactMatchStreamNamespacePredicate(ns[ExactMatchStreamNamespacePredicate.Prefix.Length..]);
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
#if NET9_0_OR_GREATER
        private readonly Lock _lock = new();
#else
        private readonly object _lock = new();
#endif
        private readonly Dictionary<string, bool> _allowedPredicateTypes = new(StringComparer.Ordinal);

        /// <summary>
        /// The prefix used to identify this predicate provider.
        /// </summary>
        public const string Prefix = "ctor";

        /// <summary>
        /// Initializes a new instance of the <see cref="ConstructorStreamNamespacePredicateProvider"/> class.
        /// </summary>
        /// <param name="grainTypeOptions">The grain type options containing known grain classes.</param>
        public ConstructorStreamNamespacePredicateProvider(IOptions<GrainTypeOptions> grainTypeOptions)
        {
            foreach (var grainClass in grainTypeOptions.Value.Classes)
            {
                foreach (var attr in grainClass.GetCustomAttributes(inherit: true))
                {
                    if (attr is ImplicitStreamSubscriptionAttribute streamSub)
                    {
                        RegisterPredicateType(streamSub.Predicate.GetType());
                    }
                }
            }
        }

        /// <summary>
        /// Registers a predicate type as allowed for construction.
        /// </summary>
        /// <param name="predicateType">The predicate type to register.</param>
        public void RegisterPredicateType(Type predicateType)
        {
            ArgumentNullException.ThrowIfNull(predicateType);
            var typeName = RuntimeTypeNameFormatter.Format(predicateType);
            lock (_lock)
            {
                _allowedPredicateTypes[typeName] = true;
            }
        }

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
                typeName = predicatePattern[start..];
                arg = null;
            }
            else
            {
                typeName = predicatePattern[start..index];
                arg = predicatePattern[(index + 1)..];
            }

            bool allowed;
            lock (_lock)
            {
                allowed = _allowedPredicateTypes.ContainsKey(typeName);
            }

            if (!allowed)
            {
                throw new InvalidOperationException($"Type \"{typeName}\" is not a registered stream namespace predicate. Ensure the grain interface assembly is loaded and the predicate type is used in an [{nameof(ImplicitStreamSubscriptionAttribute)}].");
            }

            var type = Type.GetType(typeName, throwOnError: true);

            if (!typeof(IStreamNamespacePredicate).IsAssignableFrom(type))
            {
                throw new InvalidOperationException($"Type \"{type}\" is not a valid stream namespace predicate because it does not implement {nameof(IStreamNamespacePredicate)}.");
            }

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