namespace Orleans.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Linq;

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
        private static readonly TraceLogger Logger;
        
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
            Logger = TraceLogger.GetLogger("AssemblyProcessor");
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

                // initialize serialization for all assemblies to be loaded.
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

                var assemblies = AppDomain.CurrentDomain.GetAssemblies();

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
            // If the assembly is loaded for reflection only avoid processing it.
            if (assembly.ReflectionOnly)
            {
                return;
            }

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
            var shouldProcessSerialization = SerializationManager.ShouldFindSerializationInfo(assembly);
            TypeInfo[] assemblyTypes;
            try
            {
                assemblyTypes = assembly.DefinedTypes.ToArray();
            }
            catch (Exception exception)
            {
                if (Logger.IsWarning)
                {
                    var message =
                        string.Format(
                            "AssemblyLoader encountered an exception loading types from assembly '{0}': {1}",
                            assembly.FullName,
                            exception);
                    Logger.Warn(ErrorCode.Loader_TypeLoadError_5, message, exception);
                }

                return;
            }

            // Process each type in the assembly.
            foreach (var type in assemblyTypes)
            {
                if (shouldProcessSerialization)
                {
                    SerializationManager.FindSerializationInfo(type);
                }

                GrainFactory.FindSupportClasses(type);
            }
        }
    }
}
