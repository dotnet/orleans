namespace Orleans.CodeGeneration
{
    using System;
    using System.Reflection;

    using Orleans.Runtime;

    internal static class CodeGeneratorManager
    {
        /// <summary>
        /// The name of the code generator assembly.
        /// </summary>
        private const string CodeGenAssemblyName = "OrleansCodeGenerator";

        /// <summary>
        /// The runtime code generator.
        /// </summary>
        private static readonly Lazy<IRuntimeCodeGenerator> CodeGeneratorInstance = new Lazy<IRuntimeCodeGenerator>(LoadCodeGenerator);

        /// <summary>
        /// The log.
        /// </summary>
        private static readonly TraceLogger Log = TraceLogger.GetLogger("CodeGenerator");

        /// <summary>
        /// Ensures code for the <paramref name="input"/> assembly has been generated and loaded.
        /// </summary>
        public static void GenerateAndCacheCodeForAssembly(Assembly input)
        {
            var codeGen = CodeGeneratorInstance.Value;
            if (codeGen != null)
            {
                codeGen.GenerateAndLoadForAssembly(input);
            }
        }

        /// <summary>
        /// Ensures code for all currently loaded assemblies has been generated and loaded.
        /// </summary>
        public static void GenerateAndCacheCodeForAllAssemblies()
        {
            var codeGen = CodeGeneratorInstance.Value;
            if (codeGen != null)
            {
                codeGen.GenerateAndLoadForAssemblies(AppDomain.CurrentDomain.GetAssemblies());
            }
        }

        /// <summary>
        /// Loads the code generator.
        /// </summary>
        /// <returns>The laoded code generator.</returns>
        private static IRuntimeCodeGenerator LoadCodeGenerator()
        {
            var result = AssemblyLoader.TryLoadAndCreateInstance<IRuntimeCodeGenerator>(CodeGenAssemblyName, Log);
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
