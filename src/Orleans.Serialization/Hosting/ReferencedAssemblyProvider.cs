using Microsoft.Extensions.DependencyModel;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

#if NETCOREAPP3_1_OR_GREATER
using System.Runtime.Loader;
#endif

namespace Orleans.Serialization.Internal
{
    /// <summary>
    /// Discovers assemblies which contain Orleans serialization application parts.
    /// </summary>
    public static class ReferencedAssemblyProvider
    {
        /// <summary>
        /// Gets the loaded assemblies which contain Orleans serialization application parts.
        /// </summary>
        /// <returns>The assemblies which contain Orleans serialization application parts.</returns>
        public static IEnumerable<Assembly> GetRelevantAssemblies()
        {
            var parts = new HashSet<Assembly>();
            var entryAssembly = Assembly.GetEntryAssembly();

            if (entryAssembly is not null)
            {
                AddAssembly(parts, entryAssembly);
            }

#if NET5_0_OR_GREATER
            if (AssemblyFilesAvailable)
            {
                AddFromDependencyContext(parts, entryAssembly);
            }
#else
            AddFromDependencyContext(parts, entryAssembly);
#endif

#if NETCOREAPP3_1_OR_GREATER
            AddFromAssemblyLoadContext(parts);
#endif

            foreach (var loadedAsm in AppDomain.CurrentDomain.GetAssemblies())
            {
                AddAssembly(parts, loadedAsm);
            }

            return parts;
        }

        /// <summary>
        /// Adds an assembly and its referenced Orleans serialization application parts to a collection.
        /// </summary>
        /// <param name="parts">The collection to add assemblies to.</param>
        /// <param name="assembly">The assembly to inspect.</param>
        public static void AddAssembly(HashSet<Assembly> parts, Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            if (!assembly.IsDefined(typeof(ApplicationPartAttribute)))
            {
                return;
            }

            if (!parts.Add(assembly))
            {
                return;
            }

            AddAssembly(parts, assembly);

            // Add all referenced application parts.
            foreach (var referencedAsm in GetApplicationPartAssemblies(assembly))
            {
                AddAssembly(parts, referencedAsm);
            }
        }

#if NETCOREAPP3_1_OR_GREATER
        /// <summary>
        /// Adds Orleans serialization application parts from an assembly load context to a collection.
        /// </summary>
        /// <param name="parts">The collection to add assemblies to.</param>
        /// <param name="context">The assembly load context to inspect.</param>
        public static void AddFromAssemblyLoadContext(HashSet<Assembly> parts, AssemblyLoadContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            foreach (var asm in context.Assemblies)
            {
                AddAssembly(parts, asm);
            }
        }

        /// <summary>
        /// Adds Orleans serialization application parts from the load context containing an assembly to a collection.
        /// </summary>
        /// <param name="parts">The collection to add assemblies to.</param>
        /// <param name="assembly">
        /// The assembly whose load context is inspected, or <see langword="null"/> to use the assembly containing this type.
        /// </param>
        public static void AddFromAssemblyLoadContext(HashSet<Assembly> parts, Assembly? assembly = null)
        {
            assembly ??= typeof(ReferencedAssemblyProvider).Assembly;
            var assemblies = new HashSet<Assembly>();
            var context = AssemblyLoadContext.GetLoadContext(assembly)!;
            foreach (var asm in context.Assemblies)
            {
                // Skip assemblies which have not had code generation executed against them and already-seen assemblies.
                if (!asm.IsDefined(typeof(ApplicationPartAttribute)) || !assemblies.Add(asm))
                {
                    continue;
                }

                AddAssembly(parts, asm);
            }
        }
#endif

        /// <summary>
        /// Adds Orleans serialization application parts from an assembly's dependency context to a collection.
        /// </summary>
        /// <param name="parts">The collection to add assemblies to.</param>
        /// <param name="assembly">
        /// The assembly whose dependency context is inspected, or <see langword="null"/> to use the entry assembly.
        /// </param>
        [RequiresAssemblyFiles("Dependency-context discovery reads assembly files. Use GetRelevantAssemblies for single-file-compatible discovery.")]
        public static void AddFromDependencyContext(HashSet<Assembly> parts, Assembly? assembly = null)
        {
            assembly ??= Assembly.GetEntryAssembly();
            DependencyContext? dependencyContext;
            if (assembly is null || assembly.IsDynamic)
            {
                dependencyContext = DependencyContext.Default;
            }
            else
            {
                dependencyContext = DependencyContext.Load(assembly);
            }

            var assemblies = new HashSet<Assembly>();
            if (assembly != null && assembly.IsDefined(typeof(ApplicationPartAttribute)))
            {
                AddAssembly(parts, assembly);
                assemblies.Add(assembly);
            }

            if (dependencyContext == null)
            {
                return;
            }

#if NETCOREAPP3_1_OR_GREATER
            var assemblyContext = assembly is not null
                ? AssemblyLoadContext.GetLoadContext(assembly) ?? AssemblyLoadContext.Default
                : AssemblyLoadContext.Default;
#endif

            foreach (var lib in dependencyContext.RuntimeLibraries)
            {
                if (!lib.Name.Contains("Orleans.Serialization", StringComparison.Ordinal) && !lib.Dependencies.Any(dep => dep.Name.Contains("Orleans.Serialization", StringComparison.Ordinal)))
                {
                    continue;
                }

                try
                {
#if NET5_0_OR_GREATER
                    var name = lib.GetRuntimeAssemblyNames(dependencyContext, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier).FirstOrDefault();
                    if (name is null)
                    {
                        continue;
                    }

                    var asm = assemblyContext.LoadFromAssemblyName(name);
#else
                    var name = lib.GetRuntimeAssemblyNames(dependencyContext, Microsoft.DotNet.PlatformAbstractions.RuntimeEnvironment.GetRuntimeIdentifier()).FirstOrDefault();
                    if (name is null)
                    {
                        continue;
                    }

#if NETCOREAPP3_1_OR_GREATER
                    var asm = assemblyContext.LoadFromAssemblyName(name);
#else
                    var asm = Assembly.Load(name);
#endif
#endif
                    if (asm.IsDefined(typeof(ApplicationPartAttribute)) && assemblies.Add(asm))
                    {
                        AddAssembly(parts, asm);
                    }
                }
                catch
                {
                    // Ignore any exceptions thrown during non-explicit assembly loading.
                }
            }
        }

#if NET5_0_OR_GREATER
#if NET9_0_OR_GREATER
        [FeatureGuard(typeof(RequiresAssemblyFilesAttribute))]
#endif
        internal static bool AssemblyFilesAvailable => AreAssemblyFilesAvailable(Assembly.GetEntryAssembly());

        [UnconditionalSuppressMessage(
            "SingleFile",
            "IL3000",
            Justification = "Assembly.Location is used only as the documented availability check for bundled assembly files.")]
        internal static bool AreAssemblyFilesAvailable(Assembly? entryAssembly)
        {
            var assembly = entryAssembly is null || entryAssembly.IsDynamic
                ? typeof(ReferencedAssemblyProvider).Assembly
                : entryAssembly;
            return !string.IsNullOrEmpty(assembly.Location);
        }
#endif

        private static IEnumerable<Assembly> GetApplicationPartAssemblies(Assembly assembly)
        {
            if (!assembly.IsDefined(typeof(ApplicationPartAttribute)))
            {
                return Array.Empty<Assembly>();
            }

#if NETCOREAPP3_1_OR_GREATER
            var assemblyContext = AssemblyLoadContext.GetLoadContext(assembly) ?? AssemblyLoadContext.Default;
#endif

            return ExpandApplicationParts(
                new[] { assembly }.Concat(assembly.GetCustomAttributes<ApplicationPartAttribute>()
                    .Select(name =>
#if NETCOREAPP3_1_OR_GREATER
                        assemblyContext.LoadFromAssemblyName(new AssemblyName(name.AssemblyName))
#else
                        Assembly.Load(new AssemblyName(name.AssemblyName))
#endif
                    )));

            static IEnumerable<Assembly> ExpandApplicationParts(IEnumerable<Assembly> assemblies)
            {
                if (assemblies == null)
                {
                    throw new ArgumentNullException(nameof(assemblies));
                }

                var relatedAssemblies = new HashSet<Assembly>();
                foreach (var assembly in assemblies)
                {
                    if (relatedAssemblies.Add(assembly))
                    {
                        ExpandAssembly(relatedAssemblies, assembly);
                    }
                }

                return relatedAssemblies.OrderBy(assembly => assembly.FullName, StringComparer.Ordinal);

                static void ExpandAssembly(HashSet<Assembly> assemblies, Assembly assembly)
                {
                    var attributes = assembly.GetCustomAttributes<ApplicationPartAttribute>().ToArray();
                    if (attributes.Length == 0)
                    {
                        return;
                    }

#if NETCOREAPP3_1_OR_GREATER
                    var assemblyContext = AssemblyLoadContext.GetLoadContext(assembly) ?? AssemblyLoadContext.Default;
#endif

                    foreach (var attribute in attributes)
                    {
                        var assemblyName = new AssemblyName(attribute.AssemblyName);
#if NETCOREAPP3_1_OR_GREATER
                        var referenced = assemblyContext.LoadFromAssemblyName(assemblyName);
#else
                        var referenced = Assembly.Load(assemblyName);
#endif
                        if (assemblies.Add(referenced))
                        {
                            ExpandAssembly(assemblies, referenced);
                        }
                    }
                }
            }
        }
    }
}
