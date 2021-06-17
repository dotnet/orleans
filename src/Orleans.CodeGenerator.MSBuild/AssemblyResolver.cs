using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.DependencyModel.Resolution;

namespace Orleans.CodeGenerator.MSBuild
{
    /// <summary>
    /// Simple class that loads the reference assemblies upon the AppDomain.AssemblyResolve
    /// </summary>
    internal class AssemblyResolver : IDisposable
    {
        private readonly ICompilationAssemblyResolver _assemblyResolver;
        
        private readonly DependencyContext _resolverRependencyContext;
        private readonly AssemblyLoadContext _loadContext;

        public AssemblyResolver()
        {
            _resolverRependencyContext = DependencyContext.Load(typeof(AssemblyResolver).Assembly);
            var codegenPath = Path.GetDirectoryName(new Uri(typeof(AssemblyResolver).Assembly.Location).LocalPath);
            _assemblyResolver = new CompositeCompilationAssemblyResolver(
                new ICompilationAssemblyResolver[]
                {
                    new AppBaseCompilationAssemblyResolver(codegenPath),
                    new ReferenceAssemblyPathResolver(),
                    new PackageCompilationAssemblyResolver()
                });

            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            _loadContext = AssemblyLoadContext.GetLoadContext(typeof(AssemblyResolver).Assembly);
            _loadContext.Resolving += AssemblyLoadContextResolving;
            if (_loadContext != AssemblyLoadContext.Default)
            {
                AssemblyLoadContext.Default.Resolving += AssemblyLoadContextResolving;
            }
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;

            _loadContext.Resolving -= AssemblyLoadContextResolving;
            if (_loadContext != AssemblyLoadContext.Default)
            {
                AssemblyLoadContext.Default.Resolving -= AssemblyLoadContextResolving;
            }
        }

        /// <summary>
        /// Handles System.AppDomain.AssemblyResolve event of an System.AppDomain
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="args">The event data.</param>
        /// <returns>The assembly that resolves the type, assembly, or resource; 
        /// or null if theassembly cannot be resolved.
        /// </returns>
        public Assembly ResolveAssembly(object sender, ResolveEventArgs args) => AssemblyLoadContextResolving(null, new AssemblyName(args.Name));

        public Assembly AssemblyLoadContextResolving(AssemblyLoadContext context, AssemblyName name)
        {
            // Attempt to resolve the library from one of the dependency contexts.
            var library = _resolverRependencyContext?.RuntimeLibraries?.FirstOrDefault(NamesMatch);
            if (library is null)
            {
                return null;
            }

            var wrapper = new CompilationLibrary(
                library.Type,
                library.Name,
                library.Version,
                library.Hash,
                library.RuntimeAssemblyGroups.SelectMany(g => g.AssetPaths),
                library.Dependencies,
                library.Serviceable);

            var assemblies = new List<string>();
            if (_assemblyResolver.TryResolveAssemblyPaths(wrapper, assemblies))
            {
                foreach (var asm in assemblies)
                {
                    var assembly = TryLoadAssemblyFromPath(asm);
                    if (assembly != null)
                    {
                        return assembly;
                    }
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
                return _loadContext.LoadFromAssemblyPath(path);
            }
            catch
            {
                return null;
            }
        }
    }
}