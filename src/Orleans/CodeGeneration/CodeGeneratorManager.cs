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
        private static readonly Lazy<IRuntimeCodeGenerator> CodeGeneratorInstance =
            new Lazy<IRuntimeCodeGenerator>(LoadCodeGenerator);

        /// <summary>
        /// The code generator cache.
        /// </summary>
        private static readonly Lazy<ICodeGeneratorCache> CodeGeneratorCacheInstance =
            new Lazy<ICodeGeneratorCache>(LoadCodeGeneratorCache);

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
        /// Ensures code for the <paramref name="input"/> assembly has been generated and loaded.
        /// </summary>
        /// <param name="input">
        /// The input assembly.
        /// </param>
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
        /// Returns the collection of generated assemblies as pairs of target assembly name to raw assembly bytes.
        /// </summary>
        /// <returns>The collection of generated assemblies.</returns>
        public static IDictionary<string, byte[]> GetGeneratedAssemblies()
        {
            var codeGen = CodeGeneratorCacheInstance.Value;
            if (codeGen != null)
            {
                return codeGen.GetGeneratedAssemblies();
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
            var codeGen = CodeGeneratorCacheInstance.Value;
            if (codeGen != null)
            {
                codeGen.AddGeneratedAssembly(targetAssemblyName, generatedAssembly);
            }
            else
            {
                Log.Warn(
                    ErrorCode.CodeGenDllMissing,
                    "CodeGenerationManager.AddCachedAssembly called but no code geenrator has been loaded.");
            }
        }

        /// <summary>
        /// Loads the code generator.
        /// </summary>
        /// <returns>The code generator.</returns>
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

        /// <summary>
        /// Loads the code generator cache.
        /// </summary>
        /// <returns>The code generator cache, or <see langword="null"/> if none was loaded.</returns>
        private static ICodeGeneratorCache LoadCodeGeneratorCache()
        {
            return CodeGeneratorInstance.Value as ICodeGeneratorCache;
        }
    }
}
