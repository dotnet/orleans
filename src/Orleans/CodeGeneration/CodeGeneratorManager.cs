namespace Orleans.CodeGeneration
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Reflection;

    using Orleans.Runtime;

    /// <summary>
    /// Methods for invoking code generation.
    /// </summary>
    internal static class CodeGeneratorManager
    {
        /// <summary>
        /// The name of the code generator assembly.
        /// </summary>
        private const string CodeGenAssemblyName = "OrleansCodeGenerator";

        /// <summary>
        /// The runtime code generator.
        /// </summary>
        private static IRuntimeCodeGenerator CodeGeneratorInstance;

        /// <summary>
        /// The code generator cache.
        /// </summary>
        private static ICodeGeneratorCache CodeGeneratorCacheInstance;

        /// <summary>
        /// The log.
        /// </summary>
        private static readonly TraceLogger Log = TraceLogger.GetLogger("CodeGenerator");

        /// <summary>
        /// Empty generated assemblies.
        /// </summary>
        private static readonly ReadOnlyDictionary<string, byte[]> EmptyGeneratedAssemblies =
            new ReadOnlyDictionary<string, byte[]>(new Dictionary<string, byte[]>());

        /// <summary>
        /// Loads the code generator on demand
        /// </summary>
        public static void Initialize()
        {
            CodeGeneratorInstance = LoadCodeGenerator();
            CodeGeneratorCacheInstance = CodeGeneratorInstance as ICodeGeneratorCache;
        }

        /// <summary>
        /// Ensures code for the <paramref name="input"/> assembly has been generated and loaded.
        /// </summary>
        /// <param name="input">
        /// The input assembly.
        /// </param>
        public static void GenerateAndCacheCodeForAssembly(Assembly input)
        {
            if (CodeGeneratorInstance != null)
            {
                CodeGeneratorInstance.GenerateAndLoadForAssembly(input);
            }
        }

        /// <summary>
        /// Ensures code for all currently loaded assemblies has been generated and loaded.
        /// </summary>
        public static void GenerateAndCacheCodeForAllAssemblies()
        {
            if (CodeGeneratorInstance != null)
            {
                CodeGeneratorInstance.GenerateAndLoadForAssemblies(AppDomain.CurrentDomain.GetAssemblies());
            }
        }

        /// <summary>
        /// Returns the collection of generated assemblies as pairs of target assembly name to raw assembly bytes.
        /// </summary>
        /// <returns>The collection of generated assemblies.</returns>
        public static IDictionary<string, byte[]> GetGeneratedAssemblies()
        {
            if (CodeGeneratorCacheInstance != null)
            {
                return CodeGeneratorCacheInstance.GetGeneratedAssemblies();
            }

            return EmptyGeneratedAssemblies;
        }

        /// <summary>
        /// Adds a pre-generated assembly to the assembly cache.
        /// </summary>
        /// <param name="targetAssemblyName">
        /// The name of the assembly the provided <paramref name="generatedAssembly"/> targets.
        /// </param>
        /// <param name="generatedAssembly">
        /// The generated assembly.
        /// </param>
        public static void AddGeneratedAssembly(string targetAssemblyName, byte[] generatedAssembly)
        {
            if (CodeGeneratorCacheInstance != null)
            {
                CodeGeneratorCacheInstance.AddGeneratedAssembly(targetAssemblyName, generatedAssembly);
            }
            else
            {
                Log.Warn(
                    ErrorCode.CodeGenDllMissing,
                    "CodeGenerationManager.AddCachedAssembly called but no code generator has been loaded.");
            }
        }

        /// <summary>
        /// Loads the code generator.
        /// </summary>
        /// <returns>The code generator.</returns>
        private static IRuntimeCodeGenerator LoadCodeGenerator()
        {
            IRuntimeCodeGenerator result = AssemblyLoader.TryLoadAndCreateInstance<IRuntimeCodeGenerator>(CodeGenAssemblyName, Log);
            if (result == null)
            {
                Log.Warn(
                    ErrorCode.CodeGenDllMissing,
                    "Code generator assembly (" + CodeGenAssemblyName + ".dll) not present.");
            }

            return result;
        }
    }
}
