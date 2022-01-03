using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Orleans.Serialization
{
    internal static class ReferencedAssemblyHelper
    {
        public static IEnumerable<Assembly> GetRelevantAssemblies(this IServiceCollection services)
        {
            var parts = new HashSet<Assembly>();

            AddFromDependencyContext(parts);

#if NETCOREAPP
            AddFromAssemblyLoadContext(parts);
#endif

            foreach (var loadedAsm in AppDomain.CurrentDomain.GetAssemblies())
            {
                AddAssembly(parts, loadedAsm);
            }

            return parts;
        }

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

#if NETCOREAPP
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

        public static void AddFromAssemblyLoadContext(HashSet<Assembly> parts, Assembly assembly = null)
        {
            assembly ??= typeof(ReferencedAssemblyHelper).Assembly;
            var assemblies = new HashSet<Assembly>();
            var context = AssemblyLoadContext.GetLoadContext(assembly);
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

        public static void AddFromDependencyContext(HashSet<Assembly> parts, Assembly assembly = null)
        {
            assembly ??= Assembly.GetEntryAssembly();
            DependencyContext dependencyContext;
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

#if NETCOREAPP
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
#if NET5_0
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

#if NETCOREAPP
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

        private static IEnumerable<Assembly> GetApplicationPartAssemblies(Assembly assembly)
        {
            if (!assembly.IsDefined(typeof(ApplicationPartAttribute)))
            {
                return Array.Empty<Assembly>();
            }

            return ExpandApplicationParts(
                new[] { assembly }.Concat(assembly.GetCustomAttributes<ApplicationPartAttribute>()
                    .Select(name => Assembly.Load(new AssemblyName(name.AssemblyName)))));

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

                    foreach (var attribute in attributes)
                    {
                        var referenced = Assembly.Load(new AssemblyName(attribute.AssemblyName));
                        if (assemblies.Add(referenced))
                        {
                            ExpandAssembly(assemblies, referenced);
                        }
                    }
                }
            }
        }

        private static T GetServiceFromCollection<T>(IServiceCollection services) => (T)services.LastOrDefault(d => d.ServiceType == typeof(T))?.ImplementationInstance;
    }
}
