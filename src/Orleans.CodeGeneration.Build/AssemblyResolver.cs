using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.DependencyModel.Resolution;
using Orleans.Runtime;
#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Simple class that loads the reference assemblies upon the AppDomain.AssemblyResolve
    /// </summary>
    internal class AssemblyResolver
    {
        /// <summary>
        /// Needs to be public so can be serialized accross the the app domain.
        /// </summary>
        public Dictionary<string, string> ReferenceAssemblyPaths { get; } = new Dictionary<string, string>();

        private readonly bool installDefaultResolveHandler;
        private readonly ICompilationAssemblyResolver assemblyResolver;

        private readonly DependencyContext dependencyContext;
        private readonly DependencyContext resolverRependencyContext;
#if NETCOREAPP
        private readonly AssemblyLoadContext loadContext;
#endif

        public AssemblyResolver(string path, List<string> referencedAssemblies, bool installDefaultResolveHandler = true)
        {
            this.installDefaultResolveHandler = installDefaultResolveHandler;

            if (Path.GetFileName(path) == "Orleans.Core.dll")
                this.Assembly = typeof(RuntimeVersion).Assembly;
            else
                this.Assembly = Assembly.LoadFrom(path);

            this.dependencyContext = DependencyContext.Load(this.Assembly);
            this.resolverRependencyContext = DependencyContext.Load(typeof(AssemblyResolver).Assembly);
            var codegenPath = Path.GetDirectoryName(new Uri(typeof(AssemblyResolver).Assembly.CodeBase).LocalPath);
            this.assemblyResolver = new CompositeCompilationAssemblyResolver(
                new ICompilationAssemblyResolver[]
                {
                    new AppBaseCompilationAssemblyResolver(codegenPath),
                    new AppBaseCompilationAssemblyResolver(Path.GetDirectoryName(path)),
                    new ReferenceAssemblyPathResolver(),
                    new PackageCompilationAssemblyResolver()
                });

#if NETCOREAPP
            this.loadContext = AssemblyLoadContext.GetLoadContext(this.Assembly);

            if (this.loadContext == AssemblyLoadContext.Default)
            {
                if (this.installDefaultResolveHandler)
                {
                    AssemblyLoadContext.Default.Resolving += this.AssemblyLoadContextResolving;
                }
            }
            else
            {
                this.loadContext.Resolving += this.AssemblyLoadContextResolving;
            }
#else
            if (this.installDefaultResolveHandler)
            {
                AppDomain.CurrentDomain.AssemblyResolve += this.ResolveAssembly;
            }
#endif

            foreach (var assemblyPath in referencedAssemblies)
            {
                var libName = Path.GetFileNameWithoutExtension(assemblyPath);
                if (!string.IsNullOrWhiteSpace(libName)) this.ReferenceAssemblyPaths[libName] = assemblyPath;
                var asmName = AssemblyName.GetAssemblyName(assemblyPath);
                this.ReferenceAssemblyPaths[asmName.FullName] = assemblyPath;
            }
        }

        public Assembly Assembly { get; }

        public void Dispose()
        {
#if NETCOREAPP

            if (this.loadContext == AssemblyLoadContext.Default)
            {
                if (this.installDefaultResolveHandler)
                {
                    AssemblyLoadContext.Default.Resolving -= this.AssemblyLoadContextResolving;
                }
            }
            else
            {
                this.loadContext.Resolving -= this.AssemblyLoadContextResolving;
            }
#else
            if (this.installDefaultResolveHandler)
            {
                AppDomain.CurrentDomain.AssemblyResolve -= this.ResolveAssembly;
            }
#endif
        }

        /// <summary>
        /// Handles System.AppDomain.AssemblyResolve event of an System.AppDomain
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="args">The event data.</param>
        /// <returns>The assembly that resolves the type, assembly, or resource; 
        /// or null if theassembly cannot be resolved.
        /// </returns>
        public Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            var context = default(AssemblyLoadContext);

#if NETCOREAPP
            context = AssemblyLoadContext.GetLoadContext(args.RequestingAssembly);
#endif

            return this.AssemblyLoadContextResolving(context, new AssemblyName(args.Name));
        }

            public Assembly AssemblyLoadContextResolving(AssemblyLoadContext context, AssemblyName name)
        {
            // Attempt to resolve the library from one of the dependency contexts.
            var library = this.resolverRependencyContext?.RuntimeLibraries?.FirstOrDefault(NamesMatch)
                ?? this.dependencyContext?.RuntimeLibraries?.FirstOrDefault(NamesMatch);
            if (library != null)
            {
                var wrapper = new CompilationLibrary(
                    library.Type,
                    library.Name,
                    library.Version,
                    library.Hash,
                    library.RuntimeAssemblyGroups.SelectMany(g => g.AssetPaths),
                    library.Dependencies,
                    library.Serviceable);

                var assemblies = new List<string>();
                if (this.assemblyResolver.TryResolveAssemblyPaths(wrapper, assemblies))
                {
                    foreach (var asm in assemblies)
                    {
                        var assembly = TryLoadAssemblyFromPath(asm);
                        if (assembly != null) return assembly;
                    }
                }
            }

            if (this.ReferenceAssemblyPaths.TryGetValue(name.FullName, out var pathByFullName))
            {
                var assembly = TryLoadAssemblyFromPath(pathByFullName);
                if (assembly != null) return assembly;
            }

            if (this.ReferenceAssemblyPaths.TryGetValue(name.Name, out var pathByName))
            {
                //
                // Only try to load it if the resolved path is different than from before
                //

                if (String.Compare(pathByFullName, pathByName, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    var assembly = TryLoadAssemblyFromPath(pathByName);
                    if (assembly != null) return assembly;
                }
            }

            return null;

            bool NamesMatch(RuntimeLibrary runtime)
            {
                return string.Equals(runtime.Name, name.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        private Assembly TryLoadAssemblyFromPath(string path)
        {
            try
            {
#if NETCOREAPP
                return this.loadContext.LoadFromAssemblyPath(path);
#else
                return Assembly.LoadFrom(path);
#endif
            }
            catch
            {
                return null;
            }
        }

#if !NETCOREAPP
        internal class AssemblyLoadContext
        {
        }
#endif
    }
}