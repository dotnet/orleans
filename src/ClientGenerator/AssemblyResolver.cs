using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.DependencyModel.Resolution;
using Orleans.Runtime;
#if NETCOREAPP2_0
using System.Runtime.Loader;
#endif

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Simple class that loads the reference assemblies upon the AppDomain.AssemblyResolve
    /// </summary>
    internal class AssemblyResolver
    {
        private readonly Action<string> log;

        /// <summary>
        /// Needs to be public so can be serialized accross the the app domain.
        /// </summary>
        public Dictionary<string, string> ReferenceAssemblyPaths { get; } = new Dictionary<string, string>();
        
        private readonly ICompilationAssemblyResolver assemblyResolver;

        private readonly DependencyContext dependencyContext;
        private readonly DependencyContext resolverRependencyContext;
#if NETCOREAPP2_0
        private readonly AssemblyLoadContext loadContext;
#endif

        public AssemblyResolver(string path, List<string> referencedAssemblies, Action<string> log)
        {
            this.log = log;
            if (Path.GetFileName(path) == "Orleans.dll")  this.Assembly = typeof(RuntimeVersion).Assembly;
            else this.Assembly = Assembly.LoadFrom(path);

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

            AppDomain.CurrentDomain.AssemblyResolve += this.ResolveAssembly;
#if NETCOREAPP2_0
            this.loadContext = AssemblyLoadContext.GetLoadContext(this.Assembly);
            this.loadContext.Resolving += this.AssemblyLoadContextResolving;
            if (this.loadContext != AssemblyLoadContext.Default)
            {
                AssemblyLoadContext.Default.Resolving += this.AssemblyLoadContextResolving;
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
            AppDomain.CurrentDomain.AssemblyResolve -= this.ResolveAssembly;

#if NETCOREAPP2_0
            this.loadContext.Resolving -= this.AssemblyLoadContextResolving;
            if (this.loadContext != AssemblyLoadContext.Default)
            {
                AssemblyLoadContext.Default.Resolving -= this.AssemblyLoadContextResolving;
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
#if NETCOREAPP2_0
            var context = AssemblyLoadContext.GetLoadContext(args.RequestingAssembly);
#else
            AssemblyLoadContext context = null;
#endif
            return this.AssemblyLoadContextResolving(context, new AssemblyName(args.Name));
        }

        public Assembly AssemblyLoadContextResolving(AssemblyLoadContext context, AssemblyName name)
        {
            try
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

                if (this.ReferenceAssemblyPaths.TryGetValue(name.FullName, out string path))
                {
                    var assembly = TryLoadAssemblyFromPath(path);
                    if (assembly != null) return assembly;
                }

                if (this.ReferenceAssemblyPaths.TryGetValue(name.Name, out path))
                {
                    var assembly = TryLoadAssemblyFromPath(path);
                    if (assembly != null) return assembly;
                }

                this.log($"Could not resolve {name.Name}");
                return null;
            }
            catch (Exception exception)
            {
                this.log($"Exception in AssemblyLoadContextResolving for assembly {name}: {exception}");
                throw;
            }

            Assembly TryLoadAssemblyFromPath(string path)
            {
                try
                {
                    this.log($"Trying to load assembly from path {path}");
#if NETCOREAPP2_0
                    return this.loadContext.LoadFromAssemblyPath(path);
#else
                    return Assembly.LoadFrom(path);
#endif
                }
                catch (Exception exception)
                {
                    this.log($"Failed to load assembly {name} from path {path}: {exception}");
                }

                return null;
            }
            
            bool NamesMatch(RuntimeLibrary runtime)
            {
                return string.Equals(runtime.Name, name.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

#if !NETCOREAPP2_0
        internal class AssemblyLoadContext
        {
        }
#endif
    }
}