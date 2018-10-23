﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Orleans.CodeGenerator.MSBuild
{
    public class CodeGeneratorCommand
    {
        private const string AbstractionsAssemblyShortName = "Orleans.Core.Abstractions";

        private static readonly int[] SuppressCompilerWarnings =
        {
            162, // CS0162 - Unreachable code detected.
            219, // CS0219 - The variable 'V' is assigned but its value is never used.
            414, // CS0414 - The private field 'F' is assigned but its value is never used.
            618, // CS0616 - Member is obsolete.
            649, // CS0649 - Field 'F' is never assigned to, and will always have its default value.
            693, // CS0693 - Type parameter 'type parameter' has the same name as the type parameter from outer type 'T'
            1591, // CS1591 - Missing XML comment for publicly visible type or member 'Type_or_Member'
            1998 // CS1998 - This async method lacks 'await' operators and will run synchronously
        };

        public ILogger Log { get; set; }

        /// <summary>
        /// The MSBuild project path.
        /// </summary>
        public string ProjectPath { get; set; }

        /// <summary>
        /// The optional ProjectGuid.
        /// </summary>
        public string ProjectGuid { get; set; }

        /// <summary>
        /// The output type, such as Exe, or Library.
        /// </summary>
        public string OutputType { get; set; }

        /// <summary>
        /// The target path of the compilation.
        /// </summary>
        public string TargetPath { get; set; }

        /// <summary>
        /// The source files.
        /// </summary>
        public List<string> Compile { get; } = new List<string>();

        /// <summary>
        /// The libraries referenced by the project.
        /// </summary>
        public List<string> Reference { get; } = new List<string>();

        /// <summary>
        /// The defined constants for the project.
        /// </summary>
        public List<string> DefineConstants { get; } = new List<string>();

        /// <summary>
        /// The file which holds the generated code.
        /// </summary>
        public string CodeGenOutputFile { get; set; }

        /// <summary>
        /// The project's assembly name, important for id calculations.
        /// </summary>
        public string AssemblyName { get; set; }

        public async Task<bool> Execute(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var projectName = Path.GetFileNameWithoutExtension(ProjectPath);
            var projectId = !string.IsNullOrEmpty(ProjectGuid) && Guid.TryParse(ProjectGuid, out var projectIdGuid)
                ? ProjectId.CreateFromSerialized(projectIdGuid)
                : ProjectId.CreateNewId();

            this.Log.LogDebug($"AssemblyName: {this.AssemblyName}");
            if (!string.IsNullOrWhiteSpace(this.ProjectGuid)) this.Log.LogDebug($"ProjectGuid: {this.ProjectGuid}");
            this.Log.LogDebug($"ProjectID: {projectId}");
            this.Log.LogDebug($"ProjectName: {projectName}");
            this.Log.LogDebug($"CodeGenOutputFile: {this.CodeGenOutputFile}");
            this.Log.LogDebug($"ProjectPath: {this.ProjectPath}");
            this.Log.LogDebug($"OutputType: {this.OutputType}");
            this.Log.LogDebug($"TargetPath: {this.TargetPath}");
            this.Log.LogDebug($"DefineConstants ({this.DefineConstants.Count}): {string.Join(", ", this.DefineConstants)}");
            this.Log.LogDebug($"Sources ({this.Compile.Count}): {string.Join(", ", this.Compile)}");
            this.Log.LogDebug($"References ({this.Reference.Count}): {string.Join(", ", this.Reference)}");

            var languageName = GetLanguageName(ProjectPath);

            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Default,
                projectName,
                AssemblyName,
                languageName,
                ProjectPath,
                TargetPath,
                CreateCompilationOptions(this),
                documents: GetDocuments(Compile, projectId),
                metadataReferences: GetMetadataReferences(Reference),
                parseOptions: new CSharpParseOptions(preprocessorSymbols: this.DefineConstants)
            );
            
            var workspace = new AdhocWorkspace();
            workspace.AddProject(projectInfo);

            var project = workspace.CurrentSolution.Projects.Single();
            this.Log.LogDebug($"Workspace creation completed in {stopwatch.ElapsedMilliseconds}ms.");
            stopwatch.Restart();

            var compilation = await project.GetCompilationAsync(cancellationToken);
            this.Log.LogDebug($"GetCompilation completed in {stopwatch.ElapsedMilliseconds}ms.");
            stopwatch.Restart();

            if (compilation.ReferencedAssemblyNames.All(name => name.Name != AbstractionsAssemblyShortName))
            {
                this.Log.LogWarning($"Assembly {compilation.AssemblyName} does not reference {AbstractionsAssemblyShortName} (references: {string.Join(", ", compilation.ReferencedAssemblyNames)})");
                return false;
            }

            var generator = new CodeGenerator(compilation, this.Log);
            var syntax = generator.GenerateCode(cancellationToken);
            this.Log.LogDebug($"GenerateCode completed in {stopwatch.ElapsedMilliseconds}ms.");
            stopwatch.Restart();

            var normalized = syntax.NormalizeWhitespace();
            this.Log.LogDebug($"NormalizeWhitespace completed in {stopwatch.ElapsedMilliseconds}ms.");
            stopwatch.Restart();
            
            var sourceBuilder = new StringBuilder();
            sourceBuilder.AppendLine("// <auto-generated />");
            sourceBuilder.AppendLine("#if !EXCLUDE_GENERATED_CODE");
            foreach (var warningNum in SuppressCompilerWarnings) sourceBuilder.AppendLine($"#pragma warning disable {warningNum}");
            sourceBuilder.AppendLine(normalized.ToFullString());
            foreach (var warningNum in SuppressCompilerWarnings) sourceBuilder.AppendLine($"#pragma warning restore {warningNum}");
            sourceBuilder.AppendLine("#endif");
            var source = sourceBuilder.ToString();

            this.Log.LogDebug($"Generate source from syntax completed in {stopwatch.ElapsedMilliseconds}ms.");
            stopwatch.Restart();

            if (File.Exists(this.CodeGenOutputFile))
            {
                using (var reader = new StreamReader(this.CodeGenOutputFile))
                {
                    var existing = await reader.ReadToEndAsync();
                    if (string.Equals(source, existing, StringComparison.Ordinal))
                    {
                        this.Log.LogDebug("Generated code matches existing code.");
                        return true;
                    }
                }
            }

            using (var sourceWriter = new StreamWriter(this.CodeGenOutputFile))
            {
                await sourceWriter.WriteAsync(source);
            }

            this.Log.LogDebug($"Write source to disk completed in {stopwatch.ElapsedMilliseconds}ms.");

            return true;
        }

        private static IEnumerable<DocumentInfo> GetDocuments(List<string> sources, ProjectId projectId) =>
            sources
                ?.Where(File.Exists)
                .Select(x => DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(x),
                    loader: TextLoader.From(
                        TextAndVersion.Create(
                            SourceText.From(File.ReadAllText(x)), VersionStamp.Create())),
                    filePath: x))
            ?? Array.Empty<DocumentInfo>();

        private static IEnumerable<MetadataReference> GetMetadataReferences(List<string> references) =>
            references
                ?.Where(File.Exists)
                .Select(x => MetadataReference.CreateFromFile(x))
            ?? (IEnumerable<MetadataReference>)Array.Empty<MetadataReference>();


        private static string GetLanguageName(string projectPath)
        {
            switch (Path.GetExtension(projectPath))
            {
                case ".csproj":
                    return LanguageNames.CSharp;
                case string ext when !string.IsNullOrWhiteSpace(ext):
                    throw new NotSupportedException($"Projects of type {ext} are not supported.");
                default:
                    throw new InvalidOperationException("Could not determine supported language from project path");

            }
        }

        private static CompilationOptions CreateCompilationOptions(CodeGeneratorCommand command)
        {
            OutputKind kind;
            switch (command.OutputType)
            {
                case "Exe":
                    kind = OutputKind.ConsoleApplication;
                    break;
                case "Module":
                    kind = OutputKind.NetModule;
                    break;
                case "Winexe":
                    kind = OutputKind.WindowsApplication;
                    break;
                default:
                case "Library":
                    kind = OutputKind.DynamicallyLinkedLibrary;
                    break;
            }

            return new CSharpCompilationOptions(kind)
                .WithMetadataImportOptions(MetadataImportOptions.All)
                .WithAllowUnsafe(true)
                .WithConcurrentBuild(true)
                .WithOptimizationLevel(OptimizationLevel.Debug);
        }
    }
}
