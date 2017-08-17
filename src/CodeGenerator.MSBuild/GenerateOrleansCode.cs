using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Orleans.CodeGeneration;
using Orleans.CodeGenerator;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Microsoft.Orleans.CodeGenerator.MSBuild
{
    public class GenerateOrleansCode : MSBuildTask
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
        
        [Required]
        public ITaskItem InputAssembly { get; set; }

        [Required]
        public ITaskItem OutputFileName { get; set; }

        [Required]
        public ITaskItem[] ReferencePaths { get; set; }

        public override bool Execute()
        {
            var options = new CodeGenOptions
            {
                InputAssembly = new FileInfo(this.InputAssembly.ItemSpec),
                OutputFileName = this.OutputFileName.ItemSpec,
                ReferencedAssemblies = this.ReferencePaths.Select(item => item.ItemSpec).ToList()
            };
            
            return this.GenerateSource(options);
        }
        
        /// <summary>
        /// Generates one GrainReference class for each Grain Type in the inputLib file 
        /// and output code file under outputLib directory
        /// </summary>
        private bool GenerateSource(CodeGenOptions options)
        {
            // Set up assembly resolver
            var refResolver = new ReferenceResolver(options.ReferencedAssemblies, this.Log);
            AppDomain.CurrentDomain.AssemblyResolve += refResolver.ResolveAssembly;

            var generatedCode = this.GenerateCodeInner(options);

            if (!string.IsNullOrWhiteSpace(generatedCode))
            {
                using (var sourceWriter = new StreamWriter(options.OutputFileName))
                {
                    sourceWriter.WriteLine("#if !EXCLUDE_CODEGEN");
                    DisableWarnings(sourceWriter, SuppressCompilerWarnings);
                    sourceWriter.WriteLine(generatedCode);
                    RestoreWarnings(sourceWriter, SuppressCompilerWarnings);
                    sourceWriter.WriteLine("#endif");
                }

                this.Log.LogMessage(MessageImportance.Normal, "Orleans-CodeGen - Generated file written {0}", options.OutputFileName);
                return true;
            }

            return false;
        }
        
        private string GenerateCodeInner(CodeGenOptions options)
        {
            // Load input assembly 
            // Special-case Orleans.dll because there is a circular dependency.
            var assemblyName = AssemblyName.GetAssemblyName(options.InputAssembly.FullName);
            var grainAssembly = Path.GetFileName(options.InputAssembly.FullName) != "Orleans.dll"
                ? Assembly.LoadFrom(options.InputAssembly.FullName)
                : Assembly.Load(assemblyName);
            
            // Create directory for output file if it does not exist
            var outputFileDirectory = Path.GetDirectoryName(options.OutputFileName);

            if (!string.IsNullOrEmpty(outputFileDirectory) && !Directory.Exists(outputFileDirectory))
            {
                Directory.CreateDirectory(outputFileDirectory);
            }

            var config = new ClusterConfiguration();
            var codeGenerator = new RoslynCodeGenerator(new SerializationManager(null, config.Globals, config.Defaults));

            // Generate source
            this.Log.LogMessage(MessageImportance.Normal, "Orleans-CodeGen - Generating file {0}", options.OutputFileName);

            return codeGenerator.GenerateSourceForAssembly(grainAssembly);
        }

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
