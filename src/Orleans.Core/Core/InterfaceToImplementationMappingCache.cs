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
            /// Gets the grain implementation <see cref="MethodInfo"/>.
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

                public bool Equals(Type[]? x, Type[]? y) => ReferenceEquals(x, y) || x is null && y is null || x!.Length != y!.Length || x.AsSpan().SequenceEqual(y.AsSpan());

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
        /// The map from implementation and interface types to method mappings.
        /// </summary>
        private readonly ConcurrentDictionary<(Type ImplementationType, Type InterfaceType), Dictionary<MethodInfo, Entry>> mappings = new();

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
            if (!interfaceType.IsInterface)
            {
                throw new ArgumentException($"Type {interfaceType} is not an interface", nameof(interfaceType));
            }

            if (!interfaceType.IsAssignableFrom(implementationType))
            {
                throw new InvalidOperationException($"Type {implementationType} does not implement interface {interfaceType}");
            }

            return mappings.GetOrAdd(
                (implementationType, interfaceType),
                static key => CreateInterfaceToImplementationMap(key.ImplementationType, key.InterfaceType));
        }

        /// <summary>
        /// Maps each method from <paramref name="interfaceType"/>, including inherited interface methods,
        /// to its implementation on <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <param name="interfaceType">The interface type.</param>
        /// <returns>A mapping from interface methods to implementation methods.</returns>
        private static Dictionary<MethodInfo, Entry> CreateInterfaceToImplementationMap(Type implementationType, Type interfaceType)
        {
            var methods = GrainInterfaceUtils.GetMethods(interfaceType);

            // Map every method on this interface from the definition interface onto the implementation class.
            var result = new Dictionary<MethodInfo, Entry>(methods.Length);

            var mapping = default(InterfaceMapping);
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];

                // If this method is not from the expected interface (eg, because it's from a parent interface), then
                // get the mapping for the interface which it does belong to.
                if (mapping.InterfaceType != method.DeclaringType)
                {
                    mapping = GrainInterfaceUtils.GetInterfaceMap(implementationType, method.DeclaringType!);
                }

                // Find the index of the interface method and then get the implementation method at that position.
                for (var k = 0; k < mapping.InterfaceMethods!.Length; k++)
                {
                    if (mapping.InterfaceMethods[k] != method) continue;
                    Debug.Assert(method is not null);
                    result[method] = new Entry(mapping.TargetMethods![k], method);

                    break;
                }
            }

            return result;
        }
    }
}
