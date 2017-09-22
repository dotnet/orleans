using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Runtime.Configuration;
using Orleans.CodeGenerator;
using Orleans.Serialization;

#if NET461
namespace Orleans.CodeGeneration
{
    public class AppDomainCodeGenerator : MarshalByRefObject
    {
        public static string GenerateCode(CodeGenOptions options)
        {
            AppDomain appDomain = null;
            try
            {
                var assembly = typeof(AppDomainCodeGenerator).GetTypeInfo().Assembly;

                // Create AppDomain.
                var appDomainSetup = new AppDomainSetup
                {
                    ApplicationBase = Path.GetDirectoryName(assembly.Location),
                    DisallowBindingRedirects = false,
                    ConfigurationFile = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile
                };
                appDomain = AppDomain.CreateDomain("Orleans-CodeGen Domain", null, appDomainSetup);

                // Set up assembly resolver
                var refResolver = new ReferenceResolver(options.ReferencedAssemblies);
                appDomain.AssemblyResolve += refResolver.ResolveAssembly;

                // Create an instance 
                var generator =
                    (AppDomainCodeGenerator)
                    appDomain.CreateInstanceAndUnwrap(
                        assembly.FullName,
                        // ReSharper disable once AssignNullToNotNullAttribute
                        typeof(AppDomainCodeGenerator).FullName);

                // Call a method 
                return generator.GenerateCodeInternal(options);
            }
            finally
            {
                if (appDomain != null) AppDomain.Unload(appDomain); // Unload the AppDomain
            }
        }

        private string GenerateCodeInternal(CodeGenOptions options)
        {
            // Load input assembly 
            // special case Orleans.dll because there is a circular dependency.
            var assemblyName = AssemblyName.GetAssemblyName(options.InputAssembly.FullName);
            var grainAssembly = Path.GetFileName(options.InputAssembly.FullName) != "Orleans.dll"
                ? Assembly.LoadFrom(options.InputAssembly.FullName)
                : Assembly.Load(assemblyName);

            // Create directory for output file if it does not exist
            var outputFileDirectory = Path.GetDirectoryName(options.OutputFileName);

            if (!String.IsNullOrEmpty(outputFileDirectory) && !Directory.Exists(outputFileDirectory))
            {
                Directory.CreateDirectory(outputFileDirectory);
            }

            var config = new ClusterConfiguration();
            var codeGenerator = new RoslynCodeGenerator(new SerializationManager(null, config.Globals, config.Defaults));
            return codeGenerator.GenerateSourceForAssembly(grainAssembly);
        }

        /// <summary>
        /// Simple class that loads the reference assemblies upon the AppDomain.AssemblyResolve
        /// </summary>
        [Serializable]
        public class ReferenceResolver
        {
            /// <summary>
            /// Needs to be public so can be serialized accross the the app domain.
            /// </summary>
            public Dictionary<string, string> ReferenceAssemblyPaths { get; } = new Dictionary<string, string>();

            /// <summary>
            /// Inits the resolver
            /// </summary>
            /// <param name="referencedAssemblies">Full paths of referenced assemblies</param>
            public ReferenceResolver(IEnumerable<string> referencedAssemblies)
            {
                if (null == referencedAssemblies) return;

                foreach (var assemblyPath in referencedAssemblies) ReferenceAssemblyPaths[Path.GetFileNameWithoutExtension(assemblyPath)] = assemblyPath;
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
                if (ReferenceAssemblyPaths.TryGetValue(asmName.Name, out path)) assembly = Assembly.LoadFrom(path);
                else Console.WriteLine("Could not resolve {0}:", asmName.Name);
                return assembly;
            }
        }

    }
}
#endif
