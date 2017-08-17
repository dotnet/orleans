using System;
using System.Collections.Generic;
using System.IO;
#if !NET461
using System.Runtime.Loader;
using Orleans.Runtime.Configuration;
using Orleans.CodeGenerator;
using Orleans.Serialization;
#endif

namespace Orleans.CodeGeneration
{
    public class CodeGenerator
    {
        private static readonly int[] SuppressCompilerWarnings =
        {
            162, // CS0162 - Unreachable code detected.
            219, // CS0219 - The variable 'V' is assigned but its value is never used.
            414, // CS0414 - The private field 'F' is assigned but its value is never used.
            649, // CS0649 - Field 'F' is never assigned to, and will always have its default value.
            693, // CS0693 - Type parameter 'type parameter' has the same name as the type parameter from outer type 'T'
            1591, // CS1591 - Missing XML comment for publicly visible type or member 'Type_or_Member'
            1998 // CS1998 - This async method lacks 'await' operators and will run synchronously
        };

        private readonly CodeGenOptions options;
        private readonly Action<string> log;

        public CodeGenerator(CodeGenOptions options, Action<string> log)
        {
            this.options = options;
            this.log = log;
        }

        public bool GenerateCode()
        {
            var outputFileName = this.options.OutputFileName;

            // Create directory for output file if it does not exist
            var outputFileDirectory = Path.GetDirectoryName(outputFileName);

            if (!string.IsNullOrEmpty(outputFileDirectory) && !Directory.Exists(outputFileDirectory))
            {
                Directory.CreateDirectory(outputFileDirectory);
            }
            
#if NET461
            var generatedCode = AppDomainCodeGenerator.GenerateCode(this.options);
#else
            var generatedCode = this.GenerateCodeInternal();
#endif

            if (!string.IsNullOrWhiteSpace(generatedCode))
            {
                using (var sourceWriter = new StreamWriter(outputFileName))
                {
                    sourceWriter.WriteLine("#if !EXCLUDE_CODEGEN");
                    DisableWarnings(sourceWriter, SuppressCompilerWarnings);
                    sourceWriter.WriteLine(generatedCode);
                    RestoreWarnings(sourceWriter, SuppressCompilerWarnings);
                    sourceWriter.WriteLine("#endif");
                }

                this.log($"Orleans-CodeGen - Generated file written {outputFileName}");
                return true;
            }

            return false;
        }

#if !NET461
        private string GenerateCodeInternal()
        {
            var outputFileName = this.options.OutputFileName;
            var inputAssembly = this.options.InputAssembly.FullName;
            var referencedAssemblies = this.options.ReferencedAssemblies;

            // Set up assembly resolver
            var refResolver = new AssemblyResolver(inputAssembly, referencedAssemblies, this.log);

            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += refResolver.ResolveAssembly;
                AssemblyLoadContext.Default.Resolving += refResolver.AssemblyLoadContextResolving;

                // Load input assembly 
                // Special-case Orleans.dll because there is a circular dependency.
                var grainAssembly = refResolver.Assembly;


                var config = new ClusterConfiguration();
                var codeGenerator = new RoslynCodeGenerator(new SerializationManager(null, config.Globals, config.Defaults));

                // Generate source
                this.log($"Orleans-CodeGen - Generating file {outputFileName}");
                return codeGenerator.GenerateSourceForAssembly(grainAssembly);
            }
            finally
            {
                refResolver.Dispose();
                AppDomain.CurrentDomain.AssemblyResolve -= refResolver.ResolveAssembly;
                AssemblyLoadContext.Default.Resolving -= refResolver.AssemblyLoadContextResolving;
            }
        }
#endif

        private static void DisableWarnings(TextWriter sourceWriter, IEnumerable<int> warnings)
        {
            foreach (var warningNum in warnings) sourceWriter.WriteLine("#pragma warning disable {0}", warningNum);
        }

        private static void RestoreWarnings(TextWriter sourceWriter, IEnumerable<int> warnings)
        {
            foreach (var warningNum in warnings) sourceWriter.WriteLine("#pragma warning restore {0}", warningNum);
        }
    }
}