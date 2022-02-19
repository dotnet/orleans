using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Type resolver which caches results.
    /// </summary>
    public sealed class CachedTypeResolver : TypeResolver
    {
        private readonly ConcurrentDictionary<string, Type> _typeCache = new ConcurrentDictionary<string, Type>();
        private readonly ConcurrentDictionary<string, Assembly> _assemblyCache = new ConcurrentDictionary<string, Assembly>();

        /// <inheritdoc />
        public override Type ResolveType(string name)
        {
            if (TryResolveType(name, out var result))
            {
                return result;
            }

            throw new TypeAccessException($"Unable to find a type named {name}");
        }

        /// <inheritdoc />
        public override bool TryResolveType(string name, out Type type)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A FullName must not be null nor consist of only whitespace.", nameof(name));
            }

            if (TryGetCachedType(name, out type))
            {
                return true;
            }

            if (!TryPerformUncachedTypeResolution(name, out type))
            {
                return false;
            }

            AddTypeToCache(name, type);
            return true;
        }

        private bool TryPerformUncachedTypeResolution(string name, out Type type)
        {
            IEnumerable<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (!TryPerformUncachedTypeResolution(name, out type, assemblies))
            {
                return false;
            }

            if (type.Assembly.ReflectionOnly)
            {
                throw new InvalidOperationException($"Type resolution for {name} yielded reflection-only type.");
            }

            return true;
        }

        private bool TryGetCachedType(string name, out Type result)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("type name was null or whitespace");
            }

            return _typeCache.TryGetValue(name, out result);
        }

        private void AddTypeToCache(string name, Type type)
        {
            var entry = _typeCache.GetOrAdd(name, type);
            if (!ReferenceEquals(entry, type))
            {
                throw new InvalidOperationException("inconsistent type name association");
            }
        }

        private bool TryPerformUncachedTypeResolution(string fullName, out Type type, IEnumerable<Assembly> assemblies)
        {
            if (null == assemblies)
            {
                throw new ArgumentNullException(nameof(assemblies));
            }

            if (string.IsNullOrWhiteSpace(fullName))
            {
                throw new ArgumentException("A type name must not be null nor consist of only whitespace.", nameof(fullName));
            }

            foreach (var assembly in assemblies)
            {
                type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return true;
                }
            }

            type = Type.GetType(fullName, throwOnError: false);
            if (type is null)
            { 
                type = Type.GetType(
                       fullName,
                       ResolveAssembly,
                       ResolveType,
                       false);
            }

            return type != null;

            Assembly ResolveAssembly(AssemblyName assemblyName)
            {
                var fullAssemblyName = assemblyName.FullName;
                if (_assemblyCache.TryGetValue(fullAssemblyName, out var result))
                {
                    return result;
                }

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = assembly.GetName();
                    _assemblyCache[name.FullName] = assembly;
                    _assemblyCache[name.Name] = assembly;
                }

                if (_assemblyCache.TryGetValue(fullAssemblyName, out result))
                {
                    return result;
                }

                result = Assembly.Load(assemblyName);
                var resultName = result.GetName();
                _assemblyCache[resultName.Name] = result;
                _assemblyCache[resultName.FullName] = result;

                return result;
            }

            static Type ResolveType(Assembly asm, string name, bool ignoreCase)
            {
                return asm?.GetType(name, throwOnError: false, ignoreCase: ignoreCase) ?? Type.GetType(name, throwOnError: false, ignoreCase: ignoreCase);
            }
        }
    }
}