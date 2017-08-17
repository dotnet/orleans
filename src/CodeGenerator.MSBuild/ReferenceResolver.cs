using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Build.Utilities;

namespace Microsoft.Orleans.CodeGenerator.MSBuild
{
    /// <summary>
    /// Simple class that loads the reference assemblies upon the AppDomain.AssemblyResolve
    /// </summary>
    internal class ReferenceResolver
    {
        private readonly TaskLoggingHelper log;

        /// <summary>
        /// Needs to be public so can be serialized accross the the app domain.
        /// </summary>
        public Dictionary<string, string> ReferenceAssemblyPaths { get; } = new Dictionary<string, string>();

        /// <summary>
        /// Inits the resolver
        /// </summary>
        /// <param name="referencedAssemblies">Full paths of referenced assemblies</param>
        public ReferenceResolver(IEnumerable<string> referencedAssemblies, TaskLoggingHelper log)
        {
            this.log = log;
            if (null == referencedAssemblies) return;

            foreach (var assemblyPath in referencedAssemblies) this.ReferenceAssemblyPaths[Path.GetFileNameWithoutExtension(assemblyPath)] = assemblyPath;
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
            Assembly assembly = null;
            string path;
            var asmName = new AssemblyName(args.Name);
            if (this.ReferenceAssemblyPaths.TryGetValue(asmName.Name, out path)) assembly = Assembly.LoadFrom(path);
            else this.log.LogWarning("Could not resolve {0}:", asmName.Name);
            return assembly;
        }
    }
}