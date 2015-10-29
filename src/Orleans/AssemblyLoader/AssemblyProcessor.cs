/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
            // If the assembly is loaded for reflection only or it does not reference Orleans, avoid processing it.
            if (assembly.ReflectionOnly || !TypeUtils.IsOrleansOrReferencesOrleans(assembly))
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

            // Code generation occurs in a self-contained assembly, so invoke it separately.
            CodeGeneratorManager.GenerateAndCacheCodeForAssembly(assembly);

            // Process each type in the assembly.
            var shouldProcessSerialization = SerializationManager.ShouldFindSerializationInfo(assembly);
            Type[] assemblyTypes;
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
                    Logger.Warn(
                        ErrorCode.Loader_TypeLoadError_5,
                        message,
                        exception);
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
