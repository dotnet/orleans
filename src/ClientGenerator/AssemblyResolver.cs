using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.DependencyModel.Resolution;

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
        private readonly AssemblyLoadContext loadContext;

        public AssemblyResolver(string path, List<string> referencedAssemblies, Action<string> log)
        {
            this.log = log;
            if (Path.GetFileName(path) == "Orleans.dll")  this.Assembly = Assembly.Load("Orleans");
            else this.Assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            this.dependencyContext = DependencyContext.Load(this.Assembly);

            this.assemblyResolver = new CompositeCompilationAssemblyResolver(
                new ICompilationAssemblyResolver[]
                {
                    new AppBaseCompilationAssemblyResolver(Path.GetDirectoryName(path)),
                    new ReferenceAssemblyPathResolver(),
                    new PackageCompilationAssemblyResolver()
                });

            this.loadContext = AssemblyLoadContext.GetLoadContext(this.Assembly);
            AppDomain.CurrentDomain.AssemblyResolve += this.ResolveAssembly;
            this.loadContext.Resolving += this.AssemblyLoadContextResolving;
            if (this.loadContext != AssemblyLoadContext.Default)
            {
                AssemblyLoadContext.Default.Resolving += this.AssemblyLoadContextResolving;
            }
            
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
            this.loadContext.Resolving -= this.AssemblyLoadContextResolving;
            if (this.loadContext != AssemblyLoadContext.Default)
            {
                AssemblyLoadContext.Default.Resolving -= this.AssemblyLoadContextResolving;
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
        public Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            return this.AssemblyLoadContextResolving(AssemblyLoadContext.GetLoadContext(args.RequestingAssembly), new AssemblyName(args.Name));
        }

        public Assembly AssemblyLoadContextResolving(AssemblyLoadContext context, AssemblyName name)
        {
            bool NamesMatch(RuntimeLibrary runtime)
            {
                return string.Equals(runtime.Name, name.Name, StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                var library = this.dependencyContext?.RuntimeLibraries?.FirstOrDefault(NamesMatch);
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
                    this.assemblyResolver.TryResolveAssemblyPaths(wrapper, assemblies);
                    foreach (var asm in assemblies)
                    {
                        try
                        {
                            return this.loadContext.LoadFromAssemblyPath(asm);
                        }
                        catch (Exception exception)
                        {
                            this.log($"Failed to load assembly {name} from path {asm}: {exception}");
                        }
                    }
                }

                Assembly assembly = null;
                if (this.ReferenceAssemblyPaths.TryGetValue(name.FullName, out string path)) assembly = this.loadContext.LoadFromAssemblyPath(path);
                else if (this.ReferenceAssemblyPaths.TryGetValue(name.Name, out path)) assembly = this.loadContext.LoadFromAssemblyPath(path);
                else this.log($"Could not resolve {name.Name}");
                return assembly;
            }
            catch (Exception exception)
            {
                this.log($"Exception in AssemblyLoadContextResolving for assembly {name}: {exception}");
                throw;
            }
        }
    }
}