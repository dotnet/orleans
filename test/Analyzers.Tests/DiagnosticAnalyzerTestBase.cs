// Derived from Entity Framework Core Analyzer Tests
// https://github.com/aspnet/EntityFrameworkCore/blob/cbefe76162b16c1122629dbf6c85becbbb5cdc8e/test/EFCore.Analyzers.Tests/TestUtilities/DiagnosticAnalyzerTestBase.cs
// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using Xunit;

namespace Analyzers.Tests
{
    /// <summary>
    /// Base class for testing Roslyn diagnostic analyzers in the Orleans project.
    /// Provides common infrastructure for compiling test code and running analyzers to verify diagnostics.
    /// This enables testing of Orleans-specific code analysis rules that help developers avoid common mistakes.
    /// </summary>
    /// <typeparam name="TDiagnosticAnalyzer">The type of diagnostic analyzer being tested.</typeparam>
    public abstract class DiagnosticAnalyzerTestBase<TDiagnosticAnalyzer>
        where TDiagnosticAnalyzer : DiagnosticAnalyzer, new()
    {
        private static readonly string[] Usings = new[] {
            "System",
            "System.Threading.Tasks",
            "Orleans"
        };

        /// <summary>
        /// Provides test data for all Orleans grain interface types.
        /// Used by theory tests to ensure analyzers work correctly with all grain interface variations.
        /// </summary>
        public static IEnumerable<object[]> GrainInterfaces =>
            new List<object[]>
            {
                new object[] { "Orleans.IGrain" },
                new object[] { "Orleans.IGrainWithStringKey" },
                new object[] { "Orleans.IGrainWithGuidKey" },
                new object[] { "Orleans.IGrainWithGuidCompoundKey" },
                new object[] { "Orleans.IGrainWithIntegerKey" },
                new object[] { "Orleans.IGrainWithIntegerCompoundKey" }
            };

        /// <summary>
        /// Creates an instance of the diagnostic analyzer being tested.
        /// Can be overridden in derived classes to customize analyzer creation.
        /// </summary>
        protected virtual DiagnosticAnalyzer CreateDiagnosticAnalyzer() => new TDiagnosticAnalyzer();

        /// <summary>
        /// Asserts that the provided source code produces no diagnostics when analyzed.
        /// Used to verify that valid code patterns don't trigger false positives.
        /// </summary>
        /// <param name="source">The C# source code to analyze.</param>
        /// <param name="extraUsings">Additional using statements to include.</param>
        protected async Task AssertNoDiagnostics(string source, params string[] extraUsings)
        {
            var (diagnostics, _) = await this.GetDiagnosticsAsync(source, extraUsings);
            Assert.Empty(diagnostics);
        }

        /// <summary>
        /// Compiles the provided source code and runs the diagnostic analyzer on it.
        /// Returns any diagnostics produced along with the formatted source code.
        /// </summary>
        /// <param name="source">The C# source code to analyze.</param>
        /// <param name="extraUsings">Additional using statements to include.</param>
        /// <returns>A tuple containing the diagnostics array and the formatted source code.</returns>
        protected virtual async Task<(Diagnostic[], string)> GetDiagnosticsAsync(string source, params string[] extraUsings)
        {
            var sb = new StringBuilder();
            foreach (var @using in Usings.Concat(extraUsings))
            {
                sb.AppendLine($"using {@using};");
            }
            sb.AppendLine(source);

            var sourceText = sb.ToString();
            return (await this.GetDiagnosticsFullSourceAsync(sourceText), sourceText);
        }

        protected async Task<Diagnostic[]> GetDiagnosticsFullSourceAsync(string source)
        {
            var compilation = await CreateProject(source).GetCompilationAsync();
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);

            Assert.Empty(errors);
            var analyzer = this.CreateDiagnosticAnalyzer();
            var compilationWithAnalyzers
                = compilation
                    .WithOptions(
                        compilation.Options.WithSpecificDiagnosticOptions(
                            analyzer.SupportedDiagnostics.ToDictionary(d => d.Id, d => ReportDiagnostic.Default)))
                    .WithAnalyzers(ImmutableArray.Create(analyzer));

            var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

            return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToArray();
        }

        private static Project CreateProject(string source)
        {
            const string fileName = "Test.cs";

            var projectId = ProjectId.CreateNewId(debugName: "TestProject");
            var documentId = DocumentId.CreateNewId(projectId, fileName);

            var assemblies = new[]
            {
                typeof(Task).Assembly,
                typeof(Orleans.IGrain).Assembly,
                typeof(Orleans.Grain).Assembly,
                typeof(Attribute).Assembly,
                typeof(int).Assembly,
                typeof(object).Assembly,
            }; 

            var metadataReferences = assemblies
                .SelectMany(x => x.GetReferencedAssemblies().Select(Assembly.Load))
                .Concat(assemblies)
                .Distinct()
                .Select(x => MetadataReference.CreateFromFile(x.Location))
                .Cast<MetadataReference>()
                .ToList();

            var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location);
            metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "mscorlib.dll")));
            metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.dll")));
            metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Core.dll")));
            metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));

            var solution = new AdhocWorkspace()
                .CurrentSolution
                .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
                .AddMetadataReferences(projectId, metadataReferences)
                .AddDocument(documentId, fileName, SourceText.From(source));

            return solution.GetProject(projectId)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }
    }
}
