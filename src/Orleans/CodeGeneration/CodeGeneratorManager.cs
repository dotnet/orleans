namespace Orleans.CodeGeneration
{
    using System;
    using System.Reflection;

    using Orleans.Runtime;

    internal static class CodeGeneratorManager
    {
        /// <summary>
        /// The runtime code generator.
        /// </summary>
        private static readonly Lazy<IRuntimeCodeGenerator> CodeGeneratorInstance = new Lazy<IRuntimeCodeGenerator>(LoadCodeGenerator);

        /// <summary>
        /// Ensures code the the <paramref name="input"/> assembly has been generated and loaded.
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
        /// Loads the code generator.
        /// </summary>
        /// <returns>The laoded code generator.</returns>
        private static IRuntimeCodeGenerator LoadCodeGenerator()
        {
            return AssemblyLoader.TryLoadAndCreateInstance<IRuntimeCodeGenerator>(
                "OrleansCodeGenerator",
                TraceLogger.GetLogger("OrleansCodeGenerator"));
        }
    }
}
