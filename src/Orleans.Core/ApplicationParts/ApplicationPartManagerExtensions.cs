using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.ApplicationParts;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for working with <see cref="ApplicationPartManager"/>.
    /// </summary>
    public static class ApplicationPartManagerExtensions
    {
        private static readonly string CoreAssemblyName = typeof(RuntimeVersion).Assembly.GetName().Name;
        private static readonly string AbstractionsAssemblyName = typeof(IGrain).Assembly.GetName().Name;
        private static readonly IEnumerable<string> NoReferenceComplaint = new[] { $"Assembly does not reference {CoreAssemblyName} or {AbstractionsAssemblyName}" };
        private static readonly object ApplicationPartsKey = new object();

        /// <summary>
        /// Returns the <see cref="ApplicationPartManager"/> for the provided context.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>The <see cref="ApplicationPartManager"/> belonging to the provided context.</returns>
        public static ApplicationPartManager GetApplicationPartManager(this HostBuilderContext context) => GetApplicationPartManager(context.Properties);

        /// <summary>
        /// Creates and populates a feature.
        /// </summary>
        /// <typeparam name="TFeature">The feature.</typeparam>
        /// <param name="applicationPartManager">The application part manager.</param>
        /// <returns>The populated feature.</returns>
        public static TFeature CreateAndPopulateFeature<TFeature>(this ApplicationPartManager applicationPartManager) where TFeature : new()
        {
            var result = new TFeature();
            applicationPartManager.PopulateFeature(result);
            return result;
        }

        /// <summary>
        /// Returns the <see cref="ApplicationPartManager"/> for the provided properties.
        /// </summary>
        /// <param name="properties">The properties.</param>
        /// <returns>The <see cref="ApplicationPartManager"/> belonging to the provided properties.</returns>
        internal static ApplicationPartManager GetApplicationPartManager(IDictionary<object, object> properties)
        {
            ApplicationPartManager result;
            if (properties.TryGetValue(ApplicationPartsKey, out var value))
            {
                result = value as ApplicationPartManager;
                if (result == null) throw new InvalidOperationException($"The ApplicationPartManager value is of the wrong type {value.GetType()}. It should be {nameof(ApplicationPartManager)}");
            }
            else
            {
                properties[ApplicationPartsKey] = result = new ApplicationPartManager();
            }

            return result;
        }

        /// <summary>
        /// Adds the provided <paramref name="assembly"/> as an application part.
        /// </summary>
        /// <param name="applicationPartManager">The application part manager.</param>
        /// <param name="assembly">The assembly.</param>
        public static void AddApplicationPart(this ApplicationPartManager applicationPartManager, Assembly assembly)
        {
            if (applicationPartManager == null)
            {
                throw new ArgumentNullException(nameof(applicationPartManager));
            }

            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            applicationPartManager.AddApplicationPart(new AssemblyPart(assembly));
        }

        /// <summary>
        /// Adds all assemblies in the current <see cref="AppDomain"/> as application parts.
        /// </summary>
        /// <param name="applicationPartManager">The application part manager.</param>
        /// <param name="loadReferencedAssemblies">Whether or not try to load all referenced assemblies.</param>
        public static void AddApplicationPartsFromAppDomain(this ApplicationPartManager applicationPartManager, bool loadReferencedAssemblies = true)
        {
            if (applicationPartManager == null)
            {
                throw new ArgumentNullException(nameof(applicationPartManager));
            }

            var processedAssemblies = new HashSet<Assembly>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (processedAssemblies.Add(assembly) && loadReferencedAssemblies)
                {
                    LoadReferencedAssemblies(assembly, processedAssemblies);
                }
            }

            foreach (var assembly in processedAssemblies)
            {
                applicationPartManager.AddApplicationPart(assembly);
            }
        }

        /// <summary>
        /// Adds all assemblies referenced by the provided <paramref name="assembly"/> as application parts.
        /// </summary>
        /// <param name="applicationPartManager">The application part manager.</param>
        /// <param name="assembly">The assembly</param>
        public static void AddApplicationPartsFromReferences(this ApplicationPartManager applicationPartManager, Assembly assembly)
        {
            if (applicationPartManager == null)
            {
                throw new ArgumentNullException(nameof(applicationPartManager));
            }

            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            var processedAssemblies = new HashSet<Assembly>();
            processedAssemblies.Add(assembly);
            LoadReferencedAssemblies(assembly, processedAssemblies);

            foreach (var asm in processedAssemblies)
            {
                applicationPartManager.AddApplicationPart(asm);
            }
        }

        /// <summary>
        /// Attempts to load all assemblies in the application base path and add them as application parts.
        /// </summary>
        /// <param name="applicationPartManager">The application part manager.</param>
        public static void AddApplicationPartsFromBasePath(this ApplicationPartManager applicationPartManager)
        {
            var appDomainBase = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(appDomainBase) && Directory.Exists(appDomainBase))
            {
                applicationPartManager.AddApplicationPartsFromProbingPath(appDomainBase);
            }
        }

        /// <summary>
        /// Attempts to load and add assemblies from the specified directories as application parts.
        /// </summary>
        /// <param name="applicationPartManager">The application part manager.</param>
        /// <param name="directories">The directories to search.</param>
        private static void AddApplicationPartsFromProbingPath(this ApplicationPartManager applicationPartManager, params string[] directories)
        {
            if (directories == null) throw new ArgumentNullException(nameof(directories));
            var dirs = new Dictionary<string, SearchOption>();
            foreach (var dir in directories)
            {
                dirs[dir] = SearchOption.TopDirectoryOnly;
            }

            AssemblyLoaderPathNameCriterion[] excludeCriteria =
            {
                AssemblyLoaderCriteria.ExcludeResourceAssemblies
            };

            AssemblyLoaderReflectionCriterion[] loadCriteria =
            {
                AssemblyLoaderReflectionCriterion.NewCriterion(ReferencesOrleansOrAbstractionsAssemblyPredicate)
            };

            var loadedAssemblies = AssemblyLoader.LoadAssemblies(dirs, excludeCriteria, loadCriteria, new LoggerWrapper(nameof(ApplicationPartManagerExtensions), NullLoggerFactory.Instance));
            foreach (var assembly in loadedAssemblies)
            {
                applicationPartManager.AddApplicationPart(assembly);
            }

            // Returns true if the provided assembly references the Orleans core or abstractions assembly.
            bool ReferencesOrleansOrAbstractionsAssemblyPredicate(Assembly assembly, out IEnumerable<string> complaints)
            {
                var referencesOrleans = assembly.GetReferencedAssemblies().Any(ReferencesOrleansOrAbstractions);
                complaints = referencesOrleans ? null : NoReferenceComplaint;
                return referencesOrleans;

                bool ReferencesOrleansOrAbstractions(AssemblyName reference)
                {
                    return string.Equals(reference.Name, CoreAssemblyName, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(reference.Name, AbstractionsAssemblyName, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private static void LoadReferencedAssemblies(Assembly asm, HashSet<Assembly> loadedAssemblies)
        {
            if (asm == null)
            {
                throw new ArgumentNullException(nameof(asm));
            }

            if (loadedAssemblies == null)
            {
                throw new ArgumentNullException(nameof(loadedAssemblies));
            }

            var referenced = asm.GetReferencedAssemblies();
            foreach (var asmName in referenced)
            {
                try
                {
                    var refAsm = Assembly.Load(asmName);
                    if (loadedAssemblies.Add(refAsm)) LoadReferencedAssemblies(refAsm, loadedAssemblies);
                }
                catch
                {
                    // Ignore loading exceptions.
                }
            }
        }
    }
}