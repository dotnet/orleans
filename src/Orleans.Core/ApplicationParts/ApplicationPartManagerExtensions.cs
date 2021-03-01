using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Hosting;
using Orleans.ApplicationParts;
using Orleans.Metadata;
using System.Runtime.InteropServices;

namespace Orleans
{
    /// <summary>
    /// Extensions for working with <see cref="ApplicationPartManager"/>.
    /// </summary>
    public static class ApplicationPartManagerExtensions
    {
        /// <summary>
        /// Returns the <see cref="IApplicationPartManager"/> for the provided context.
        /// </summary>
        /// <returns>The <see cref="IApplicationPartManager"/> belonging to the provided context.</returns>
        public static IApplicationPartManager GetApplicationPartManager(this IServiceProvider services) => services.GetRequiredService<IApplicationPartManager>();

        /// <summary>
        /// Returns the <see cref="IApplicationPartManager"/> for the provided context.
        /// </summary>
        /// <returns>The <see cref="IApplicationPartManager"/> belonging to the provided context.</returns>
        public static IApplicationPartManager GetApplicationPartManager(this IServiceCollection services)
        {
            var manager = GetServiceFromCollection<IApplicationPartManager>(services);

            if (manager is null)
            {
                manager = new ApplicationPartManager();
                services.AddSingleton(manager);
                services.AddSingleton<IApplicationPartManager>(manager);

                manager.AddFromDependencyContext();
                manager.AddFromAssemblyLoadContext();

                if (GetServiceFromCollection<IHostEnvironment>(services)?.ApplicationName is string applicationName)
                {
                    try
                    {
                        var entryAssembly = Assembly.Load(new AssemblyName(applicationName));
                        manager.AddApplicationPart(entryAssembly);
                    }
                    catch
                    {
                        // Ignore exceptions here.
                    }
                }

                if (Assembly.GetEntryAssembly() is Assembly asm)
                {
                    manager.AddApplicationPart(asm);
                }
            }

            return manager;
        }

        /// <summary>
        /// Creates and populates a feature.
        /// </summary>
        /// <typeparam name="TFeature">The feature.</typeparam>
        /// <param name="applicationPartManager">The application part manager.</param>
        /// <returns>The populated feature.</returns>
        public static TFeature CreateAndPopulateFeature<TFeature>(this IApplicationPartManager applicationPartManager) where TFeature : new()
        {
            var result = new TFeature();
            applicationPartManager.PopulateFeature(result);
            return result;
        }

        /// <summary>
        /// Removes all application parts.
        /// </summary>
        public static IApplicationPartManager ClearApplicationParts(this IApplicationPartManager applicationPartManager) => applicationPartManager.RemoveApplicationParts(_ => true);

        /// <summary>
        /// Removes all feature providers.
        /// </summary>
        public static IApplicationPartManager ClearFeatureProviders(this IApplicationPartManager applicationPartManager) => applicationPartManager.RemoveFeatureProviders(_ => true);

        /// <summary>
        /// Adds the provided assembly to the builder as a framework assembly.
        /// </summary>
        /// <param name="manager">The builder.</param>
        /// <param name="assembly">The assembly.</param>
        /// <returns>The builder with the additionally added assembly.</returns>
        public static IApplicationPartManager AddFrameworkPart(this IApplicationPartManager manager, Assembly assembly)
        {
            if (manager == null)
            {
                throw new ArgumentNullException(nameof(manager));
            }

            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            return manager.AddApplicationPart(new AssemblyPart(assembly) { IsFrameworkAssembly = true });
        }

        /// <summary>
        /// Adds the provided assembly to the builder.
        /// </summary>
        /// <param name="manager">The builder.</param>
        /// <param name="assembly">The assembly.</param>
        /// <returns>The builder with the additionally added assembly.</returns>
        public static IApplicationPartManager AddApplicationPart(this IApplicationPartManager manager, Assembly assembly)
        {
            if (manager == null)
            {
                throw new ArgumentNullException(nameof(manager));
            }

            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            // Always add the provided part, whether or not it contains generated code. 
            manager.AddApplicationPart(new AssemblyPart(assembly));

            // Add all referenced application parts.
            foreach (var referencedAsm in GetApplicationPartAssemblies(assembly))
            {
                var part = new AssemblyPart(referencedAsm);
                if (manager.ApplicationParts.Contains(part))
                {
                    continue;
                }

                manager.AddApplicationPart(part);
            }

            return manager;
        }

        public static IApplicationPartManager AddFromAssemblyLoadContext(this IApplicationPartManager manager, Assembly assembly = null)
        {
            assembly ??= typeof(ApplicationPartManagerExtensions).Assembly;
            var assemblies = new HashSet<Assembly>();
            var context = AssemblyLoadContext.GetLoadContext(assembly);
            foreach (var asm in context.Assemblies)
            {
                // Skip assemblies which have not had code generation executed against them and already-seen assemblies.
                if (!asm.IsDefined(typeof(ApplicationPartAttribute)) || !assemblies.Add(asm))
                {
                    continue;
                } 

                manager.AddApplicationPart(asm);
            }

            return manager;
        }

        /// <summary>
        /// Adds all assemblies referencing Orleans found in the provided assembly's <see cref="DependencyContext"/>.
        /// </summary>
        /// <param name="manager">The builder.</param>
        /// <param name="assembly">Assembly to start looking for application parts from.</param>
        /// <returns>The builder with the additionally included assemblies.</returns>
        public static IApplicationPartManager AddFromDependencyContext(this IApplicationPartManager manager, Assembly assembly = null)
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
                manager = manager.AddApplicationPart(assembly);
                assemblies.Add(assembly);
            }

            if (dependencyContext == null) return manager;

            var assemblyContext = assembly is not null
                ? AssemblyLoadContext.GetLoadContext(assembly) ?? AssemblyLoadContext.Default
                : AssemblyLoadContext.Default;

            foreach (var lib in dependencyContext.RuntimeLibraries)
            {
                if (!lib.Name.Contains("Orleans") && !lib.Dependencies.Any(dep => dep.Name.Contains("Orleans"))) continue;

                try
                {
#if NET5_0
                    var name = lib.GetRuntimeAssemblyNames(dependencyContext, RuntimeInformation.RuntimeIdentifier).FirstOrDefault();
                    if (name is null) continue;
                    var asm = assemblyContext.LoadFromAssemblyName(name);
#else
                    var name = lib.GetRuntimeAssemblyNames(dependencyContext, Microsoft.DotNet.PlatformAbstractions.RuntimeEnvironment.GetRuntimeIdentifier()).FirstOrDefault();
                    if (name is null) continue;
                    var asm = assemblyContext.LoadFromAssemblyName(name);
#endif
                    if (asm.IsDefined(typeof(ApplicationPartAttribute)) && assemblies.Add(asm))
                    {
                        manager.AddApplicationPart(new AssemblyPart(asm));
                    }
                }
                catch
                {
                    // Ignore any exceptions thrown during non-explicit assembly loading.
                }
            }

            return manager;
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

        private static T GetServiceFromCollection<T>(IServiceCollection services)
        {
            return (T)services
                .LastOrDefault(d => d.ServiceType == typeof(T))
                ?.ImplementationInstance;
        }
    }
}