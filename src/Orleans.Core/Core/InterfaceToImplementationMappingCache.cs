using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Orleans.CodeGeneration;

namespace Orleans
{
    /// <summary>
    /// Maintains a map between grain classes and corresponding interface-implementation mappings.
    /// </summary>
    internal class InterfaceToImplementationMappingCache
    {
        /// <summary>
        /// Maps a grain interface method's <see cref="MethodInfo"/> to an implementation's <see cref="MethodInfo"/>.
        /// </summary>
        public readonly struct Entry
        {
            public Entry(MethodInfo implementationMethod, MethodInfo interfaceMethod)
            {
                Debug.Assert(implementationMethod is not null);
                Debug.Assert(interfaceMethod is not null);
                ImplementationMethod = implementationMethod;
                InterfaceMethod = interfaceMethod;
            }

            /// <summary>
            /// Gets the grain implmentation <see cref="MethodInfo"/>.
            /// </summary>
            public MethodInfo ImplementationMethod { get; }

            /// <summary>
            /// Gets the grain interface <see cref="MethodInfo"/>.
            /// </summary>
            public MethodInfo InterfaceMethod { get; }

            public (MethodInfo ImplementationMethod, MethodInfo InterfaceMethod) GetConstructedGenericMethod(MethodInfo method)
            {
                return ConstructedGenericMethods.GetOrAdd(method.GetGenericArguments(), (key, state) =>
                {
                    var (entry, method) = state;
                    var genericArgs = key;
                    var constructedImplementationMethod = entry.ImplementationMethod.MakeGenericMethod(genericArgs);
                    var constructedInterfaceMethod = entry.InterfaceMethod.MakeGenericMethod(genericArgs);
                    return (constructedImplementationMethod, constructedInterfaceMethod);
                }, (this, method));
            }

            /// <summary>
            /// Gets the constructed generic instances of this method.
            /// </summary>
            public ConcurrentDictionary<Type[], (MethodInfo ImplementationMethod, MethodInfo InterfaceMethod)> ConstructedGenericMethods { get; } = new(TypeArrayComparer.Instance);

            private sealed class TypeArrayComparer : IEqualityComparer<Type[]>
            {
                internal static readonly TypeArrayComparer Instance = new();

                public bool Equals(Type[] x, Type[] y) => ReferenceEquals(x, y) || x is null && y is null || x.Length != y.Length || x.AsSpan().SequenceEqual(y.AsSpan());

                public int GetHashCode([DisallowNull] Type[] obj)
                {
                    HashCode result = new();
                    result.Add(obj.Length);
                    foreach (var value in obj)
                    {
                        result.Add(value);
                    }

                    return result.ToHashCode();
                }
            }
        }

        /// <summary>
        /// The map from implementation types to interface types to map of method to method infos.
        /// </summary>
        private readonly ConcurrentDictionary<Type, Dictionary<Type, Dictionary<MethodInfo, Entry>>> mappings = new();

        /// <summary>
        /// Returns a mapping from method id to method info for the provided implementation and interface types.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <param name="interfaceType">The interface type.</param>
        /// <returns>
        /// A mapping from method id to method info.
        /// </returns>
        public Dictionary<MethodInfo, Entry> GetOrCreate(Type implementationType, Type interfaceType)
        {
            // Get or create the mapping between interfaceId and invoker for the provided type.
            if (!this.mappings.TryGetValue(implementationType, out var invokerMap))
            {
                // Generate an the invoker mapping using the provided invoker.
                invokerMap = mappings.GetOrAdd(implementationType, CreateInterfaceToImplementationMap(implementationType));
            }

            // Attempt to get the invoker for the provided interfaceId.
            if (!invokerMap.TryGetValue(interfaceType, out var interfaceToImplementationMap))
            {
                throw new InvalidOperationException($"Type {implementationType} does not implement interface {interfaceType}");
            }

            return interfaceToImplementationMap;
        }

        /// <summary>
        /// Maps the interfaces of the provided <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <returns>The mapped interface.</returns>
        private static Dictionary<Type, Dictionary<MethodInfo, Entry>> CreateInterfaceToImplementationMap(Type implementationType)
        {
            var interfaces = implementationType.GetInterfaces();

            // Create an invoker for every interface on the provided type.
            var result = new Dictionary<Type, Dictionary<MethodInfo, Entry>>(interfaces.Length);
            foreach (var iface in interfaces)
            {
                var methods = GrainInterfaceUtils.GetMethods(iface);

                // Map every method on this interface from the definition interface onto the implementation class.
                var methodMap = new Dictionary<MethodInfo, Entry>(methods.Length);

                var mapping = default(InterfaceMapping);
                for (var i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];

                    // If this method is not from the expected interface (eg, because it's from a parent interface), then
                    // get the mapping for the interface which it does belong to.
                    if (mapping.InterfaceType != method.DeclaringType)
                    {
                        mapping = implementationType.GetInterfaceMap(method.DeclaringType);
                    }

                    // Find the index of the interface method and then get the implementation method at that position.
                    for (var k = 0; k < mapping.InterfaceMethods.Length; k++)
                    {
                        if (mapping.InterfaceMethods[k] != method) continue;
                        Debug.Assert(method is not null);
                        methodMap[method] = new Entry(mapping.TargetMethods[k], method);

                        break;
                    }
                }

                // Add the resulting map of methodId -> method to the interface map.
                result[iface] = methodMap;
            }

            return result;
        }
    }
}