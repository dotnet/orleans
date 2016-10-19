namespace Orleans.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Orleans.CodeGeneration;
    using Orleans.Serialization;

    /// <summary>
    /// The assembly processor.
    /// </summary>
    internal class AssemblyProcessor
    {
        /// <summary>
        /// The collection of assemblies which have already been processed.
        /// </summary>
        private static readonly HashSet<Assembly> ProcessedAssemblies = new HashSet<Assembly>();

        /// <summary>
        /// The logger.
        /// </summary>
        private static readonly Logger Logger;
        
        /// <summary>
        /// The initialization lock.
        /// </summary>
        private static readonly object InitializationLock = new object();

        /// <summary>
        /// Whether or not this class has been initialized.
        /// </summary>
        private static bool initialized;

        /// <summary>
        /// Initializes static members of the <see cref="AssemblyProcessor"/> class.
        /// </summary>
        static AssemblyProcessor()
        {
            Logger = LogManager.GetLogger("AssemblyProcessor");
        }

        /// <summary>
        /// Initializes this instance.
        /// </summary>
        public static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            lock (InitializationLock)
            {
                if (initialized)
                {
                    return;
                }

                // load the code generator before intercepting assembly loading
                CodeGeneratorManager.Initialize(); 

                // initialize serialization for all assemblies to be loaded.
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

                // initialize serialization for already loaded assemblies.
                CodeGeneratorManager.GenerateAndCacheCodeForAllAssemblies();
                foreach (var assembly in assemblies)
                {
                    ProcessAssembly(assembly);
                }

                initialized = true;
            }
        }

        /// <summary>
        /// Handles <see cref="AppDomain.AssemblyLoad"/> events.
        /// </summary>
        /// <param name="sender">The sender of the event.</param>
        /// <param name="args">The event arguments.</param>
        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            ProcessAssembly(args.LoadedAssembly);
        }

        /// <summary>
        /// Processes the provided assembly.
        /// </summary>
        /// <param name="assembly">The assembly to process.</param>
        private static void ProcessAssembly(Assembly assembly)
        {
            string assemblyName = assembly.GetName().Name;
            if (Logger.IsVerbose3)
            {
                Logger.Verbose3("Processing assembly {0}", assemblyName);
            }

#if !NETSTANDARD
            // If the assembly is loaded for reflection only avoid processing it.
            if (assembly.ReflectionOnly)
            {
                return;
            }
#endif

            // Don't bother re-processing an assembly we've already scanned
            lock (ProcessedAssemblies)
            {
                if (!ProcessedAssemblies.Add(assembly))
                {
                    return;
                }
            }

            // If the assembly does not reference Orleans, avoid generating code for it.
            if (TypeUtils.IsOrleansOrReferencesOrleans(assembly))
            {
                // Code generation occurs in a self-contained assembly, so invoke it separately.
                CodeGeneratorManager.GenerateAndCacheCodeForAssembly(assembly);
            }

            // Process each type in the assembly.
            var assemblyTypes = TypeUtils.GetDefinedTypes(assembly, Logger).ToArray();

            // Process each type in the assembly.
            foreach (TypeInfo typeInfo in assemblyTypes)
            {
                try
                {
                    var type = typeInfo.AsType();
                    string typeName = typeInfo.FullName;
                    if (Logger.IsVerbose3)
                    {
                        Logger.Verbose3("Processing type {0}", typeName);
                    }

                    SerializationManager.FindSerializationInfo(type);
    
                    GrainFactory.FindSupportClasses(type);
                }
                catch (Exception exception)
                {
                    Logger.Error(ErrorCode.SerMgr_TypeRegistrationFailure, "Failed to load type " + typeInfo.FullName + " in assembly " + assembly.FullName + ".", exception);
                }
            }
        }
    }
}
