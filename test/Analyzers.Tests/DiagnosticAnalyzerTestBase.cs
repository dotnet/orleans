// Derived from Entity Framework Core Analyzer Tests
// https://github.com/aspnet/EntityFrameworkCore/blob/cbefe76162b16c1122629dbf6c85becbbb5cdc8e/test/EFCore.Analyzers.Tests/TestUtilities/DiagnosticAnalyzerTestBase.cs
// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using Xunit;

namespace Analyzers.Tests
{
    public abstract class DiagnosticAnalyzerTestBase<TDiagnosticAnalyzer>
        where TDiagnosticAnalyzer : DiagnosticAnalyzer, new()
    {
        private static readonly string[] Usings = new[] {
            "System",
            "System.Threading.Tasks",
            "Orleans"
        };

        protected virtual DiagnosticAnalyzer CreateDiagnosticAnalyzer() => new TDiagnosticAnalyzer();

        protected async Task AssertNoDiagnostics(string source, params string[] extraUsings)
        {
            var (diagnostics, _) = await this.GetDiagnosticsAsync(source, extraUsings);
            Assert.Empty(diagnostics);
        }

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
